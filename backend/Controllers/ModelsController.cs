using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using MasterEquipmentApi.Auth;
using MasterEquipmentApi.Data;
using MasterEquipmentApi.Models;

namespace MasterEquipmentApi.Controllers;

[ApiController]
[Route("api/[controller]")]
public class ModelsController : ControllerBase
{
    private readonly MelDbContext _db;
    public ModelsController(MelDbContext db) => _db = db;

    [HttpGet]
    public async Task<IActionResult> List(CancellationToken ct) =>
        Ok(await _db.Models.AsNoTracking().OrderBy(m => m.DisplayName)
            .Select(m => new { m.ModelGuid, m.DisplayName, m.Region, m.CloudProjectGuid, m.CloudModelGuid, m.RevitVersion }).ToListAsync(ct));

    /// <summary>
    /// Links one of our models to its Revit cloud model so Design Automation can open it. The GUIDs come from the model version's
    /// extension data in the Data Management API (project GUID and model GUID), not from the ACC project/item ids.
    /// </summary>
    [Authorize(Policy = MelPolicies.CanReview)]
    [HttpPut("{modelGuid}/cloud-link")]
    public async Task<IActionResult> LinkCloudModel(string modelGuid, [FromBody] CloudLinkRequest req, CancellationToken ct)
    {
        var region = req.Region?.Trim().ToUpperInvariant();
        if (region is not ("US" or "EMEA")) return BadRequest(new { error = "Region must be US or EMEA." });
        if (!Guid.TryParse(req.ProjectGuid, out var project) || !Guid.TryParse(req.ModelGuid, out var cloudModel))
            return BadRequest(new { error = "ProjectGuid and ModelGuid must be GUIDs." });

        if (req.RevitVersion is { } rv && (rv < 2015 || rv > 2100)) return BadRequest(new { error = "RevitVersion must be a release year such as 2025." });

        var model = await _db.Models.FirstOrDefaultAsync(m => m.ModelGuid == modelGuid, ct);
        if (model == null)
        {
            if (!await _db.Equipment.AnyAsync(e => e.ModelGuid == modelGuid, ct)) return NotFound(new { error = "Unknown model." });
            model = new RevitModel { ModelGuid = modelGuid, DisplayName = modelGuid };
            _db.Models.Add(model);
        }
        model.Region = region;
        model.CloudProjectGuid = project.ToString();
        model.CloudModelGuid = cloudModel.ToString();
        model.RevitVersion = req.RevitVersion;

        _db.AuditLog.Add(new AuditEntry
        {
            Actor = User.ActorName(), ActorId = User.ActorId(), Action = "ModelLinked", ModelGuid = modelGuid, EquipmentUniqueId = "-",
            NewValue = $"{region}/{project}/{cloudModel}/Revit {req.RevitVersion?.ToString() ?? "unknown"}"
        });
        await _db.SaveChangesAsync(ct);
        return Ok(new { model.ModelGuid, model.DisplayName, model.Region, model.CloudProjectGuid, model.CloudModelGuid, model.RevitVersion });
    }
}

public record CloudLinkRequest(string? Region, string? ProjectGuid, string? ModelGuid, int? RevitVersion = null);
