namespace MasterEquipmentApi.Models;

public enum SyncJobStatus
{
    /// <summary>Created at dispatch; not yet submitted to the runner.</summary>
    Queued,
    /// <summary>Submitted to Design Automation; waiting for completion.</summary>
    Submitted,
    Succeeded,
    PartiallySucceeded,
    Failed
}

public class SyncJob
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid BatchId { get; set; }
    public string ModelGuid { get; set; } = string.Empty;
    public SyncJobStatus Status { get; set; } = SyncJobStatus.Queued;
    /// <summary>"Mock" or "Live": which runner handled the job.</summary>
    public string Runner { get; set; } = string.Empty;
    public string? WorkItemId { get; set; }
    /// <summary>Last raw Design Automation status (pending, inprogress, success, failedInstructions, ...).</summary>
    public string? DaStatus { get; set; }
    public string RequestedBy { get; set; } = string.Empty;
    public string? RequestedById { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? SubmittedAt { get; set; }
    public DateTime? LastPolledAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public string? Error { get; set; }
    public string? ReportUrl { get; set; }
    /// <summary>MelSync.SyncPayload (json) sent to the activity.</summary>
    public string PayloadJson { get; set; } = "{}";
    /// <summary>MelSync.SyncResult (json) returned by the activity.</summary>
    public string? ResultJson { get; set; }
    /// <summary>Runner-private bookkeeping (e.g. OSS object keys), json.</summary>
    public string? RunnerState { get; set; }
}

/// <summary>Where a model lives in the cloud (needed to open it from Design Automation).</summary>
public class RevitModel
{
    /// <summary>Our model id (the value used in equipment_master.ModelGuid).</summary>
    public string ModelGuid { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    /// <summary>"US" or "EMEA".</summary>
    public string? Region { get; set; }
    public string? CloudProjectGuid { get; set; }
    public string? CloudModelGuid { get; set; }
    /// <summary>Revit release the model was last saved in (e.g. 2025); decides which engine may open it. Null = unknown (sync blocked).</summary>
    public int? RevitVersion { get; set; }
    public bool IsLinked => !string.IsNullOrEmpty(Region) && !string.IsNullOrEmpty(CloudProjectGuid) && !string.IsNullOrEmpty(CloudModelGuid);
}

/// <summary>The APS user the system acts as (Revit cloud models need a user context). Refresh token is encrypted.</summary>
public class ApsConnection
{
    public string Id { get; set; } = "service";
    public string ProtectedRefreshToken { get; set; } = string.Empty;
    public string? Account { get; set; }
    public string? ConnectedBy { get; set; }
    public DateTime ConnectedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
