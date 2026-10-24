using MelSync;
using MasterEquipmentApi.Models;

namespace MasterEquipmentApi.Sync;

public enum RunnerState { Running, Done, Failed }

/// <param name="State">Running: ask again later. Done: <paramref name="Result"/> is set. Failed: the run itself failed (no usable result).</param>
public record RunnerPoll(RunnerState State, string? DaStatus, string? Message = null, string? ReportUrl = null, SyncResult? Result = null);

public record SubmitOutcome(string WorkItemId, string? RunnerState);

/// <summary>Executes a <see cref="SyncPayload"/> against a Revit model. Real (Design Automation) or mock.</summary>
public interface ISyncRunner
{
    string Name { get; }
    /// <summary>Cheap checks before a job is created (linked model, connected account). Null = OK, otherwise the reason.</summary>
    Task<string?> PreflightAsync(RevitModel? model, CancellationToken ct);
    Task<SubmitOutcome> SubmitAsync(SyncJob job, SyncPayload payload, CancellationToken ct);
    Task<RunnerPoll> PollAsync(SyncJob job, CancellationToken ct);
}
