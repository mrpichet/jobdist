using System.Security.Cryptography;

namespace JobDist.Contracts.Security;

/// <summary>
/// Private key pair of a payload recipient (a worker). Hybrid: ECDH P-384 + ML-KEM-768 (FIPS 203) when the OS supports it.
/// Keys live only in the worker process memory; a new pair is generated at every start (forward secrecy across restarts).
/// </summary>
public sealed class HybridKeyPair : IDisposable
{
    internal ECDiffieHellman Ecdh { get; }
    internal MLKem? MlKem { get; }

    private HybridKeyPair(ECDiffieHellman ecdh, MLKem? mlKem)
    {
        Ecdh = ecdh;
        MlKem = mlKem;
    }

    /// <summary>Generates a new key pair.</summary>
    /// <returns>Key pair (ML-KEM included when <see cref="MLKem.IsSupported"/>).</returns>
    public static HybridKeyPair Generate() =>
        new(ECDiffieHellman.Create(ECCurve.NamedCurves.nistP384),
            MLKem.IsSupported ? MLKem.GenerateKey(MLKemAlgorithm.MLKem768) : null);

    /// <summary>Base64 ECDH public key (SubjectPublicKeyInfo).</summary>
    public string EcdhPublicKeyBase64 => Convert.ToBase64String(Ecdh.ExportSubjectPublicKeyInfo());

    /// <summary>Base64 ML-KEM encapsulation key, or null without PQC.</summary>
    public string? MlKemPublicKeyBase64 => MlKem is null ? null : Convert.ToBase64String(MlKem.ExportEncapsulationKey());

    /// <inheritdoc />
    public void Dispose()
    {
        Ecdh.Dispose();
        MlKem?.Dispose();
    }
}

/// <summary>
/// Hybrid post-quantum envelope encryption. Shared secret = HKDF-SHA384(ECDH secret || ML-KEM secret), cipher AES-256-GCM.
/// Secure while either ECDH or ML-KEM stays unbroken ("harvest now, decrypt later" protection for sensitive payloads).
/// Pure static code: no I/O, thread-safe.
/// </summary>
public static class PqcEnvelope
{
    private static readonly byte[] Info = "JobDist/v1/envelope"u8.ToArray();

    /// <summary>
    /// Encrypts a payload for a recipient.
    /// </summary>
    /// <param name="ecdhPublicKeyBase64">Recipient ECDH P-384 public key (SubjectPublicKeyInfo, Base64).</param>
    /// <param name="mlKemPublicKeyBase64">Recipient ML-KEM-768 encapsulation key (Base64) or null.</param>
    /// <param name="plaintext">Data to protect.</param>
    /// <param name="associatedData">Authenticated context (e.g. attempt id) that must match on decrypt.</param>
    /// <returns>Envelope safe to transmit.</returns>
    public static EncryptedPayload Seal(string ecdhPublicKeyBase64, string? mlKemPublicKeyBase64, ReadOnlySpan<byte> plaintext, ReadOnlySpan<byte> associatedData)
    {
        // Step 1: ECDH with a fresh ephemeral key.
        using var ephemeral = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP384);
        using var peer = ECDiffieHellman.Create();
        peer.ImportSubjectPublicKeyInfo(Convert.FromBase64String(ecdhPublicKeyBase64), out _);
        var ecdhSecret = ephemeral.DeriveRawSecretAgreement(peer.PublicKey);

        // Step 2: ML-KEM encapsulation when the recipient advertised a key and this OS supports it.
        byte[]? kemCiphertext = null;
        byte[] kemSecret = [];
        if (mlKemPublicKeyBase64 is not null && MLKem.IsSupported)
        {
            using var kem = MLKem.ImportEncapsulationKey(MLKemAlgorithm.MLKem768, Convert.FromBase64String(mlKemPublicKeyBase64));
            kem.Encapsulate(out kemCiphertext, out kemSecret);
        }

