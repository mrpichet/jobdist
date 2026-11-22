using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Channels;
using JobDist.Contracts;
using JobDist.Contracts.Security;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace JobDist.Worker.Sdk;

/// <summary>Job types registered with <c>AddJobHandler&lt;T&gt;()</c> (handler type per registration).</summary>
public sealed class JobHandlerRegistry
{
    /// <summary>Registered handler implementation types.</summary>
    public List<Type> HandlerTypes { get; } = [];
}

/// <summary>
/// The worker runtime (hosted service). Life cycle:
/// <list type="number">
/// <item>Discover handlers, measure a benchmark, generate a hybrid (ML-KEM-768 + ECDH-P384) key pair for sensitive payloads.</item>
/// <item>Register with exponential back-off until a server instance answers.</item>
/// <item>Run three loops in parallel: heartbeat (load + commands), long-poll (assignments), outbox (completion reports).</item>
/// <item>Each assignment is verified (worker id, hybrid signature, payload hash), decrypted if needed, and executed under
///       per-type and total concurrency limits with a timeout and a server-cancellable token.</item>
/// <item>Network loss: reports wait in the outbox and are retried; a lost identity (403/404) triggers re-registration.</item>
/// <item>Graceful shutdown: stop polling, let running jobs finish within the grace period, flush reports, unregister.</item>
/// </list>
/// </summary>
public sealed class JobDistWorkerService(
    WorkerApiClient api,
    IServiceScopeFactory scopes,
    JobHandlerRegistry registry,
    WorkerMetrics metrics,
    IOptions<JobDistWorkerOptions> options,
    TimeProvider time,
    ILogger<JobDistWorkerService> logger) : BackgroundService
{
    private const int MaxResultChars = 1_048_576;
    private const int MaxErrorChars = 4000;

    private readonly ConcurrentDictionary<Guid, RunningJob> _running = new();
    private readonly ConcurrentDictionary<Guid, DateTimeOffset> _seenCommands = new();
    private readonly ConcurrentDictionary<Guid, DateTimeOffset> _cancelledJobs = new();
    private readonly Channel<PendingReport> _outbox = Channel.CreateUnbounded<PendingReport>(new UnboundedChannelOptions { SingleReader = false });
    private readonly SemaphoreSlim _registerLock = new(1, 1);
    private readonly SemaphoreSlim _slotFreed = new(0, int.MaxValue);
    private readonly CancellationTokenSource _jobsCts = new();
    private readonly Dictionary<string, HandlerInfo> _handlers = new(StringComparer.Ordinal);

    private HybridKeyPair? _keyPair;
    private HybridSignatureVerifier? _verifier;
    private DateTimeOffset _lastKeyRefresh = DateTimeOffset.MinValue;
    private RegisterWorkerRequest? _registration;
    private int _maxTotal;
    private int _activeCount;
    private int _heartbeatSeconds = 10;
    private long _generation;
    private volatile bool _draining;
    private volatile bool _stopping;

    /// <summary>True when the server asked this worker to drain (no new jobs).</summary>
    public bool IsDraining => _draining;

    /// <summary>Worker id assigned by the server (empty before registration).</summary>
    public Guid WorkerId => api.WorkerId;

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Step 1: build the handler table and the registration message.
        DiscoverHandlers();
        _keyPair = HybridKeyPair.Generate();
        _registration = BuildRegistration();

        // Step 2: register (retries forever with back-off; the server may be starting or the network down).
        await RegisterWithBackoffAsync(stoppingToken).ConfigureAwait(false);

        // Step 3: run the loops until the host stops.
        var outboxWorkers = Enumerable.Range(0, 4).Select(_ => OutboxLoopAsync(_jobsCts.Token)).ToArray();
        var heartbeat = HeartbeatLoopAsync(stoppingToken);
        var poll = PollLoopAsync(stoppingToken);
        try
        {
            await Task.WhenAll(heartbeat, poll).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Normal shutdown.
        }

        // Step 4: graceful shutdown.
        await ShutdownAsync(outboxWorkers).ConfigureAwait(false);
    }

    // =========================================================================================== registration

    /// <summary>Resolves every registered handler once to read its job type and concurrency.</summary>
    /// <exception cref="InvalidOperationException">No handler, or two handlers for the same job type.</exception>
    private void DiscoverHandlers()
    {
        // Step 1: resolve each handler type in a temporary scope (handlers are scoped services).
        using var scope = scopes.CreateScope();
        var o = options.Value;
        foreach (var type in registry.HandlerTypes)
        {
            var handler = (IJobHandler)scope.ServiceProvider.GetRequiredService(type);
            if (!InputGuardJobType(handler.JobType))
            {
                throw new InvalidOperationException($"Handler {type.Name} has an invalid job type '{handler.JobType}'.");
            }

            // Step 2: configuration overrides beat handler defaults.
            var concurrency = o.ConcurrencyOverrides.TryGetValue(handler.JobType, out var c) ? c : handler.MaxConcurrency;
            concurrency = Math.Clamp(concurrency, 1, 1024);
            if (!_handlers.TryAdd(handler.JobType, new HandlerInfo(type, concurrency, Math.Clamp(handler.SpeedFactor, 0.01, 100), new SemaphoreSlim(concurrency, concurrency))))
            {
                throw new InvalidOperationException($"Two handlers registered for job type '{handler.JobType}'.");
            }
        }
        if (_handlers.Count == 0)
        {
            throw new InvalidOperationException("No job handler registered. Call AddJobHandler<T>().");
        }

        // Step 3: total cap defaults to the number of cores (never more than the sum of per-type limits).
        _maxTotal = o.MaxTotalConcurrency > 0 ? o.MaxTotalConcurrency : Environment.ProcessorCount;
        _maxTotal = Math.Clamp(Math.Min(_maxTotal, _handlers.Values.Sum(h => h.MaxConcurrency)), 1, 4096);
    }

    /// <summary>Builds the registration request (capabilities, spec, encryption public keys).</summary>
    private RegisterWorkerRequest BuildRegistration()
    {
        var o = options.Value;
        var memoryMb = Math.Max(64, GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / (1024 * 1024));
        var os = RuntimeInformation.OSDescription;
        var spec = new WorkerSpecRequest(
            CpuCores: Math.Clamp(Environment.ProcessorCount, 1, 4096),
            MemoryMb: Math.Min(memoryMb, 16_777_216),
            GpuCount: o.GpuCount,
            BenchmarkScore: o.BenchmarkScore > 0 ? o.BenchmarkScore : MeasureBenchmark(),
            Os: os.Length > 200 ? os[..200] : os);
        var capabilities = _handlers.Select(kv => new WorkerCapabilityRequest(kv.Key, kv.Value.MaxConcurrency, kv.Value.SpeedFactor)).ToList();
        return new RegisterWorkerRequest(
            Name: string.IsNullOrWhiteSpace(o.WorkerName) ? SanitizeName(Environment.MachineName) : o.WorkerName,
            Capabilities: capabilities,
            Spec: spec,
            MaxTotalConcurrency: _maxTotal,
            EcdhPublicKey: _keyPair!.EcdhPublicKeyBase64,
            MlKemPublicKey: _keyPair.MlKemPublicKeyBase64,
            Version: typeof(JobDistWorkerService).Assembly.GetName().Version?.ToString(3),
            Tags: o.Tags.Take(20).ToList());
    }

    /// <summary>Registers, retrying with exponential back-off (1 s … 60 s, ±20 % jitter) until success or shutdown.</summary>
    /// <param name="ct">Stopping token.</param>
    private async Task RegisterWithBackoffAsync(CancellationToken ct)
    {
        var delay = TimeSpan.FromSeconds(1);
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                // Step 1: register and trust the returned keys (optionally pinned).
                var response = await api.RegisterAsync(_registration!, ct).ConfigureAwait(false);
                InstallKeys(response.ServerKeys);
                _heartbeatSeconds = Math.Clamp(response.HeartbeatIntervalSeconds, 1, 300);
                Interlocked.Increment(ref _generation);
                logger.LogInformation("Registered as worker {WorkerId} ({Name}) with {Types} job types, max {Max} parallel jobs.",
                    response.WorkerId, _registration!.Name, _handlers.Count, _maxTotal);
                return;
            }
            catch (WorkerApiException ex) when (ex.StatusCode is HttpStatusCode.Unauthorized)
            {
                // Step 2a: wrong API key will not fix itself quickly; log loudly and retry slowly.
                logger.LogError("Registration rejected: API key invalid or missing the Worker role.");
                delay = TimeSpan.FromSeconds(60);
            }
            catch (WorkerApiException ex) when (ex.StatusCode is HttpStatusCode.BadRequest)
            {
                // Step 2b: invalid registration data is a configuration bug: stop the worker.
                throw new InvalidOperationException($"Registration rejected by the server: {ex.Message}", ex);
            }
            catch (WorkerApiException ex)
            {
                logger.LogWarning("Registration failed ({Status}): {Message}. Retrying in {Delay}.", (int)ex.StatusCode, ex.Message, delay);
            }

            // Step 3: back off with jitter so a recovering cluster is not hit by every worker at once.
            await Task.Delay(Jitter(delay), time, ct).ConfigureAwait(false);
            delay = TimeSpan.FromSeconds(Math.Min(60, delay.TotalSeconds * 2));
        }
    }

    /// <summary>
    /// Re-registers after the server forgot this worker (403/404), e.g. it was declared lost during a network outage.
    /// Jobs held by the old identity were already re-distributed, so local executions are abandoned.
    /// </summary>
    /// <param name="observedGeneration">Generation seen by the caller (prevents several loops re-registering twice).</param>
    /// <param name="ct">Stopping token.</param>
    private async Task ReRegisterAsync(long observedGeneration, CancellationToken ct)
    {
        await _registerLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // Step 1: another loop already re-registered.
            if (Interlocked.Read(ref _generation) != observedGeneration)
            {
                return;
            }
            logger.LogWarning("Worker identity lost (declared offline or unregistered by an admin). Re-registering.");
            metrics.ReRegistered();

            // Step 2: abandon executions of the old identity silently (their reports would be rejected as stale).
            foreach (var job in _running.Values)
            {
                job.Abandoned = true;
                job.Cancel();
            }
            await RegisterWithBackoffAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _registerLock.Release();
        }
    }

    /// <summary>Installs a signature verifier for the server keys, enforcing the optional key pin.</summary>
    /// <param name="keys">Keys from the server.</param>
    /// <exception cref="CryptographicException">Pinned key id does not match.</exception>
    private void InstallKeys(ServerKeysResponse keys)
    {
        var pin = options.Value.PinnedServerKeyId;
        if (!string.IsNullOrEmpty(pin) && !string.Equals(pin, keys.KeyId, StringComparison.Ordinal))
        {
            throw new CryptographicException($"Server key id '{keys.KeyId}' does not match the pinned key id.");
        }
        // Old verifiers may still be in use by a concurrent verification; they are released by the GC.
        Interlocked.Exchange(ref _verifier, new HybridSignatureVerifier(keys));
        _lastKeyRefresh = time.GetUtcNow();
    }

    // =========================================================================================== heartbeat

    /// <summary>Sends heartbeats with live load and processes returned commands.</summary>
    /// <param name="ct">Stopping token.</param>
    private async Task HeartbeatLoopAsync(CancellationToken ct)
    {
        var cpu = new CpuSampler();
        while (!ct.IsCancellationRequested)
        {
            var generation = Interlocked.Read(ref _generation);
            try
            {
                // Step 1: report running jobs and resource usage.
                var memInfo = GC.GetGCMemoryInfo();
                var memPercent = memInfo.TotalAvailableMemoryBytes > 0 ? 100.0 * memInfo.MemoryLoadBytes / memInfo.TotalAvailableMemoryBytes : 0;
                var response = await api.HeartbeatAsync(new HeartbeatRequest(
                    _running.Keys.Take(4096).ToList(), Math.Clamp(cpu.Sample(), 0, 100), Math.Clamp(memPercent, 0, 100)), ct).ConfigureAwait(false);

                // Step 2: server-side drain flag and signed commands.
                if (response.Status == WorkerStatus.Draining && !_draining)
                {
                    logger.LogInformation("Server set this worker to Draining: no new jobs will be accepted.");
                    _draining = true;
                }
                await HandleCommandsAsync(response.Commands, ct).ConfigureAwait(false);
            }
            catch (WorkerApiException ex) when (ex.RequiresReRegistration)
            {
                await ReRegisterAsync(generation, ct).ConfigureAwait(false);
                continue;
            }
            catch (WorkerApiException ex)
            {
                logger.LogWarning("Heartbeat failed: {Message}", ex.Message);
            }
            PruneMemory();
            await Task.Delay(TimeSpan.FromSeconds(_heartbeatSeconds), time, ct).ConfigureAwait(false);
        }
    }

    /// <summary>Applies signed commands (cancel / drain) once each.</summary>
    /// <param name="commands">Commands from heartbeat or poll.</param>
    /// <param name="ct">Cancellation token.</param>
    private async Task HandleCommandsAsync(IReadOnlyList<WorkerCommandResponse> commands, CancellationToken ct)
    {
        foreach (var command in commands)
        {
            // Step 1: commands are re-delivered until acknowledged: process each id once.
            if (!_seenCommands.TryAdd(command.CommandId, time.GetUtcNow()))
            {
                continue;
            }

            // Step 2: authenticity (server signature) and addressing (this worker).
            if (command.WorkerId != api.WorkerId || !await VerifyAsync(CanonicalDigest.ForCommand(command), command.Signature, ct).ConfigureAwait(false))
            {
                metrics.Rejected("command-signature");
                logger.LogWarning("Rejected command {CommandId}: signature or worker id invalid.", command.CommandId);
                continue;
            }

            // Step 3: execute.
            switch (command.Kind)
            {
                case WorkerCommandKind.CancelJob when command.JobId is { } jobId:
                    _cancelledJobs[jobId] = time.GetUtcNow();
                    if (_running.TryGetValue(jobId, out var job))
                    {
                        logger.LogInformation("Cancelling job {JobId} on server request: {Reason}", jobId, command.Reason);
                        job.CancelledByServer = true;
                        job.Cancel();
                    }
                    break;
                case WorkerCommandKind.Drain:
                    logger.LogInformation("Drain command received: {Reason}", command.Reason);
                    _draining = true;
                    break;
            }
        }
    }

    // =========================================================================================== polling

    /// <summary>Long-polls for assignments whenever there is free capacity.</summary>
    /// <param name="ct">Stopping token.</param>
    private async Task PollLoopAsync(CancellationToken ct)
    {
        var errorDelay = TimeSpan.FromSeconds(1);
        while (!ct.IsCancellationRequested)
        {
            // Step 1: no polling while draining or full; wait for a slot (or re-check every second).
            var free = _maxTotal - Volatile.Read(ref _activeCount);
            if (_draining || free <= 0)
            {
                await _slotFreed.WaitAsync(TimeSpan.FromSeconds(1), ct).ConfigureAwait(false);
                continue;
            }

            var generation = Interlocked.Read(ref _generation);
            try
            {
                // Step 2: ask for at most as many jobs as we can start now.
                var response = await api.PollAsync(new PollRequest(Math.Min(free, 256), options.Value.PollWaitSeconds), ct).ConfigureAwait(false);
                errorDelay = TimeSpan.FromSeconds(1);
                await HandleCommandsAsync(response.Commands, ct).ConfigureAwait(false);

                // Step 3: verify and start each assignment.
                foreach (var assignment in response.Assignments)
                {
                    await AcceptAsync(assignment, ct).ConfigureAwait(false);
                }
            }
            catch (WorkerApiException ex) when (ex.RequiresReRegistration)
            {
                await ReRegisterAsync(generation, ct).ConfigureAwait(false);
            }
            catch (WorkerApiException ex)
            {
                // Step 4: network down / all servers busy → back off (max 30 s).
                logger.LogWarning("Poll failed: {Message}. Retrying in {Delay}.", ex.Message, errorDelay);
                await Task.Delay(Jitter(errorDelay), time, ct).ConfigureAwait(false);
                errorDelay = TimeSpan.FromSeconds(Math.Min(30, errorDelay.TotalSeconds * 2));
            }
        }
    }

    /// <summary>
    /// Validates an assignment and starts it in the background.
    /// Checks: addressed to this worker, known job type, not a duplicate, valid hybrid signature, payload decrypts and
    /// matches the signed SHA-256.
    /// </summary>
    /// <param name="a">Assignment.</param>
    /// <param name="ct">Cancellation token.</param>
    private async Task AcceptAsync(JobAssignmentResponse a, CancellationToken ct)
    {
        metrics.Received(a.JobType);

        // Step 1: duplicates (re-delivery after a lost poll response) are ignored.
        if (_running.TryGetValue(a.JobId, out var existing) && existing.AttemptId == a.AttemptId)
        {
            return;
        }

        // Step 2: addressing and signature.
        string? reason = null;
        if (a.WorkerId != api.WorkerId)
        {
            reason = "assignment for another worker";
        }
        else if (!await VerifyAsync(CanonicalDigest.ForAssignment(a), a.Signature, ct).ConfigureAwait(false))
        {
            reason = "invalid server signature";
        }
        else if (!_handlers.ContainsKey(a.JobType))
        {
            reason = $"no handler for job type '{a.JobType}'";
        }

        // Step 3: decrypt sensitive payloads (associated data = attempt id binds the ciphertext to this attempt).
        string? payload = a.Payload;
        if (reason is null && a.EncryptedPayload is not null)
        {
            try
            {
                payload = Encoding.UTF8.GetString(PqcEnvelope.Open(_keyPair!, a.EncryptedPayload, a.AttemptId.ToByteArray()));
            }
            catch (CryptographicException)
            {
                reason = "payload decryption failed";
            }
        }

        // Step 4: integrity of the (decrypted) payload against the signed hash.
        if (reason is null && !string.Equals(CanonicalDigest.PayloadHash(payload), a.PayloadSha256, StringComparison.OrdinalIgnoreCase))
        {
            reason = "payload hash mismatch";
        }

        if (reason is not null)
        {
            // Step 5: refuse; tell the server (retryable) so the job goes to another worker quickly.
            metrics.Rejected(reason);
            logger.LogWarning("Rejected assignment {AttemptId} of job {JobId}: {Reason}.", a.AttemptId, a.JobId, reason);
            Enqueue(a.JobId, a.JobType, new JobCompletedRequest(a.AttemptId, AttemptOutcome.Failed, 0, null, "Worker rejected assignment: " + reason, Retryable: true));
            return;
        }

        // Step 6: reserve a slot and run in the background (the poll loop keeps polling for other work).
        var job = new RunningJob(a, payload, a.EncryptedPayload is not null, _jobsCts.Token, Interlocked.Read(ref _generation));
        _running[a.JobId] = job;
        Interlocked.Increment(ref _activeCount);
        metrics.RunningDelta(1);
        _ = Task.Run(() => RunJobAsync(job), CancellationToken.None);
    }

    /// <summary>Verifies a server signature; on failure refreshes the server keys once (key rotation) unless pinned.</summary>
    /// <param name="digest">Canonical digest.</param>
    /// <param name="signature">Signature.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>True when valid, or when signatures are not required.</returns>
    private async Task<bool> VerifyAsync(byte[] digest, SignatureEnvelope signature, CancellationToken ct)
    {
        if (!options.Value.RequireSignatures)
        {
            return true;
        }
        if (Volatile.Read(ref _verifier)!.Verify(digest, signature))
        {
            return true;
        }

        // Key rotation: re-fetch at most once a minute and never when a key id is pinned.
        if (!string.IsNullOrEmpty(options.Value.PinnedServerKeyId) || time.GetUtcNow() - _lastKeyRefresh < TimeSpan.FromMinutes(1))
        {
            return false;
        }
        try
        {
            InstallKeys(await api.GetServerKeysAsync(ct).ConfigureAwait(false));
            return Volatile.Read(ref _verifier)!.Verify(digest, signature);
        }
        catch (Exception ex) when (ex is WorkerApiException or CryptographicException or FormatException)
        {
            logger.LogWarning("Could not refresh server keys: {Message}", ex.Message);
            return false;
        }
    }

    // =========================================================================================== execution

    /// <summary>Executes one job: concurrency slot, started report, handler with timeout/cancel, outcome report.</summary>
    /// <param name="job">Running job state.</param>
    private async Task RunJobAsync(RunningJob job)
    {
        var a = job.Assignment;
        var info = _handlers[a.JobType];
        var slotTaken = false;
        var start = Stopwatch.GetTimestamp();
        try
        {
            // Step 1: per-type slot (the server already respects the limits; this is a safety net).
            await info.Slots.WaitAsync(job.Token).ConfigureAwait(false);
            slotTaken = true;

            // Step 2: a cancel may have arrived before we started.
            if (_cancelledJobs.ContainsKey(a.JobId))
            {
                Report(job, AttemptOutcome.Cancelled, 0, null, "Cancelled before start", false);
                return;
            }

            // Step 3: tell the server we started (best effort: a network error must not block execution).
            try
            {
                if (await api.StartedAsync(a.JobId, a.AttemptId, job.Token).ConfigureAwait(false))
                {
                    Report(job, AttemptOutcome.Cancelled, 0, null, "Cancelled before start", false);
                    return;
                }
            }
            catch (WorkerApiException ex) when (ex.StatusCode is HttpStatusCode.Conflict or HttpStatusCode.NotFound)
            {
                logger.LogInformation("Job {JobId} was re-distributed before it started; skipping.", a.JobId);
                job.Abandoned = true;
                return;
            }
            catch (WorkerApiException ex)
            {
                logger.LogDebug("Started report for {JobId} failed ({Message}); executing anyway.", a.JobId, ex.Message);
            }

            // Step 4: run the handler with the server timeout.
            job.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, a.TimeoutSeconds)));
            start = Stopwatch.GetTimestamp();
            using var scope = scopes.CreateScope();
            var handler = (IJobHandler)scope.ServiceProvider.GetRequiredService(info.HandlerType);
            var context = new JobContext(a.JobId, a.AttemptId, a.JobType, a.Attempt, a.Priority, TimeSpan.FromSeconds(a.TimeoutSeconds), job.Payload, job.WasEncrypted);
            var result = await handler.ExecuteAsync(context, job.Token).ConfigureAwait(false);
            var ms = Stopwatch.GetElapsedTime(start).TotalMilliseconds;

            // Step 5: map the handler result.
            if (result.Success && result.Output is { Length: > MaxResultChars })
            {
                Report(job, AttemptOutcome.Failed, ms, null, "Result exceeds 1 MB", false);
            }
            else
            {
                Report(job, result.Success ? AttemptOutcome.Succeeded : AttemptOutcome.Failed, ms, result.Output, result.Error, result.Retryable);
            }
        }
        catch (OperationCanceledException) when (job.Token.IsCancellationRequested)
        {
            // Step 6: why was it cancelled? server cancel, timeout, re-registration or shutdown.
            var ms = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            if (job.CancelledByServer)
            {
                Report(job, AttemptOutcome.Cancelled, ms, null, "Cancelled by server", false);
            }
            else if (!job.Abandoned && !_stopping)
            {
                Report(job, AttemptOutcome.TimedOut, ms, null, $"Timed out after {a.TimeoutSeconds} s", true);
            }
            // Shutdown / abandoned: no report; unregister (Released) or heartbeat loss (WorkerLost) re-distributes the job.
        }
        catch (NonRetryableJobException ex)
        {
            Report(job, AttemptOutcome.Failed, Stopwatch.GetElapsedTime(start).TotalMilliseconds, null, ex.Message, false);
        }
        catch (Exception ex)
        {
            // Step 7: unexpected handler error: retryable, report type + message only (no stack trace leaves the machine).
            logger.LogError(ex, "Handler for {JobType} failed on job {JobId}.", a.JobType, a.JobId);
            Report(job, AttemptOutcome.Failed, Stopwatch.GetElapsedTime(start).TotalMilliseconds, null, $"{ex.GetType().Name}: {ex.Message}", true);
        }
        finally
        {
            // Step 8: free the slots and wake the poll loop.
            if (slotTaken)
            {
                info.Slots.Release();
            }
            _running.TryRemove(new KeyValuePair<Guid, RunningJob>(a.JobId, job));
            job.Dispose();
            Interlocked.Decrement(ref _activeCount);
            metrics.RunningDelta(-1);
            _slotFreed.Release();
        }
    }

    /// <summary>Records metrics and queues the completion report.</summary>
    private void Report(RunningJob job, AttemptOutcome outcome, double ms, string? result, string? error, bool retryable)
    {
        metrics.Completed(job.Assignment.JobType, outcome.ToString(), ms);
        if (error is { Length: > MaxErrorChars })
        {
            error = error[..MaxErrorChars];
        }
        Enqueue(job.Assignment.JobId, job.Assignment.JobType,
            new JobCompletedRequest(job.Assignment.AttemptId, outcome, Math.Max(0, ms), result, error, outcome == AttemptOutcome.Failed && retryable || outcome == AttemptOutcome.TimedOut));
    }

    /// <summary>Puts a report in the outbox.</summary>
    private void Enqueue(Guid jobId, string jobType, JobCompletedRequest request)
    {
        metrics.OutboxDelta(1);
        _outbox.Writer.TryWrite(new PendingReport(jobId, jobType, request, time.GetUtcNow()));
    }

    // =========================================================================================== outbox

    /// <summary>
    /// Delivers completion reports. Unreachable servers → retry with back-off until the retention expires;
    /// 409/404/400 (stale or invalid) → drop; 403 → identity lost, drop (the job was re-distributed).
    /// </summary>
    /// <param name="ct">Token cancelled at the very end of shutdown.</param>
    private async Task OutboxLoopAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var report in _outbox.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                await DeliverAsync(report, ct).ConfigureAwait(false);
                metrics.OutboxDelta(-1);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Shutdown.
        }
    }

    /// <summary>Delivers one report with retries.</summary>
    private async Task DeliverAsync(PendingReport report, CancellationToken ct)
    {
        var delay = TimeSpan.FromMilliseconds(500);
        while (true)
        {
            try
            {
                await api.CompletedAsync(report.JobId, report.Request, ct).ConfigureAwait(false);
                return;
            }
            catch (WorkerApiException ex) when (!ex.IsTransient)
            {
                // Stale (job re-distributed / cancelled), invalid or identity lost: retrying cannot help.
                metrics.ReportDropped(((int)ex.StatusCode).ToString(System.Globalization.CultureInfo.InvariantCulture));
                logger.LogInformation("Report for job {JobId} dropped by server ({Status}): {Message}", report.JobId, (int)ex.StatusCode, ex.Message);
                return;
            }
            catch (WorkerApiException ex)
            {
                if (time.GetUtcNow() - report.CreatedUtc > options.Value.ReportRetention)
                {
                    metrics.ReportDropped("expired");
                    logger.LogWarning("Report for job {JobId} expired after {Retention}; the server will retry the job.", report.JobId, options.Value.ReportRetention);
                    return;
                }
                logger.LogDebug("Report for job {JobId} not delivered ({Message}); retrying in {Delay}.", report.JobId, ex.Message, delay);
            }
            await Task.Delay(Jitter(delay), time, ct).ConfigureAwait(false);
            delay = TimeSpan.FromSeconds(Math.Min(30, delay.TotalSeconds * 2));
        }
    }

    // =========================================================================================== shutdown

    /// <summary>Stops gracefully: wait for running jobs (grace period), cancel the rest, flush reports, unregister.</summary>
    /// <param name="outboxWorkers">Outbox loop tasks.</param>
    private async Task ShutdownAsync(Task[] outboxWorkers)
    {
        var o = options.Value;
        _draining = true;
        logger.LogInformation("Worker stopping: waiting up to {Grace} for {Count} running jobs.", o.ShutdownGracePeriod, _running.Count);

        // Step 1: let running jobs finish within the grace period.
        var deadline = time.GetUtcNow() + o.ShutdownGracePeriod;
        while (!_running.IsEmpty && time.GetUtcNow() < deadline)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(200), time).ConfigureAwait(false);
        }

        // Step 2: cancel what is left (no reports; unregister releases them without counting an attempt).
        _stopping = true;
        foreach (var job in _running.Values)
        {
            job.Cancel();
        }

        // Step 3: give the outbox a few seconds to deliver finished results.
        _outbox.Writer.TryComplete();
        await Task.WhenAny(Task.WhenAll(outboxWorkers), Task.Delay(TimeSpan.FromSeconds(5), time)).ConfigureAwait(false);
        await _jobsCts.CancelAsync().ConfigureAwait(false);

        // Step 4: unregister so held jobs are re-distributed immediately instead of after the heartbeat timeout.
        if (o.UnregisterOnShutdown && api.WorkerId != Guid.Empty)
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await api.UnregisterAsync(cts.Token).ConfigureAwait(false);
                logger.LogInformation("Worker {WorkerId} unregistered.", api.WorkerId);
            }
            catch (Exception ex) when (ex is WorkerApiException or OperationCanceledException)
            {
                logger.LogWarning("Unregister failed ({Message}); the server will detect the loss by heartbeat timeout.", ex.Message);
            }
        }
    }

    /// <inheritdoc />
    public override void Dispose()
    {
        base.Dispose();
        _jobsCts.Dispose();
        _registerLock.Dispose();
        _slotFreed.Dispose();
        _keyPair?.Dispose();
        _verifier?.Dispose();
        foreach (var h in _handlers.Values)
        {
            h.Slots.Dispose();
        }
    }

    // =========================================================================================== helpers

    /// <summary>Removes command / cancel memory older than one hour (bounded memory on long-running workers).</summary>
    private void PruneMemory()
    {
        var cutoff = time.GetUtcNow().AddHours(-1);
        foreach (var kv in _seenCommands)
        {
            if (kv.Value < cutoff)
            {
                _seenCommands.TryRemove(kv);
            }
        }
        foreach (var kv in _cancelledJobs)
        {
            if (kv.Value < cutoff)
            {
                _cancelledJobs.TryRemove(kv);
            }
        }
    }

    /// <summary>±20 % random jitter.</summary>
    private static TimeSpan Jitter(TimeSpan d) => d * (0.8 + Random.Shared.NextDouble() * 0.4);

    /// <summary>Same job type rule as the server (letters, digits, dot, dash, underscore; max 100).</summary>
    private static bool InputGuardJobType(string? value) =>
        !string.IsNullOrEmpty(value) && value.Length <= 100 && char.IsAsciiLetterOrDigit(value[0]) &&
        value.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-');

    /// <summary>Turns a machine name into a valid worker name.</summary>
    private static string SanitizeName(string name)
    {
        var chars = name.Select(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-' ? c : '-').ToArray();
        var s = new string(chars).TrimStart('-', '.', '_');
        return string.IsNullOrEmpty(s) ? "worker-" + Guid.NewGuid().ToString("N")[..8] : s[..Math.Min(s.Length, 200)];
    }

    /// <summary>
    /// Quick single-thread benchmark (~150 ms SHA-256 throughput) relative to a baseline of 500 MB/s.
    /// Only a starting hint: the server's ML model learns the real per-type speed from history.
    /// </summary>
    /// <returns>Relative score 0.01-1000.</returns>
    public static double MeasureBenchmark()
    {
        // Step 1: hash a 64 KB buffer repeatedly for a fixed time.
        var data = new byte[64 * 1024];
        Random.Shared.NextBytes(data);
        Span<byte> hash = stackalloc byte[32];
        long bytes = 0;
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < 150)
        {
            SHA256.HashData(data, hash);
            bytes += data.Length;
        }

        // Step 2: normalise to the baseline.
        var mbPerSecond = bytes / (1024.0 * 1024.0) / sw.Elapsed.TotalSeconds;
        return Math.Round(Math.Clamp(mbPerSecond / 500.0, 0.01, 1000), 3);
    }

    // =========================================================================================== types

    /// <summary>Handler metadata.</summary>
    private sealed record HandlerInfo(Type HandlerType, int MaxConcurrency, double SpeedFactor, SemaphoreSlim Slots);

    /// <summary>A queued completion report.</summary>
    private sealed record PendingReport(Guid JobId, string JobType, JobCompletedRequest Request, DateTimeOffset CreatedUtc);

    /// <summary>State of a job executing on this worker.</summary>
    private sealed class RunningJob : IDisposable
    {
        private readonly CancellationTokenSource _cts;

        public RunningJob(JobAssignmentResponse assignment, string? payload, bool wasEncrypted, CancellationToken shutdown, long generation)
        {
            Assignment = assignment;
            Payload = payload;
            WasEncrypted = wasEncrypted;
            Generation = generation;
            _cts = CancellationTokenSource.CreateLinkedTokenSource(shutdown);
            Token = _cts.Token;
        }

        public JobAssignmentResponse Assignment { get; }
        public Guid AttemptId => Assignment.AttemptId;
        public string? Payload { get; }
        public bool WasEncrypted { get; }
        public long Generation { get; }
        public CancellationToken Token { get; }
        public volatile bool CancelledByServer;
        public volatile bool Abandoned;

        public void Cancel()
        {
            try
            {
                _cts.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // Job already finished.
            }
        }

        public void CancelAfter(TimeSpan delay) => _cts.CancelAfter(delay);

        public void Dispose() => _cts.Dispose();
    }

    /// <summary>Process CPU usage sampler (percent of all cores since the previous sample).</summary>
    private sealed class CpuSampler
    {
        private TimeSpan _lastCpu = Process.GetCurrentProcess().TotalProcessorTime;
        private long _lastTicks = Stopwatch.GetTimestamp();

        public double Sample()
        {
            using var p = Process.GetCurrentProcess();
            var cpu = p.TotalProcessorTime;
            var now = Stopwatch.GetTimestamp();
            var wall = Stopwatch.GetElapsedTime(_lastTicks, now).TotalMilliseconds;
            var used = (cpu - _lastCpu).TotalMilliseconds;
            _lastCpu = cpu;
            _lastTicks = now;
            return wall <= 0 ? 0 : 100.0 * used / (wall * Environment.ProcessorCount);
        }
    }
}
