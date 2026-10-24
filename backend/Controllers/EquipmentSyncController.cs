using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using MasterEquipmentApi.Auth;
using MasterEquipmentApi.Data;
using MasterEquipmentApi.Models;
using MasterEquipmentApi.Sync;
using MelSync;

namespace MasterEquipmentApi.Controllers;

[ApiController]
[Route("api/[controller]")]
public class EquipmentSyncController : ControllerBase
{
    private readonly MelDbContext _db;
    private readonly ISyncRunner _runner;
    public EquipmentSyncController(MelDbContext db, ISyncRunner runner) { _db = db; _runner = runner; }

    // Identity always comes from the validated token, never from the request body.
    private void Audit(string action, StagedParameterRecord s, string? oldV, string? newV, Guid? batch = null, string? details = null) =>
        _db.AuditLog.Add(new AuditEntry
        {
            Actor = User.ActorName(), ActorId = User.ActorId(), Action = action, ModelGuid = s.ModelGuid, EquipmentUniqueId = s.EquipmentUniqueId,
            ParameterGuid = s.ParameterGuid, ParameterName = s.ParameterName,
            OldValue = oldV, NewValue = newV, StagingId = s.Id, BatchId = batch, Details = details
        });

    [Authorize(Policy = MelPolicies.CanStage)]
    [HttpPost("stage")]
    public async Task<IActionResult> StageParameter([FromBody] StageParameterRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.ModelGuid) || string.IsNullOrWhiteSpace(req.EquipmentUniqueId))
            return BadRequest(new { error = "ModelGuid and EquipmentUniqueId are required." });

        var def = ParameterCatalog.Find(req.ParameterGuid);
        if (def == null)
            return BadRequest(new { error = $"Unknown parameter GUID {req.ParameterGuid}. See GET /api/parameters." });
        if (!def.Stageable)
            return BadRequest(new { error = $"{def.Name} is system-owned and cannot be staged." });

        var equipment = await _db.Equipment.AsNoTracking()
            .FirstOrDefaultAsync(e => e.ModelGuid == req.ModelGuid && e.UniqueId == req.EquipmentUniqueId);
        if (equipment == null) return NotFound(new { error = "Equipment element not found in this model." });

        var actor = User.ActorName();
        // OldValue comes from the database, not the client, so the review vector can't be spoofed.
        equipment.Values.TryGetValue(def.Guid, out var current);

        var record = new StagedParameterRecord
        {
            EquipmentId = equipment.Id,
            ModelGuid = req.ModelGuid,
            EquipmentUniqueId = req.EquipmentUniqueId,
            ParameterGuid = req.ParameterGuid,
            ParameterName = def.Name,
            StorageType = def.DataType,
            OldValue = current ?? string.Empty,
            ProposedValue = req.ProposedValue,
            RequestedBy = actor
        };
        _db.StagedUpdates.Add(record);
        Audit("Staged", record, record.OldValue, record.ProposedValue);

        try
        {
            await _db.SaveChangesAsync();
        }
        catch (DbUpdateException)
        {
            return Conflict(new { error = $"{def.Name} already has a pending change for this equipment. Review it first." });
        }
        return Ok(new { message = "Parameter change staged; awaiting BIM Lead approval.", record });
    }

    [HttpGet("staged/{modelGuid}")]
    public async Task<IActionResult> GetStagedByModel(string modelGuid, [FromQuery] ApprovalStatus? status = null)
    {
        var q = _db.StagedUpdates.AsNoTracking().Where(s => s.ModelGuid == modelGuid);
        if (status != null) q = q.Where(s => s.Status == status);
        return Ok(await q.OrderBy(s => s.RequestedAt).ToListAsync());
    }

    [Authorize(Policy = MelPolicies.CanReview)]
    [HttpPost("approve/{id:guid}")]
    public Task<IActionResult> Approve(Guid id, [FromBody] ReviewRequest req) => Review(id, req, ApprovalStatus.Approved);

    [Authorize(Policy = MelPolicies.CanReview)]
    [HttpPost("reject/{id:guid}")]
    public Task<IActionResult> Reject(Guid id, [FromBody] ReviewRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.Reason))
            return Task.FromResult<IActionResult>(BadRequest(new { error = "A rejection reason is required." }));
        return Review(id, req, ApprovalStatus.Rejected);
    }

    private async Task<IActionResult> Review(Guid id, ReviewRequest req, ApprovalStatus outcome)
    {
        var record = await _db.StagedUpdates.FirstOrDefaultAsync(s => s.Id == id);
        if (record == null) return NotFound();
        if (record.Status != ApprovalStatus.Pending)
            return BadRequest(new { error = $"Update is {record.Status}, not Pending." });

        var actor = User.ActorName();
        // Segregation of duties: nobody reviews their own request.
        if (string.Equals(record.RequestedBy, actor, StringComparison.OrdinalIgnoreCase))
            return StatusCode(StatusCodes.Status403Forbidden, new { error = "You cannot review a change you requested." });

        record.Status = outcome;
        record.ReviewedBy = actor;
        record.ReviewedAt = DateTime.UtcNow;
        record.RejectionReason = outcome == ApprovalStatus.Rejected ? req.Reason : null;
        Audit(outcome.ToString(), record, record.OldValue, record.ProposedValue, details: record.RejectionReason);

        try { await _db.SaveChangesAsync(); }
        catch (DbUpdateConcurrencyException)
        {
            return Conflict(new { error = "This update was changed by someone else. Reload and try again." });
        }
        return Ok(record);
    }

    /// <summary>
    /// Queues a sync of the approved changes for the selected equipment. Returns 202 immediately; the background worker runs the
    /// Design Automation job and writes the outcome back (poll GET /api/sync/jobs/{modelGuid}/{jobId}).
    /// </summary>
    [Authorize(Policy = MelPolicies.CanReview)]
    [HttpPost("dispatch-revit-sync")]
    public async Task<IActionResult> DispatchRevitSync([FromBody] SyncBatchRequest req, CancellationToken ct)
    {
        // Staging gate: only BIM Lead-approved changes for this model may reach Revit.
        var approved = await _db.StagedUpdates
            .Where(s => s.ModelGuid == req.ModelGuid
                     && s.Status == ApprovalStatus.Approved
                     && req.EquipmentUniqueIds.Contains(s.EquipmentUniqueId))
            .ToListAsync(ct);

        if (approved.Count == 0)
            return BadRequest(new { error = "No approved staged parameters found for the selected equipment items." });

        var model = await _db.Models.AsNoTracking().FirstOrDefaultAsync(m => m.ModelGuid == req.ModelGuid, ct);
        var blocker = await _runner.PreflightAsync(model, ct);
        if (blocker != null) return Conflict(new { error = blocker });

        var uids = approved.Select(s => s.EquipmentUniqueId).Distinct().ToList();
        var equipment = await _db.Equipment.AsNoTracking()
            .Where(e => e.ModelGuid == req.ModelGuid && uids.Contains(e.UniqueId)).ToListAsync(ct);

        var batchId = Guid.NewGuid();
        var payload = new SyncPayload
        {
            BatchId = batchId.ToString(),
            Region = model?.Region ?? "US",
            ProjectGuid = model?.CloudProjectGuid ?? string.Empty,
            ModelGuid = model?.CloudModelGuid ?? string.Empty,
            RevitVersion = model?.RevitVersion,
        };
        foreach (var s in approved)
        {
            var def = ParameterCatalog.Find(s.ParameterGuid);
            payload.Updates.Add(new UpdateItem
            {
                StagingId = s.Id.ToString(),
                UniqueId = s.EquipmentUniqueId,
                EquipmentGuid = equipment.FirstOrDefault(e => e.UniqueId == s.EquipmentUniqueId)?.EquipmentGuid,
                ParameterGuid = s.ParameterGuid.ToString(),
                ParameterName = s.ParameterName,
                DataType = def?.DataType ?? s.StorageType,
                SiUnit = def?.SiUnit,
                Value = s.ProposedValue
            });
        }

        var job = new SyncJob
        {
            BatchId = batchId,
            ModelGuid = req.ModelGuid,
            Runner = _runner.Name,
            RequestedBy = User.ActorName(),
            RequestedById = User.ActorId(),
            PayloadJson = SyncJson.Serialize(payload)
        };
        _db.SyncJobs.Add(job);
        foreach (var s in approved)
        {
            s.Status = ApprovalStatus.Dispatched;
            s.SyncJobId = job.Id;
            s.LastError = null;
            Audit("Dispatched", s, s.OldValue, s.ProposedValue, batchId);
        }

        try { await _db.SaveChangesAsync(ct); }
        catch (DbUpdateConcurrencyException)
        {
            return Conflict(new { error = "Another sync is already processing these changes. Reload and try again." });
        }
        catch (DbUpdateException)
        {
            // Unique index: one in-flight sync per model.
            return Conflict(new { error = "A sync for this model is already running. Wait for it to finish, then dispatch again." });
        }

        return StatusCode(StatusCodes.Status202Accepted, new
        {
            status = "Queued",
            jobId = job.Id,
            batchId,
            itemsQueued = approved.Count,
            runner = _runner.Name,
            message = "Sync queued. Track it at GET /api/sync/jobs/{modelGuid}/{jobId}."
        });
    }
}
