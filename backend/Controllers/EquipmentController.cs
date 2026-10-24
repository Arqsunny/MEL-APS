using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using MasterEquipmentApi.Data;

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
}
