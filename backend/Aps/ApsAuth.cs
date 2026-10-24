using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MasterEquipmentApi.Data;
using MasterEquipmentApi.Models;

namespace MasterEquipmentApi.Aps;

public record ApsConnectionInfo(string? Account, string? ConnectedBy, DateTime ConnectedAt);

/// <summary>
/// APS authentication: 2-legged tokens for Design Automation/OSS, and a 3-legged "service account" for Revit cloud models.
/// APS refresh tokens rotate (each refresh returns a new one and invalidates the old), so the newest one is persisted,
/// encrypted with ASP.NET Data Protection, after every refresh. Single instance assumed (see docs/APS.md).
/// </summary>
public class ApsAuth
{
    private readonly ApsOptions _o;
    private readonly IHttpClientFactory _http;
    private readonly IServiceScopeFactory _scopes;
    private readonly IDataProtector _protector;
    private readonly ILogger<ApsAuth> _log;

    private readonly ConcurrentDictionary<string, (string Token, DateTime Expires)> _twoLegged = new();
    private (string Token, DateTime Expires)? _userToken;
    private readonly SemaphoreSlim _userLock = new(1, 1);
    // state -> (who started the connect flow, expiry). One-time use; in memory is fine for a single instance.
    private readonly ConcurrentDictionary<string, (string User, DateTime Expires)> _pendingStates = new();

    public ApsAuth(IOptions<ApsOptions> o, IHttpClientFactory http, IServiceScopeFactory scopes, IDataProtectionProvider dp, ILogger<ApsAuth> log)
    {
        _o = o.Value; _http = http; _scopes = scopes; _log = log;
        _protector = dp.CreateProtector("MEL.Aps.RefreshToken.v1");
    }

    // ---- 2-legged ----------------------------------------------------------------------------------------------
    public async Task<string> GetTwoLeggedAsync(string scope, CancellationToken ct)
    {
        if (_twoLegged.TryGetValue(scope, out var c) && c.Expires > DateTime.UtcNow.AddSeconds(60)) return c.Token;
        var r = await TokenRequestAsync(new() { ["grant_type"] = "client_credentials", ["scope"] = scope }, ct);
        _twoLegged[scope] = (r.AccessToken, DateTime.UtcNow.AddSeconds(r.ExpiresIn));
        return r.AccessToken;
    }

    // ---- 3-legged service account ------------------------------------------------------------------------------
    public async Task<string> GetServiceUserTokenAsync(CancellationToken ct)
    {
        if (_userToken is { } t && t.Expires > DateTime.UtcNow.AddSeconds(60)) return t.Token;

        await _userLock.WaitAsync(ct);
        try
        {
            if (_userToken is { } t2 && t2.Expires > DateTime.UtcNow.AddSeconds(60)) return t2.Token;

            using var scope = _scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<MelDbContext>();
            var conn = await db.ApsConnections.FirstOrDefaultAsync(c => c.Id == "service", ct)
                ?? throw new ApsNotConnectedException("No APS account is connected. A BIM Lead must connect one (POST /api/aps/connect).");

            string refresh;
            try { refresh = _protector.Unprotect(conn.ProtectedRefreshToken); }
            catch (CryptographicException)
            {
                throw new ApsNotConnectedException("The stored APS credential cannot be decrypted (data-protection keys changed). Reconnect the APS account.");
            }

            TokenResponse r;
            try
            {
                r = await TokenRequestAsync(new() { ["grant_type"] = "refresh_token", ["refresh_token"] = refresh, ["scope"] = ApsOptions.ScopeUser }, ct);
            }
            catch (ApsException ex) when (ex.Status is 400 or 401)
            {
                throw new ApsNotConnectedException("APS rejected the stored credential (expired, revoked or already used). Reconnect the APS account.");
            }

            // Persist the rotated refresh token BEFORE using the new access token; losing it would force a reconnect.
            var changed = false;
            if (!string.IsNullOrEmpty(r.RefreshToken))
            {
                conn.ProtectedRefreshToken = _protector.Protect(r.RefreshToken!);
                changed = true;
            }
            // Self-heal: if the account name could not be read when connecting, try again now that we have a fresh token.
            if (string.IsNullOrEmpty(conn.Account) && await TryGetAccountAsync(r.AccessToken, ct) is { } healed)
            {
                conn.Account = healed;
                changed = true;
            }
            if (changed)
            {
                conn.UpdatedAt = DateTime.UtcNow;
                await db.SaveChangesAsync(ct);
            }
            _userToken = (r.AccessToken, DateTime.UtcNow.AddSeconds(r.ExpiresIn));
            return r.AccessToken;
        }
        finally { _userLock.Release(); }
    }

    public async Task<ApsConnectionInfo?> GetConnectionAsync(CancellationToken ct)
    {
        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MelDbContext>();
        var c = await db.ApsConnections.AsNoTracking().FirstOrDefaultAsync(x => x.Id == "service", ct);
        return c == null ? null : new ApsConnectionInfo(c.Account, c.ConnectedBy, c.ConnectedAt);
    }

