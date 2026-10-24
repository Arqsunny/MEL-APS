using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;

namespace MasterEquipmentApi.Auth;

public static class MelRoles
{
    public const string Contributor = "Contributor";
    public const string BimLead = "BimLead";
}

public static class MelPolicies
{
    /// <summary>May propose parameter changes (Contributor or BIM Lead).</summary>
    public const string CanStage = "CanStage";
    /// <summary>May approve, reject and dispatch (BIM Lead only).</summary>
    public const string CanReview = "CanReview";
}

public class DevUser
{
    public string Username { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string[] Roles { get; set; } = Array.Empty<string>();
}

public class DevTokenOptions
{
    public bool Enabled { get; set; }
    public string Issuer { get; set; } = "mel-dev";
    public string Audience { get; set; } = "mel-api";
    public string SigningKey { get; set; } = string.Empty;
    public List<DevUser> Users { get; set; } = new();
}

public static class AuthExtensions
{
    public const string NameClaim = "preferred_username";
    public const string RoleClaim = "roles";   // Entra ID app roles arrive in this claim

    public static string ActorName(this ClaimsPrincipal u) =>
        u.FindFirstValue(NameClaim) ?? u.FindFirstValue("name") ?? "unknown";

    /// <summary>Immutable subject of the signed-in user (sub, or Entra's oid).</summary>
    public static string? ActorId(this ClaimsPrincipal u) =>
        u.FindFirstValue("sub") ?? u.FindFirstValue("oid");

    /// <summary>
    /// Bearer-token authentication. Production: set Auth:Authority + Auth:Audience (any OIDC provider, e.g. Entra ID with
    /// app roles "Contributor"/"BimLead"). Development: Auth:DevTokens issues local tokens through the same validation path.
    /// Secure by default: with neither configured the app refuses to start.
    /// </summary>
    public static DevTokenOptions AddMelAuth(this WebApplicationBuilder builder)
    {
        var section = builder.Configuration.GetSection("Auth");
        var dev = section.GetSection("DevTokens").Get<DevTokenOptions>() ?? new DevTokenOptions();

        if (dev.Enabled && !builder.Environment.IsDevelopment())
            throw new InvalidOperationException("Auth:DevTokens:Enabled is only allowed in the Development environment.");
        if (dev.Enabled && Encoding.UTF8.GetByteCount(dev.SigningKey) < 32)
            throw new InvalidOperationException("Auth:DevTokens:SigningKey must be at least 32 bytes. Run tools/init-dev-secrets.ps1 (stores a random key in user secrets).");

        // Validate eagerly so a misconfigured deployment fails at startup, not on the first request.
        var authority = section["Authority"];
        var audience = section["Audience"];
        if (!dev.Enabled && (string.IsNullOrWhiteSpace(authority) || string.IsNullOrWhiteSpace(audience)))
            throw new InvalidOperationException(
                "Authentication is not configured. Set Auth:Authority and Auth:Audience (OIDC), or enable Auth:DevTokens in Development.");

        builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(o =>
        {
            o.MapInboundClaims = false;
            o.TokenValidationParameters.NameClaimType = NameClaim;
            o.TokenValidationParameters.RoleClaimType = RoleClaim;
            o.TokenValidationParameters.ClockSkew = TimeSpan.FromSeconds(30);

            if (dev.Enabled)
            {
                o.TokenValidationParameters.ValidIssuer = dev.Issuer;
                o.TokenValidationParameters.ValidAudience = dev.Audience;
                o.TokenValidationParameters.IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(dev.SigningKey));
            }
            else
            {
                o.Authority = authority;
                o.Audience = audience;
            }
        });

        builder.Services.AddAuthorization(o =>
        {
            // Everything requires a signed-in user unless explicitly [AllowAnonymous].
            o.FallbackPolicy = new Microsoft.AspNetCore.Authorization.AuthorizationPolicyBuilder()
                .RequireAuthenticatedUser().Build();
            o.AddPolicy(MelPolicies.CanStage, p => p.RequireRole(MelRoles.Contributor, MelRoles.BimLead));
            o.AddPolicy(MelPolicies.CanReview, p => p.RequireRole(MelRoles.BimLead));
        });

        builder.Services.AddSingleton(dev);
        return dev;
    }
}
