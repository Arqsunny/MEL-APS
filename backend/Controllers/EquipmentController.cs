using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using MasterEquipmentApi.Auth;
using MasterEquipmentApi.Data;
using MasterEquipmentApi.Models;

namespace MasterEquipmentApi.Controllers;

[ApiController]
[Route("api/[controller]")]
public class EquipmentController : ControllerBase
{
    private readonly MelDbContext _db;
    public EquipmentController(MelDbContext db) => _db = db;

    [HttpGet("{modelGuid}")]
    public async Task<IActionResult> GetByModel(string modelGuid) =>
        Ok(await _db.Equipment.AsNoTracking().Where(e => e.ModelGuid == modelGuid).OrderBy(e => e.Tag).ToListAsync());

    /// <summary>
    /// Registers an element the user picked in the viewer so it can be staged. TEXT and INTEGER values are stored as shown.
    /// Measured values are left alone: the viewer reports display units, and the database stores SI.
    /// </summary>
    [Authorize(Policy = MelPolicies.CanStage)]
    [HttpPut("{modelGuid}/from-viewer")]
    public async Task<IActionResult> FromViewer(string modelGuid, [FromBody] ViewerEquipmentRequest req, CancellationToken ct)
    {
        var uniqueId = req.UniqueId?.Trim() ?? "";
        if (uniqueId.Length is < 1 or > 100) return BadRequest(new { error = "UniqueId is required." });
        if (!await _db.Models.AnyAsync(m => m.ModelGuid == modelGuid, ct) && !await _db.Equipment.AnyAsync(e => e.ModelGuid == modelGuid, ct))
            return NotFound(new { error = "Unknown model." });

        var item = await _db.Equipment.FirstOrDefaultAsync(e => e.ModelGuid == modelGuid && e.UniqueId == uniqueId, ct);
        var created = item == null;
        if (item == null)
        {
            item = new EquipmentItem { ModelGuid = modelGuid, UniqueId = uniqueId, Discipline = "Model", Values = new() };
            _db.Equipment.Add(item);
        }

        // Demo rows keep their seeded identity. Elements picked in the viewer can be refreshed from the selection.
        if (created || item.Discipline == "Model")
        {
            item.Tag = Clip(string.IsNullOrWhiteSpace(req.Tag) ? "Selected" : req.Tag, 100);
            item.FamilyType = Clip(req.FamilyType, 200);
            item.Discipline = "Model";
            if (!string.IsNullOrWhiteSpace(req.EquipmentGuid)) item.EquipmentGuid = Clip(req.EquipmentGuid, 100);
            else if (created) item.EquipmentGuid = "";
            var merged = new Dictionary<Guid, string>(item.Values);
            foreach (var (key, value) in ViewerValues(req.Values)) merged[key] = value;
            item.Values = merged;
            item.UpdatedAt = DateTime.UtcNow;
        }

        if (created)
        {
            _db.AuditLog.Add(new AuditEntry
            {
                Actor = User.ActorName(), ActorId = User.ActorId(), Action = "EquipmentSeen",
                ModelGuid = modelGuid, EquipmentUniqueId = uniqueId, NewValue = item.Tag
            });
        }
        await _db.SaveChangesAsync(ct);
        return Ok(item);
    }

    private static Dictionary<Guid, string> ViewerValues(Dictionary<string, string>? incoming)
    {
        var values = new Dictionary<Guid, string>();
        if (incoming == null) return values;
        foreach (var (key, raw) in incoming)
        {
            if (!Guid.TryParse(key, out var guid)) continue;
            var def = ParameterCatalog.Find(guid);
            if (def == null || def.DataType is not ("TEXT" or "INTEGER")) continue;
            var value = (raw ?? "").Trim();
            if (value.Length > 500) value = value[..500];
            if (def.DataType == "INTEGER" && !int.TryParse(value, out _)) continue;
            values[guid] = value;
        }
        return values;
    }

    private static string Clip(string? value, int max)
    {
        var v = (value ?? "").Trim();
        return v.Length <= max ? v : v[..max];
    }
}

public class ViewerEquipmentRequest
{
    public string? UniqueId { get; set; }
    public string? Tag { get; set; }
    public string? FamilyType { get; set; }
    public string? EquipmentGuid { get; set; }
    public Dictionary<string, string>? Values { get; set; }
}
