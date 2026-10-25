using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace MasterEquipmentApi.Aps;

public record HubInfo(string Id, string Name, string? Region);
public record ProjectInfo(string Id, string Name);
public record CloudModelInfo(string ItemId, string Name, string FolderPath, string ProjectGuid, string ModelGuid, bool? IsComposite, string? LastModified, int? RevitVersion);
public record ModelScan(List<CloudModelInfo> Models, int FoldersVisited, int RevitFilesChecked, int NotCloudWorkshared, bool Truncated, List<string> Notes);
public record ResolvedCloudFile(string HubId, string ProjectId, CloudModelInfo Item);
public record ModelVersionTip(string VersionId, string? Name, string? ModelGuid, string? ProjectGuid, int? RevitVersion);
public record FolderEntry(string Id, string Name, bool Folder);
public record ItemVersionInfo(string Id, string Name);

/// <summary>
/// Read-only Data Management API client used to look up the Revit cloud model identifiers (region, project GUID, model GUID)
/// that Design Automation needs. Acts as the connected APS service account.
/// </summary>
public class DataManagementClient
{
    // Bounds for a scan: a project can hold thousands of folders and we make one request per folder/file.
    public const int MaxFolders = 150;
    public const int MaxModels = 100;
    public const int MaxDepth = 8;
    public const int MaxPagesPerList = 20;
    /// <summary>Concurrent requests during a Link-model scan. Kept low: APS answers 429 Quota limit exceeded when folder reads pile up.</summary>
    public const int Concurrency = 2;
    private const int MaxRateLimitRetries = 6;
    private const string CloudWorksharingType = "versions:autodesk.bim360:C4RModel";

    private readonly ApsOptions _o;
    private readonly IHttpClientFactory _http;
    private readonly ApsAuth _auth;

    public DataManagementClient(IOptions<ApsOptions> o, IHttpClientFactory http, ApsAuth auth) { _o = o.Value; _http = http; _auth = auth; }

    private sealed class ForeignLinkException : Exception { public ForeignLinkException(string url) : base(url) { } }

    private async Task<JsonDocument> GetAsync(string pathOrUrl, CancellationToken ct)
    {
        var baseUri = new Uri(_o.DataManagementBaseUrl);
        var uri = pathOrUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase)
            ? new Uri(pathOrUrl)
            : new Uri(baseUri, pathOrUrl);

        // The user's APS token goes on every request, so never follow a link (e.g. pagination) to any other host.
        if (!uri.Scheme.Equals(baseUri.Scheme, StringComparison.OrdinalIgnoreCase) || !uri.Authority.Equals(baseUri.Authority, StringComparison.OrdinalIgnoreCase))
            throw new ForeignLinkException(uri.GetLeftPart(UriPartial.Authority));

        for (var attempt = 0; ; attempt++)
        {
            var req = new HttpRequestMessage(HttpMethod.Get, uri);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await _auth.GetServiceUserTokenAsync(ct));
            using var res = await _http.CreateClient().SendAsync(req, ct);

            // Quota / rate limit. Wait it out inside this request so a reload does not start the walk again immediately.
            if (res.StatusCode == HttpStatusCode.TooManyRequests && attempt < MaxRateLimitRetries)
            {
                var wait = res.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(Math.Min(45, 5 * (attempt + 1)));
                if (wait < TimeSpan.FromSeconds(5)) wait = TimeSpan.FromSeconds(5);
                if (wait > TimeSpan.FromSeconds(45)) wait = TimeSpan.FromSeconds(45);
                await Task.Delay(wait, ct);
                continue;
            }

