using System.Collections.Concurrent;
using JobDist.Contracts;
using JobDist.Core.DAO;
using JobDist.Core.Domain;
using JobDist.Core.Options;
using JobDist.Core.Security;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace JobDist.Core.Services;

/// <summary>
/// Worker membership: register (auto, on worker start-up), unregister (graceful shutdown), heartbeats, drain, and per-worker
/// token authentication. When a worker leaves or re-registers after a crash, the jobs it held are released for re-distribution.
/// </summary>
public sealed class WorkerRegistryService(
    IWorkerDAO workers,
    IJobDAO jobs,
    IWorkerCommandDAO commands,
    JobExecutionService execution,
    PqcSignatureService signer,
    WorkerSignalService signals,
    IOptions<JobDistOptions> options,
    TimeProvider time,
    ILogger<WorkerRegistryService> logger)
{
    private static readonly TimeSpan TokenCacheTtl = TimeSpan.FromSeconds(30);
    private readonly ConcurrentDictionary<Guid, (byte[] Hash, DateTimeOffset Expires)> _tokenCache = new();

    /// <summary>
    /// Registers a worker (or re-registers an existing name, keeping its id and statistics).
    /// </summary>
    /// <param name="request">Registration data.</param>
    /// <param name="registeredBy">Authenticated API client (fleet key name).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Worker id, a fresh secret token and the server's public signing keys.</returns>
    /// <exception cref="ValidationException">Invalid capabilities or keys.</exception>
    public async Task<RegisterWorkerResponse> RegisterAsync(RegisterWorkerRequest request, string registeredBy, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var o = options.Value;
        var now = time.GetUtcNow();

        // Step 1: validate capabilities (no duplicates, valid names) and public keys (must parse).
        Validate(request);

        // Step 2: re-use the id of a worker with the same name (its learned performance history stays valid).
        var existing = await workers.GetByNameAsync(request.Name, ct).ConfigureAwait(false);
        var workerId = existing?.WorkerId ?? Guid.CreateVersion7();

        // Step 3: a re-registration means the previous process is gone - release whatever it still held.
        if (existing is not null)
        {
            var held = await jobs.GetHeldByWorkerAsync(workerId, ct).ConfigureAwait(false);
            foreach (var job in held)
            {
                await execution.ReleaseAsync(job, AttemptOutcome.Released, "Worker re-registered", ct).ConfigureAwait(false);
            }
        }

        // Step 4: issue a new token (old token stops working) and store the worker.
        var (token, hash) = WorkerTokenService.Issue();
        var worker = new WorkerDTO
        {
            WorkerId = workerId,
            Name = request.Name,
            Status = WorkerStatus.Online,
            Capabilities = request.Capabilities
                .Select(c => new WorkerCapabilityDTO { JobType = c.JobType, MaxConcurrency = c.MaxConcurrency, SpeedFactor = c.SpeedFactor })
                .ToList(),
            Spec = new WorkerSpecDTO
            {
                CpuCores = request.Spec.CpuCores,
                MemoryMb = request.Spec.MemoryMb,
                GpuCount = request.Spec.GpuCount,
                BenchmarkScore = request.Spec.BenchmarkScore,
                Os = InputGuard.CleanText(request.Spec.Os, 200)
            },
            MaxTotalConcurrency = request.MaxTotalConcurrency,
            TokenHash = hash,
            EcdhPublicKey = request.EcdhPublicKey,
            MlKemPublicKey = request.MlKemPublicKey,
            Version = InputGuard.CleanText(request.Version, 50),
            Tags = request.Tags?.Select(t => InputGuard.CleanText(t, 50)!).ToList() ?? [],
            RegisteredUtc = existing?.RegisteredUtc ?? now,
            LastHeartbeatUtc = now,
            OfflineCount = existing?.OfflineCount ?? 0,
            RegisteredBy = registeredBy
        };
        await workers.UpsertAsync(worker, ct).ConfigureAwait(false);
        _tokenCache[workerId] = (hash, now + TokenCacheTtl);

        logger.LogInformation("Worker {Name} ({WorkerId}) registered with {Count} job types, PQC={Pqc}.",
            worker.Name, workerId, worker.Capabilities.Count, worker.MlKemPublicKey is not null);
        return new RegisterWorkerResponse(workerId, token, o.HeartbeatIntervalSeconds, o.OfflineAfterSeconds, signer.PublicKeys);
    }

    /// <summary>
    /// Unregisters a worker; jobs it still holds are released and re-distributed immediately.
    /// </summary>
    /// <param name="workerId">Worker.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task UnregisterAsync(Guid workerId, CancellationToken ct)
    {
        // Step 1: mark unregistered (stops new assignments) and invalidate the token cache.
        var worker = await workers.GetAsync(workerId, ct).ConfigureAwait(false) ?? throw new NotFoundException($"Worker {workerId} not found.");
        await workers.SetStatusAsync(workerId, WorkerStatus.Unregistered, false, null, ct).ConfigureAwait(false);
        _tokenCache.TryRemove(workerId, out _);

        // Step 2: release held jobs.
        var held = await jobs.GetHeldByWorkerAsync(workerId, ct).ConfigureAwait(false);
        foreach (var job in held)
        {
            await execution.ReleaseAsync(job, AttemptOutcome.Released, "Worker unregistered", ct).ConfigureAwait(false);
        }
        logger.LogInformation("Worker {Name} ({WorkerId}) unregistered; {Count} jobs released.", worker.Name, workerId, held.Count);
    }

    /// <summary>
    /// Puts a worker in Draining state (no new jobs; current jobs finish) and tells it through a signed command.
    /// </summary>
    public async Task DrainAsync(Guid workerId, CancellationToken ct)
    {
        _ = await workers.GetAsync(workerId, ct).ConfigureAwait(false) ?? throw new NotFoundException($"Worker {workerId} not found.");
        await workers.SetStatusAsync(workerId, WorkerStatus.Draining, false, null, ct).ConfigureAwait(false);
        await commands.InsertAsync(new WorkerCommandDTO
        {
            CommandId = Guid.CreateVersion7(), WorkerId = workerId, Kind = WorkerCommandKind.Drain, Reason = "Drain requested by administrator",
            IssuedUtc = time.GetUtcNow()
        }, ct).ConfigureAwait(false);
        signals.Notify(workerId);
    }

    /// <summary>
    /// Authenticates a worker call by its token (constant-time hash compare, short in-memory cache to avoid a DB read per call).
    /// </summary>
    /// <param name="workerId">Worker id from the route.</param>
    /// <param name="token">Value of <c>X-Worker-Token</c>.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <exception cref="ForbiddenException">Unknown worker, unregistered or wrong token.</exception>
    public async Task AuthenticateAsync(Guid workerId, string? token, CancellationToken ct)
    {
        var now = time.GetUtcNow();

        // Step 1: cached hash (valid for 30 s).
        if (_tokenCache.TryGetValue(workerId, out var cached) && cached.Expires > now)
        {
            if (WorkerTokenService.Verify(token, cached.Hash))
            {
                return;
            }
            throw new ForbiddenException("Invalid worker token.");
        }

        // Step 2: load from the database.
        var worker = await workers.GetAsync(workerId, ct).ConfigureAwait(false);
        if (worker is null || worker.Status == WorkerStatus.Unregistered || !WorkerTokenService.Verify(token, worker.TokenHash))
        {
            throw new ForbiddenException("Invalid worker token.");
        }
        _tokenCache[workerId] = (worker.TokenHash, now + TokenCacheTtl);
    }

    /// <summary>
    /// Records a heartbeat and returns pending signed commands (cancel / drain).
    /// </summary>
    /// <param name="workerId">Authenticated worker.</param>
    /// <param name="request">Load figures.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Current status and commands.</returns>
    /// <exception cref="NotFoundException">Worker unknown / unregistered → the SDK re-registers.</exception>
    public async Task<HeartbeatResponse> HeartbeatAsync(Guid workerId, HeartbeatRequest request, CancellationToken ct)
    {
        var now = time.GetUtcNow();

        // Step 1: record (also brings an Offline worker back Online - it reconnected).
        var status = await workers.RecordHeartbeatAsync(workerId, now, Math.Clamp(request.CpuUsagePercent, 0, 100),
            Math.Clamp(request.MemoryUsagePercent, 0, 100), ct).ConfigureAwait(false)
            ?? throw new NotFoundException("Worker is not registered.");

        // Step 2: pending commands, signed.
        var cmds = await TakeCommandsAsync(workerId, ct).ConfigureAwait(false);
        return new HeartbeatResponse(status, cmds);
    }

    /// <summary>Takes pending commands for a worker and signs them (shared by heartbeat and poll).</summary>
    internal async Task<IReadOnlyList<WorkerCommandResponse>> TakeCommandsAsync(Guid workerId, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var pending = await commands.TakePendingAsync(workerId, now, now.AddSeconds(-options.Value.CommandRedeliverSeconds), ct).ConfigureAwait(false);
        return pending.Select(c => signer.Sign(new WorkerCommandResponse(c.CommandId, c.Kind, c.WorkerId, c.JobId, c.Reason, c.IssuedUtc, PqcSignatureService.Unsigned))).ToList();
    }

    /// <summary>Lists workers with their active job counts.</summary>
    public async Task<IReadOnlyList<WorkerResponse>> ListAsync(bool includeUnregistered, CancellationToken ct)
    {
        var list = await workers.ListAsync(includeUnregistered, ct).ConfigureAwait(false);
        var load = (await jobs.GetActiveLoadAsync(ct).ConfigureAwait(false)).GroupBy(l => l.WorkerId).ToDictionary(g => g.Key, g => g.Sum(x => x.Active));
        return list.Select(w => w.ToResponse(load.GetValueOrDefault(w.WorkerId))).ToList();
    }

    /// <summary>Gets one worker.</summary>
    public async Task<WorkerResponse> GetAsync(Guid workerId, CancellationToken ct)
    {
        var w = await workers.GetAsync(workerId, ct).ConfigureAwait(false) ?? throw new NotFoundException($"Worker {workerId} not found.");
        var held = await jobs.GetHeldByWorkerAsync(workerId, ct).ConfigureAwait(false);
        return w.ToResponse(held.Count);
    }

    /// <summary>Business validation of a registration.</summary>
    private static void Validate(RegisterWorkerRequest r)
    {
        if (string.IsNullOrWhiteSpace(r.Name) || r.Name.Length > 200)
        {
            throw new ValidationException("Name is required (max 200 characters).");
        }
        if (r.Capabilities is null || r.Capabilities.Count is 0 or > 200)
        {
            throw new ValidationException("A worker must register 1-200 job types.");
        }
        if (r.Capabilities.Any(c => !InputGuard.IsValidJobType(c.JobType) || c.MaxConcurrency is < 1 or > 1024 || c.SpeedFactor is <= 0 or > 100))
        {
            throw new ValidationException("Invalid capability (job type format, MaxConcurrency 1-1024, SpeedFactor 0-100).");
        }
        if (r.Capabilities.GroupBy(c => c.JobType, StringComparer.OrdinalIgnoreCase).Any(g => g.Count() > 1))
        {
            throw new ValidationException("Duplicate job type in capabilities.");
        }
        if (r.Spec is null || r.Spec.CpuCores < 1 || r.Spec.MemoryMb < 64 || r.Spec.BenchmarkScore <= 0)
        {
            throw new ValidationException("Spec is required (CpuCores >= 1, MemoryMb >= 64, BenchmarkScore > 0).");
        }
        if (r.MaxTotalConcurrency is < 1 or > 4096)
        {
            throw new ValidationException("MaxTotalConcurrency must be 1-4096.");
        }

        // Keys must be well-formed so a broken worker is rejected at registration, not at the first sensitive job.
        try
        {
            if (r.EcdhPublicKey is not null)
            {
                using var ecdh = System.Security.Cryptography.ECDiffieHellman.Create();
                ecdh.ImportSubjectPublicKeyInfo(Convert.FromBase64String(r.EcdhPublicKey), out _);
            }
            if (r.MlKemPublicKey is not null && System.Security.Cryptography.MLKem.IsSupported)
            {
                using var kem = System.Security.Cryptography.MLKem.ImportEncapsulationKey(System.Security.Cryptography.MLKemAlgorithm.MLKem768, Convert.FromBase64String(r.MlKemPublicKey));
            }
        }
        catch (Exception ex) when (ex is FormatException or System.Security.Cryptography.CryptographicException)
        {
            throw new ValidationException("Invalid public key.");
        }
    }
}
