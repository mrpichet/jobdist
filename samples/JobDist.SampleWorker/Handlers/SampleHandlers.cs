using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using JobDist.Worker.Sdk;

namespace JobDist.SampleWorker.Handlers;

/// <summary>JSON options shared by the sample handlers (case-insensitive, web defaults).</summary>
internal static class SampleJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    /// <summary>Deserializes the payload or throws a non-retryable error (bad input never succeeds on retry).</summary>
    /// <typeparam name="T">Payload type.</typeparam>
    /// <param name="payload">JSON payload.</param>
    /// <returns>Parsed payload.</returns>
    /// <exception cref="NonRetryableJobException">Missing or invalid JSON.</exception>
    public static T Parse<T>(string? payload)
    {
        try
        {
            return JsonSerializer.Deserialize<T>(payload ?? "null", Options) ?? throw new NonRetryableJobException("Payload is required.");
        }
        catch (JsonException ex)
        {
            throw new NonRetryableJobException($"Invalid payload: {ex.Message}");
        }
    }
}

/// <summary>
/// <c>hash.sha256</c>: hashes the text in the payload <c>{"text":"..."}</c>. Payload is often marked <c>sensitive</c>,
/// so it arrives encrypted with ML-KEM-768 + ECDH and is decrypted by the SDK before this handler runs.
/// </summary>
public sealed class HashSha256Handler : IJobHandler
{
    /// <inheritdoc />
    public string JobType => "hash.sha256";

    /// <inheritdoc />
    public int MaxConcurrency => 16;

    /// <summary>Hashes the text.</summary>
    /// <param name="context">Job context; payload <c>{"text":"..."}</c>.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns><c>{"sha256":"HEX","encrypted":bool}</c>.</returns>
    public Task<JobResult> ExecuteAsync(JobContext context, CancellationToken ct)
    {
        // Step 1: parse input.
        var input = SampleJson.Parse<TextPayload>(context.Payload);

        // Step 2: compute and return.
        var hex = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(input.Text ?? string.Empty)));
        return Task.FromResult(JobResult.Ok(JsonSerializer.Serialize(new { sha256 = hex, encrypted = context.WasEncrypted }, SampleJson.Options)));
    }

    private sealed record TextPayload(string? Text);
}

/// <summary>
/// <c>math.primes</c>: counts primes up to N (payload <c>{"limit":5000000}</c>) using a parallel segmented check —
/// shows CPU-bound work using all cores with <see cref="Parallel"/> and honouring cancellation.
/// </summary>
public sealed class PrimeCountHandler : IJobHandler
{
    /// <inheritdoc />
    public string JobType => "math.primes";

    /// <inheritdoc />
    public int MaxConcurrency => 2;

    /// <inheritdoc />
    public double SpeedFactor => 1.0;

    /// <summary>Counts primes ≤ limit.</summary>
    /// <param name="context">Payload <c>{"limit":N}</c>, 2 ≤ N ≤ 200,000,000.</param>
    /// <param name="ct">Cancellation token (timeout / server cancel).</param>
    /// <returns><c>{"limit":N,"primes":count}</c>.</returns>
    public Task<JobResult> ExecuteAsync(JobContext context, CancellationToken ct)
    {
        // Step 1: validate (non-retryable on bad input).
        var input = SampleJson.Parse<PrimePayload>(context.Payload);
        if (input.Limit is < 2 or > 200_000_000)
        {
            throw new NonRetryableJobException("limit must be between 2 and 200,000,000.");
        }

        // Step 2: split the range into blocks; each block is checked in parallel with its own local counter.
        const int block = 65_536;
        var blocks = (int)((input.Limit + block - 1) / block);
        long total = 0;
        Parallel.For(0, blocks, new ParallelOptions { CancellationToken = ct }, () => 0L, (b, _, local) =>
        {
            var from = Math.Max(2L, (long)b * block);
            var to = Math.Min(input.Limit, (long)(b + 1) * block - 1);
            for (var n = from; n <= to; n++)
            {
                if (IsPrime(n))
                {
                    local++;
                }
            }
            return local;
        }, local => Interlocked.Add(ref total, local));

        // Step 3: result.
        return Task.FromResult(JobResult.Ok(JsonSerializer.Serialize(new { limit = input.Limit, primes = total }, SampleJson.Options)));
    }

    /// <summary>Deterministic trial division with the 6k±1 wheel.</summary>
    private static bool IsPrime(long n)
    {
        if (n < 4)
        {
            return n >= 2;
        }
        if (n % 2 == 0 || n % 3 == 0)
        {
            return false;
        }
        for (long i = 5; i * i <= n; i += 6)
        {
            if (n % i == 0 || n % (i + 2) == 0)
            {
                return false;
            }
        }
        return true;
    }

