using System.Net;
using System.Net.Http.Json;

using SupportDesk.Api.Contracts;
using SupportDesk.Domain;
using SupportDesk.Tests.Infrastructure;

namespace SupportDesk.Tests;

[Collection(ApiTestGroup.Name)]
public class TicketTests(ApiFactory factory)
{
    [Fact]
    public async Task Tickets_are_numbered_sequentially_within_each_organisation()
    {
        var acme = await Session.RegisterOrganizationAsync(factory);
        var globex = await Session.RegisterOrganizationAsync(factory);

        var numbers = new List<int>();
        for (var i = 0; i < 3; i++)
        {
            numbers.Add((await acme.CreateTicketAsync()).Number);
        }

        Assert.Equal([1, 2, 3], numbers);
        Assert.Equal(1, (await globex.CreateTicketAsync()).Number);
    }

    [Fact]
    public async Task Concurrent_creates_never_share_a_ticket_number()
    {
        var admin = await Session.RegisterOrganizationAsync(factory);

        var tickets = await Task.WhenAll(Enumerable.Range(0, 20).Select(i => admin.CreateTicketAsync($"Parallel ticket {i}")));

        Assert.Equal(Enumerable.Range(1, 20), tickets.Select(t => t.Number).Order());
    }

    [Fact]
    public async Task A_new_ticket_starts_open_with_a_creation_event()
    {
        var admin = await Session.RegisterOrganizationAsync(factory);

        var ticket = await admin.CreateTicketAsync(priority: TicketPriority.Urgent);

        Assert.Equal(TicketStatus.Open, ticket.Status);
        Assert.Equal(TicketPriority.Urgent, ticket.Priority);
        Assert.Equal(admin.UserId, ticket.Requester.Id);
        Assert.Null(ticket.Assignee);
        Assert.Equal(TicketEventType.Created, Assert.Single(ticket.Events).Type);
        Assert.NotEmpty(ticket.RowVersion);
    }

