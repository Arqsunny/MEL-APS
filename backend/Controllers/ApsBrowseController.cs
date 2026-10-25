using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using MasterEquipmentApi.Aps;
using MasterEquipmentApi.Auth;

namespace MasterEquipmentApi.Controllers;

/// <summary>
/// BIM Lead helper: browse the connected APS account's hubs, projects and Revit cloud models to find the identifiers
/// (region, project GUID, model GUID) needed by PUT /api/models/{id}/cloud-link. Read-only.
/// </summary>
[ApiController]
[Authorize(Policy = MelPolicies.CanReview)]
[Route("api/aps/browse")]
public class ApsBrowseController : ControllerBase
{
    private readonly ApsOptions _o;
    private readonly DataManagementClient _dm;
    private readonly ApsAuth _auth;
    public ApsBrowseController(IOptions<ApsOptions> o, DataManagementClient dm, ApsAuth auth) { _o = o.Value; _dm = dm; _auth = auth; }

    private async Task<IActionResult?> PreconditionAsync(CancellationToken ct)
    {
        if (!_o.IsLive) return Conflict(new { error = "APS is in Mock mode; there is nothing to browse." });
        if (await _auth.GetConnectionAsync(ct) == null) return Conflict(new { error = "No APS account is connected. Connect one first." });
        return null;
    }

    // Upstream problems are reported as 502 with APS's status so a lead can tell "no access" from "our bug".
    private async Task<IActionResult> GuardAsync(Func<Task<IActionResult>> work, CancellationToken ct)
    {
        if (await PreconditionAsync(ct) is { } blocked) return blocked;
        try { return await work(); }
        catch (ApsNotConnectedException ex) { return Conflict(new { error = ex.Message }); }
        catch (ApsException ex) when (ex.Status == 401)
        {
            // The token itself was refused (expired/revoked), which is different from "no access to this project".
            return Conflict(new { error = "APS rejected the connected account's token. Reconnect the APS account and try again.", detail = ex.Message });
        }
        catch (ApsException ex) when (ex.Status == 403)
        {
            return StatusCode(StatusCodes.Status502BadGateway, new { error = "The connected APS account is not allowed to see this (403). Check its ACC project membership and that the app is enabled in Account Admin > Custom Integrations.", detail = ex.Message });
        }
        catch (ApsException ex) { return StatusCode(StatusCodes.Status502BadGateway, new { error = ex.Message }); }
        catch (HttpRequestException ex)
        {
            return StatusCode(StatusCodes.Status502BadGateway, new { error = "Could not reach Autodesk Platform Services. Check the network and try again.", detail = ex.Message });
        }
    }

    [HttpGet("hubs")]
    public Task<IActionResult> Hubs(CancellationToken ct) => GuardAsync(async () => Ok(await _dm.GetHubsAsync(ct)), ct);

    [HttpGet("hubs/{hubId}/projects")]
    public Task<IActionResult> Projects(string hubId, CancellationToken ct) => GuardAsync(async () => Ok(await _dm.GetProjectsAsync(hubId, ct)), ct);

    /// <summary>One folder level, same shape as the APS hubs browser. Omit folderId for the project's top folders.</summary>
    [HttpGet("hubs/{hubId}/projects/{projectId}/contents")]
    public Task<IActionResult> Contents(string hubId, string projectId, [FromQuery] string? folderId, CancellationToken ct) =>
        GuardAsync(async () => Ok(await _dm.GetContentsAsync(hubId, projectId, folderId, ct)), ct);

    /// <summary>Versions of one file. Clicking one opens it in the viewer.</summary>
    [HttpGet("hubs/{hubId}/projects/{projectId}/versions")]
    public Task<IActionResult> Versions(string hubId, string projectId, [FromQuery] string itemId, CancellationToken ct) =>
        GuardAsync(async () =>
        {
            if (string.IsNullOrWhiteSpace(itemId)) return BadRequest(new { error = "itemId is required." });
            return Ok(await _dm.GetVersionsAsync(projectId, itemId, ct));
        }, ct);

    /// <summary>Latest version of a file, so choosing the file itself opens it without listing every version.</summary>
    [HttpGet("hubs/{hubId}/projects/{projectId}/tip")]
    public Task<IActionResult> Tip(string hubId, string projectId, [FromQuery] string itemId, CancellationToken ct) =>
        GuardAsync(async () =>
        {
            if (string.IsNullOrWhiteSpace(itemId)) return BadRequest(new { error = "itemId is required." });
            var tip = await _dm.GetTipAsync(projectId, itemId, ct) ?? throw new ApsException("This file has no version the viewer can open.");
            return Ok(new { id = tip.VersionId, name = tip.Name });
        }, ct);

    /// <summary>Scans a project for Revit cloud models (bounded; see `scan.truncated`). `search` filters by file name.</summary>
    [HttpGet("hubs/{hubId}/projects/{projectId}/models")]
    public Task<IActionResult> Models(string hubId, string projectId, [FromQuery] string? search, CancellationToken ct) => GuardAsync(async () =>
    {
        // A scan can take a while on big projects; it is time-boxed and returns partial results (not an error) when the budget ends.
        var hub = await _dm.GetHubAsync(hubId, ct);
        var scan = await _dm.FindModelsAsync(hubId, projectId, search, ct, TimeSpan.FromSeconds(Math.Max(1, _o.BrowseBudgetSeconds)));
        var region = hub?.Region?.ToUpperInvariant();
        return Ok(new
        {
            hub = hub?.Name,
            // Not all hubs report a region. When null, choose US or EMEA yourself: it is where the project is hosted.
            region,
            regionKnown = region is "US" or "EMEA",
            supportedRevitYears = _o.RevitYears,
            models = scan.Models.Select(m => new
            {
                m.ItemId, m.Name, m.FolderPath, m.ProjectGuid, m.ModelGuid, m.IsComposite, m.LastModified, m.RevitVersion,
                supported = _o.Supports(m.RevitVersion),
                cloudLink = new { region, projectGuid = m.ProjectGuid, modelGuid = m.ModelGuid, revitVersion = m.RevitVersion }
            }),
            scan = new { scan.FoldersVisited, scan.RevitFilesChecked, scan.NotCloudWorkshared, scan.Truncated, scan.Notes }
        });
    }, ct);
}
