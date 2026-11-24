using System.Net;
using System.Net.Http.Json;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Time.Testing;

using SupportDesk.Api.Contracts;
using SupportDesk.Api.Security;
using SupportDesk.Domain;
using SupportDesk.Tests.Infrastructure;

namespace SupportDesk.Tests;

[Collection(ApiTestGroup.Name)]
public class SecurityTests(ApiFactory factory)
{
    private const string Password = "CorrectHorse!123";

    // ---- Response headers ----

    [Theory]
    [InlineData("/api/tickets")]
    [InlineData("/health")]
    public async Task Responses_carry_defensive_headers(string url)
    {
        var response = await factory.CreateClient().GetAsync(url);

        Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Equal("DENY", response.Headers.GetValues("X-Frame-Options").Single());
        Assert.Equal("no-referrer", response.Headers.GetValues("Referrer-Policy").Single());
        Assert.Equal("same-origin", response.Headers.GetValues("Cross-Origin-Opener-Policy").Single());
        Assert.Contains("default-src 'none'", response.Headers.GetValues("Content-Security-Policy").Single(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task API_responses_are_never_cached()
    {
        var response = await factory.CreateClient().GetAsync("/api/tickets");

        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
    }

    // ---- Account lockout ----

    private static (WebApplicationFactory<Program> Factory, FakeTimeProvider Clock) WithClock(ApiFactory baseFactory)
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var derived = baseFactory.WithWebHostBuilder(b => b.ConfigureTestServices(services =>
        {
            services.RemoveAll<TimeProvider>();
            services.AddSingleton<TimeProvider>(clock);
        }));
        return (derived, clock);
    }

    private static Task<HttpResponseMessage> TryLogin(HttpClient client, string email, string password) =>
        client.PostAsJsonAsync("/api/auth/login", new LoginRequest(email, password), Session.Json);

    private static async Task FailRepeatedly(HttpClient client, string email, int times)
    {
        for (var i = 0; i < times; i++)
        {
            Assert.Equal(HttpStatusCode.Unauthorized, (await TryLogin(client, email, "wrong-password-!")).StatusCode);
        }
    }

    [Fact]
    public async Task Repeated_failures_lock_the_account_even_for_the_correct_password()
    {
        var admin = await Session.RegisterOrganizationAsync(factory);
        var (derived, clock) = WithClock(factory);
        await using var _ = derived;
        var client = derived.CreateClient();

        await FailRepeatedly(client, admin.Auth.User.Email, 5);

        var correct = await TryLogin(client, admin.Auth.User.Email, Password);
        Assert.Equal(HttpStatusCode.Unauthorized, correct.StatusCode);

        // The lockout message must not reveal that the account is locked.
        var problem = await Session.Read<Microsoft.AspNetCore.Mvc.ProblemDetails>(correct);
        Assert.Equal("The email or password is incorrect.", problem.Detail);

        clock.Advance(TimeSpan.FromMinutes(14));
        Assert.Equal(HttpStatusCode.Unauthorized, (await TryLogin(client, admin.Auth.User.Email, Password)).StatusCode);

        clock.Advance(TimeSpan.FromMinutes(2));
        Assert.Equal(HttpStatusCode.OK, (await TryLogin(client, admin.Auth.User.Email, Password)).StatusCode);
    }

    [Fact]
    public async Task A_successful_sign_in_resets_the_failure_count()
    {
        var admin = await Session.RegisterOrganizationAsync(factory);
        var (derived, _) = WithClock(factory);
        await using var _ = derived;
        var client = derived.CreateClient();

        await FailRepeatedly(client, admin.Auth.User.Email, 4);
        Assert.Equal(HttpStatusCode.OK, (await TryLogin(client, admin.Auth.User.Email, Password)).StatusCode);

        // Four more failures would have crossed the threshold if the first four still counted.
        await FailRepeatedly(client, admin.Auth.User.Email, 4);
        Assert.Equal(HttpStatusCode.OK, (await TryLogin(client, admin.Auth.User.Email, Password)).StatusCode);
    }

    [Fact]
    public async Task Lockout_is_per_account()
    {
        var victim = await Session.RegisterOrganizationAsync(factory);
        var bystander = await Session.RegisterOrganizationAsync(factory);
        var (derived, _) = WithClock(factory);
        await using var _ = derived;
        var client = derived.CreateClient();

        await FailRepeatedly(client, victim.Auth.User.Email, 5);

        Assert.Equal(HttpStatusCode.OK, (await TryLogin(client, bystander.Auth.User.Email, Password)).StatusCode);
    }

    // ---- Password policy ----

    [Theory]
    [InlineData("password123")]
    [InlineData("1234567890")]
    [InlineData("aaaaaaaaaaaa")]
    public async Task Predictable_passwords_are_refused_at_registration(string password)
    {
        var response = await factory.CreateClient().PostAsJsonAsync(
            "/api/auth/register",
            new RegisterOrganizationRequest("Weak Org", "Someone Else", $"weak-{Guid.NewGuid():N}@example.test", password),
            Session.Json);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }

    [Fact]
    public async Task Passwords_containing_the_email_or_name_are_refused()
    {
        var admin = await Session.RegisterOrganizationAsync(factory);

        var byEmail = await admin.Http.PostAsJsonAsync("/api/users", new CreateUserRequest("Some Person", "janedoe@example.test", "xx-janedoe-xx-99", Role.Agent), Session.Json);
        var byName = await admin.Http.PostAsJsonAsync("/api/users", new CreateUserRequest("Jane Doe", "jd@example.test", "Jane-loves-pizza-9", Role.Agent), Session.Json);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, byEmail.StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, byName.StatusCode);
    }

    // ---- Revocation ----

    [Fact]
    public async Task Deactivating_a_user_ends_their_existing_session_immediately()
    {
        var admin = await Session.RegisterOrganizationAsync(factory);
        var agent = await admin.AddUserAsync(Role.Agent);
        Assert.Equal(HttpStatusCode.OK, (await agent.Http.GetAsync("/api/tickets")).StatusCode); // warms the session cache

        (await admin.Http.PutAsJsonAsync($"/api/users/{agent.UserId}/active", new SetUserActiveRequest(false), Session.Json)).EnsureSuccessStatusCode();

        Assert.Equal(HttpStatusCode.Unauthorized, (await agent.Http.GetAsync("/api/tickets")).StatusCode);
    }

    [Fact]
    public async Task A_token_for_a_deleted_or_unknown_user_is_rejected()
    {
        var admin = await Session.RegisterOrganizationAsync(factory);
        var options = Microsoft.Extensions.Options.Options.Create(new SupportDesk.Api.Configuration.JwtOptions
        {
            Key = "integration-test-signing-key-0123456789-abcdef",
        });
        var ghost = new User
        {
            Id = Guid.NewGuid(),
            TenantId = Guid.NewGuid(),
            Email = "ghost@example.test",
            DisplayName = "Ghost",
            PasswordHash = "x",
            Role = Role.Admin,
        };
        var (token, _) = new TokenService(options, TimeProvider.System).Issue(ghost);
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

        // Correctly signed, but nobody by that id exists: signature alone is not enough.
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/tickets")).StatusCode);
        Assert.NotNull(admin);
    }

    // ---- Rate limiting behind a proxy ----

    private sealed class PretendProxyAddress : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) =>
            app =>
            {
                // Every request appears to come from the reverse proxy at 10.1.2.3, as it would inside Docker.
                app.Use((context, continuation) =>
                {
                    context.Connection.RemoteIpAddress = IPAddress.Parse("10.1.2.3");
                    return continuation();
                });
                next(app);
            };
    }

    private WebApplicationFactory<Program> BehindProxy(bool trustProxy) =>
        factory.WithWebHostBuilder(b =>
        {
            b.UseSetting("Throttling:AuthPermitLimit", "3");
            if (trustProxy)
            {
                b.UseSetting("ForwardedHeaders:TrustedNetworks:0", "10.0.0.0/8");
            }

            b.ConfigureTestServices(services => services.AddSingleton<IStartupFilter>(new PretendProxyAddress()));
        });

    private static HttpClient ClientAt(WebApplicationFactory<Program> app, string clientAddress)
    {
        var client = app.CreateClient();
        client.DefaultRequestHeaders.Add("X-Forwarded-For", clientAddress);
        return client;
    }

    [Fact]
    public async Task Behind_a_trusted_proxy_each_real_client_gets_its_own_rate_limit()
    {
        await using var app = BehindProxy(trustProxy: true);
        var noisy = ClientAt(app, "203.0.113.5");
        var quiet = ClientAt(app, "203.0.113.6");

        var noisyStatuses = new List<HttpStatusCode>();
        for (var i = 0; i < 4; i++)
        {
            noisyStatuses.Add((await TryLogin(noisy, "nobody@example.test", "wrong-password")).StatusCode);
        }

        Assert.Equal(HttpStatusCode.TooManyRequests, noisyStatuses[3]);
        Assert.Equal(HttpStatusCode.Unauthorized, (await TryLogin(quiet, "nobody@example.test", "wrong-password")).StatusCode);
    }

    [Fact]
    public async Task Forwarded_addresses_are_ignored_unless_the_proxy_is_trusted()
    {
        await using var app = BehindProxy(trustProxy: false);

        // A client rotating a forged X-Forwarded-For must not get a fresh allowance each time.
        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < 5; i++)
        {
            var client = ClientAt(app, $"198.51.100.{i}");
            statuses.Add((await TryLogin(client, "nobody@example.test", "wrong-password")).StatusCode);
        }

        Assert.Equal(3, statuses.Count(s => s == HttpStatusCode.Unauthorized));
        Assert.Equal(2, statuses.Count(s => s == HttpStatusCode.TooManyRequests));
    }
}
