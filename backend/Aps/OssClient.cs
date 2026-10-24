using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace MasterEquipmentApi.Aps;

/// <summary>Object Storage Service (OSS) v2 via signed S3 URLs: carries work item inputs and results.</summary>
public class OssClient
{
    private readonly ApsOptions _o;
    private readonly IHttpClientFactory _http;
    private readonly ApsAuth _auth;

    public OssClient(IOptions<ApsOptions> o, IHttpClientFactory http, ApsAuth auth) { _o = o.Value; _http = http; _auth = auth; }

    public record SignedUpload(string Url, string UploadKey);

    private async Task<HttpRequestMessage> ReqAsync(HttpMethod m, string path, CancellationToken ct, object? body = null)
    {
        var req = new HttpRequestMessage(m, $"{_o.OssBaseUrl}{path}");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await _auth.GetTwoLeggedAsync(ApsOptions.ScopeOss, ct));
        if (body != null) req.Content = JsonContent.Create(body);
        return req;
    }

    private async Task<JsonDocument> SendJsonAsync(HttpRequestMessage req, CancellationToken ct, params int[] okExtra)
    {
        using var res = await _http.CreateClient().SendAsync(req, ct);
        var text = await res.Content.ReadAsStringAsync(ct);
        if (!res.IsSuccessStatusCode && !okExtra.Contains((int)res.StatusCode))
            throw new ApsException($"OSS {req.Method} {req.RequestUri!.AbsolutePath} failed: {(int)res.StatusCode} {ApsAuth.Trim(text)}", (int)res.StatusCode);
        return JsonDocument.Parse(string.IsNullOrWhiteSpace(text) ? "{}" : text);
    }

    public async Task EnsureBucketAsync(CancellationToken ct)
    {
        // 409 = already exists (fine). "transient" objects expire after 24h, which is right for per-job scratch data.
        using var _ = await SendJsonAsync(await ReqAsync(HttpMethod.Post, "/buckets", ct, new { bucketKey = _o.Bucket, policyKey = "transient" }), ct, 409);
    }

    private string Obj(string key) => $"/buckets/{Uri.EscapeDataString(_o.Bucket)}/objects/{Uri.EscapeDataString(key)}";

    public async Task<SignedUpload> GetSignedUploadAsync(string objectKey, CancellationToken ct)
    {
        using var doc = await SendJsonAsync(await ReqAsync(HttpMethod.Get, $"{Obj(objectKey)}/signeds3upload?parts=1&firstPart=1&minutesExpiration=60", ct), ct);
        var urls = doc.RootElement.GetProperty("urls");
        return new SignedUpload(urls[0].GetString()!, doc.RootElement.GetProperty("uploadKey").GetString()!);
    }

    public async Task CompleteUploadAsync(string objectKey, string uploadKey, CancellationToken ct)
    {
        using var _ = await SendJsonAsync(await ReqAsync(HttpMethod.Post, $"{Obj(objectKey)}/signeds3upload", ct, new { uploadKey }), ct);
    }

    public async Task<string> GetSignedDownloadUrlAsync(string objectKey, int minutes, CancellationToken ct)
    {
        using var doc = await SendJsonAsync(await ReqAsync(HttpMethod.Get, $"{Obj(objectKey)}/signeds3download?minutesExpiration={minutes}", ct), ct);
        return doc.RootElement.GetProperty("url").GetString()!;
    }

    /// <summary>Uploads a small document and returns a signed download URL the activity can read.</summary>
    public async Task<string> PutJsonAsync(string objectKey, string json, CancellationToken ct)
    {
        var up = await GetSignedUploadAsync(objectKey, ct);
        using (var res = await _http.CreateClient().PutAsync(up.Url, new StringContent(json, Encoding.UTF8, "application/json"), ct))
            if (!res.IsSuccessStatusCode) throw new ApsException($"Upload of {objectKey} failed: {(int)res.StatusCode}", (int)res.StatusCode);
        await CompleteUploadAsync(objectKey, up.UploadKey, ct);
        return await GetSignedDownloadUrlAsync(objectKey, 60, ct);
    }

    /// <summary>Downloads an object, or null if it does not exist (e.g. the activity never wrote it).</summary>
    public async Task<string?> TryGetTextAsync(string objectKey, CancellationToken ct)
    {
        string url;
        try { url = await GetSignedDownloadUrlAsync(objectKey, 15, ct); }
        catch (ApsException ex) when (ex.Status == 404) { return null; }
        using var res = await _http.CreateClient().GetAsync(url, ct);
        if (res.StatusCode == System.Net.HttpStatusCode.NotFound) return null;
        if (!res.IsSuccessStatusCode) throw new ApsException($"Download of {objectKey} failed: {(int)res.StatusCode}", (int)res.StatusCode);
        return await res.Content.ReadAsStringAsync(ct);
    }
}
