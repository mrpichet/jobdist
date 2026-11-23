using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace JobDist.Contracts.Security;

/// <summary>
/// Builds unambiguous, length-prefixed byte forms of protocol messages and hashes them (SHA-256).
/// The server signs these digests and the worker SDK recomputes them to verify, so both sides share this code.
/// </summary>
public static class CanonicalDigest
{
    /// <summary>
    /// Digest of a job assignment. Binds job, attempt, worker, type, timing and payload hash, so a captured assignment
    /// cannot be replayed to another worker or with a different payload.
    /// </summary>
    /// <param name="a">Assignment as sent/received.</param>
    /// <returns>32-byte SHA-256 digest.</returns>
    public static byte[] ForAssignment(JobAssignmentResponse a)
    {
        ArgumentNullException.ThrowIfNull(a);

        // Step 1: domain separation tag first (an assignment digest can never equal a command digest).
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Append(hash, "jobdist/v1/assignment");

        // Step 2: every security relevant field, in a fixed order, with invariant formatting.
        Append(hash, a.JobId.ToString("N"));
        Append(hash, a.AttemptId.ToString("N"));
        Append(hash, a.WorkerId.ToString("N"));
        Append(hash, a.JobType);
        Append(hash, a.Attempt.ToString(CultureInfo.InvariantCulture));
        Append(hash, a.TimeoutSeconds.ToString(CultureInfo.InvariantCulture));
        Append(hash, a.IssuedUtc.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture));

        // Step 3: payload is bound through its SHA-256 (plaintext hash, also for encrypted payloads).
        Append(hash, a.PayloadSha256);
        return hash.GetHashAndReset();
    }

    /// <summary>Digest of a worker command (cancel / drain).</summary>
    /// <param name="c">Command.</param>
    /// <returns>32-byte SHA-256 digest.</returns>
    public static byte[] ForCommand(WorkerCommandResponse c)
    {
        ArgumentNullException.ThrowIfNull(c);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Append(hash, "jobdist/v1/command");
        Append(hash, c.CommandId.ToString("N"));
        Append(hash, ((int)c.Kind).ToString(CultureInfo.InvariantCulture));
        Append(hash, c.WorkerId.ToString("N"));
        Append(hash, c.JobId?.ToString("N") ?? string.Empty);
        Append(hash, c.Reason ?? string.Empty);
        Append(hash, c.IssuedUtc.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture));
        return hash.GetHashAndReset();
    }

    /// <summary>Hex SHA-256 of a payload string (UTF-8). Empty string for null.</summary>
    /// <param name="payload">Payload.</param>
    /// <returns>Upper-case hex digest.</returns>
    public static string PayloadHash(string? payload) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload ?? string.Empty)));

    /// <summary>Appends a 4-byte big-endian length then the UTF-8 bytes (prevents field-boundary ambiguity).</summary>
    private static void Append(IncrementalHash hash, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(length, bytes.Length);
        hash.AppendData(length);
        hash.AppendData(bytes);
    }
}