            var text = await res.Content.ReadAsStringAsync(ct);
            if (res.StatusCode == HttpStatusCode.TooManyRequests)
                throw new ApsException("Autodesk is rate-limiting folder reads. Wait a minute and reload; the file only has to be found once.", 429);
            if (!res.IsSuccessStatusCode)
                throw new ApsException($"Data Management GET {uri.AbsolutePath} failed: {(int)res.StatusCode} {ApsAuth.Trim(text)}", (int)res.StatusCode);
            return JsonDocument.Parse(text);
        }
    }

    /// <summary>
    /// The Revit release the model was last saved in (e.g. 2025). A Revit engine can only safely open a model saved in the same release:
    /// an older engine cannot open a newer model, and a newer engine would upgrade the shared cloud model for everyone.
    /// Read from the version's extension data (`revitProjectVersion`); null when APS does not report it.
    /// </summary>
    private static int? RevitYear(JsonElement version)
    {
        if (!version.TryGetProperty("attributes", out var a) || !a.TryGetProperty("extension", out var x)
            || !x.TryGetProperty("data", out var d) || !d.TryGetProperty("revitProjectVersion", out var v)) return null;
        return v.ValueKind switch
        {
            JsonValueKind.Number when v.TryGetInt32(out var n) => n,
            JsonValueKind.String when int.TryParse(v.GetString(), out var n) => n,
            _ => null
        };
    }

    private static string? Str(JsonElement e, params string[] path)
    {
        foreach (var p in path)
        {
            if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(p, out e)) return null;
        }
        return e.ValueKind == JsonValueKind.String ? e.GetString() : null;
    }

    /// <summary>Follows JSON:API `links.next` for one list, with a page cap. A foreign next-link ends the list and is reported.</summary>
    private async IAsyncEnumerable<JsonElement> PagedAsync(string path, Action<string> note, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        string? next = path;
        for (var page = 0; next != null; page++)
        {
            if (page >= MaxPagesPerList) { note($"Stopped paging {path} after {MaxPagesPerList} pages."); yield break; }

            JsonDocument doc;
            try { doc = await GetAsync(next, ct); }
            catch (ForeignLinkException f)
            {
                note($"Ignored a pagination link to a different host ({f.Message}); the list may be incomplete.");
                yield break;
            }
            using (doc)
            {
                if (doc.RootElement.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
                    foreach (var item in data.EnumerateArray()) yield return item.Clone();
                next = doc.RootElement.TryGetProperty("links", out var links) ? Str(links, "next", "href") : null;
            }
        }
    }

    public async Task<List<HubInfo>> GetHubsAsync(CancellationToken ct)
    {
        var hubs = new List<HubInfo>();
        await foreach (var h in PagedAsync("/project/v1/hubs", _ => { }, ct))
            hubs.Add(new HubInfo(Str(h, "id") ?? "", Str(h, "attributes", "name") ?? "(unnamed hub)", Str(h, "attributes", "region")));
        return hubs;
    }

    public async Task<HubInfo?> GetHubAsync(string hubId, CancellationToken ct)
    {
        using var doc = await GetAsync($"/project/v1/hubs/{Uri.EscapeDataString(hubId)}", ct);
        if (!doc.RootElement.TryGetProperty("data", out var h)) return null;
        return new HubInfo(Str(h, "id") ?? hubId, Str(h, "attributes", "name") ?? hubId, Str(h, "attributes", "region"));
    }

    public async Task<List<ProjectInfo>> GetProjectsAsync(string hubId, CancellationToken ct)
    {
        var list = new List<ProjectInfo>();
        await foreach (var p in PagedAsync($"/project/v1/hubs/{Uri.EscapeDataString(hubId)}/projects", _ => { }, ct))
            list.Add(new ProjectInfo(Str(p, "id") ?? "", Str(p, "attributes", "name") ?? "(unnamed project)"));
        return list;
    }

    private sealed record Folder(string Id, string Path, int Depth);
    private sealed record Candidate(string ItemId, string Name, string FolderPath);

    /// <summary>
    /// Finds Revit Cloud Worksharing models in a project by walking its folders (breadth-first, a few folders at a time, bounded).
    /// Only .rvt files whose latest version is a C4RModel are returned; others are counted, not listed.
    /// If <paramref name="budget"/> runs out, the models found so far are returned and the scan is marked truncated (never an error).
    /// </summary>
    public async Task<ModelScan> FindModelsAsync(string hubId, string projectId, string? search, CancellationToken ct, TimeSpan? budget = null, string? matchModelGuid = null, int concurrency = 0)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (budget is { } b) cts.CancelAfter(b);
        var token = cts.Token;

        var notes = new ConcurrentQueue<string>();
        var models = new ConcurrentBag<CloudModelInfo>();
        int foldersVisited = 0, rvtChecked = 0, notCloud = 0;
        var truncated = false;
        var proj = Uri.EscapeDataString(projectId);
        var popts = new ParallelOptions { MaxDegreeOfParallelism = concurrency > 0 ? concurrency : Concurrency, CancellationToken = token };

        try
        {
            var level = new List<Folder>();
            // Top folders are served by the Projects API (hub + project), not under /data/v1/projects/... (that path 404s).
            await foreach (var f in PagedAsync($"/project/v1/hubs/{Uri.EscapeDataString(hubId)}/projects/{proj}/topFolders", notes.Enqueue, token))
                level.Add(new Folder(Str(f, "id") ?? "", Str(f, "attributes", "displayName") ?? Str(f, "attributes", "name") ?? "?", 0));

            while (level.Count > 0)
            {
                token.ThrowIfCancellationRequested();
                var room = MaxFolders - foldersVisited;
                if (room <= 0) { truncated = true; break; }
                var batch = level.Take(room).ToList();
                if (level.Count > batch.Count) truncated = true;
                foldersVisited += batch.Count;

                var nextLevel = new ConcurrentBag<Folder>();
                var candidates = new ConcurrentBag<Candidate>();

                await Parallel.ForEachAsync(batch, popts, async (folder, t) =>
                {
                    await foreach (var entry in PagedAsync($"/data/v1/projects/{proj}/folders/{Uri.EscapeDataString(folder.Id)}/contents", notes.Enqueue, t))
                    {
                        var type = Str(entry, "type");
                        var name = Str(entry, "attributes", "displayName") ?? Str(entry, "attributes", "name") ?? "?";
                        if (type == "folders")
                        {
                            if (folder.Depth + 1 > MaxDepth) { truncated = true; continue; }
                            nextLevel.Add(new Folder(Str(entry, "id") ?? "", $"{folder.Path}/{name}", folder.Depth + 1));
                        }
                        else if (type == "items" && name.EndsWith(".rvt", StringComparison.OrdinalIgnoreCase)
                                 && (string.IsNullOrWhiteSpace(search) || name.Contains(search, StringComparison.OrdinalIgnoreCase)))
                        {
                            candidates.Add(new Candidate(Str(entry, "id") ?? "", name, folder.Path));
                        }
                    }
                });

                // Inspect each candidate file's latest version (one request each), also a few at a time.
                var toCheck = candidates.OrderBy(c => c.FolderPath).ThenBy(c => c.Name).Take(Math.Max(0, MaxModels - models.Count)).ToList();
                if (candidates.Count > toCheck.Count) truncated = true;
                await Parallel.ForEachAsync(toCheck, popts, async (c, t) =>
                {
                    Interlocked.Increment(ref rvtChecked);
                    using var tip = await GetAsync($"/data/v1/projects/{proj}/items/{Uri.EscapeDataString(c.ItemId)}/tip", t);
                    if (!tip.RootElement.TryGetProperty("data", out var v)) return;

                    var projectGuid = Str(v, "attributes", "extension", "data", "projectGuid");
                    var modelGuid = Str(v, "attributes", "extension", "data", "modelGuid");
                    if (Str(v, "attributes", "extension", "type") != CloudWorksharingType || projectGuid == null || modelGuid == null)
                    {
                        Interlocked.Increment(ref notCloud);   // a plain uploaded RVT, not a Revit cloud model
                        return;
                    }

                    bool? composite = null;
                    if (v.TryGetProperty("attributes", out var a) && a.TryGetProperty("extension", out var x) && x.TryGetProperty("data", out var d)
                        && d.TryGetProperty("isCompositeDesign", out var comp) && comp.ValueKind is JsonValueKind.True or JsonValueKind.False)
                        composite = comp.GetBoolean();

                    models.Add(new CloudModelInfo(c.ItemId, c.Name, c.FolderPath, projectGuid, modelGuid, composite, Str(v, "attributes", "lastModifiedTime"), RevitYear(v)));
                });

                if (matchModelGuid != null && models.Any(m => string.Equals(m.ModelGuid, matchModelGuid, StringComparison.OrdinalIgnoreCase)))
                    break;
                if (models.Count >= MaxModels) { truncated = true; break; }
                level = nextLevel.OrderBy(f => f.Path).ToList();
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested && token.IsCancellationRequested)
        {
            // Our own time budget ran out (not the caller giving up): return what we have, clearly marked incomplete.
            truncated = true;
            notes.Enqueue($"Stopped after {budget?.TotalSeconds:0} seconds with {models.Count} model(s) found so far; the project is large. Type part of the file name and search again.");
        }

        var noteList = notes.Distinct().ToList();
        if (truncated && !noteList.Any(n => n.StartsWith("Stopped after")))
            noteList.Add($"Scan stopped at its limits ({MaxFolders} folders / {MaxModels} models / depth {MaxDepth}). Use the search box to narrow it.");

        return new ModelScan(models.OrderBy(m => m.FolderPath).ThenBy(m => m.Name).ToList(), foldersVisited, rvtChecked, notCloud, truncated, noteList);
    }

    /// <summary>Finds the ACC item for a Revit cloud model GUID by scanning the connected account. Stops at the first hit.</summary>
    public async Task<ResolvedCloudFile?> FindByCloudModelAsync(string cloudModelGuid, CancellationToken ct)
    {
        var truncated = false;
        foreach (var hub in await GetHubsAsync(ct))
        {
            foreach (var project in await GetProjectsAsync(hub.Id, ct))
            {
                // One folder at a time. A parallel walk is what trips APS "Quota limit exceeded" on projects of this size.
                var scan = await FindModelsAsync(hub.Id, project.Id, null, ct, TimeSpan.FromMinutes(4), cloudModelGuid, concurrency: 1);
                var hit = scan.Models.FirstOrDefault(m => string.Equals(m.ModelGuid, cloudModelGuid, StringComparison.OrdinalIgnoreCase));
                if (hit != null) return new ResolvedCloudFile(hub.Id, project.Id, hit);
                if (scan.Truncated) truncated = true;
            }
        }
        if (truncated)
            throw new ApsException("Autodesk rate-limited the folder search before the linked model was found. Wait a minute and reload.");
        return null;
    }

    /// <summary>One level of the hubs browser: top folders when <paramref name="folderId"/> is empty, otherwise that folder's contents.</summary>
    public async Task<List<FolderEntry>> GetContentsAsync(string hubId, string projectId, string? folderId, CancellationToken ct)
    {
        var path = string.IsNullOrWhiteSpace(folderId)
            ? $"/project/v1/hubs/{Uri.EscapeDataString(hubId)}/projects/{Uri.EscapeDataString(projectId)}/topFolders"
            : $"/data/v1/projects/{Uri.EscapeDataString(projectId)}/folders/{Uri.EscapeDataString(folderId)}/contents";
        var list = new List<FolderEntry>();
        await foreach (var entry in PagedAsync(path, _ => { }, ct))
        {
            var type = Str(entry, "type");
            var id = Str(entry, "id");
            if (string.IsNullOrEmpty(id) || type is not ("folders" or "items")) continue;
            var name = Str(entry, "attributes", "displayName") ?? Str(entry, "attributes", "name") ?? id;
            list.Add(new FolderEntry(id, name, type == "folders"));
        }
        return list.OrderByDescending(e => e.Folder).ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>Versions of one item, newest first as APS returns them. The id is what the viewer loads.</summary>
    public async Task<List<ItemVersionInfo>> GetVersionsAsync(string projectId, string itemId, CancellationToken ct)
    {
        var list = new List<ItemVersionInfo>();
        await foreach (var v in PagedAsync($"/data/v1/projects/{Uri.EscapeDataString(projectId)}/items/{Uri.EscapeDataString(itemId)}/versions", _ => { }, ct))
        {
            var id = Str(v, "id");
            if (string.IsNullOrEmpty(id)) continue;
            list.Add(new ItemVersionInfo(id, Str(v, "attributes", "createTime") ?? Str(v, "attributes", "lastModifiedTime") ?? id));
        }
        return list;
    }

    /// <summary>Latest version of an item. Its id is the URN the viewer loads.</summary>
    public async Task<ModelVersionTip?> GetTipAsync(string projectId, string itemId, CancellationToken ct)
    {
        using var doc = await GetAsync($"/data/v1/projects/{Uri.EscapeDataString(projectId)}/items/{Uri.EscapeDataString(itemId)}/tip", ct);
        if (!doc.RootElement.TryGetProperty("data", out var v)) return null;
        var id = Str(v, "id");
        if (string.IsNullOrWhiteSpace(id)) return null;
        return new ModelVersionTip(id, Str(v, "attributes", "displayName") ?? Str(v, "attributes", "name"),
            Str(v, "attributes", "extension", "data", "modelGuid"),
            Str(v, "attributes", "extension", "data", "projectGuid"),
            RevitYear(v));
    }
}