    [Fact]
    public async Task Timestamps_are_serialised_as_utc_so_browsers_do_not_read_them_as_local_time()
    {
        var admin = await Session.RegisterOrganizationAsync(factory);
        var ticket = await admin.CreateTicketAsync();

        // Read back from the database (not the in-memory entity) to cover SQL Server's Kind=Unspecified values.
        using var document = System.Text.Json.JsonDocument.Parse(await admin.Http.GetStringAsync($"/api/tickets/{ticket.Id}"));

        Assert.EndsWith("Z", document.RootElement.GetProperty("createdUtc").GetString(), StringComparison.Ordinal);
        Assert.EndsWith("Z", document.RootElement.GetProperty("updatedUtc").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Invalid_tickets_are_rejected()
    {
        var admin = await Session.RegisterOrganizationAsync(factory);

        var response = await admin.Http.PostAsJsonAsync("/api/tickets", new CreateTicketRequest("x", "", null), Session.Json);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Requesters_only_see_and_comment_on_their_own_tickets()
    {
        var admin = await Session.RegisterOrganizationAsync(factory);
        var dana = await admin.AddUserAsync(Role.Requester);
        var tom = await admin.AddUserAsync(Role.Requester);
        var danas = await dana.CreateTicketAsync("Dana's ticket");
        var toms = await tom.CreateTicketAsync("Tom's ticket");

        var list = await dana.GetAsync<PagedResult<TicketSummaryDto>>("/api/tickets");
        Assert.Equal([danas.Id], list.Items.Select(t => t.Id));

        Assert.Equal(HttpStatusCode.NotFound, (await dana.Http.GetAsync($"/api/tickets/{toms.Id}")).StatusCode);
        var comment = await dana.Http.PostAsJsonAsync($"/api/tickets/{toms.Id}/comments", new AddCommentRequest("peeking"), Session.Json);
        Assert.Equal(HttpStatusCode.NotFound, comment.StatusCode);

        var staffView = await admin.GetAsync<PagedResult<TicketSummaryDto>>("/api/tickets");
        Assert.Equal(2, staffView.Total);
    }

    [Fact]
    public async Task Requesters_cannot_change_tickets()
    {
        var admin = await Session.RegisterOrganizationAsync(factory);
        var dana = await admin.AddUserAsync(Role.Requester);
        var ticket = await dana.CreateTicketAsync();

        var response = await dana.PatchTicketAsync(ticket, status: TicketStatus.Closed);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Agents_move_tickets_through_the_workflow_and_the_audit_trail_records_it()
    {
        var admin = await Session.RegisterOrganizationAsync(factory);
        var agent = await admin.AddUserAsync(Role.Agent, "Priya Nair");
        var ticket = await admin.CreateTicketAsync();

        var started = await Session.Read<TicketDetailDto>(await agent.PatchTicketAsync(ticket, TicketStatus.InProgress, TicketPriority.High, agent.UserId));
        var resolved = await Session.Read<TicketDetailDto>(await agent.PatchTicketAsync(started, TicketStatus.Resolved));

        Assert.Equal(TicketStatus.Resolved, resolved.Status);
        Assert.Equal(TicketPriority.High, resolved.Priority);
        Assert.Equal(agent.UserId, resolved.Assignee?.Id);
        // Events saved in one request share a timestamp, so assert on content rather than order.
        Assert.Equal(5, resolved.Events.Count);
        Assert.Equal(2, resolved.Events.Count(e => e.Type == TicketEventType.StatusChanged));
        Assert.Contains(resolved.Events, e => e.Type == TicketEventType.PriorityChanged && e.Detail == "Normal -> High");
        Assert.Contains(resolved.Events, e => e.Detail == "Assigned to Priya Nair" && e.Actor.Id == agent.UserId);
        Assert.Equal([TicketStatus.Open, TicketStatus.Closed], resolved.AllowedTransitions);
    }

    [Theory]
    [InlineData(TicketStatus.Resolved, TicketStatus.InProgress)]
    [InlineData(TicketStatus.Closed, TicketStatus.Resolved)]
    public async Task Illegal_status_changes_are_rejected(TicketStatus start, TicketStatus illegal)
    {
        var admin = await Session.RegisterOrganizationAsync(factory);
        var ticket = await admin.CreateTicketAsync();
        if (start != TicketStatus.Open)
        {
            ticket = await Session.Read<TicketDetailDto>(await admin.PatchTicketAsync(ticket, start));
        }

        var response = await admin.PatchTicketAsync(ticket, illegal);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }

    [Fact]
    public async Task A_stale_row_version_is_a_conflict_and_changes_nothing()
    {
        var admin = await Session.RegisterOrganizationAsync(factory);
        var ticket = await admin.CreateTicketAsync();

        var first = await admin.PatchTicketAsync(ticket, priority: TicketPriority.High); // consumes the version
        first.EnsureSuccessStatusCode();
        var second = await admin.PatchTicketAsync(ticket, priority: TicketPriority.Low); // still holds the old one

        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        var current = await admin.GetAsync<TicketDetailDto>($"/api/tickets/{ticket.Id}");
        Assert.Equal(TicketPriority.High, current.Priority);
    }

    [Fact]
    public async Task Two_simultaneous_updates_cannot_both_win()
    {
        var admin = await Session.RegisterOrganizationAsync(factory);
        var ticket = await admin.CreateTicketAsync();

        var results = await Task.WhenAll(
            admin.PatchTicketAsync(ticket, priority: TicketPriority.High),
            admin.PatchTicketAsync(ticket, priority: TicketPriority.Urgent));

        var statuses = results.Select(r => r.StatusCode).Order().ToArray();
        Assert.Equal([HttpStatusCode.OK, HttpStatusCode.Conflict], statuses);
    }

    [Fact]
    public async Task An_unchanged_update_is_a_harmless_no_op()
    {
        var admin = await Session.RegisterOrganizationAsync(factory);
        var ticket = await admin.CreateTicketAsync();

        var response = await admin.PatchTicketAsync(ticket, status: TicketStatus.Open, priority: ticket.Priority);

        response.EnsureSuccessStatusCode();
        var after = await Session.Read<TicketDetailDto>(response);
        Assert.Equal(ticket.RowVersion, after.RowVersion);
        Assert.Single(after.Events);
    }

    [Fact]
    public async Task Assignees_must_be_active_staff()
    {
        var admin = await Session.RegisterOrganizationAsync(factory);
        var requester = await admin.AddUserAsync(Role.Requester);
        var agent = await admin.AddUserAsync(Role.Agent);
        var ticket = await admin.CreateTicketAsync();

        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await admin.PatchTicketAsync(ticket, assignee: requester.UserId)).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await admin.PatchTicketAsync(ticket, assignee: Guid.NewGuid())).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await admin.PatchTicketAsync(ticket, assignee: agent.UserId, clearAssignee: true)).StatusCode);

        var assigned = await Session.Read<TicketDetailDto>(await admin.PatchTicketAsync(ticket, assignee: agent.UserId));
        var cleared = await Session.Read<TicketDetailDto>(await admin.PatchTicketAsync(assigned, clearAssignee: true));
        Assert.Null(cleared.Assignee);
    }

