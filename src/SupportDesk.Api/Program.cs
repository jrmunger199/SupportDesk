using System.Text.Json.Serialization;
using System.Threading.RateLimiting;

using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

using Scalar.AspNetCore;

using SupportDesk.Api.Configuration;
using SupportDesk.Api.Controllers;
using SupportDesk.Api.Hubs;
using SupportDesk.Api.Infrastructure;
using SupportDesk.Api.Security;
using SupportDesk.Api.Services;
using SupportDesk.Data;
using SupportDesk.Domain;

// Container health probe: ask the running instance about itself and exit, without starting a second server.
if (args.Contains(HealthProbe.Argument))
{
    Environment.ExitCode = await HealthProbe.RunAsync();
    return;
}

var builder = WebApplication.CreateBuilder(args);
var services = builder.Services;

builder.WebHost.ConfigureKestrel(options =>
{
    options.AddServerHeader = false;              // don't advertise the server software
    options.Limits.MaxRequestBodySize = 1_000_000; // every payload here is small JSON; refuse anything bigger
});

// ---- Configuration (validated at startup, so a missing JWT key fails fast instead of at first login) ----
services.AddOptions<JwtOptions>().BindConfiguration(JwtOptions.SectionName).ValidateDataAnnotations().ValidateOnStart();
services.AddSingleton<IValidateOptions<JwtOptions>, JwtOptionsValidator>();
services.AddOptions<ThrottlingOptions>().BindConfiguration(ThrottlingOptions.SectionName).ValidateDataAnnotations().ValidateOnStart();
services.AddOptions<AccountLockoutOptions>().BindConfiguration(AccountLockoutOptions.SectionName).ValidateDataAnnotations().ValidateOnStart();

// Behind a reverse proxy the socket peer is the proxy, so per-client rate limiting needs the real client address
// from X-Forwarded-For. That header is only believed when it comes from a network listed here; otherwise any client
// could forge it to dodge the limiter. Empty (the default) means the headers are ignored.
services.AddOptions<ForwardedHeadersOptions>().Configure<IConfiguration>((options, config) =>
{
    var trusted = config.GetSection("ForwardedHeaders:TrustedNetworks").Get<string[]>() ?? [];
    if (trusted.Length == 0)
    {
        return;
    }

    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    options.ForwardLimit = 1;
    options.KnownProxies.Clear();
    options.KnownIPNetworks.Clear();
    foreach (var network in trusted)
    {
        options.KnownIPNetworks.Add(System.Net.IPNetwork.Parse(network));
    }
});

services.AddHsts(options =>
{
    options.MaxAge = TimeSpan.FromDays(365);
    options.IncludeSubDomains = true;
});
services.AddMemoryCache();
services.AddSingleton<SessionDirectory>();
services.AddSingleton<HubConnections>();

// ---- Data. The connection string is resolved lazily so test hosts can override it. ----
services.AddSingleton(TimeProvider.System);
services.AddHttpContextAccessor();
services.AddScoped<ITenantContext, HttpTenantContext>();
services.AddDbContext<AppDbContext>((provider, options) =>
{
    var connection = provider.GetRequiredService<IConfiguration>().GetConnectionString("Default")
        ?? throw new InvalidOperationException("ConnectionStrings:Default is not configured.");
    options.UseSqlServer(connection);
});

// ---- Authentication and authorisation ----
services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer();
services.ConfigureOptions<ConfigureJwtBearer>();
services.AddAuthorizationBuilder()
    .AddPolicy(Policies.Staff, p => p.RequireRole(nameof(Role.Agent), nameof(Role.Admin)))
    .AddPolicy(Policies.Admin, p => p.RequireRole(nameof(Role.Admin)));

services.AddScoped<ICurrentUser, CurrentUser>();
services.AddScoped<IPasswordHasher<User>, PasswordHasher<User>>();
services.AddScoped<TokenService>();
services.AddScoped<AuthService>();
services.AddScoped<UserService>();
services.AddScoped<TicketService>();
services.AddScoped<MentionService>();
services.AddScoped<MentionQueryService>();
services.AddScoped<ITicketNotifier, SignalRTicketNotifier>();
services.AddScoped<DemoDataSeeder>();

// ---- Web API ----
services.AddControllers().AddJsonOptions(o => o.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter()));
services.AddSignalR().AddJsonProtocol(o => o.PayloadSerializerOptions.Converters.Add(new JsonStringEnumConverter()));
services.AddProblemDetails();
services.AddExceptionHandler<GlobalExceptionHandler>();
services.AddOpenApi();
services.AddHealthChecks().AddDbContextCheck<AppDbContext>("database");

services.AddCors(options => options.AddDefaultPolicy(policy =>
{
    var origins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? [];
    policy.WithOrigins(origins).AllowAnyHeader().AllowAnyMethod().AllowCredentials(); // credentials are required by SignalR
}));

// Brute-force protection for sign-in and registration: a fixed window per client IP.
services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy(Policies.AuthRateLimit, context =>
        RateLimitPartition.GetFixedWindowLimiter(
            context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = context.RequestServices.GetRequiredService<IOptions<ThrottlingOptions>>().Value.AuthPermitLimit,
                Window = TimeSpan.FromMinutes(1),
            }));
});

var app = builder.Build();

app.UseForwardedHeaders(); // first, so everything after sees the real client address and scheme
app.UseSecurityHeaders();
if (!app.Environment.IsDevelopment())
{
    app.UseHsts(); // only emitted on HTTPS responses; TLS is normally terminated at the proxy, which should set it too
}

app.UseExceptionHandler();
app.UseStatusCodePages();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    app.MapScalarApiReference();
}

app.UseCors();
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();
app.MapHub<TicketHub>(TicketHub.Route, options =>
{
    // Without this a socket opened with a valid token would stay open after the token expires.
    options.CloseOnAuthenticationExpiration = true;
});
app.MapHealthChecks("/health").AllowAnonymous();

await PrepareDatabaseAsync(app);
await app.RunAsync();

static async Task PrepareDatabaseAsync(WebApplication app)
{
    var config = app.Configuration;
    await using var scope = app.Services.CreateAsyncScope();

    if (config.GetValue<bool>("Database:MigrateOnStartup"))
    {
        await scope.ServiceProvider.GetRequiredService<AppDbContext>().Database.MigrateAsync();
    }

    if (config.GetValue<bool>("Seed:DemoData"))
    {
        await scope.ServiceProvider.GetRequiredService<DemoDataSeeder>().SeedAsync(CancellationToken.None);
    }
}

/// <summary>Makes the entry point visible to WebApplicationFactory in the test project.</summary>
public partial class Program;
