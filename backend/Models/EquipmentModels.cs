namespace MasterEquipmentApi.Models;

// Staging gate lifecycle (spec section 6): Pending -> Approved | Rejected -> CommittedToModel
public enum ApprovalStatus
{
    Pending,
    Approved,
    Rejected,
    /// <summary>Sent to Design Automation; waiting for the result.</summary>
    Dispatched,
    CommittedToModel
}

public class StagedParameterRecord
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid EquipmentId { get; set; }
    public string ModelGuid { get; set; } = string.Empty;
    public string EquipmentUniqueId { get; set; } = string.Empty;
    public Guid ParameterGuid { get; set; }
    public string ParameterName { get; set; } = string.Empty;
    public string StorageType { get; set; } = "String";
    public string OldValue { get; set; } = string.Empty;
    public string ProposedValue { get; set; } = string.Empty;
    public ApprovalStatus Status { get; set; } = ApprovalStatus.Pending;
    public string RequestedBy { get; set; } = string.Empty;
    public DateTime RequestedAt { get; set; } = DateTime.UtcNow;
    public string? ReviewedBy { get; set; }
    public DateTime? ReviewedAt { get; set; }
    public string? RejectionReason { get; set; }
    /// <summary>Sync job currently or last responsible for this change.</summary>
    public Guid? SyncJobId { get; set; }
    /// <summary>Why the last sync attempt did not apply this change (it returns to Approved and can be retried).</summary>
    public string? LastError { get; set; }
}

public class StageParameterRequest
{
    public string ModelGuid { get; set; } = string.Empty;
    public string EquipmentUniqueId { get; set; } = string.Empty;
    public Guid ParameterGuid { get; set; }
    public string ParameterName { get; set; } = string.Empty;
    public string StorageType { get; set; } = "String";
    public string ProposedValue { get; set; } = string.Empty;
}

public class ReviewRequest
{
    public string? Reason { get; set; }
}

public class SyncBatchRequest
{
    public string ProjectGuid { get; set; } = string.Empty;
    public string ModelGuid { get; set; } = string.Empty;
    public List<string> EquipmentUniqueIds { get; set; } = new();
}

public class EquipmentItem
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string ModelGuid { get; set; } = string.Empty;
    public string UniqueId { get; set; } = string.Empty;
    public string EquipmentGuid { get; set; } = string.Empty;
    public string Tag { get; set; } = string.Empty;
    public string FamilyType { get; set; } = string.Empty;
    public string Discipline { get; set; } = string.Empty;
    /// <summary>Current model values keyed by parameter GUID, stored in SI units (see mel-parameters.json).</summary>
    public Dictionary<Guid, string> Values { get; set; } = new();
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>Append-only record of every state change (enforced by DB triggers). Replaces temporal tables, which PostgreSQL lacks.</summary>
public class AuditEntry
{
    public long Id { get; set; }
    public DateTime OccurredAt { get; set; } = DateTime.UtcNow;
    /// <summary>Username from the validated token (never from the request body).</summary>
    public string Actor { get; set; } = string.Empty;
    /// <summary>Immutable subject (sub/oid) from the validated token.</summary>
    public string? ActorId { get; set; }
    /// <summary>Staged | Approved | Rejected | CommittedToModel | ModelValueChanged</summary>
    public string Action { get; set; } = string.Empty;
    public string ModelGuid { get; set; } = string.Empty;
    public string EquipmentUniqueId { get; set; } = string.Empty;
    public Guid? ParameterGuid { get; set; }
    public string? ParameterName { get; set; }
    public string? OldValue { get; set; }
    public string? NewValue { get; set; }
    public Guid? StagingId { get; set; }
    /// <summary>Groups every change dispatched together in one sync.</summary>
    public Guid? BatchId { get; set; }
    public string? Details { get; set; }
}
