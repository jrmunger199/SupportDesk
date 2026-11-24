using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;

using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;

using SupportDesk.Api.Contracts;
using SupportDesk.Api.Hubs;
using SupportDesk.Api.Services;
using SupportDesk.Domain;
using SupportDesk.Tests.Infrastructure;

namespace SupportDesk.Tests;

[Collection(ApiTestGroup.Name)]
public class MentionTests(ApiFactory factory)
{
    private static string Mention(Session person) => MentionTokens.Format(person.UserId, person.Auth.User.DisplayName);

    private static Task<HttpResponseMessage> Comment(Session author, Guid ticketId, string body) =>
        author.Http.PostAsJsonAsync($"/api/tickets/{ticketId}/comments", new AddCommentRequest(body), Session.Json);

    private static Task<MentionInboxDto> Inbox(Session person) => person.GetAsync<MentionInboxDto>("/api/mentions");

    private sealed record Team(Session Admin, Session Agent, Session Dana, Session Tom, TicketDetailDto DanasTicket);

    private async Task<Team> NewTeamAsync()
    {
        var admin = await Session.RegisterOrganizationAsync(factory);
        var agent = await admin.AddUserAsync(Role.Agent, "Priya Nair");
        var dana = await admin.AddUserAsync(Role.Requester, "Dana Whitfield");
        var tom = await admin.AddUserAsync(Role.Requester, "Tom Okafor");
        var ticket = await dana.CreateTicketAsync("Laptop will not boot");
        return new Team(admin, agent, dana, tom, ticket);
    }

    // ---- Notifying people ----

    [Fact]
    public async Task Mentioning_someone_in_a_comment_puts_it_in_their_inbox()
    {
        var team = await NewTeamAsync();

        var response = await Comment(team.Admin, team.DanasTicket.Id, $"Can you take a look, {Mention(team.Agent)}? Thanks!");
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var inbox = await Inbox(team.Agent);
        var item = Assert.Single(inbox.Items);
        Assert.Equal(1, inbox.UnreadCount);
        Assert.False(item.IsRead);
        Assert.Equal(team.DanasTicket.Id, item.TicketId);
        Assert.Equal(team.DanasTicket.Number, item.TicketNumber);
        Assert.Equal("Laptop will not boot", item.TicketTitle);
        Assert.Equal(team.Admin.UserId, item.Actor.Id);
        Assert.Equal("Can you take a look, @Priya Nair? Thanks!", item.Snippet);
    }

    [Fact]
    public async Task The_stored_comment_keeps_a_canonical_mention_token_even_if_the_client_sends_the_wrong_name()
    {
        var team = await NewTeamAsync();

        await Comment(team.Admin, team.DanasTicket.Id, $"Hi @[Somebody Else]({team.Agent.UserId})");

        var detail = await team.Admin.GetAsync<TicketDetailDto>($"/api/tickets/{team.DanasTicket.Id}");
        Assert.Equal($"Hi @[Priya Nair]({team.Agent.UserId})", Assert.Single(detail.Comments).Body);
    }

    [Fact]
    public async Task People_are_not_notified_about_their_own_mentions_or_twice_for_one_comment()
    {
        var team = await NewTeamAsync();

        await Comment(team.Agent, team.DanasTicket.Id, $"Note to self {Mention(team.Agent)}");
        await Comment(team.Admin, team.DanasTicket.Id, $"{Mention(team.Agent)} and again {Mention(team.Agent)}");

        var inbox = await Inbox(team.Agent);
        Assert.Single(inbox.Items);
    }

