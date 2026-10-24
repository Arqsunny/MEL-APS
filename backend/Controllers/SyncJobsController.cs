using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using MelSync;
using MasterEquipmentApi.Auth;
using MasterEquipmentApi.Data;
using MasterEquipmentApi.Models;
using MasterEquipmentApi.Sync;

namespace MasterEquipmentApi.Controllers;

[ApiController]
[Route("api/sync/jobs")]
public class SyncJobsController : ControllerBase
{
    private readonly MelDbContext _db;
    private readonly ISyncRunner _runner;
    public SyncJobsController(MelDbContext db, ISyncRunner runner) { _db = db; _runner = runner; }

    /// <summary>
    /// Connectivity check: runs the real pipeline against the linked model with NO changes. The activity opens the model as the
    /// connected account, writes nothing, saves nothing and closes it. It proves the bundle loads, the model opens and the result
    /// comes back before any real change is risked. Shows in the job list as a job with 0 changes.
    /// </summary>
    [Authorize(Policy = MelPolicies.CanReview)]
    [HttpPost("~/api/sync/check/{modelGuid}")]
    public async Task<IActionResult> Check(string modelGuid, CancellationToken ct)
    {
        var model = await _db.Models.AsNoTracking().FirstOrDefaultAsync(m => m.ModelGuid == modelGuid, ct);
        var blocker = await _runner.PreflightAsync(model, ct);
        if (blocker != null) return Conflict(new { error = blocker });

        var batchId = Guid.NewGuid();
        var payload = new SyncPayload
        {
            BatchId = batchId.ToString(),
            Region = model?.Region ?? "US",
            ProjectGuid = model?.CloudProjectGuid ?? string.Empty,
            ModelGuid = model?.CloudModelGuid ?? string.Empty,
            RevitVersion = model?.RevitVersion,
            // Updates stay empty on purpose: nothing is applied, so the bundle neither commits nor saves.
        };
        var job = new SyncJob
        {
            BatchId = batchId, ModelGuid = modelGuid, Runner = _runner.Name,
            RequestedBy = User.ActorName(), RequestedById = User.ActorId(), PayloadJson = SyncJson.Serialize(payload)
        };
        _db.SyncJobs.Add(job);
        _db.AuditLog.Add(new AuditEntry
        {
            Actor = User.ActorName(), ActorId = User.ActorId(), Action = "ConnectivityCheck", ModelGuid = modelGuid,
            EquipmentUniqueId = "-", BatchId = batchId
        });

        try { await _db.SaveChangesAsync(ct); }
        catch (DbUpdateException)
        {
            return Conflict(new { error = "A sync or check for this model is already running. Wait for it to finish." });
        }
        return StatusCode(StatusCodes.Status202Accepted, new { status = "Queued", jobId = job.Id, batchId, runner = _runner.Name });
    }

    /// <summary>Recent sync jobs for a model, newest first (summary only).</summary>
    [HttpGet("{modelGuid}")]
    public async Task<IActionResult> List(string modelGuid, [FromQuery] int limit = 20, CancellationToken ct = default)
    {
        var jobs = await _db.SyncJobs.AsNoTracking()
            .Where(j => j.ModelGuid == modelGuid)
            .OrderByDescending(j => j.CreatedAt).Take(Math.Clamp(limit, 1, 100))
            .Select(j => new
            {
                j.Id, j.BatchId, j.Status, j.Runner, j.DaStatus, j.RequestedBy, j.CreatedAt, j.SubmittedAt, j.CompletedAt, j.Error,
                items = _db.StagedUpdates.Count(s => s.SyncJobId == j.Id)
            }).ToListAsync(ct);
        return Ok(jobs);
    }

    /// <summary>One job with its per-element results.</summary>
    [HttpGet("{modelGuid}/{id:guid}")]
    public async Task<IActionResult> Get(string modelGuid, Guid id, CancellationToken ct = default)
    {
        var j = await _db.SyncJobs.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id && x.ModelGuid == modelGuid, ct);
        if (j == null) return NotFound();

        var changes = await _db.StagedUpdates.AsNoTracking().Where(s => s.SyncJobId == id)
            .Select(s => new { s.Id, s.EquipmentUniqueId, s.ParameterName, s.OldValue, s.ProposedValue, s.Status, s.LastError })
            .ToListAsync(ct);

        return Ok(new
        {
            j.Id, j.BatchId, j.Status, j.Runner, j.WorkItemId, j.DaStatus, j.RequestedBy, j.CreatedAt, j.SubmittedAt, j.LastPolledAt, j.CompletedAt, j.Error,
            // The Design Automation report can contain internal paths and identifiers; BIM Leads only.
            reportUrl = User.IsInRole(MelRoles.BimLead) ? j.ReportUrl : null,
            changes,
            result = j.ResultJson == null ? null : SyncJson.Deserialize<SyncResult>(j.ResultJson)
        });
    }
}
