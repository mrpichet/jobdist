using System.Security.Cryptography;
using System.Text;
using JobDist.Core.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace JobDist.Core.Security;

/// <summary>
/// Encrypts sensitive job payloads at rest with AES-256-GCM. The job id is the associated data, so a ciphertext copied
/// to another row fails authentication. Format: Base64( nonce[12] | tag[16] | ciphertext ).
/// </summary>
public sealed class PayloadProtectionService
{
    private readonly byte[] _key;

    /// <summary>Loads the key from <see cref="JobDistOptions.PayloadProtectionKey"/> (or generates an ephemeral one).</summary>
    public PayloadProtectionService(IOptions<JobDistOptions> options, ILogger<PayloadProtectionService> logger)
    {
        var configured = options.Value.PayloadProtectionKey;
        if (string.IsNullOrWhiteSpace(configured))
        {
            logger.LogWarning("JobDist:PayloadProtectionKey not configured - sensitive payloads use an ephemeral key (development only).");
            _key = RandomNumberGenerator.GetBytes(32);
        }
        else
        {
            _key = Convert.FromBase64String(configured);
            if (_key.Length != 32)
            {
                throw new InvalidOperationException("JobDist:PayloadProtectionKey must be 32 bytes (Base64).");
            }
        }
    }

    /// <summary>Encrypts a payload.</summary>
    /// <param name="jobId">Job id (associated data).</param>
    /// <param name="plaintext">Payload.</param>
    /// <returns>Base64 protected blob.</returns>
    public string Protect(Guid jobId, string plaintext)
    {
        // Step 1: random 96-bit nonce per message.
        var data = Encoding.UTF8.GetBytes(plaintext);
        var output = new byte[12 + 16 + data.Length];
        var nonce = output.AsSpan(0, 12);
        RandomNumberGenerator.Fill(nonce);

        // Step 2: encrypt with the job id as AAD.
        using var aes = new AesGcm(_key, 16);
        aes.Encrypt(nonce, data, output.AsSpan(28), output.AsSpan(12, 16), jobId.ToByteArray());
        CryptographicOperations.ZeroMemory(data);
        return Convert.ToBase64String(output);
    }

    /// <summary>Decrypts a payload.</summary>
    /// <param name="jobId">Job id used at protection time.</param>
    /// <param name="protectedPayload">Base64 blob.</param>
    /// <returns>Plaintext payload.</returns>
    /// <exception cref="CryptographicException">Tampered data or wrong key.</exception>
    public string Unprotect(Guid jobId, string protectedPayload)
    {
        var blob = Convert.FromBase64String(protectedPayload);
        if (blob.Length < 28)
        {
            throw new CryptographicException("Protected payload is too short.");
        }
        var plaintext = new byte[blob.Length - 28];
        using var aes = new AesGcm(_key, 16);
        aes.Decrypt(blob.AsSpan(0, 12), blob.AsSpan(28), blob.AsSpan(12, 16), plaintext, jobId.ToByteArray());
        try
        {
            return Encoding.UTF8.GetString(plaintext);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }
}
