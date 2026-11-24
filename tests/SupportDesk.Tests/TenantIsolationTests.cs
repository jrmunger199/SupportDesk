using System.Net;
using System.Net.Http.Json;

using Microsoft.EntityFrameworkCore;

using SupportDesk.Api.Contracts;
using SupportDesk.Data;
using SupportDesk.Domain;
using SupportDesk.Tests.Infrastructure;

namespace SupportDesk.Tests;

/// <summary>The most important tests in the project: one organisation must never see or touch another's data.</summary>
[Collection(ApiTestGroup.Name)]
public class TenantIsolationTests(ApiFactory factory)
{
    [Fact]
    public async Task Tickets_are_invisible_across_organisations_on_every_endpoint()
    {
        var acme = await Session.RegisterOrganizationAsync(factory);
        var globex = await Session.RegisterOrganizationAsync(factory);
        var secret = await acme.CreateTicketAsync("Acme's confidential ticket");

        var list = await globex.GetAsync<PagedResult<TicketSummaryDto>>("/api/tickets");
        Assert.DoesNotContain(list.Items, t => t.Id == secret.Id);

        Assert.Equal(HttpStatusCode.NotFound, (await globex.Http.GetAsync($"/api/tickets/{secret.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await globex.PatchTicketAsync(secret, status: TicketStatus.Closed)).StatusCode);

        var comment = await globex.Http.PostAsJsonAsync($"/api/tickets/{secret.Id}/comments", new AddCommentRequest("hi"), Session.Json);
        Assert.Equal(HttpStatusCode.NotFound, comment.StatusCode);

        var search = await globex.GetAsync<PagedResult<TicketSummaryDto>>("/api/tickets?search=confidential");
        Assert.Equal(0, search.Total);
    }

    [Fact]
    public async Task Stats_only_count_the_callers_own_organisation()
    {
        var acme = await Session.RegisterOrganizationAsync(factory);
        var globex = await Session.RegisterOrganizationAsync(factory);
        await acme.CreateTicketAsync();
        await acme.CreateTicketAsync();

        var stats = await globex.GetAsync<TicketStatsDto>("/api/tickets/stats");

        Assert.All(stats.ByStatus.Values, count => Assert.Equal(0, count));
    }

    [Fact]
    public async Task Users_are_not_listed_or_assignable_across_organisations()
    {
        var acme = await Session.RegisterOrganizationAsync(factory);
        var globex = await Session.RegisterOrganizationAsync(factory);
        var globexTicket = await globex.CreateTicketAsync();

        var acmeUsers = await acme.GetAsync<List<UserDto>>("/api/users");
        Assert.DoesNotContain(acmeUsers, u => u.Id == globex.UserId);

        var assign = await globex.PatchTicketAsync(globexTicket, assignee: acme.UserId);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, assign.StatusCode);

        var deactivate = await acme.Http.PutAsJsonAsync($"/api/users/{globex.UserId}/active", new SetUserActiveRequest(false), Session.Json);
        Assert.Equal(HttpStatusCode.NotFound, deactivate.StatusCode);
    }

    [Fact]
    public async Task Emails_are_unique_across_organisations()
    {
        var acme = await Session.RegisterOrganizationAsync(factory);
        var globex = await Session.RegisterOrganizationAsync(factory);

        var response = await globex.Http.PostAsJsonAsync(
            "/api/users",
            new CreateUserRequest("Copycat", acme.Auth.User.Email, "CorrectHorse!123", Role.Agent),
            Session.Json);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task A_context_with_no_tenant_sees_nothing()
    {
        var acme = await Session.RegisterOrganizationAsync(factory);
        await acme.CreateTicketAsync();

        await using var db = NewContext(tenant: null);

        Assert.Equal(0, await db.Tickets.CountAsync());
        Assert.Equal(0, await db.Users.CountAsync());
        Assert.True(await db.Tickets.IgnoreQueryFilters().AnyAsync()); // the data exists; the filter is what hides it
    }

    [Fact]
    public async Task A_context_for_one_tenant_cannot_read_another_tenants_rows()
    {
        var acme = await Session.RegisterOrganizationAsync(factory);
        var globex = await Session.RegisterOrganizationAsync(factory);
        var ticket = await acme.CreateTicketAsync();
        var globexTenantId = await TenantIdOfAsync(globex.UserId);

        await using var db = NewContext(globexTenantId);

        Assert.False(await db.Tickets.AnyAsync(t => t.Id == ticket.Id));
    }

    [Fact]
    public async Task Saving_an_entity_that_belongs_to_another_tenant_is_refused()
    {
        var acme = await Session.RegisterOrganizationAsync(factory);
        var globex = await Session.RegisterOrganizationAsync(factory);
        var ticket = await acme.CreateTicketAsync();
        var acmeTenantId = await TenantIdOfAsync(acme.UserId);
        var globexTenantId = await TenantIdOfAsync(globex.UserId);

        await using var db = NewContext(globexTenantId);
        db.Comments.Add(new Comment { TenantId = acmeTenantId, TicketId = ticket.Id, AuthorId = globex.UserId, Body = "smuggled" });

        await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task Writing_without_any_tenant_requires_an_explicit_tenant_id()
    {
        await using var db = NewContext(tenant: null);
        db.Tenants.Add(new Tenant { Name = "Fine: tenants themselves are not tenant-owned" });
        db.Users.Add(new User { Email = "x@example.test", DisplayName = "X", PasswordHash = "x" }); // TenantId left empty

        await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
    }

    private AppDbContext NewContext(Guid? tenant) =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseSqlServer(factory.ConnectionString).Options, new FixedTenant(tenant), TimeProvider.System);

    private async Task<Guid> TenantIdOfAsync(Guid userId)
    {
        await using var db = NewContext(tenant: null);
        return await db.Users.IgnoreQueryFilters().Where(u => u.Id == userId).Select(u => u.TenantId).SingleAsync();
    }

    private sealed class FixedTenant(Guid? tenantId) : ITenantContext
    {
        public Guid? TenantId => tenantId;
    }
}