        // Step 3: derive the AES key and encrypt.
        var key = DeriveKey(ecdhSecret, kemSecret);
        try
        {
            var nonce = RandomNumberGenerator.GetBytes(AesGcm.NonceByteSizes.MaxSize);
            var tag = new byte[AesGcm.TagByteSizes.MaxSize];
            var ciphertext = new byte[plaintext.Length];
            using (var aes = new AesGcm(key, tag.Length))
            {
                aes.Encrypt(nonce, plaintext, ciphertext, tag, associatedData);
            }
            return new EncryptedPayload(
                kemCiphertext is null ? "ECDH-P384+AES256GCM" : "ML-KEM-768+ECDH-P384+AES256GCM",
                Convert.ToBase64String(ephemeral.ExportSubjectPublicKeyInfo()),
                kemCiphertext is null ? null : Convert.ToBase64String(kemCiphertext),
                Convert.ToBase64String(nonce),
                Convert.ToBase64String(tag),
                Convert.ToBase64String(ciphertext));
        }
        finally
        {
            // Step 4: wipe secrets.
            CryptographicOperations.ZeroMemory(key);
            CryptographicOperations.ZeroMemory(ecdhSecret);
            CryptographicOperations.ZeroMemory(kemSecret);
        }
    }

    /// <summary>
    /// Decrypts an envelope.
    /// </summary>
    /// <param name="keyPair">Recipient private keys.</param>
    /// <param name="envelope">Envelope.</param>
    /// <param name="associatedData">Same associated data as used by <see cref="Seal"/>.</param>
    /// <returns>Plaintext bytes.</returns>
    /// <exception cref="CryptographicException">Tampered data, wrong key or a downgrade (ML-KEM expected but missing).</exception>
    public static byte[] Open(HybridKeyPair keyPair, EncryptedPayload envelope, ReadOnlySpan<byte> associatedData)
    {
        ArgumentNullException.ThrowIfNull(keyPair);
        ArgumentNullException.ThrowIfNull(envelope);

        // Step 1: a recipient with ML-KEM refuses envelopes without it (stops a downgrade to classical-only).
        if (keyPair.MlKem is not null && envelope.MlKemCiphertext is null)
        {
            throw new CryptographicException("Envelope is missing the ML-KEM component.");
        }

        // Step 2: ECDH secret from the sender's ephemeral key.
        using var peer = ECDiffieHellman.Create();
        peer.ImportSubjectPublicKeyInfo(Convert.FromBase64String(envelope.EphemeralEcdhPublicKey), out _);
        var ecdhSecret = keyPair.Ecdh.DeriveRawSecretAgreement(peer.PublicKey);

        // Step 3: ML-KEM decapsulation.
        byte[] kemSecret = [];
        if (envelope.MlKemCiphertext is not null)
        {
            if (keyPair.MlKem is null)
            {
                throw new CryptographicException("Envelope requires ML-KEM but this key pair has none.");
            }
            kemSecret = keyPair.MlKem.Decapsulate(Convert.FromBase64String(envelope.MlKemCiphertext));
        }

        // Step 4: derive key, decrypt and authenticate (GCM tag).
        var key = DeriveKey(ecdhSecret, kemSecret);
        try
        {
            var ciphertext = Convert.FromBase64String(envelope.Ciphertext);
            var tag = Convert.FromBase64String(envelope.Tag);
            var plaintext = new byte[ciphertext.Length];
            using var aes = new AesGcm(key, tag.Length);
            aes.Decrypt(Convert.FromBase64String(envelope.Nonce), ciphertext, tag, plaintext, associatedData);
            return plaintext;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
            CryptographicOperations.ZeroMemory(ecdhSecret);
            CryptographicOperations.ZeroMemory(kemSecret);
        }
    }

    /// <summary>KEM combiner: concatenates both secrets and expands with HKDF-SHA384 to 256 bits.</summary>
    private static byte[] DeriveKey(byte[] ecdhSecret, byte[] kemSecret)
    {
        var ikm = new byte[ecdhSecret.Length + kemSecret.Length];
        ecdhSecret.CopyTo(ikm, 0);
        kemSecret.CopyTo(ikm, ecdhSecret.Length);
        try
        {
            return HKDF.DeriveKey(HashAlgorithmName.SHA384, ikm, 32, salt: null, info: Info);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(ikm);
        }
    }
}
