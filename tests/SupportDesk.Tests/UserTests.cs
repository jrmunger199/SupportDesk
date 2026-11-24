using System.Net;
using System.Net.Http.Json;

using SupportDesk.Api.Contracts;
using SupportDesk.Domain;
using SupportDesk.Tests.Infrastructure;

namespace SupportDesk.Tests;

[Collection(ApiTestGroup.Name)]
public class UserTests(ApiFactory factory)
{
    [Fact]
    public async Task Admins_create_users_who_can_sign_in()
    {
        var admin = await Session.RegisterOrganizationAsync(factory);

        var agent = await admin.AddUserAsync(Role.Agent, "Priya Nair");

        Assert.Equal("Priya Nair", agent.Auth.User.DisplayName);
        Assert.Equal(Role.Agent, agent.Auth.User.Role);
        Assert.Equal(admin.Auth.Organization, agent.Auth.Organization);
    }

    [Fact]
    public async Task Only_admins_can_create_or_deactivate_users()
    {
        var admin = await Session.RegisterOrganizationAsync(factory);
        var agent = await admin.AddUserAsync(Role.Agent);
        var requester = await admin.AddUserAsync(Role.Requester);

        var create = await agent.Http.PostAsJsonAsync("/api/users", new CreateUserRequest("Nope Person", "nope@example.test", "CorrectHorse!123", Role.Agent), Session.Json);
        var deactivate = await agent.Http.PutAsJsonAsync($"/api/users/{requester.UserId}/active", new SetUserActiveRequest(false), Session.Json);

        Assert.Equal(HttpStatusCode.Forbidden, create.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, deactivate.StatusCode);
    }

    [Fact]
    public async Task Staff_can_list_colleagues_but_requesters_cannot()
    {
        var admin = await Session.RegisterOrganizationAsync(factory);
        var agent = await admin.AddUserAsync(Role.Agent);
        var requester = await admin.AddUserAsync(Role.Requester);

        var visible = await agent.GetAsync<List<UserDto>>("/api/users");

        Assert.Equal(3, visible.Count);
        Assert.Equal(HttpStatusCode.Forbidden, (await requester.Http.GetAsync("/api/users")).StatusCode);
    }

    [Fact]
    public async Task Admins_cannot_deactivate_themselves()
    {
        var admin = await Session.RegisterOrganizationAsync(factory);

        var response = await admin.Http.PutAsJsonAsync($"/api/users/{admin.UserId}/active", new SetUserActiveRequest(false), Session.Json);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }

    [Fact]
    public async Task Weak_passwords_are_rejected()
    {
        var admin = await Session.RegisterOrganizationAsync(factory);

        var response = await admin.Http.PostAsJsonAsync("/api/users", new CreateUserRequest("Weak Person", "weak@example.test", "short", Role.Agent), Session.Json);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}
