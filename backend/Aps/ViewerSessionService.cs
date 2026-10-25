using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MasterEquipmentApi.Data;

namespace MasterEquipmentApi.Aps;

public record ViewerSession(string DocumentUrn, string AccessToken, int ExpiresIn, string Region, string Name);

/// <summary>
/// Builds what the APS Viewer needs for the linked cloud model: the published version URN and a short-lived
/// access token for the connected account. The file id is remembered after the first lookup.
/// </summary>
public class ViewerSessionService
{
    private readonly IServiceScopeFactory _scopes;
    private readonly DataManagementClient _dm;
    private readonly ApsAuth _auth;
    private readonly ApsOptions _o;
    private readonly IHttpClientFactory _http;
    private readonly ILogger<ViewerSessionService> _log;
    // One folder walk per model, shared by every viewer request, and not cancelled when the browser retries.
    private readonly ConcurrentDictionary<string, Task> _resolve = new();

    public ViewerSessionService(IServiceScopeFactory scopes, DataManagementClient dm, ApsAuth auth, IOptions<ApsOptions> o, IHttpClientFactory http, ILogger<ViewerSessionService> log)
    {
        _scopes = scopes; _dm = dm; _auth = auth; _o = o.Value; _http = http; _log = log;
    }

    public async Task<ViewerSession> CreateAsync(string modelGuid, CancellationToken ct)
    {
        if (!_o.IsLive) throw new ApsException("APS is in Mock mode; there is no model to view.");
        if (await _auth.GetConnectionAsync(ct) == null)
            throw new ApsNotConnectedException("No APS account is connected. Connect one first.");

        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MelDbContext>();
        var model = await db.Models.FirstOrDefaultAsync(m => m.ModelGuid == modelGuid, ct)
            ?? throw new ApsException("Unknown model.");
        if (!model.IsLinked)
            throw new ApsException("This model is not linked to a Revit cloud model. Use Link model first.");

        if (string.IsNullOrEmpty(model.AccProjectId) || string.IsNullOrEmpty(model.AccItemId))
        {
            _log.LogInformation("Resolving the ACC file for cloud model {CloudModel}", model.CloudModelGuid);
            await ResolveFileAsync(model.ModelGuid, model.CloudModelGuid!).WaitAsync(ct);
            await db.Entry(model).ReloadAsync(ct);
            if (string.IsNullOrEmpty(model.AccProjectId) || string.IsNullOrEmpty(model.AccItemId))
                throw new ApsException("Could not find this linked model in the connected account. Open Link model, search for the file name, and link it again.");
        }

        var tip = await _dm.GetTipAsync(model.AccProjectId!, model.AccItemId!, ct)
            ?? throw new ApsException("The linked file has no version the viewer can open.");
        if (!string.IsNullOrEmpty(tip.ModelGuid)
            && !string.Equals(tip.ModelGuid, model.CloudModelGuid, StringComparison.OrdinalIgnoreCase))
            throw new ApsException("The stored file is a different Revit model than the one that is linked. Link the model again.");

        var urn = ToUrlSafeBase64(tip.VersionId);
        await EnsureViewableAsync(model.Region, urn, ct);

        var (token, expires) = await _auth.GetViewerAccessAsync(ct);
        return new ViewerSession("urn:" + urn, token, expires, model.Region ?? "US", tip.Name ?? model.DisplayName);
    }

    /// <summary>Finds the ACC file and stores its ids. Shared across requests and not cancelled when one browser gives up.</summary>
    private Task ResolveFileAsync(string modelGuid, string cloudModelGuid) =>
        _resolve.GetOrAdd(modelGuid, _ => StoreResolvedFileAsync(modelGuid, cloudModelGuid));

    private async Task StoreResolvedFileAsync(string modelGuid, string cloudModelGuid)
    {
        try
        {
            var found = await _dm.FindByCloudModelAsync(cloudModelGuid, CancellationToken.None)
                ?? throw new ApsException("Could not find this linked model in the connected account. Open Link model, search for the file name, and link it again.");
            using var scope = _scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<MelDbContext>();
            var model = await db.Models.FirstOrDefaultAsync(m => m.ModelGuid == modelGuid)
                ?? throw new ApsException("Unknown model.");
            model.AccHubId = found.HubId;
            model.AccProjectId = found.ProjectId;
            model.AccItemId = found.Item.ItemId;
            if (model.RevitVersion is null) model.RevitVersion = found.Item.RevitVersion;
            await db.SaveChangesAsync();
        }
        catch
        {
            _resolve.TryRemove(modelGuid, out _);
            throw;
        }
    }

    /// <summary>URL-safe base64 of the version URN, without padding. The viewer document id is "urn:" plus this.</summary>
    internal static string ToUrlSafeBase64(string value) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes(value)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private async Task EnsureViewableAsync(string? region, string urn, CancellationToken ct)
    {
        var encoded = Uri.EscapeDataString(urn);
        var path = string.Equals(region, "EMEA", StringComparison.OrdinalIgnoreCase)
            ? $"/modelderivative/v2/regions/eu/designdata/{encoded}/manifest"
            : $"/modelderivative/v2/designdata/{encoded}/manifest";
        var req = new HttpRequestMessage(HttpMethod.Get, _o.AuthBaseUrl.TrimEnd('/') + path);
        var (token, _) = await _auth.GetViewerAccessAsync(ct);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using var res = await _http.CreateClient().SendAsync(req, ct);
        var body = await res.Content.ReadAsStringAsync(ct);
        if (res.StatusCode == HttpStatusCode.NotFound)
            throw new ApsException("This model has no published viewable yet. Publish it from Revit, then try again.");
        if (res.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            throw new ApsNotConnectedException("APS refused the viewable. Reconnect the APS account so it grants viewables:read, then reload.");
        if (!res.IsSuccessStatusCode)
            throw new ApsException($"The viewable manifest could not be read: {(int)res.StatusCode} {ApsAuth.Trim(body)}", (int)res.StatusCode);

        using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body);
        var status = doc.RootElement.TryGetProperty("status", out var s) ? s.GetString() : null;
        if (string.Equals(status, "failed", StringComparison.OrdinalIgnoreCase))
            throw new ApsException("The published viewable failed to translate. Republish the model from Revit and try again.");
    }
}
