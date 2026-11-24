using System.Net.Http.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;

using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;

using SupportDesk.Api.Contracts;
using SupportDesk.Api.Hubs;
using SupportDesk.Domain;
using SupportDesk.Tests.Infrastructure;

namespace SupportDesk.Tests;

[Collection(ApiTestGroup.Name)]
public class RealtimeTests(ApiFactory factory)
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task Staff_hear_about_new_tickets_immediately()
    {
        var admin = await Session.RegisterOrganizationAsync(factory);
        var dana = await admin.AddUserAsync(Role.Requester, "Dana");
        await using var listener = await Listener.ConnectAsync(factory, admin);

        await dana.CreateTicketAsync("Coffee machine broken");

        var change = await listener.NextAsync();
        Assert.Equal("created", change.Kind);
        Assert.Equal("Coffee machine broken", change.Ticket.Title);
        Assert.Equal("Dana", change.Ticket.Requester.DisplayName);
    }

    [Fact]
    public async Task Updates_and_comments_are_pushed_to_staff_and_to_the_requester()
    {
        var admin = await Session.RegisterOrganizationAsync(factory);
        var dana = await admin.AddUserAsync(Role.Requester);
        var ticket = await dana.CreateTicketAsync();
        await using var staff = await Listener.ConnectAsync(factory, admin);
        await using var requester = await Listener.ConnectAsync(factory, dana);

        (await admin.PatchTicketAsync(ticket, TicketStatus.InProgress)).EnsureSuccessStatusCode();

        var forStaff = await staff.NextAsync();
        var forRequester = await requester.NextAsync();
        Assert.Equal("updated", forStaff.Kind);
        Assert.Equal(TicketStatus.InProgress, forRequester.Ticket.Status);
    }

    [Fact]
    public async Task Requesters_do_not_hear_about_other_requesters_tickets()
    {
        var admin = await Session.RegisterOrganizationAsync(factory);
        var dana = await admin.AddUserAsync(Role.Requester);
        var tom = await admin.AddUserAsync(Role.Requester);
        await using var danaListener = await Listener.ConnectAsync(factory, dana);
        await using var tomListener = await Listener.ConnectAsync(factory, tom);

        await tom.CreateTicketAsync("Tom's private ticket");

        Assert.Equal("Tom's private ticket", (await tomListener.NextAsync()).Ticket.Title);
        await danaListener.AssertSilentAsync();
    }

    [Fact]
    public async Task Other_organisations_hear_nothing()
    {
        var acme = await Session.RegisterOrganizationAsync(factory);
        var globex = await Session.RegisterOrganizationAsync(factory);
        await using var globexListener = await Listener.ConnectAsync(factory, globex);

        await acme.CreateTicketAsync("Acme only");

        await globexListener.AssertSilentAsync();
    }

    [Fact]
    public async Task A_staff_requester_gets_one_message_not_two()
    {
        var admin = await Session.RegisterOrganizationAsync(factory);
        await using var listener = await Listener.ConnectAsync(factory, admin);

        await admin.CreateTicketAsync("My own ticket");

        await listener.NextAsync();
        await listener.AssertSilentAsync(); // would see a duplicate here if both groups were targeted
    }

    [Fact]
    public async Task Deactivating_a_user_closes_their_live_connection()
    {
        var admin = await Session.RegisterOrganizationAsync(factory);
        var agent = await admin.AddUserAsync(Role.Agent);
        await using var listener = await Listener.ConnectAsync(factory, agent);

        (await admin.Http.PutAsJsonAsync($"/api/users/{agent.UserId}/active", new SetUserActiveRequest(false), Session.Json)).EnsureSuccessStatusCode();

        // Token checks only happen when a socket opens, so the server must actively cut it.
        await listener.AssertClosedAsync();
    }

    [Fact]
    public async Task Unauthenticated_connections_are_refused()
    {
        var connection = new HubConnectionBuilder()
            .WithUrl(new Uri(factory.Server.BaseAddress, TicketHub.Route), o =>
            {
                o.HttpMessageHandlerFactory = _ => factory.Server.CreateHandler();
                o.Transports = HttpTransportType.LongPolling;
            })
            .Build();

        await Assert.ThrowsAnyAsync<Exception>(() => connection.StartAsync());
        await connection.DisposeAsync();
    }

    /// <summary>A SignalR client wired to the in-memory test server, collecting every ticketChanged message.</summary>
    private sealed class Listener : IAsyncDisposable
    {
        private readonly HubConnection _connection;
        private readonly Channel<TicketChange> _messages = Channel.CreateUnbounded<TicketChange>();
        private readonly TaskCompletionSource _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);

        private Listener(HubConnection connection)
        {
            _connection = connection;
            _connection.On<TicketChange>(TicketHub.ChangedMethod, message => _messages.Writer.TryWrite(message));
            _connection.Closed += _ =>
            {
                _closed.TrySetResult();
                return Task.CompletedTask;
            };
        }

        public async Task AssertClosedAsync() => await _closed.Task.WaitAsync(Patience);

        public static async Task<Listener> ConnectAsync(ApiFactory factory, Session session)
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

            var listener = new Listener(connection);
            await connection.StartAsync();

            // The server adds the connection to its groups in OnConnectedAsync, just after the handshake completes.
            await Task.Delay(300);
            return listener;
        }

        public async Task<TicketChange> NextAsync()
        {
            using var timeout = new CancellationTokenSource(Patience);
            return await _messages.Reader.ReadAsync(timeout.Token);
        }

        public async Task AssertSilentAsync()
        {
            await Task.Delay(1500);
            Assert.False(_messages.Reader.TryRead(out var unexpected), $"Received an unexpected message about '{unexpected?.Ticket.Title}'.");
        }

        public ValueTask DisposeAsync() => _connection.DisposeAsync();
    }
}