    [Fact]
    public async Task Comments_are_listed_in_order_and_closed_tickets_are_read_only()
    {
        var admin = await Session.RegisterOrganizationAsync(factory);
        var dana = await admin.AddUserAsync(Role.Requester, "Dana Whitfield");
        var ticket = await dana.CreateTicketAsync();

        var first = await dana.Http.PostAsJsonAsync($"/api/tickets/{ticket.Id}/comments", new AddCommentRequest("It happens again"), Session.Json);
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        await admin.Http.PostAsJsonAsync($"/api/tickets/{ticket.Id}/comments", new AddCommentRequest("Looking now"), Session.Json);

        var detail = await dana.GetAsync<TicketDetailDto>($"/api/tickets/{ticket.Id}");
        Assert.Equal(["It happens again", "Looking now"], detail.Comments.Select(c => c.Body));
        Assert.Equal("Dana Whitfield", detail.Comments[0].Author.DisplayName);

        (await admin.PatchTicketAsync(detail, TicketStatus.Closed)).EnsureSuccessStatusCode();
        var late = await dana.Http.PostAsJsonAsync($"/api/tickets/{ticket.Id}/comments", new AddCommentRequest("too late"), Session.Json);
        Assert.Equal(HttpStatusCode.Conflict, late.StatusCode);
    }

    [Fact]
    public async Task Listing_filters_sorts_searches_and_pages()
    {
        var admin = await Session.RegisterOrganizationAsync(factory);
        var agent = await admin.AddUserAsync(Role.Agent);
        var low = await admin.CreateTicketAsync("Mouse is sticky", TicketPriority.Low);
        var urgent = await admin.CreateTicketAsync("Server room flooded", TicketPriority.Urgent);
        var high = await admin.CreateTicketAsync("VPN is down", TicketPriority.High);
        await admin.CreateTicketAsync("Keyboard missing a key", TicketPriority.Normal);
        (await admin.PatchTicketAsync(high, assignee: agent.UserId, status: TicketStatus.InProgress)).EnsureSuccessStatusCode();

        var byPriority = await admin.GetAsync<PagedResult<TicketSummaryDto>>("/api/tickets?sort=priority");
        Assert.Equal([TicketPriority.Urgent, TicketPriority.High, TicketPriority.Normal, TicketPriority.Low], byPriority.Items.Select(t => t.Priority));

        var inProgress = await admin.GetAsync<PagedResult<TicketSummaryDto>>("/api/tickets?status=InProgress");
        Assert.Equal([high.Id], inProgress.Items.Select(t => t.Id));

        var unassigned = await admin.GetAsync<PagedResult<TicketSummaryDto>>("/api/tickets?unassigned=true");
        Assert.Equal(3, unassigned.Total);

        var assignedToAgent = await admin.GetAsync<PagedResult<TicketSummaryDto>>($"/api/tickets?assigneeId={agent.UserId}");
        Assert.Equal([high.Id], assignedToAgent.Items.Select(t => t.Id));

        var search = await admin.GetAsync<PagedResult<TicketSummaryDto>>("/api/tickets?search=flooded");
        Assert.Equal([urgent.Id], search.Items.Select(t => t.Id));

        var byNumber = await admin.GetAsync<PagedResult<TicketSummaryDto>>($"/api/tickets?search=%23{low.Number}");
        Assert.Contains(low.Id, byNumber.Items.Select(t => t.Id));

        var page1 = await admin.GetAsync<PagedResult<TicketSummaryDto>>("/api/tickets?sort=number&descending=false&pageSize=3&page=1");
        var page2 = await admin.GetAsync<PagedResult<TicketSummaryDto>>("/api/tickets?sort=number&descending=false&pageSize=3&page=2");
        Assert.Equal(4, page1.Total);
        Assert.Equal([1, 2, 3], page1.Items.Select(t => t.Number));
        Assert.Equal([4], page2.Items.Select(t => t.Number));
    }

    [Fact]
    public async Task Page_size_is_capped()
    {
        var admin = await Session.RegisterOrganizationAsync(factory);

        var page = await admin.GetAsync<PagedResult<TicketSummaryDto>>("/api/tickets?pageSize=100000&page=-4");

        Assert.Equal(100, page.PageSize);
        Assert.Equal(1, page.Page);
    }

    [Fact]
    public async Task Stats_count_tickets_by_status_and_assignment()
    {
        var admin = await Session.RegisterOrganizationAsync(factory);
        var a = await admin.CreateTicketAsync();
        await admin.CreateTicketAsync();
        var c = await admin.CreateTicketAsync();
        (await admin.PatchTicketAsync(a, status: TicketStatus.InProgress, assignee: admin.UserId)).EnsureSuccessStatusCode();
        (await admin.PatchTicketAsync(c, status: TicketStatus.Closed)).EnsureSuccessStatusCode();

        var stats = await admin.GetAsync<TicketStatsDto>("/api/tickets/stats");

        Assert.Equal(1, stats.ByStatus[TicketStatus.Open]);
        Assert.Equal(1, stats.ByStatus[TicketStatus.InProgress]);
        Assert.Equal(1, stats.ByStatus[TicketStatus.Closed]);
        Assert.Equal(0, stats.ByStatus[TicketStatus.Resolved]);
        Assert.Equal(1, stats.Unassigned);
        Assert.Equal(1, stats.AssignedToMe);
    }
}
