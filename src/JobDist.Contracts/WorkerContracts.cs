using System.ComponentModel.DataAnnotations;

namespace JobDist.Contracts;

// -----------------------------------------------------------------------------------------------------------------
// Worker protocol (referenced by the worker SDK so other projects can implement workers).
// Pull model: workers call the server (works behind NAT / firewalls / unstable networks), the server never dials out.
// -----------------------------------------------------------------------------------------------------------------

/// <summary>A job type the worker can run and how many it can run at the same time.</summary>
/// <param name="JobType">Job type name.</param>
/// <param name="MaxConcurrency">Parallel executions of this type (1-1024).</param>
/// <param name="SpeedFactor">Self-declared relative speed for this type (1.0 = baseline). The ML model refines it.</param>
public sealed record WorkerCapabilityRequest(
    [Required, StringLength(100, MinimumLength = 1), RegularExpression(@"^[A-Za-z0-9][A-Za-z0-9._\-]*$")] string JobType,
    [Range(1, 1024)] int MaxConcurrency = 1,
    [Range(0.01, 100)] double SpeedFactor = 1.0);

/// <summary>Machine specification used by the scheduler to match jobs to hardware.</summary>
/// <param name="CpuCores">Logical CPU cores.</param>
/// <param name="MemoryMb">Memory available to the worker.</param>
/// <param name="GpuCount">Number of GPUs.</param>
/// <param name="BenchmarkScore">Relative single benchmark score (1.0 = baseline machine).</param>
/// <param name="Os">Operating system description.</param>
public sealed record WorkerSpecRequest(
    [Range(1, 4096)] int CpuCores,
    [Range(64, 16_777_216)] long MemoryMb,
    [Range(0, 64)] int GpuCount = 0,
    [Range(0.01, 1000)] double BenchmarkScore = 1.0,
    [StringLength(200)] string? Os = null);

/// <summary>Worker registration (worker → server, role <c>Worker</c>).</summary>
/// <param name="Name">Unique worker name (host name). Re-registering the same name re-uses the worker id.</param>
/// <param name="Capabilities">Job types it can run (1-200).</param>
/// <param name="Spec">Machine specification.</param>
/// <param name="MaxTotalConcurrency">Cap across all job types (1-4096).</param>
/// <param name="EcdhPublicKey">Base64 P-384 SubjectPublicKeyInfo for sensitive payload envelopes.</param>
/// <param name="MlKemPublicKey">Base64 ML-KEM-768 encapsulation key (null when the worker OS has no PQC support).</param>
/// <param name="Version">Worker SDK version.</param>
/// <param name="Tags">Labels such as <c>zone=a</c>, <c>gpu</c>.</param>
public sealed record RegisterWorkerRequest(
    [Required, StringLength(200, MinimumLength = 1), RegularExpression(@"^[A-Za-z0-9][A-Za-z0-9._\-:]*$")] string Name,
    [Required, MinLength(1), MaxLength(200)] IReadOnlyList<WorkerCapabilityRequest> Capabilities,
    [Required] WorkerSpecRequest Spec,
    [Range(1, 4096)] int MaxTotalConcurrency = 4,
    [StringLength(512)] string? EcdhPublicKey = null,
    [StringLength(4096)] string? MlKemPublicKey = null,
    [StringLength(50)] string? Version = null,
    [MaxLength(20)] IReadOnlyList<string>? Tags = null);

/// <summary>Public keys the worker uses to verify server commands (hybrid ML-DSA-65 + ECDSA-P384).</summary>
/// <param name="KeyId">Key identifier.</param>
/// <param name="MlDsaPublicKey">Base64 raw ML-DSA-65 public key, or null when the server OS has no PQC.</param>
/// <param name="EcdsaPublicKey">Base64 ECDSA P-384 SubjectPublicKeyInfo.</param>
public sealed record ServerKeysResponse(string KeyId, string? MlDsaPublicKey, string EcdsaPublicKey);

/// <summary>Registration result. The token is returned once; the server stores only its hash.</summary>
/// <param name="WorkerId">Worker id.</param>
/// <param name="WorkerToken">Secret presented in <c>X-Worker-Token</c> on every worker call.</param>
/// <param name="HeartbeatIntervalSeconds">How often to send heartbeats.</param>
/// <param name="OfflineAfterSeconds">Heartbeat silence after which the worker is considered lost.</param>
/// <param name="ServerKeys">Keys for verifying signed assignments and commands.</param>
public sealed record RegisterWorkerResponse(
    Guid WorkerId,
    string WorkerToken,
    int HeartbeatIntervalSeconds,
    int OfflineAfterSeconds,
    ServerKeysResponse ServerKeys);

