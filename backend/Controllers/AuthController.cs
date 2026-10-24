using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.IdentityModel.Tokens;
using MasterEquipmentApi.Auth;

namespace MasterEquipmentApi.Controllers;

[ApiController]
[Route("api/[controller]")]
public class AuthController : ControllerBase
{
    private readonly DevTokenOptions _dev;
    public AuthController(DevTokenOptions dev) => _dev = dev;

    /// <summary>Tells the SPA how to sign in. Dev login is only offered when explicitly enabled (Development only).</summary>
    [AllowAnonymous]
    [HttpGet("config")]
    public IActionResult Config() => Ok(new
    {
        mode = _dev.Enabled ? "dev" : "oidc",
        devUsers = _dev.Enabled
            ? _dev.Users.Select(u => new { u.Username, u.DisplayName, u.Roles })
            : null
    });

    /// <summary>Development-only token issuer for the seeded demo users. Returns 404 anywhere else.</summary>
    [AllowAnonymous]
    [HttpPost("dev-login")]
    public IActionResult DevLogin([FromBody] DevLoginRequest req)
    {
        if (!_dev.Enabled) return NotFound();
        var user = _dev.Users.FirstOrDefault(u => u.Username.Equals(req.Username, StringComparison.OrdinalIgnoreCase));
        if (user == null) return Unauthorized(new { error = "Unknown demo user." });

        var claims = new List<Claim>
        {
            new("sub", "dev-" + user.Username),
            new(AuthExtensions.NameClaim, user.Username),
            new("name", user.DisplayName),
        };
        claims.AddRange(user.Roles.Select(r => new Claim(AuthExtensions.RoleClaim, r)));

        var creds = new SigningCredentials(new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_dev.SigningKey)), SecurityAlgorithms.HmacSha256);
        var token = new JwtSecurityToken(_dev.Issuer, _dev.Audience, claims, expires: DateTime.UtcNow.AddHours(8), signingCredentials: creds);
        return Ok(new { token = new JwtSecurityTokenHandler().WriteToken(token) });
    }

    [HttpGet("me")]
    public IActionResult Me() => Ok(new
    {
        id = User.ActorId(),
        username = User.ActorName(),
        displayName = User.FindFirstValue("name"),
        roles = User.FindAll(AuthExtensions.RoleClaim).Select(c => c.Value).ToArray()
    });
}

public record DevLoginRequest(string Username);