    [Fact]
    public async Task Mentions_in_a_new_ticket_description_notify_staff()
    {
        var team = await NewTeamAsync();

        var created = await team.Dana.Http.PostAsJsonAsync(
            "/api/tickets",
            new CreateTicketRequest("Screen flickers", $"Could {Mention(team.Agent)} have a look? It started today.", null),
            Session.Json);
        created.EnsureSuccessStatusCode();
        var ticket = await Session.Read<TicketDetailDto>(created);

        var item = Assert.Single((await Inbox(team.Agent)).Items);
        Assert.Equal(ticket.Id, item.TicketId);
        Assert.Equal(team.Dana.UserId, item.Actor.Id);
        Assert.Contains($"@[Priya Nair]({team.Agent.UserId})", ticket.Description, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Long_comments_produce_a_short_plain_text_snippet()
    {
        var team = await NewTeamAsync();

        await Comment(team.Admin, team.DanasTicket.Id, $"{Mention(team.Agent)} {new string('x', 500)}");

        var snippet = Assert.Single((await Inbox(team.Agent)).Items).Snippet;
        Assert.True(snippet.Length <= 140, $"snippet was {snippet.Length} characters");
        Assert.EndsWith("…", snippet, StringComparison.Ordinal);
        Assert.StartsWith("@Priya Nair ", snippet, StringComparison.Ordinal);
        Assert.DoesNotContain("](", snippet, StringComparison.Ordinal);
    }

    // ---- Who can be mentioned ----

    [Fact]
    public async Task Requesters_can_mention_staff()
    {
        var team = await NewTeamAsync();

        var response = await Comment(team.Dana, team.DanasTicket.Id, $"Any update, {Mention(team.Agent)}?");

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Single((await Inbox(team.Agent)).Items);
    }

    [Fact]
    public async Task Staff_can_mention_the_requester_of_that_ticket()
    {
        var team = await NewTeamAsync();

        await Comment(team.Agent, team.DanasTicket.Id, $"{Mention(team.Dana)} could you reboot it once more?");

        var item = Assert.Single((await Inbox(team.Dana)).Items);
        Assert.Equal(team.DanasTicket.Id, item.TicketId);
    }

    [Fact]
    public async Task A_requester_who_cannot_see_the_ticket_cannot_be_mentioned_in_it()
    {
        var team = await NewTeamAsync();

        var asStaff = await Comment(team.Agent, team.DanasTicket.Id, $"{Mention(team.Tom)} FYI");
        var asRequester = await Comment(team.Dana, team.DanasTicket.Id, $"{Mention(team.Tom)} FYI");

        Assert.Equal(HttpStatusCode.UnprocessableEntity, asStaff.StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, asRequester.StatusCode);
        Assert.Empty((await Inbox(team.Tom)).Items);
    }

    [Fact]
    public async Task People_from_other_organisations_and_unknown_ids_cannot_be_mentioned()
    {
        var team = await NewTeamAsync();
        var outsider = await Session.RegisterOrganizationAsync(factory);

        var other = await Comment(team.Admin, team.DanasTicket.Id, $"{Mention(outsider)} hello");
        var unknown = await Comment(team.Admin, team.DanasTicket.Id, $"@[Ghost]({Guid.NewGuid()}) hello");

        Assert.Equal(HttpStatusCode.UnprocessableEntity, other.StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, unknown.StatusCode);
        Assert.Empty((await Inbox(outsider)).Items);
    }

    [Fact]
    public async Task Deactivated_people_cannot_be_mentioned()
    {
        var team = await NewTeamAsync();
        (await team.Admin.Http.PutAsJsonAsync($"/api/users/{team.Agent.UserId}/active", new SetUserActiveRequest(false), Session.Json)).EnsureSuccessStatusCode();

        var response = await Comment(team.Admin, team.DanasTicket.Id, $"{Mention(team.Agent)} hello");

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }

    [Fact]
    public async Task A_rejected_mention_does_not_leave_a_comment_behind()
    {
        var team = await NewTeamAsync();

        await Comment(team.Dana, team.DanasTicket.Id, $"{Mention(team.Tom)} oops");

        var detail = await team.Dana.GetAsync<TicketDetailDto>($"/api/tickets/{team.DanasTicket.Id}");
        Assert.Empty(detail.Comments);
    }

    [Fact]
    public async Task At_most_ten_people_can_be_mentioned_at_once()
    {
        var team = await NewTeamAsync();
        var agents = new List<Session>();
        for (var i = 0; i < 11; i++)
        {
            agents.Add(await team.Admin.AddUserAsync(Role.Agent));
        }

        var ok = await Comment(team.Admin, team.DanasTicket.Id, string.Join(' ', agents.Take(10).Select(Mention)));
        var tooMany = await Comment(team.Admin, team.DanasTicket.Id, string.Join(' ', agents.Select(Mention)));

        Assert.Equal(HttpStatusCode.Created, ok.StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, tooMany.StatusCode);
    }

    [Fact]
    public async Task Text_with_no_valid_tokens_is_left_alone()
    {
        var team = await NewTeamAsync();

        await Comment(team.Admin, team.DanasTicket.Id, "Plain @Priya Nair and a malformed @[Priya](not-a-guid) mention");

        var detail = await team.Admin.GetAsync<TicketDetailDto>($"/api/tickets/{team.DanasTicket.Id}");
        Assert.Equal("Plain @Priya Nair and a malformed @[Priya](not-a-guid) mention", Assert.Single(detail.Comments).Body);
        Assert.Empty((await Inbox(team.Agent)).Items);
    }

    // ---- The inbox ----

    [Fact]
    public async Task Mentions_can_be_marked_read_one_at_a_time_or_all_at_once()
    {
        var team = await NewTeamAsync();
        await Comment(team.Admin, team.DanasTicket.Id, $"{Mention(team.Agent)} one");
        await Comment(team.Admin, team.DanasTicket.Id, $"{Mention(team.Agent)} two");
        await Comment(team.Admin, team.DanasTicket.Id, $"{Mention(team.Agent)} three");

        var inbox = await Inbox(team.Agent);
        Assert.Equal(3, inbox.UnreadCount);
        Assert.Equal(["@Priya Nair three", "@Priya Nair two", "@Priya Nair one"], inbox.Items.Select(i => i.Snippet)); // newest first

        (await team.Agent.Http.PostAsync($"/api/mentions/{inbox.Items[0].Id}/read", null)).EnsureSuccessStatusCode();
        var afterOne = await Inbox(team.Agent);
        Assert.Equal(2, afterOne.UnreadCount);
        Assert.Equal(2, (await team.Agent.GetAsync<MentionInboxDto>("/api/mentions?unreadOnly=true")).Items.Count);

        (await team.Agent.Http.PostAsync("/api/mentions/read-all", null)).EnsureSuccessStatusCode();
        var afterAll = await Inbox(team.Agent);
        Assert.Equal(0, afterAll.UnreadCount);
        Assert.All(afterAll.Items, i => Assert.True(i.IsRead));
    }

    [Fact]
    public async Task Nobody_can_read_or_clear_someone_elses_mentions()
    {
        var team = await NewTeamAsync();
        await Comment(team.Admin, team.DanasTicket.Id, $"{Mention(team.Agent)} private");
        var mentionId = Assert.Single((await Inbox(team.Agent)).Items).Id;

        var attempt = await team.Admin.Http.PostAsync($"/api/mentions/{mentionId}/read", null);
        await team.Admin.Http.PostAsync("/api/mentions/read-all", null);

        Assert.Equal(HttpStatusCode.NotFound, attempt.StatusCode);
        Assert.Equal(1, (await Inbox(team.Agent)).UnreadCount);
        Assert.Empty((await Inbox(team.Admin)).Items);
    }

    [Fact]
    public async Task The_mentions_inbox_requires_sign_in()
    {
        var response = await factory.CreateClient().GetAsync("/api/mentions");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // ---- Autocomplete candidates ----

    [Fact]
    public async Task Candidates_for_staff_on_a_ticket_are_colleagues_and_that_tickets_requester()
    {
        var team = await NewTeamAsync();
        var colleague = await team.Admin.AddUserAsync(Role.Agent, "Marcus Lee");

        var candidates = await team.Agent.GetAsync<List<MentionCandidateDto>>($"/api/mentions/candidates?ticketId={team.DanasTicket.Id}");

        var names = candidates.Select(c => c.DisplayName).ToList();
        Assert.Contains("Dana Whitfield", names); // the requester
        Assert.Contains("Marcus Lee", names);
        Assert.DoesNotContain("Tom Okafor", names); // another requester who cannot see the ticket
        Assert.DoesNotContain(candidates, c => c.Id == team.Agent.UserId); // not yourself
        Assert.Contains(candidates, c => c.Id == colleague.UserId && c.Role == Role.Agent);
    }

    [Fact]
    public async Task Candidates_for_a_requester_are_staff_only()
    {
        var team = await NewTeamAsync();

        var onTicket = await team.Dana.GetAsync<List<MentionCandidateDto>>($"/api/mentions/candidates?ticketId={team.DanasTicket.Id}");
        var forNewTicket = await team.Dana.GetAsync<List<MentionCandidateDto>>("/api/mentions/candidates");

        Assert.All(onTicket.Concat(forNewTicket), c => Assert.True(c.Role.IsStaff()));
        Assert.NotEmpty(onTicket);
        Assert.DoesNotContain(forNewTicket, c => c.Id == team.Tom.UserId);
    }

    [Fact]
    public async Task Candidates_can_be_searched_by_name()
    {
        var team = await NewTeamAsync();
        await team.Admin.AddUserAsync(Role.Agent, "Marcus Lee");

        var result = await team.Admin.GetAsync<List<MentionCandidateDto>>($"/api/mentions/candidates?ticketId={team.DanasTicket.Id}&q=marc");

        Assert.Equal("Marcus Lee", Assert.Single(result).DisplayName);
    }

    [Fact]
    public async Task Candidates_never_include_other_organisations_or_tickets_you_cannot_see()
    {
        var team = await NewTeamAsync();
        var outsider = await Session.RegisterOrganizationAsync(factory);

        var candidates = await team.Admin.GetAsync<List<MentionCandidateDto>>($"/api/mentions/candidates?ticketId={team.DanasTicket.Id}");
        var foreignTicket = await team.Admin.Http.GetAsync($"/api/mentions/candidates?ticketId={(await outsider.CreateTicketAsync()).Id}");
        var othersTicket = await team.Tom.Http.GetAsync($"/api/mentions/candidates?ticketId={team.DanasTicket.Id}");

        Assert.DoesNotContain(candidates, c => c.Id == outsider.UserId);
        Assert.Equal(HttpStatusCode.NotFound, foreignTicket.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, othersTicket.StatusCode);
    }

    // ---- Live notification ----

    [Fact]
    public async Task The_mentioned_person_is_notified_live_and_nobody_else_is()
    {
        var team = await NewTeamAsync();
        var bystander = await team.Admin.AddUserAsync(Role.Agent);
        await using var mentioned = await MentionListener.ConnectAsync(factory, team.Agent);
        await using var other = await MentionListener.ConnectAsync(factory, bystander);

        await Comment(team.Admin, team.DanasTicket.Id, $"{Mention(team.Agent)} please check");

        var push = await mentioned.NextAsync();
        Assert.Equal(team.DanasTicket.Number, push.TicketNumber);
        Assert.Equal(team.Admin.UserId, push.Actor.Id);
        Assert.False(push.IsRead);
        await other.AssertSilentAsync();

        // The push and the inbox agree.
        Assert.Equal(push.Id, Assert.Single((await Inbox(team.Agent)).Items).Id);
    }

    private sealed class MentionListener : IAsyncDisposable
    {
        private readonly HubConnection _connection;
        private readonly Channel<MentionDto> _messages = Channel.CreateUnbounded<MentionDto>();

        private MentionListener(HubConnection connection)
        {
            _connection = connection;
            _connection.On<MentionDto>(TicketHub.MentionedMethod, message => _messages.Writer.TryWrite(message));
        }

        public static async Task<MentionListener> ConnectAsync(ApiFactory factory, Session session)
        {
            var connection = new HubConnectionBuilder()
                .WithUrl(new Uri(factory.Server.BaseAddress, TicketHub.Route), o =>
                {
                    o.HttpMessageHandlerFactory = _ => factory.Server.CreateHandler();
                    o.AccessTokenProvider = () => Task.FromResult<string?>(session.Token);
                    o.Transports = HttpTransportType.LongPolling;
                })
                .AddJsonProtocol(o => o.PayloadSerializerOptions.Converters.Add(new JsonStringEnumConverter()))
                .Build();

            var listener = new MentionListener(connection);
            await connection.StartAsync();
            await Task.Delay(300); // the server joins groups just after the handshake
            return listener;
        }

        public async Task<MentionDto> NextAsync()
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            return await _messages.Reader.ReadAsync(timeout.Token);
        }

        public async Task AssertSilentAsync()
        {
            await Task.Delay(1500);
            Assert.False(_messages.Reader.TryRead(out _), "A person who was not mentioned received a mention notification.");
        }

        public ValueTask DisposeAsync() => _connection.DisposeAsync();
    }
}
