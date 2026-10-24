using Microsoft.AspNetCore.Mvc;
using MasterEquipmentApi.Models;

namespace MasterEquipmentApi.Controllers;

[ApiController]
[Route("api/[controller]")]
public class ParametersController : ControllerBase
{
    [HttpGet]
    public IActionResult GetAll() => Ok(ParameterCatalog.All);
}
