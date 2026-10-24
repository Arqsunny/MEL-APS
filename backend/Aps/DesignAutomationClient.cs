using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace MasterEquipmentApi.Aps;

public record WorkItemStatus(string Status, string? ReportUrl)
{
    public bool IsRunning => Status is "pending" or "inprogress";
    public bool IsSuccess => Status == "success";
}

/// <summary>Design Automation v3 REST client (app bundles, activities, work items).</summary>
public class DesignAutomationClient
{
    private readonly ApsOptions _o;
    private readonly IHttpClientFactory _http;
    private readonly ApsAuth _auth;
    private string? _nickname;

    public DesignAutomationClient(IOptions<ApsOptions> o, IHttpClientFactory http, ApsAuth auth) { _o = o.Value; _http = http; _auth = auth; }

    private async Task<(HttpStatusCode Status, JsonDocument Body)> SendAsync(HttpMethod m, string path, CancellationToken ct, object? body = null)
    {
        var req = new HttpRequestMessage(m, $"{_o.DesignAutomationBaseUrl}{path}");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await _auth.GetTwoLeggedAsync(ApsOptions.ScopeDesignAutomation, ct));
        if (body != null) req.Content = JsonContent.Create(body);
        using var res = await _http.CreateClient().SendAsync(req, ct);
        var text = await res.Content.ReadAsStringAsync(ct);
        return (res.StatusCode, JsonDocument.Parse(string.IsNullOrWhiteSpace(text) ? "{}" : text));
    }

    private static ApsException Fail(string what, HttpStatusCode s, JsonDocument b) =>
        new($"Design Automation {what} failed: {(int)s} {ApsAuth.Trim(b.RootElement.GetRawText())}", (int)s);

    public async Task<string> GetNicknameAsync(CancellationToken ct)
    {
        if (_nickname != null) return _nickname;
        var (s, b) = await SendAsync(HttpMethod.Get, "/forgeapps/me", ct);
        using (b)
        {
            if (s != HttpStatusCode.OK) throw Fail("forgeapps/me", s, b);
            return _nickname = b.RootElement.GetString()!;
        }
    }

    public string FullId(string nickname, string id, string alias) => $"{nickname}.{id}+{alias}";

    // ---- app bundle / activity management (used by `aps-setup`) -------------------------------------------------
    public async Task<int> UpsertAppBundleAsync(int year, string zipPath, CancellationToken ct)
    {
        var id = _o.AppBundleIdFor(year);
        var def = new { id, engine = _o.EngineFor(year), description = $"MEL parameter sync (Revit {year})" };
        var (s, b) = await SendAsync(HttpMethod.Post, "/appbundles", ct, def);
        if (s == HttpStatusCode.Conflict)
        {
            b.Dispose();
            (s, b) = await SendAsync(HttpMethod.Post, $"/appbundles/{id}/versions", ct, new { engine = _o.EngineFor(year), description = $"MEL parameter sync (Revit {year})" });
        }
        using (b)
        {
            if (s is not (HttpStatusCode.OK or HttpStatusCode.Created)) throw Fail("appbundle create/version", s, b);
            var version = b.RootElement.GetProperty("version").GetInt32();
            await UploadBundleAsync(b.RootElement.GetProperty("uploadParameters"), zipPath, ct);
            await SetAliasAsync("appbundles", id, version, ct);
            return version;
        }
    }

    private async Task UploadBundleAsync(JsonElement up, string zipPath, CancellationToken ct)
    {
        var form = new MultipartFormDataContent();
        foreach (var kv in up.GetProperty("formData").EnumerateObject())
            form.Add(new StringContent(kv.Value.GetString() ?? string.Empty), kv.Name);
        // The file part must come last for S3 POST uploads.
        var file = new ByteArrayContent(await File.ReadAllBytesAsync(zipPath, ct));
        file.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        form.Add(file, "file", Path.GetFileName(zipPath));

        using var res = await _http.CreateClient().PostAsync(up.GetProperty("endpointURL").GetString(), form, ct);
        if (!res.IsSuccessStatusCode)
            throw new ApsException($"AppBundle upload failed: {(int)res.StatusCode} {ApsAuth.Trim(await res.Content.ReadAsStringAsync(ct))}", (int)res.StatusCode);
    }

    public async Task<int> UpsertActivityAsync(string nickname, int year, CancellationToken ct)
    {
        var activityId = _o.ActivityIdFor(year);
        var bundleId = _o.AppBundleIdFor(year);
        // No input file: the cloud model is opened from GUIDs in input.json using the adsk3LeggedToken argument.
        // "$(appbundles[<id>].path)" resolves to the unpacked bundle folder.
        var def = new
        {
            id = activityId,
            commandLine = new[] { $"$(engine.path)\\revitcoreconsole.exe /al \"$(appbundles[{bundleId}].path)\"" },
            parameters = new Dictionary<string, object>
            {
                ["inputJson"] = new { verb = "get", description = "Sync payload (MelSync.SyncPayload)", localName = "input.json", required = true },
                ["result"] = new { verb = "put", description = "Per-element results (MelSync.SyncResult)", localName = "result.json", required = false },
            },
            engine = _o.EngineFor(year),
            appbundles = new[] { FullId(nickname, bundleId, _o.Alias) },
            description = "Apply approved MEL parameter changes to a Revit cloud model"
        };
        var (s, b) = await SendAsync(HttpMethod.Post, "/activities", ct, def);
        if (s == HttpStatusCode.Conflict)
        {
            b.Dispose();
            var versionDef = new { def.commandLine, def.parameters, def.engine, def.appbundles, def.description };
            (s, b) = await SendAsync(HttpMethod.Post, $"/activities/{activityId}/versions", ct, versionDef);
        }
        using (b)
        {
            if (s is not (HttpStatusCode.OK or HttpStatusCode.Created)) throw Fail("activity create/version", s, b);
            var version = b.RootElement.GetProperty("version").GetInt32();
            await SetAliasAsync("activities", activityId, version, ct);
            return version;
        }
    }

    private async Task SetAliasAsync(string kind, string id, int version, CancellationToken ct)
    {
        var (s, b) = await SendAsync(HttpMethod.Post, $"/{kind}/{id}/aliases", ct, new { id = _o.Alias, version });
        if (s == HttpStatusCode.Conflict)
        {
            b.Dispose();
            (s, b) = await SendAsync(HttpMethod.Patch, $"/{kind}/{id}/aliases/{_o.Alias}", ct, new { version });
        }
        using (b)
            if (s is not (HttpStatusCode.OK or HttpStatusCode.Created)) throw Fail($"{kind} alias", s, b);
    }

    // ---- work items --------------------------------------------------------------------------------------------
    public async Task<string> SubmitWorkItemAsync(string activityFullId, Dictionary<string, object> arguments, CancellationToken ct)
    {
        var (s, b) = await SendAsync(HttpMethod.Post, "/workitems", ct, new { activityId = activityFullId, arguments });
        using (b)
        {
            if (s is not (HttpStatusCode.OK or HttpStatusCode.Created or HttpStatusCode.Accepted)) throw Fail("work item submit", s, b);
            return b.RootElement.GetProperty("id").GetString()!;
        }
    }

    public async Task<WorkItemStatus> GetWorkItemAsync(string id, CancellationToken ct)
    {
        var (s, b) = await SendAsync(HttpMethod.Get, $"/workitems/{id}", ct);
        using (b)
        {
            if (s != HttpStatusCode.OK) throw Fail("work item status", s, b);
            return new WorkItemStatus(
                b.RootElement.GetProperty("status").GetString() ?? "unknown",
                b.RootElement.TryGetProperty("reportUrl", out var r) ? r.GetString() : null);
        }
    }
}
