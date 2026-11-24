using System.Security.Claims;
using System.Text;

using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

using SupportDesk.Api.Configuration;
using SupportDesk.Data;
using SupportDesk.Domain;

namespace SupportDesk.Api.Security;

public static class ClaimNames
{
    public const string Subject = "sub";
    public const string Email = "email";
    public const string Name = "name";
    public const string Role = "role";
    public const string TenantId = "tenant_id";
}

public static class PrincipalExtensions
{
    public static Guid? TryGetTenantId(this ClaimsPrincipal principal) =>
        Guid.TryParse(principal.FindFirstValue(ClaimNames.TenantId), out var id) ? id : null;

    public static Guid GetUserId(this ClaimsPrincipal principal) =>
        Guid.TryParse(principal.FindFirstValue(ClaimNames.Subject), out var id)
            ? id
            : throw new InvalidOperationException("The token has no valid subject claim.");

    public static Guid GetTenantId(this ClaimsPrincipal principal) =>
        principal.TryGetTenantId() ?? throw new InvalidOperationException("The token has no tenant claim.");

    public static Role GetRole(this ClaimsPrincipal principal) =>
        Enum.TryParse<Role>(principal.FindFirstValue(ClaimNames.Role), out var role)
            ? role
            : throw new InvalidOperationException("The token has no valid role claim.");

    public static string GetDisplayName(this ClaimsPrincipal principal) =>
        principal.FindFirstValue(ClaimNames.Name) ?? "Unknown";
}

/// <summary>Reads the tenant from the caller's token. Anonymous callers have none, so tenant-scoped queries return nothing.</summary>
public sealed class HttpTenantContext(IHttpContextAccessor accessor) : ITenantContext
{
    public Guid? TenantId => accessor.HttpContext?.User.TryGetTenantId();
}

public interface ICurrentUser
{
    Guid Id { get; }

    Guid TenantId { get; }

    Role Role { get; }

    string DisplayName { get; }

    bool IsStaff { get; }
}

public sealed class CurrentUser(IHttpContextAccessor accessor) : ICurrentUser
{
    private System.Security.Claims.ClaimsPrincipal Principal =>
        accessor.HttpContext?.User ?? throw new InvalidOperationException("There is no active request.");

    public Guid Id => Principal.GetUserId();

    public Guid TenantId => Principal.GetTenantId();

    public Role Role => Principal.GetRole();

    public string DisplayName => Principal.GetDisplayName();

    public bool IsStaff => Role.IsStaff();
}

public sealed class TokenService(IOptions<JwtOptions> options, TimeProvider clock)
{
    public (string Token, DateTime ExpiresUtc) Issue(User user)
    {
        var jwt = options.Value;
        var now = clock.GetUtcNow().UtcDateTime;
        var expires = now.AddMinutes(jwt.ExpiryMinutes);

        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = jwt.Issuer,
            Audience = jwt.Audience,
            IssuedAt = now,
            NotBefore = now,
            Expires = expires,
            Claims = new Dictionary<string, object>
            {
                [ClaimNames.Subject] = user.Id.ToString(),
                [ClaimNames.Email] = user.Email,
                [ClaimNames.Name] = user.DisplayName,
                [ClaimNames.Role] = user.Role.ToString(),
                [ClaimNames.TenantId] = user.TenantId.ToString(),
            },
            SigningCredentials = new SigningCredentials(SigningKey(jwt), SecurityAlgorithms.HmacSha256),
        };

        return (new JsonWebTokenHandler().CreateToken(descriptor), expires);
    }

    internal static SymmetricSecurityKey SigningKey(JwtOptions jwt) => new(Encoding.UTF8.GetBytes(jwt.Key));
}

/// <summary>
/// Configures JWT validation from <see cref="JwtOptions"/> at resolve time, so the key can come from any
/// configuration source (including values a test host injects after Program has run).
/// </summary>
public sealed class ConfigureJwtBearer(IOptions<JwtOptions> jwt) : IConfigureNamedOptions<JwtBearerOptions>
{
    public void Configure(string? name, JwtBearerOptions options)
    {
        if (name != JwtBearerDefaults.AuthenticationScheme)
        {
            return;
        }

        var settings = jwt.Value;
        options.MapInboundClaims = false;
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = settings.Issuer,
            ValidateAudience = true,
            ValidAudience = settings.Audience,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = TokenService.SigningKey(settings),
            ValidateLifetime = true,
            ClockSkew = TimeSpan.FromSeconds(30),
            NameClaimType = ClaimNames.Name,
            RoleClaimType = ClaimNames.Role,
        };

        // Browsers cannot set headers on WebSocket upgrades, so SignalR passes the token in the query string.
        options.Events = new JwtBearerEvents
        {
            OnTokenValidated = RejectInactiveSessionAsync,
            OnMessageReceived = context =>
            {
                var token = context.Request.Query["access_token"];
                if (!string.IsNullOrEmpty(token) && context.HttpContext.Request.Path.StartsWithSegments("/hubs"))
                {
                    context.Token = token;
                }

                return Task.CompletedTask;
            },
        };
    }

    public void Configure(JwtBearerOptions options) => Configure(JwtBearerDefaults.AuthenticationScheme, options);

    /// <summary>
    /// A valid signature only proves the token was once issued. Re-check that the account is still active and that the
    /// role and tenant in the token still match, so deactivating someone takes effect without waiting for expiry.
    /// </summary>
    private static async Task RejectInactiveSessionAsync(TokenValidatedContext context)
    {
        if (context.Principal is not { } principal)
        {
            context.Fail("No principal.");
            return;
        }

        Guid userId;
        Role role;
        Guid tenantId;
        try
        {
            userId = principal.GetUserId();
            role = principal.GetRole();
            tenantId = principal.GetTenantId();
        }
        catch (InvalidOperationException)
        {
            context.Fail("The token is missing required claims.");
            return;
        }

        var services = context.HttpContext.RequestServices;
        var db = services.GetRequiredService<AppDbContext>();

        SessionInfo? session;
        try
        {
            // The principal is not attached to the request yet, so the tenant filters would hide everything; look across tenants.
            session = await services.GetRequiredService<SessionDirectory>().GetAsync(
                userId,
                () => db.Users.IgnoreQueryFilters()
                    .Where(u => u.Id == userId)
                    .Select(u => new SessionInfo(u.IsActive, u.Role, u.TenantId))
                    .SingleOrDefaultAsync(context.HttpContext.RequestAborted));
        }
        catch (OperationCanceledException) when (context.HttpContext.RequestAborted.IsCancellationRequested)
        {
            // The client hung up (or the host is stopping) mid-request. Nobody is left to answer, and treating it as an
            // error would let anyone generate error-level log noise just by disconnecting at the right moment.
            context.Fail("The request was cancelled.");
            return;
        }

        if (session is not { IsActive: true } || session.Role != role || session.TenantId != tenantId)
        {
            context.Fail("The session is no longer valid.");
        }
    }
}
