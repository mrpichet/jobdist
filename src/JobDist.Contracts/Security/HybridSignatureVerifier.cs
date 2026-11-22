using System.Security.Cryptography;

namespace JobDist.Contracts.Security;

/// <summary>
/// Verifies hybrid signatures (ML-DSA-65 FIPS 204 + ECDSA P-384) with public keys only. Used by workers to make sure
/// assignments and cancel commands really come from the server (protects against a spoofed / MITM server).
/// Thread-safe after construction.
/// </summary>
public sealed class HybridSignatureVerifier : IDisposable
{
    /// <summary>Context string used by ML-DSA (domain separation from other protocols using the same key).</summary>
    public static readonly byte[] MlDsaContext = "JobDist/v1"u8.ToArray();

    private readonly ECDsa _ecdsa;
    private readonly MLDsa? _mlDsa;

    /// <summary>Creates a verifier from published server keys.</summary>
    /// <param name="keys">Server public keys (from registration or <c>GET /api/keys</c>).</param>
    /// <exception cref="CryptographicException">A key is malformed.</exception>
    public HybridSignatureVerifier(ServerKeysResponse keys)
    {
        ArgumentNullException.ThrowIfNull(keys);
        KeyId = keys.KeyId;

        // Step 1: classical key is mandatory.
        _ecdsa = ECDsa.Create();
        _ecdsa.ImportSubjectPublicKeyInfo(Convert.FromBase64String(keys.EcdsaPublicKey), out _);

        // Step 2: PQC key when the server published one and this OS can verify ML-DSA.
        if (keys.MlDsaPublicKey is not null && MLDsa.IsSupported)
        {
            _mlDsa = MLDsa.ImportMLDsaPublicKey(MLDsaAlgorithm.MLDsa65, Convert.FromBase64String(keys.MlDsaPublicKey));
        }
        ServerHasPqc = keys.MlDsaPublicKey is not null;
    }

    /// <summary>Key id these keys belong to.</summary>
    public string KeyId { get; }

    /// <summary><c>true</c> when the server advertised an ML-DSA key (hybrid signatures expected).</summary>
    public bool ServerHasPqc { get; }

    /// <summary>
    /// Verifies a detached signature over a digest.
    /// </summary>
    /// <param name="digest">Digest from <see cref="CanonicalDigest"/>.</param>
    /// <param name="signature">Signature received.</param>
    /// <returns><c>true</c> only when the key id matches and every required component verifies (no downgrade).</returns>
    public bool Verify(ReadOnlySpan<byte> digest, SignatureEnvelope signature)
    {
        ArgumentNullException.ThrowIfNull(signature);
        try
        {
            // Step 1: must be signed by the key we trust.
            if (!string.Equals(signature.KeyId, KeyId, StringComparison.Ordinal))
            {
                return false;
            }

            // Step 2: classical component always required.
            if (!_ecdsa.VerifyData(digest, Convert.FromBase64String(signature.ClassicSignature), HashAlgorithmName.SHA384))
            {
                return false;
            }

            // Step 3: a server with an ML-DSA key must always send the PQC component (downgrade protection).
            if (ServerHasPqc && signature.PqcSignature is null)
            {
                return false;
            }

            // Step 4: verify PQC part when present and verifiable on this OS.
            if (signature.PqcSignature is not null && _mlDsa is not null)
            {
                return _mlDsa.VerifyData(digest, Convert.FromBase64String(signature.PqcSignature), MlDsaContext);
            }
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
        catch (CryptographicException)
        {
            return false;
        }
    }

    /// <summary>Releases native key handles.</summary>
    public void Dispose()
    {
        _ecdsa.Dispose();
        _mlDsa?.Dispose();
    }
}