/// <summary>Heartbeat with live load figures.</summary>
/// <param name="RunningJobIds">Jobs currently executing on the worker.</param>
/// <param name="CpuUsagePercent">0-100.</param>
/// <param name="MemoryUsagePercent">0-100.</param>
public sealed record HeartbeatRequest(
    [MaxLength(4096)] IReadOnlyList<Guid>? RunningJobIds = null,
    [Range(0, 100)] double CpuUsagePercent = 0,
    [Range(0, 100)] double MemoryUsagePercent = 0);

/// <summary>Detached hybrid signature.</summary>
/// <param name="Algorithm"><c>ML-DSA-65+ECDSA-P384</c> or <c>ECDSA-P384</c>.</param>
/// <param name="KeyId">Signing key id.</param>
/// <param name="PqcSignature">Base64 ML-DSA signature (null on platforms without PQC).</param>
/// <param name="ClassicSignature">Base64 ECDSA signature (IEEE P1363).</param>
public sealed record SignatureEnvelope(string Algorithm, string KeyId, string? PqcSignature, string ClassicSignature);

/// <summary>Command for a worker, signed by the server.</summary>
public sealed record WorkerCommandResponse(
    Guid CommandId,
    WorkerCommandKind Kind,
    Guid WorkerId,
    Guid? JobId,
    string? Reason,
    DateTimeOffset IssuedUtc,
    SignatureEnvelope Signature);

/// <summary>Heartbeat result.</summary>
/// <param name="Status">Worker status as seen by the server (e.g. <c>Draining</c>).</param>
/// <param name="Commands">Pending commands.</param>
public sealed record HeartbeatResponse(WorkerStatus Status, IReadOnlyList<WorkerCommandResponse> Commands);

/// <summary>Long-poll for assigned jobs.</summary>
/// <param name="MaxJobs">Max jobs to receive now (1-256).</param>
/// <param name="WaitSeconds">Long-poll wait when nothing is ready (0-30).</param>
public sealed record PollRequest([Range(1, 256)] int MaxJobs = 8, [Range(0, 30)] int WaitSeconds = 20);

/// <summary>Sensitive payload encrypted for one worker (ML-KEM-768 + ECDH-P384 → HKDF → AES-256-GCM).</summary>
public sealed record EncryptedPayload(
    string Algorithm,
    string EphemeralEcdhPublicKey,
    string? MlKemCiphertext,
    string Nonce,
    string Tag,
    string Ciphertext);

/// <summary>A job given to a worker. The signature binds the job, attempt and worker so it cannot be replayed elsewhere.</summary>
public sealed record JobAssignmentResponse(
    Guid JobId,
    Guid AttemptId,
    Guid WorkerId,
    string JobType,
    int Attempt,
    int Priority,
    int TimeoutSeconds,
    DateTimeOffset IssuedUtc,
    string? Payload,
    EncryptedPayload? EncryptedPayload,
    string PayloadSha256,
    SignatureEnvelope Signature);

/// <summary>Poll result.</summary>
public sealed record PollResponse(IReadOnlyList<JobAssignmentResponse> Assignments, IReadOnlyList<WorkerCommandResponse> Commands);

/// <summary>Worker reports that an attempt started.</summary>
public sealed record JobStartedRequest([Required] Guid AttemptId);

/// <summary>Worker reports the end of an attempt.</summary>
/// <param name="AttemptId">Attempt id from the assignment.</param>
/// <param name="Outcome">Succeeded / Failed / TimedOut / Cancelled.</param>
/// <param name="DurationMs">Execution time measured by the worker.</param>
/// <param name="Result">Optional output (max 1 MB).</param>
/// <param name="Error">Optional error text (max 4,000 chars, no stack traces with secrets).</param>
/// <param name="Retryable">Whether a failure may be retried.</param>
public sealed record JobCompletedRequest(
    [Required] Guid AttemptId,
    AttemptOutcome Outcome,
    [Range(0, 1e10)] double DurationMs,
    [StringLength(1_048_576)] string? Result = null,
    [StringLength(4000)] string? Error = null,
    bool Retryable = true);

/// <summary>Public view of a worker.</summary>
public sealed record WorkerResponse(
    Guid WorkerId,
    string Name,
    WorkerStatus Status,
    IReadOnlyList<WorkerCapabilityRequest> Capabilities,
    WorkerSpecRequest Spec,
    int MaxTotalConcurrency,
    int ActiveJobs,
    double CpuUsagePercent,
    double MemoryUsagePercent,
    DateTimeOffset RegisteredUtc,
    DateTimeOffset LastHeartbeatUtc,
    int OfflineCount,
    bool SupportsPqc,
    string? Version,
    IReadOnlyList<string> Tags);
