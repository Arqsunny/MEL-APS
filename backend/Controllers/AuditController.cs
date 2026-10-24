using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using MasterEquipmentApi.Data;

namespace MasterEquipmentApi.Controllers;

[ApiController]
[Route("api/[controller]")]
public class AuditController : ControllerBase
{
    private readonly MelDbContext _db;
    public AuditController(MelDbContext db) => _db = db;

    /// <summary>Audit trail for a model, newest first. Optionally narrowed to one element or one sync batch.</summary>
    [HttpGet("{modelGuid}")]
    public async Task<IActionResult> Get(string modelGuid, [FromQuery] string? equipmentUniqueId = null,
        [FromQuery] Guid? batchId = null, [FromQuery] int limit = 200)
    {
        var q = _db.AuditLog.AsNoTracking().Where(a => a.ModelGuid == modelGuid);
        if (!string.IsNullOrWhiteSpace(equipmentUniqueId)) q = q.Where(a => a.EquipmentUniqueId == equipmentUniqueId);
        if (batchId != null) q = q.Where(a => a.BatchId == batchId);
        return Ok(await q.OrderByDescending(a => a.Id).Take(Math.Clamp(limit, 1, 1000)).ToListAsync());
    }
}
