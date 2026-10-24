using Microsoft.EntityFrameworkCore;
using MelSync;
using MasterEquipmentApi.Data;
using MasterEquipmentApi.Models;

namespace MasterEquipmentApi.Sync;

/// <summary>
/// Turns a finished job into database state, atomically. A change is "committed" only when Revit applied it AND the model
/// was saved/synchronized; everything else returns to Approved with the reason, so a BIM Lead can retry.
/// </summary>
public class SyncResultApplier
{
    private const string System = "system:sync-worker";
    private readonly ILogger<SyncResultApplier> _log;
    public SyncResultApplier(ILogger<SyncResultApplier> log) => _log = log;

    public async Task ApplyResultAsync(MelDbContext db, SyncJob job, SyncResult result, CancellationToken ct)
    {
        var records = await LoadRecordsAsync(db, job, ct);
        var items = await LoadEquipmentAsync(db, job, records, ct);
        var byId = result.Results.GroupBy(r => r.StagingId).ToDictionary(g => g.Key, g => g.First());
        // Applied items are only durable if the model was saved; otherwise Revit's changes were discarded on close.
        var durable = result.Opened && result.Synced && string.IsNullOrEmpty(result.Error);

        int committed = 0;
        foreach (var s in records)
        {
            byId.TryGetValue(s.Id.ToString(), out var r);
            if (r is { Outcome: Outcome.Applied } && durable)
            {
                var item = items.FirstOrDefault(e => e.UniqueId == s.EquipmentUniqueId);
                var newValue = r.AppliedValue ?? s.ProposedValue;
                if (item != null)
                {
                    item.Values.TryGetValue(s.ParameterGuid, out var before);
                    item.Values[s.ParameterGuid] = newValue;
                    item.UpdatedAt = DateTime.UtcNow;
                    Audit(db, job, "ModelValueChanged", s, before, newValue);
                }
                s.Status = ApprovalStatus.CommittedToModel;
                s.LastError = null;
                Audit(db, job, "CommittedToModel", s, s.OldValue, newValue);
                committed++;
            }
            else
            {
                var why = r == null ? (result.Error != null
                                          ? $"{Codes.Error}: {result.Error}"
                                          : $"{Codes.NoResult}: the activity returned no result for this change.")
                        : r.Outcome == Outcome.Applied ? $"NotSynced: applied in Revit but the model was not saved ({result.Error ?? "synchronize did not complete"})."
                        : $"{r.Code}: {r.Message}";
                ReturnToApproved(db, job, s, why);
            }
        }

        job.ResultJson = SyncJson.Serialize(result);
        // A run that could not open the model has failed even when there was nothing to commit (a connectivity check has no changes).
        var runFailed = !result.Opened || !string.IsNullOrEmpty(result.Error);
        job.Status = runFailed && committed == 0 ? SyncJobStatus.Failed
                   : committed == records.Count ? SyncJobStatus.Succeeded
                   : committed == 0 ? SyncJobStatus.Failed
                   : SyncJobStatus.PartiallySucceeded;
        job.Error = result.Error ?? (result.Opened ? null : "The model could not be opened.");
        job.CompletedAt = DateTime.UtcNow;
        await SaveAsync(db, job, ct);
    }

    public async Task FailAsync(MelDbContext db, SyncJob job, string message, CancellationToken ct)
    {
        var records = await LoadRecordsAsync(db, job, ct);
        foreach (var s in records) ReturnToApproved(db, job, s, message);
        job.Status = SyncJobStatus.Failed;
        job.Error = message;
        job.CompletedAt = DateTime.UtcNow;
        await SaveAsync(db, job, ct);
    }

    private static Task<List<StagedParameterRecord>> LoadRecordsAsync(MelDbContext db, SyncJob job, CancellationToken ct) =>
        db.StagedUpdates.Where(s => s.SyncJobId == job.Id && s.Status == ApprovalStatus.Dispatched).ToListAsync(ct);

    private static Task<List<EquipmentItem>> LoadEquipmentAsync(MelDbContext db, SyncJob job, List<StagedParameterRecord> records, CancellationToken ct)
    {
        var uids = records.Select(r => r.EquipmentUniqueId).Distinct().ToList();
        return db.Equipment.Where(e => e.ModelGuid == job.ModelGuid && uids.Contains(e.UniqueId)).ToListAsync(ct);
    }

    private static void ReturnToApproved(MelDbContext db, SyncJob job, StagedParameterRecord s, string why)
    {
        s.Status = ApprovalStatus.Approved;
        s.LastError = why;
        Audit(db, job, "SyncFailed", s, s.OldValue, s.ProposedValue, why);
    }

    private static void Audit(MelDbContext db, SyncJob job, string action, StagedParameterRecord s, string? oldV, string? newV, string? details = null) =>
        db.AuditLog.Add(new AuditEntry
        {
            Actor = System, Action = action, ModelGuid = s.ModelGuid, EquipmentUniqueId = s.EquipmentUniqueId,
            ParameterGuid = s.ParameterGuid, ParameterName = s.ParameterName, OldValue = oldV, NewValue = newV,
            StagingId = s.Id, BatchId = job.BatchId, Details = details
        });

    private async Task SaveAsync(MelDbContext db, SyncJob job, CancellationToken ct)
    {
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateConcurrencyException)
        {
            // Someone else finished this job first (e.g. a second worker). Their result stands; ours must not double-apply.
            _log.LogWarning("Job {Job} was already finalized by another process; discarding this attempt.", job.Id);
            db.ChangeTracker.Clear();
        }
    }
}