    public async Task DisconnectAsync(CancellationToken ct)
    {
        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MelDbContext>();
        var c = await db.ApsConnections.FirstOrDefaultAsync(x => x.Id == "service", ct);
        if (c != null) { db.ApsConnections.Remove(c); await db.SaveChangesAsync(ct); }
        _userToken = null;
    }

    // ---- connect flow (authorization code) ---------------------------------------------------------------------
    public string BeginConnect(string user)
    {
        if (string.IsNullOrWhiteSpace(_o.CallbackUrl)) throw new InvalidOperationException("Aps:CallbackUrl is not configured.");
        foreach (var k in _pendingStates.Where(p => p.Value.Expires < DateTime.UtcNow).Select(p => p.Key).ToList()) _pendingStates.TryRemove(k, out _);

        var state = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
        _pendingStates[state] = (user, DateTime.UtcNow.AddMinutes(10));
        var q = new Dictionary<string, string>
        {
            ["response_type"] = "code", ["client_id"] = _o.ClientId, ["redirect_uri"] = _o.CallbackUrl,
            ["scope"] = ApsOptions.ScopeUser, ["state"] = state
        };
        return $"{_o.AuthBaseUrl}/authentication/v2/authorize?" + string.Join("&", q.Select(kv => $"{kv.Key}={Uri.EscapeDataString(kv.Value)}"));
    }

    /// <summary>Completes the flow. Returns the BIM Lead who started it (from the one-time state), or null if the state is invalid.</summary>
    public async Task<string?> CompleteConnectAsync(string code, string state, CancellationToken ct)
    {
        if (!_pendingStates.TryRemove(state, out var pending) || pending.Expires < DateTime.UtcNow) return null;

        var r = await TokenRequestAsync(new() { ["grant_type"] = "authorization_code", ["code"] = code, ["redirect_uri"] = _o.CallbackUrl }, ct);
        if (string.IsNullOrEmpty(r.RefreshToken)) throw new ApsException("APS returned no refresh token.");

        var account = await TryGetAccountAsync(r.AccessToken, ct);

        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MelDbContext>();
        var conn = await db.ApsConnections.FirstOrDefaultAsync(c => c.Id == "service", ct);
        if (conn == null) { conn = new ApsConnection(); db.ApsConnections.Add(conn); }
        conn.ProtectedRefreshToken = _protector.Protect(r.RefreshToken!);
        conn.Account = account;
        conn.ConnectedBy = pending.User;
        conn.ConnectedAt = DateTime.UtcNow;
        conn.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);

        _userToken = (r.AccessToken, DateTime.UtcNow.AddSeconds(r.ExpiresIn));
        return pending.User;
    }

    /// <summary>Best-effort display name (email) of the connected account. Never fails the caller: the name is cosmetic.</summary>
    private async Task<string?> TryGetAccountAsync(string accessToken, CancellationToken ct)
    {
        try
        {
            var req = new HttpRequestMessage(HttpMethod.Get, _o.UserInfoUrl);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            using var res = await _http.CreateClient().SendAsync(req, ct);
            if (!res.IsSuccessStatusCode)
            {
                _log.LogWarning("APS user-info lookup returned {Status}; the connected account's name is unavailable.", (int)res.StatusCode);
                return null;
            }
            using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync(ct));
            foreach (var key in new[] { "email", "preferred_username", "emailId", "userName", "name" })
                if (doc.RootElement.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(v.GetString()))
                    return v.GetString();
            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogWarning(ex, "APS user-info lookup failed; the connected account's name is unavailable.");
            return null;
        }
    }

    // ---- token endpoint ----------------------------------------------------------------------------------------
    private record TokenResponse(string AccessToken, string? RefreshToken, int ExpiresIn);

    private async Task<TokenResponse> TokenRequestAsync(Dictionary<string, string> form, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_o.ClientId) || string.IsNullOrWhiteSpace(_o.ClientSecret))
            throw new ApsException("Aps:ClientId / Aps:ClientSecret are not configured.");

        var req = new HttpRequestMessage(HttpMethod.Post, $"{_o.AuthBaseUrl}/authentication/v2/token") { Content = new FormUrlEncodedContent(form) };
        req.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{_o.ClientId}:{_o.ClientSecret}")));
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        using var res = await _http.CreateClient().SendAsync(req, ct);
        var body = await res.Content.ReadAsStringAsync(ct);
        if (!res.IsSuccessStatusCode)
            throw new ApsException($"APS token request ({form["grant_type"]}) failed: {(int)res.StatusCode} {Trim(body)}", (int)res.StatusCode);

        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        return new TokenResponse(
            root.GetProperty("access_token").GetString()!,
            root.TryGetProperty("refresh_token", out var rt) ? rt.GetString() : null,
            root.TryGetProperty("expires_in", out var ex) ? ex.GetInt32() : 3600);
    }

    internal static string Trim(string s) => s.Length <= 300 ? s : s[..300] + "…";
}
