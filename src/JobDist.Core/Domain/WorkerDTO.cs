using JobDist.Contracts;

namespace JobDist.Core.Domain;

/// <summary>A worker node registered with the cluster.</summary>
public sealed class WorkerDTO
{
    public Guid WorkerId { get; set; }
    public string Name { get; set; } = string.Empty;
    public WorkerStatus Status { get; set; }
    public List<WorkerCapabilityDTO> Capabilities { get; set; } = [];
    public WorkerSpecDTO Spec { get; set; } = new();
    public int MaxTotalConcurrency { get; set; }
    /// <summary>SHA-256 of the worker token (the token itself is never stored).</summary>
    public byte[] TokenHash { get; set; } = [];
    public string? EcdhPublicKey { get; set; }
    public string? MlKemPublicKey { get; set; }
    public string? Version { get; set; }
    public List<string> Tags { get; set; } = [];
    public DateTimeOffset RegisteredUtc { get; set; }
    public DateTimeOffset LastHeartbeatUtc { get; set; }
    public double CpuUsagePercent { get; set; }
    public double MemoryUsagePercent { get; set; }
    /// <summary>How often the worker went offline (instability indicator).</summary>
    public int OfflineCount { get; set; }
    public string? RegisteredBy { get; set; }

    /// <summary>Capability for a job type, or null.</summary>
    public WorkerCapabilityDTO? CapabilityFor(string jobType) =>
        Capabilities.Find(c => string.Equals(c.JobType, jobType, StringComparison.OrdinalIgnoreCase));

    public WorkerDTO Clone()
    {
        var copy = (WorkerDTO)MemberwiseClone();
        copy.Capabilities = Capabilities.Select(c => c with { }).ToList();
        copy.Spec = Spec with { };
        copy.Tags = [.. Tags];
        return copy;
    }
}

/// <summary>A job type a worker can run.</summary>
public sealed record WorkerCapabilityDTO
{
    public string JobType { get; init; } = string.Empty;
    public int MaxConcurrency { get; init; } = 1;
    public double SpeedFactor { get; init; } = 1.0;
}

/// <summary>Machine specification.</summary>
public sealed record WorkerSpecDTO
{
    public int CpuCores { get; init; } = 1;
    public long MemoryMb { get; init; } = 1024;
    public int GpuCount { get; init; }
    public double BenchmarkScore { get; init; } = 1.0;
    public string? Os { get; init; }
}

/// <summary>Live load of a worker: running/assigned jobs per type (from the job table).</summary>
public sealed record WorkerLoadDTO(Guid WorkerId, string JobType, int Active);

/// <summary>Command queued for a worker.</summary>
public sealed class WorkerCommandDTO
{
    public Guid CommandId { get; set; }
    public Guid WorkerId { get; set; }
    public WorkerCommandKind Kind { get; set; }
    public Guid? JobId { get; set; }
    public string? Reason { get; set; }
    public DateTimeOffset IssuedUtc { get; set; }
    public DateTimeOffset? DeliveredUtc { get; set; }
    public WorkerCommandDTO Clone() => (WorkerCommandDTO)MemberwiseClone();
}

/// <summary>Leader lease row used for HA leader election.</summary>
/// <param name="Name">Lease name (e.g. <c>scheduler</c>).</param>
/// <param name="HolderId">Instance currently holding it.</param>
/// <param name="ExpiresUtc">Expiry.</param>
public sealed record LeaderLeaseDTO(string Name, string HolderId, DateTimeOffset ExpiresUtc, long Term = 0);
