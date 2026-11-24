using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;

using Microsoft.AspNetCore.Mvc;

using SupportDesk.Api.Contracts;
using SupportDesk.Domain;
using SupportDesk.Tests.Infrastructure;

namespace SupportDesk.Tests;

[Collection(ApiTestGroup.Name)]
public class AuthTests(ApiFactory factory)
{
    [Fact]
    public async Task Registering_creates_an_organisation_with_a_working_admin()
    {
        var admin = await Session.RegisterOrganizationAsync(factory, "Initech");

        Assert.Equal("Initech", admin.Auth.Organization);
        Assert.Equal(Role.Admin, admin.Auth.User.Role);

        var me = await admin.GetAsync<AuthResponse>("/api/auth/me");
        Assert.Equal(admin.UserId, me.User.Id);
    }

    [Fact]
    public async Task Registering_the_same_email_twice_conflicts()
    {
        var first = await Session.RegisterOrganizationAsync(factory);

        var response = await factory.CreateClient().PostAsJsonAsync(
            "/api/auth/register",
            new RegisterOrganizationRequest("Another Org", "Someone", first.Auth.User.Email.ToUpperInvariant(), "CorrectHorse!123"),
            Session.Json);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task Invalid_registration_returns_validation_problem_details()
    {
        var response = await factory.CreateClient().PostAsJsonAsync(
            "/api/auth/register",
            new RegisterOrganizationRequest("X", "Y", "not-an-email", "short"),
            Session.Json);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await Session.Read<ValidationProblemDetails>(response);
        Assert.Contains("Password", problem.Errors.Keys);
        Assert.Contains("Email", problem.Errors.Keys);
    }

    [Fact]
    public async Task Wrong_password_and_unknown_email_are_indistinguishable()
    {
        var admin = await Session.RegisterOrganizationAsync(factory);
        var client = factory.CreateClient();

        var wrongPassword = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest(admin.Auth.User.Email, "definitely-wrong"), Session.Json);
        var unknownEmail = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest("nobody@example.test", "definitely-wrong"), Session.Json);

        Assert.Equal(HttpStatusCode.Unauthorized, wrongPassword.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, unknownEmail.StatusCode);
        Assert.Equal(
            (await Session.Read<ProblemDetails>(wrongPassword)).Detail,
            (await Session.Read<ProblemDetails>(unknownEmail)).Detail);
    }

    [Fact]
    public async Task Email_is_case_insensitive_at_login()
    {
        var admin = await Session.RegisterOrganizationAsync(factory);

        var session = await Session.LoginAsync(factory, admin.Auth.User.Email.ToUpperInvariant(), "CorrectHorse!123");

        Assert.Equal(admin.UserId, session.UserId);
    }

    [Fact]
    public async Task Deactivated_users_cannot_sign_in()
    {
        var admin = await Session.RegisterOrganizationAsync(factory);
        var agent = await admin.AddUserAsync(Role.Agent);

        var off = await admin.Http.PutAsJsonAsync($"/api/users/{agent.UserId}/active", new SetUserActiveRequest(false), Session.Json);
        off.EnsureSuccessStatusCode();

        var login = await factory.CreateClient().PostAsJsonAsync("/api/auth/login", new LoginRequest(agent.Auth.User.Email, "CorrectHorse!123"), Session.Json);
        Assert.Equal(HttpStatusCode.Unauthorized, login.StatusCode);
    }

    [Theory]
    [InlineData("/api/tickets")]
    [InlineData("/api/tickets/stats")]
    [InlineData("/api/users")]
    [InlineData("/api/auth/me")]
    public async Task Protected_endpoints_require_a_token(string url)
    {
        var response = await factory.CreateClient().GetAsync(url);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task A_tampered_token_is_rejected()
    {
        var admin = await Session.RegisterOrganizationAsync(factory);
        var token = admin.Token;
        var tampered = token[..^3] + (token[^3] == 'A' ? "BBB" : "AAA");
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tampered);

        var response = await client.GetAsync("/api/tickets");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Sign_in_attempts_are_rate_limited()
    {
        await using var limited = factory.WithWebHostBuilder(b => b.UseSetting("Throttling:AuthPermitLimit", "3"));
        var client = limited.CreateClient();

        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < 6; i++)
        {
            var response = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest("nobody@example.test", "wrong-password"), Session.Json);
            statuses.Add(response.StatusCode);
        }

        Assert.Equal(3, statuses.Count(s => s == HttpStatusCode.Unauthorized));
        Assert.Equal(3, statuses.Count(s => s == HttpStatusCode.TooManyRequests));
    }

    [Fact]
    public async Task Health_endpoint_reports_the_database()
    {
        var response = await factory.CreateClient().GetAsync("/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Healthy", await response.Content.ReadAsStringAsync());
    }
}