    private sealed record PrimePayload(long Limit);
}

/// <summary>
/// <c>image.resize</c>: simulated image resize (payload <c>{"width":..,"height":..,"targetWidth":..}</c>). Uses SIMD
/// (<see cref="Vector{T}"/>) to scale a synthetic grey-scale buffer, then waits proportionally to the pixel count.
/// </summary>
public sealed class ImageResizeHandler : IJobHandler
{
    /// <inheritdoc />
    public string JobType => "image.resize";

    /// <inheritdoc />
    public int MaxConcurrency => 4;

    /// <summary>Resizes a synthetic image.</summary>
    /// <param name="context">Payload with width, height and targetWidth (1-10,000).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns><c>{"width":..,"height":..,"checksum":..}</c>.</returns>
    public async Task<JobResult> ExecuteAsync(JobContext context, CancellationToken ct)
    {
        // Step 1: validate.
        var p = SampleJson.Parse<ResizePayload>(context.Payload);
        if (p.Width is < 1 or > 10_000 || p.Height is < 1 or > 10_000 || p.TargetWidth is < 1 or > 10_000)
        {
            return JobResult.Fail("width/height/targetWidth must be 1-10,000.", retryable: false);
        }

        // Step 2: brighten a row buffer with SIMD (stands in for real pixel work) and compute a checksum.
        var row = new float[p.Width];
        for (var i = 0; i < row.Length; i++)
        {
            row[i] = i % 256;
        }
        var factor = new Vector<float>((float)p.TargetWidth / p.Width);
        var k = 0;
        for (; k <= row.Length - Vector<float>.Count; k += Vector<float>.Count)
        {
            (new Vector<float>(row, k) * factor).CopyTo(row, k);
        }
        for (; k < row.Length; k++)
        {
            row[k] *= (float)p.TargetWidth / p.Width;
        }

        // Step 3: simulate I/O time (1 ms per 100k pixels, at most 10 s), honouring cancellation.
        var ms = Math.Min(10_000, (long)p.Width * p.Height / 100_000);
        await Task.Delay(TimeSpan.FromMilliseconds(ms), ct).ConfigureAwait(false);
        var targetHeight = (int)Math.Round((double)p.Height * p.TargetWidth / p.Width);
        return JobResult.Ok(JsonSerializer.Serialize(new { width = p.TargetWidth, height = targetHeight, checksum = row.Sum() }, SampleJson.Options));
    }

    private sealed record ResizePayload(int Width, int Height, int TargetWidth);
}

/// <summary>
/// <c>report.generate</c>: simulated long report with a configurable random failure rate
/// (payload <c>{"seconds":5,"failureRate":0.2}</c>) to demonstrate retries, back-off and anomaly detection.
/// </summary>
public sealed class ReportGenerateHandler(ILogger<ReportGenerateHandler> logger) : IJobHandler
{
    /// <inheritdoc />
    public string JobType => "report.generate";

    /// <inheritdoc />
    public int MaxConcurrency => 4;

    /// <summary>Simulates report generation.</summary>
    /// <param name="context">Payload with seconds (0-600) and failureRate (0-1).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Report summary, or a retryable failure.</returns>
    public async Task<JobResult> ExecuteAsync(JobContext context, CancellationToken ct)
    {
        // Step 1: validate.
        var p = SampleJson.Parse<ReportPayload>(context.Payload);
        var seconds = Math.Clamp(p.Seconds, 0, 600);

        // Step 2: work in 100 ms slices so cancellation is quick.
        for (var elapsed = 0.0; elapsed < seconds; elapsed += 0.1)
        {
            await Task.Delay(100, ct).ConfigureAwait(false);
        }

        // Step 3: random transient failure (e.g. downstream database busy) → retryable.
        if (Random.Shared.NextDouble() < Math.Clamp(p.FailureRate, 0, 1))
        {
            logger.LogInformation("Simulated transient failure for job {JobId} (attempt {Attempt}).", context.JobId, context.Attempt);
            return JobResult.Fail("Simulated transient failure: downstream service unavailable.", retryable: true);
        }
        return JobResult.Ok(JsonSerializer.Serialize(new { pages = (int)(seconds * 3) + 1, attempt = context.Attempt }, SampleJson.Options));
    }

    private sealed record ReportPayload(double Seconds = 1, double FailureRate = 0);
}
