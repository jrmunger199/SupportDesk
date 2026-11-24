using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

using SupportDesk.Api.Contracts;
using SupportDesk.Api.Security;
using SupportDesk.Domain;

namespace SupportDesk.Api.Hubs;

/// <summary>
/// Push channel for ticket changes. Staff join one group per organisation; everyone also joins a group of their own,
/// which is how requesters hear about their tickets (and only theirs).
/// </summary>
[Authorize]
public sealed class TicketHub(HubConnections connections) : Hub
{
    public const string Route = "/hubs/tickets";
    public const string ChangedMethod = "ticketChanged";
    public const string MentionedMethod = "mentioned";

    public static string StaffGroup(Guid tenantId) => $"staff:{tenantId:N}";

    public static string UserGroup(Guid userId) => $"user:{userId:N}";

    public override async Task OnConnectedAsync()
    {
        var principal = Context.User ?? throw new HubException("Not authenticated.");

        connections.Add(principal.GetUserId(), Context);
        await Groups.AddToGroupAsync(Context.ConnectionId, UserGroup(principal.GetUserId()));
        if (principal.GetRole().IsStaff())
        {
            await Groups.AddToGroupAsync(Context.ConnectionId, StaffGroup(principal.GetTenantId()));
        }

        await base.OnConnectedAsync();
    }

    public override Task OnDisconnectedAsync(Exception? exception)
    {
        if (Context.User is { } principal)
        {
            connections.Remove(principal.GetUserId(), Context.ConnectionId);
        }

        return base.OnDisconnectedAsync(exception);
    }
}

public interface ITicketNotifier
{
    Task NotifyAsync(string kind, TicketSummaryDto ticket, Guid tenantId, bool requesterIsStaff, CancellationToken cancellationToken);

    /// <summary>Tells one person, and only them, that they were mentioned.</summary>
    Task MentionedAsync(Guid userId, MentionDto mention, CancellationToken cancellationToken);
}

public sealed class SignalRTicketNotifier(IHubContext<TicketHub> hub) : ITicketNotifier
{
    public Task NotifyAsync(string kind, TicketSummaryDto ticket, Guid tenantId, bool requesterIsStaff, CancellationToken cancellationToken)
    {
        var groups = new List<string> { TicketHub.StaffGroup(tenantId) };

        // Staff requesters are already in the staff group; adding their personal group would deliver the message twice.
        if (!requesterIsStaff)
        {
            groups.Add(TicketHub.UserGroup(ticket.Requester.Id));
        }

        return hub.Clients.Groups(groups).SendAsync(TicketHub.ChangedMethod, new TicketChange(kind, ticket), cancellationToken);
    }

    public Task MentionedAsync(Guid userId, MentionDto mention, CancellationToken cancellationToken) =>
        hub.Clients.Group(TicketHub.UserGroup(userId)).SendAsync(TicketHub.MentionedMethod, mention, cancellationToken);
}
