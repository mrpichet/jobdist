using System.Security.Cryptography;
using JobDist.Contracts;
using JobDist.Contracts.Security;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace JobDist.Core.Security;

/// <summary>Signing key material (section <c>Crypto</c>). Supply through environment variables / a secret store.</summary>
public sealed class CryptoOptions
{
    public const string SectionName = "Crypto";

    /// <summary>Key identifier published with every signature.</summary>
    public string KeyId { get; set; } = "jobdist-dev";

    /// <summary>Base64 ML-DSA-65 private seed (32 bytes). Empty = ephemeral key (dev only; all HA instances must share it).</summary>
    public string? MlDsaPrivateSeed { get; set; }

    /// <summary>Base64 PKCS#8 ECDSA P-384 private key. Empty = ephemeral key (dev only).</summary>
    public string? EcdsaPrivateKeyPkcs8 { get; set; }
}

/// <summary>
/// Signs assignments and worker commands with a <b>hybrid</b> post-quantum signature: ML-DSA-65 (NIST FIPS 204, built into
/// .NET 10) + ECDSA P-384. Workers accept a message only if both verify, so security holds while either algorithm is unbroken.
/// When the OS crypto library has no ML-DSA (OpenSSL &lt; 3.5) it falls back to ECDSA only and labels the algorithm.
/// Cost: one ML-DSA-65 + one ECDSA signature per assignment (well under a millisecond), only on the dispatch path.
/// </summary>
public sealed class PqcSignatureService : IDisposable
{
    private readonly MLDsa? _mlDsa;
    private readonly ECDsa _ecdsa;
    private readonly string _keyId;
    private readonly ServerKeysResponse _publicKeys;

    /// <summary>Loads or generates keys.</summary>
    /// <param name="options">Key material.</param>
    /// <param name="logger">Logger (never logs key material).</param>
    public PqcSignatureService(IOptions<CryptoOptions> options, ILogger<PqcSignatureService> logger)
    {
        var o = options.Value;
        _keyId = o.KeyId;

        // Step 1: classical half (always available).
        _ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP384);
        if (!string.IsNullOrWhiteSpace(o.EcdsaPrivateKeyPkcs8))
        {
            _ecdsa.ImportPkcs8PrivateKey(Convert.FromBase64String(o.EcdsaPrivateKeyPkcs8), out _);
        }
        else
        {
            logger.LogWarning("Crypto:EcdsaPrivateKeyPkcs8 not configured - using an ephemeral ECDSA key (development only).");
        }

        // Step 2: PQC half when the platform supports ML-DSA.
        if (MLDsa.IsSupported)
        {
            if (string.IsNullOrWhiteSpace(o.MlDsaPrivateSeed))
            {
                logger.LogWarning("Crypto:MlDsaPrivateSeed not configured - using an ephemeral ML-DSA key (development only).");
                _mlDsa = MLDsa.GenerateKey(MLDsaAlgorithm.MLDsa65);
            }
            else
            {
                _mlDsa = MLDsa.ImportMLDsaPrivateSeed(MLDsaAlgorithm.MLDsa65, Convert.FromBase64String(o.MlDsaPrivateSeed));
            }
        }
        else
        {
            logger.LogWarning("ML-DSA is not supported by this OS crypto provider; signatures fall back to ECDSA-P384 only.");
        }

        // Step 3: cache the public keys (published at /api/keys and in the registration response).
        _publicKeys = new ServerKeysResponse(
            _keyId,
            _mlDsa is null ? null : Convert.ToBase64String(_mlDsa.ExportMLDsaPublicKey()),
            Convert.ToBase64String(_ecdsa.ExportSubjectPublicKeyInfo()));
    }

    /// <summary><c>true</c> when ML-DSA is active.</summary>
    public bool IsPqcAvailable => _mlDsa is not null;

    /// <summary>Algorithm label written into signatures.</summary>
    public string Algorithm => IsPqcAvailable ? "ML-DSA-65+ECDSA-P384" : "ECDSA-P384";

    /// <summary>Public keys for workers.</summary>
    public ServerKeysResponse PublicKeys => _publicKeys;

    /// <summary>
    /// Signs a digest.
    /// </summary>
    /// <param name="digest">Digest from <see cref="CanonicalDigest"/>.</param>
    /// <returns>Detached hybrid signature.</returns>
    public SignatureEnvelope Sign(ReadOnlySpan<byte> digest)
    {
        // Step 1: classical ECDSA (IEEE P1363 fixed-size format).
        var data = digest.ToArray();
        var classic = _ecdsa.SignData(data, HashAlgorithmName.SHA384);

        // Step 2: ML-DSA with the protocol context string (domain separation).
        var pqc = _mlDsa?.SignData(data, HybridSignatureVerifier.MlDsaContext);

        // Step 3: package.
        return new SignatureEnvelope(Algorithm, _keyId, pqc is null ? null : Convert.ToBase64String(pqc), Convert.ToBase64String(classic));
    }

    /// <summary>Signs an assignment and returns a copy carrying the signature.</summary>
    /// <param name="unsigned">Assignment with a placeholder signature.</param>
    /// <returns>Signed assignment.</returns>
    public JobAssignmentResponse Sign(JobAssignmentResponse unsigned) =>
        unsigned with { Signature = Sign(CanonicalDigest.ForAssignment(unsigned)) };

    /// <summary>Signs a command and returns a copy carrying the signature.</summary>
    public WorkerCommandResponse Sign(WorkerCommandResponse unsigned) =>
        unsigned with { Signature = Sign(CanonicalDigest.ForCommand(unsigned)) };

    /// <summary>Placeholder used before signing.</summary>
    public static readonly SignatureEnvelope Unsigned = new("none", string.Empty, null, string.Empty);

    /// <summary>Releases native handles.</summary>
    public void Dispose()
    {
        _mlDsa?.Dispose();
        _ecdsa.Dispose();
    }
}
