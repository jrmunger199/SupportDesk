using System.Net;
using System.Net.Http.Json;

using SupportDesk.Api.Contracts;
using SupportDesk.Domain;
using SupportDesk.Tests.Infrastructure;

namespace SupportDesk.Tests;

[Collection(ApiTestGroup.Name)]
public class BoardTests(ApiFactory factory)
{
    private static Task<BoardDto> Board(Session session, string query = "") =>
        session.GetAsync<BoardDto>("/api/tickets/board" + query);

    private static BoardColumnDto Column(BoardDto board, TicketStatus status) => board.Columns.Single(c => c.Status == status);

    [Fact]
    public async Task The_board_has_one_column_per_status_in_workflow_order()
    {
        var admin = await Session.RegisterOrganizationAsync(factory);
        var a = await admin.CreateTicketAsync();
        var b = await admin.CreateTicketAsync();
        (await admin.PatchTicketAsync(b, TicketStatus.InProgress)).EnsureSuccessStatusCode();

        var board = await Board(admin);

        Assert.Equal([TicketStatus.Open, TicketStatus.InProgress, TicketStatus.Resolved, TicketStatus.Closed], board.Columns.Select(c => c.Status));
        Assert.Equal([a.Id], Column(board, TicketStatus.Open).Cards.Select(c => c.Id));
        Assert.Equal([b.Id], Column(board, TicketStatus.InProgress).Cards.Select(c => c.Id));
        Assert.Equal(0, Column(board, TicketStatus.Resolved).Total);
        Assert.True(board.CanMove);
    }

    [Fact]
    public async Task Cards_are_ordered_by_priority_then_by_recent_activity()
    {
        var admin = await Session.RegisterOrganizationAsync(factory);
        var low = await admin.CreateTicketAsync("Low", TicketPriority.Low);
        var urgentOld = await admin.CreateTicketAsync("Urgent old", TicketPriority.Urgent);
        var urgentNew = await admin.CreateTicketAsync("Urgent new", TicketPriority.Urgent);
        var normal = await admin.CreateTicketAsync("Normal", TicketPriority.Normal);

        var cards = Column(await Board(admin), TicketStatus.Open).Cards;

        Assert.Equal([urgentNew.Id, urgentOld.Id, normal.Id, low.Id], cards.Select(c => c.Id));
    }

    [Fact]
    public async Task Columns_are_capped_but_still_report_their_true_total()
    {
        var admin = await Session.RegisterOrganizationAsync(factory);
        for (var i = 0; i < 5; i++)
        {
            await admin.CreateTicketAsync($"Ticket {i}");
        }

        var open = Column(await Board(admin, "?perColumn=2"), TicketStatus.Open);

        Assert.Equal(2, open.Cards.Count);
        Assert.Equal(5, open.Total);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(-5, 1)]
    [InlineData(100_000, 50)]
    public async Task The_per_column_limit_is_clamped(int requested, int expectedCap)
    {
        var admin = await Session.RegisterOrganizationAsync(factory);
        for (var i = 0; i < 3; i++)
        {
            await admin.CreateTicketAsync($"Ticket {i}");
        }

        var open = Column(await Board(admin, $"?perColumn={requested}"), TicketStatus.Open);

        Assert.Equal(Math.Min(expectedCap, 3), open.Cards.Count);
    }

    [Fact]
    public async Task The_board_can_be_filtered_by_assignee_unassigned_and_search()
    {
        var admin = await Session.RegisterOrganizationAsync(factory);
        var agent = await admin.AddUserAsync(Role.Agent);
        var vpn = await admin.CreateTicketAsync("VPN drops");
        var printer = await admin.CreateTicketAsync("Printer jam");
        (await admin.PatchTicketAsync(vpn, assignee: agent.UserId)).EnsureSuccessStatusCode();

        var mine = Column(await Board(admin, $"?assigneeId={agent.UserId}"), TicketStatus.Open);
        var unassigned = Column(await Board(admin, "?unassigned=true"), TicketStatus.Open);
        var search = Column(await Board(admin, "?search=printer"), TicketStatus.Open);

        Assert.Equal([vpn.Id], mine.Cards.Select(c => c.Id));
        Assert.Equal([printer.Id], unassigned.Cards.Select(c => c.Id));
        Assert.Equal([printer.Id], search.Cards.Select(c => c.Id));
        Assert.Equal(1, mine.Total);
    }

    [Fact]
    public async Task Requesters_get_a_read_only_board_of_their_own_tickets()
    {
        var admin = await Session.RegisterOrganizationAsync(factory);
        var dana = await admin.AddUserAsync(Role.Requester);
        var tom = await admin.AddUserAsync(Role.Requester);
        var mine = await dana.CreateTicketAsync("Dana's");
        await tom.CreateTicketAsync("Tom's");

        var board = await Board(dana);

        Assert.False(board.CanMove);
        Assert.Equal([mine.Id], board.Columns.SelectMany(c => c.Cards).Select(c => c.Id));
        Assert.Equal(1, board.Columns.Sum(c => c.Total));
    }

    [Fact]
    public async Task The_board_never_shows_another_organisations_tickets()
    {
        var acme = await Session.RegisterOrganizationAsync(factory);
        var globex = await Session.RegisterOrganizationAsync(factory);
        await acme.CreateTicketAsync("Acme secret");

        var board = await Board(globex);

        Assert.Empty(board.Columns.SelectMany(c => c.Cards));
        Assert.All(board.Columns, c => Assert.Equal(0, c.Total));
    }

    [Fact]
    public async Task Anonymous_callers_cannot_load_the_board()
    {
        var response = await factory.CreateClient().GetAsync("/api/tickets/board");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Cards_carry_what_is_needed_to_move_them_without_another_request()
    {
        var admin = await Session.RegisterOrganizationAsync(factory);
        var ticket = await admin.CreateTicketAsync("Move me");
        await admin.Http.PostAsJsonAsync($"/api/tickets/{ticket.Id}/comments", new AddCommentRequest("a comment"), Session.Json);

        var card = Assert.Single(Column(await Board(admin), TicketStatus.Open).Cards);

        Assert.Equal(1, card.CommentCount);
        Assert.Equal(ticket.Requester.Id, card.Requester.Id);
        Assert.Equal([TicketStatus.InProgress, TicketStatus.Resolved, TicketStatus.Closed], card.AllowedTransitions);

        // The card's row version is exactly what a PATCH needs, and it is single-use.
        var move = await admin.Http.PatchAsJsonAsync(
            $"/api/tickets/{card.Id}",
            new UpdateTicketRequest(TicketStatus.InProgress, null, null, false, card.RowVersion),
            Session.Json);
        var again = await admin.Http.PatchAsJsonAsync(
            $"/api/tickets/{card.Id}",
            new UpdateTicketRequest(TicketStatus.Resolved, null, null, false, card.RowVersion),
            Session.Json);

        Assert.Equal(HttpStatusCode.OK, move.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
    }

    [Fact]
    public async Task Each_columns_cards_list_only_the_moves_the_workflow_allows()
    {
        var admin = await Session.RegisterOrganizationAsync(factory);
        var ticket = await admin.CreateTicketAsync();
        (await admin.PatchTicketAsync(ticket, TicketStatus.Closed)).EnsureSuccessStatusCode();

        var closed = Assert.Single(Column(await Board(admin), TicketStatus.Closed).Cards);

        Assert.Equal([TicketStatus.Open], closed.AllowedTransitions); // closed tickets can only be reopened
    }
}
