using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using MasterEquipmentApi.Aps;
using MasterEquipmentApi.Auth;
using MasterEquipmentApi.Data;
using MasterEquipmentApi.Models;

namespace MasterEquipmentApi.Controllers;

/// <summary>Connects the APS account the system acts as (Revit cloud models need a user context).</summary>
[ApiController]
[Route("api/[controller]")]
public class ApsController : ControllerBase
{
    private readonly ApsOptions _o;
    private readonly ApsAuth _auth;
    private readonly MelDbContext _db;

    public ApsController(IOptions<ApsOptions> o, ApsAuth auth, MelDbContext db) { _o = o.Value; _auth = auth; _db = db; }

    [HttpGet("status")]
    public async Task<IActionResult> Status(CancellationToken ct)
    {
        var c = _o.IsLive ? await _auth.GetConnectionAsync(ct) : null;
        return Ok(new { mode = _o.Mode, connected = c != null, account = c?.Account, connectedBy = c?.ConnectedBy, connectedAt = c?.ConnectedAt, supportedRevitYears = _o.RevitYears });
    }

    /// <summary>Returns the APS sign-in URL; the SPA navigates to it. Only BIM Leads may connect or disconnect.</summary>
    [Authorize(Policy = MelPolicies.CanReview)]
    [HttpPost("connect")]
    public IActionResult Connect()
    {
        if (!_o.IsLive) return Conflict(new { error = "APS is in Mock mode; there is nothing to connect." });
        return Ok(new { url = _auth.BeginConnect(User.ActorName()) });
    }

    /// <summary>
    /// APS redirects the browser here. It carries no bearer token, so the one-time `state` (issued to a signed-in BIM Lead
    /// by /connect) is what authorizes completing the flow.
    /// </summary>
    [AllowAnonymous]
    [HttpGet("callback")]
    public async Task<IActionResult> Callback([FromQuery] string? code, [FromQuery] string? state, [FromQuery] string? error, CancellationToken ct)
    {
        if (!_o.IsLive || string.IsNullOrEmpty(code) || string.IsNullOrEmpty(state) || !string.IsNullOrEmpty(error))
            return Redirect("/?aps=error");
        try
        {
            var who = await _auth.CompleteConnectAsync(code, state, ct);
            if (who == null) return Redirect("/?aps=error");
            _db.AuditLog.Add(new AuditEntry { Actor = who, Action = "ApsConnected", ModelGuid = "*", EquipmentUniqueId = "-" });
            await _db.SaveChangesAsync(ct);
            return Redirect("/?aps=connected");
        }
        catch (ApsException)
        {
            return Redirect("/?aps=error");
        }
    }

    [Authorize(Policy = MelPolicies.CanReview)]
    [HttpDelete("connection")]
    public async Task<IActionResult> Disconnect(CancellationToken ct)
    {
        await _auth.DisconnectAsync(ct);
        _db.AuditLog.Add(new AuditEntry { Actor = User.ActorName(), ActorId = User.ActorId(), Action = "ApsDisconnected", ModelGuid = "*", EquipmentUniqueId = "-" });
        await _db.SaveChangesAsync(ct);
        return NoContent();
    }
}
