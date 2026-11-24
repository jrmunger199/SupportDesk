using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

using SupportDesk.Data;
using SupportDesk.Domain;

namespace SupportDesk.Api.Infrastructure;

/// <summary>
/// Creates two organisations with people and tickets so the app has something to show, and so tenant
/// isolation can be seen first-hand. Development only; it does nothing once any tenant exists.
/// </summary>
public sealed class DemoDataSeeder(AppDbContext db, IPasswordHasher<User> hasher, TimeProvider clock)
{
    public const string DemoPassword = "DemoPass!2026";

    private sealed record SeedTicket(
        string Title, string Description, TicketStatus Status, TicketPriority Priority,
        int Requester, int? Assignee, int HoursAgo, string? Comment = null);

    public async Task SeedAsync(CancellationToken cancellationToken)
    {
        if (await db.Tenants.AnyAsync(cancellationToken))
        {
            return;
        }

        await SeedOrganisationAsync(
            "Acme Corp",
            [
                ("admin@acme.test", "Alex Morgan", Role.Admin),
                ("priya@acme.test", "Priya Nair", Role.Agent),
                ("marcus@acme.test", "Marcus Lee", Role.Agent),
                ("dana@acme.test", "Dana Whitfield", Role.Requester),
                ("tom@acme.test", "Tom Okafor", Role.Requester),
            ],
            [
                new("VPN drops every 20 minutes", "Since Monday the VPN disconnects roughly every twenty minutes while working from home.", TicketStatus.Open, TicketPriority.High, 3, null, 2),
                new("New starter laptop setup", "Please prepare a laptop for Jordan who starts on the 1st. Standard developer image.", TicketStatus.InProgress, TicketPriority.Normal, 4, 1, 26, "Image is building, laptop should be ready tomorrow."),
                new("Printer on floor 3 jams constantly", "The shared printer jams on anything thicker than standard paper.", TicketStatus.Open, TicketPriority.Low, 3, null, 50),
                new("Cannot access finance share", "Access denied on \\\\fs01\\finance since the permissions change.", TicketStatus.InProgress, TicketPriority.Urgent, 4, 2, 5, "Checking group membership now."),
                new("Request: second monitor", "Would like a second monitor for the accounts desk.", TicketStatus.Resolved, TicketPriority.Low, 3, 1, 90, "Delivered and installed."),
                new("Outlook keeps asking for password", "Prompted every few minutes after the latest update.", TicketStatus.Closed, TicketPriority.Normal, 4, 2, 140),
                new("Wi-Fi slow in meeting room B", "Video calls stutter in meeting room B but not elsewhere.", TicketStatus.Open, TicketPriority.Normal, 3, 2, 12),
                new("Shared mailbox missing", "The sales@ shared mailbox disappeared from my Outlook profile.", TicketStatus.Open, TicketPriority.High, 4, null, 1),
            ],
            cancellationToken);

        await SeedOrganisationAsync(
            "Globex Industries",
            [
                ("admin@globex.test", "Hank Scorpio", Role.Admin),
                ("agent@globex.test", "Frank Grimes", Role.Agent),
                ("requester@globex.test", "Lena Ortiz", Role.Requester),
            ],
            [
                new("Badge reader offline at loading dock", "The east dock badge reader shows an offline error.", TicketStatus.Open, TicketPriority.Urgent, 2, null, 3),
                new("Forecast spreadsheet is locked", "The Q3 forecast says it is open by another user.", TicketStatus.InProgress, TicketPriority.Normal, 2, 1, 30, "Releasing the stale lock."),
            ],
            cancellationToken);
    }

    private async Task SeedOrganisationAsync(
        string name,
        (string Email, string DisplayName, Role Role)[] people,
        SeedTicket[] tickets,
        CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var tenant = new Tenant { Name = name, NextTicketNumber = tickets.Length + 1 };
        db.Tenants.Add(tenant);

        var users = people.Select(p =>
        {
            var user = new User { TenantId = tenant.Id, Email = p.Email, DisplayName = p.DisplayName, PasswordHash = string.Empty, Role = p.Role };
            user.PasswordHash = hasher.HashPassword(user, DemoPassword);
            return user;
        }).ToArray();
        db.Users.AddRange(users);

        var created = new List<(Ticket Ticket, int HoursAgo)>();
        for (var i = 0; i < tickets.Length; i++)
        {
            var t = tickets[i];
            var requester = users[t.Requester];
            var assignee = t.Assignee is { } a ? users[a] : null;
            var ticket = new Ticket
            {
                TenantId = tenant.Id,
                Number = i + 1,
                Title = t.Title,
                Description = t.Description,
                Status = t.Status,
                Priority = t.Priority,
                RequesterId = requester.Id,
                AssigneeId = assignee?.Id,
            };

            ticket.Events.Add(new TicketEvent { TenantId = tenant.Id, ActorId = requester.Id, Type = TicketEventType.Created, Detail = "Ticket created" });
            if (assignee is not null)
            {
                ticket.Events.Add(new TicketEvent { TenantId = tenant.Id, ActorId = assignee.Id, Type = TicketEventType.Assigned, Detail = $"Assigned to {assignee.DisplayName}" });
            }

            if (t.Comment is not null && assignee is not null)
            {
                ticket.Comments.Add(new Comment { TenantId = tenant.Id, AuthorId = assignee.Id, Body = t.Comment });
            }

            db.Tickets.Add(ticket);
            created.Add((ticket, t.HoursAgo));
        }

        await db.SaveChangesAsync(cancellationToken);

        // The DbContext stamps "now" on insert; spread the timestamps out so the board looks lived-in.
        foreach (var (ticket, hoursAgo) in created)
        {
            var stamp = now.AddHours(-hoursAgo);
            await db.Tickets.IgnoreQueryFilters()
                .Where(x => x.Id == ticket.Id)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.CreatedUtc, stamp).SetProperty(x => x.UpdatedUtc, stamp), cancellationToken);
        }
    }
}
