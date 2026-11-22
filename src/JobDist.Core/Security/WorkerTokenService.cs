using System.Security.Cryptography;
using System.Text;

namespace JobDist.Core.Security;

/// <summary>
/// Issues per-worker secrets. A worker proves its identity on every call with <c>X-Worker-Token</c>; the server stores only
/// SHA-256(token) and compares in constant time. A stolen fleet API key therefore cannot impersonate an existing worker.
/// </summary>
public static class WorkerTokenService
{
    /// <summary>Creates a new 256-bit random token.</summary>
    /// <returns>Token (Base64Url) and its SHA-256 hash.</returns>
    public static (string Token, byte[] Hash) Issue()
    {
        // Step 1: 32 bytes from the OS CSPRNG; Step 2: URL-safe text; Step 3: hash for storage.
        var raw = RandomNumberGenerator.GetBytes(32);
        var token = Convert.ToBase64String(raw).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        return (token, Hash(token));
    }

    /// <summary>SHA-256 of a token.</summary>
    public static byte[] Hash(string token) => SHA256.HashData(Encoding.UTF8.GetBytes(token));

    /// <summary>Constant-time verification.</summary>
    /// <param name="presented">Token from the request (may be null).</param>
    /// <param name="storedHash">Stored hash.</param>
    /// <returns><c>true</c> when it matches.</returns>
    public static bool Verify(string? presented, byte[] storedHash)
    {
        if (string.IsNullOrEmpty(presented) || presented.Length > 128 || storedHash.Length != 32)
        {
            return false;
        }
        return CryptographicOperations.FixedTimeEquals(Hash(presented), storedHash);
    }
}
