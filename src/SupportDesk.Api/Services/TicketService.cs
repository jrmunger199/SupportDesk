using Microsoft.EntityFrameworkCore;

using SupportDesk.Api.Contracts;
using SupportDesk.Api.Hubs;
using SupportDesk.Api.Infrastructure;
using SupportDesk.Api.Security;
using SupportDesk.Data;
using SupportDesk.Domain;

namespace SupportDesk.Api.Services;

public sealed class TicketService(AppDbContext db, ICurrentUser user, ITicketNotifier notifier, MentionService mentions)
{
    private const int MaxPageSize = 100;
    private const int DescriptionMaxLength = 10_000;
    private const int CommentMaxLength = 4_000;

    /// <summary>
    /// Everything the caller may see. The tenant filter is applied by the DbContext; on top of that,
    /// requesters only see tickets they raised. Anything else looks like "not found".
    /// </summary>
    private IQueryable<Ticket> Visible() =>
        user.IsStaff ? db.Tickets : db.Tickets.Where(t => t.RequesterId == user.Id);

    public async Task<PagedResult<TicketSummaryDto>> ListAsync(TicketQuery query, CancellationToken cancellationToken)
    {
        var page = Math.Max(query.Page, 1);
        var pageSize = Math.Clamp(query.PageSize, 1, MaxPageSize);

        var tickets = Visible().AsNoTracking();

        if (query.Status is { } status)
        {
            tickets = tickets.Where(t => t.Status == status);
        }

        if (query.Priority is { } priority)
        {
            tickets = tickets.Where(t => t.Priority == priority);
        }

        if (query.AssigneeId is { } assigneeId)
        {
            tickets = tickets.Where(t => t.AssigneeId == assigneeId);
        }

        if (query.Unassigned == true)
        {
            tickets = tickets.Where(t => t.AssigneeId == null);
        }

        tickets = ApplySearch(tickets, query.Search);

        var total = await tickets.CountAsync(cancellationToken);

        var items = await Sort(tickets, query)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(t => new TicketSummaryDto(
                t.Id,
                t.Number,
                t.Title,
                t.Status,
                t.Priority,
                new UserRef(t.Requester!.Id, t.Requester.DisplayName),
                t.Assignee == null ? null : new UserRef(t.Assignee.Id, t.Assignee.DisplayName),
                t.CreatedUtc,
                t.UpdatedUtc,
                t.Comments.Count))
            .ToListAsync(cancellationToken);

        return new PagedResult<TicketSummaryDto>(items, total, page, pageSize);
    }

    public async Task<TicketDetailDto> GetAsync(Guid id, CancellationToken cancellationToken)
    {
        var ticket = await LoadDetailAsync(id, cancellationToken) ?? throw new NotFoundException("Ticket");
        return ToDetail(ticket);
    }

    public async Task<TicketStatsDto> StatsAsync(CancellationToken cancellationToken)
    {
        var counts = await Visible()
            .AsNoTracking()
            .GroupBy(t => t.Status)
            .Select(g => new { Status = g.Key, Count = g.Count() })
            .ToListAsync(cancellationToken);

        var byStatus = Enum.GetValues<TicketStatus>().ToDictionary(
            s => s,
            s => counts.FirstOrDefault(c => c.Status == s)?.Count ?? 0);

        var open = new[] { TicketStatus.Open, TicketStatus.InProgress };
        var unassigned = await Visible().CountAsync(t => t.AssigneeId == null && open.Contains(t.Status), cancellationToken);
        var mine = await Visible().CountAsync(t => t.AssigneeId == user.Id && open.Contains(t.Status), cancellationToken);

        return new TicketStatsDto(byStatus, unassigned, mine);
    }

    public async Task<TicketDetailDto> CreateAsync(CreateTicketRequest request, CancellationToken cancellationToken)
    {
        Guid ticketId;
        var mentioned = await mentions.ResolveAsync(request.Description.Trim(), user.Id, DescriptionMaxLength, cancellationToken);
        List<TicketMention> mentionRows = [];

        // The counter row is locked by the UPDATE until commit, so concurrent creates are numbered one at a time.
        await using (var transaction = await db.Database.BeginTransactionAsync(cancellationToken))
        {
            await db.Tenants
                .Where(t => t.Id == user.TenantId)
                .ExecuteUpdateAsync(s => s.SetProperty(t => t.NextTicketNumber, t => t.NextTicketNumber + 1), cancellationToken);

            var next = await db.Tenants
                .Where(t => t.Id == user.TenantId)
                .Select(t => t.NextTicketNumber)
                .SingleAsync(cancellationToken);

            var ticket = new Ticket
            {
                Number = next - 1,
                Title = request.Title.Trim(),
                Description = mentioned.Text,
                Priority = request.Priority ?? TicketPriority.Normal,
                RequesterId = user.Id,
            };
            ticket.Events.Add(NewEvent(TicketEventType.Created, "Ticket created"));

            mentionRows = [.. mentions.BuildRows(mentioned, ticket.Id, commentId: null)];
            db.Tickets.Add(ticket);
            db.TicketMentions.AddRange(mentionRows);
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            ticketId = ticket.Id;
        }

        var created = await LoadDetailAsync(ticketId, cancellationToken) ?? throw new NotFoundException("Ticket");
        await notifier.NotifyAsync("created", ToSummary(created), user.TenantId, created.Requester!.Role.IsStaff(), cancellationToken);
        await NotifyMentionsAsync(mentionRows, created.Number, created.Title, cancellationToken);
        return ToDetail(created);
    }

    public async Task<TicketDetailDto> UpdateAsync(Guid id, UpdateTicketRequest request, CancellationToken cancellationToken)
    {
        if (!user.IsStaff)
        {
            throw new ForbiddenException("Only agents and admins can update tickets.");
        }

        if (request.ClearAssignee && request.AssigneeId is not null)
        {
            throw new UnprocessableException("Provide either an assignee or clearAssignee, not both.");
        }

        var ticket = await db.Tickets.SingleOrDefaultAsync(t => t.Id == id, cancellationToken)
            ?? throw new NotFoundException("Ticket");

        // Optimistic concurrency: the save only succeeds if the row still has the version the client last saw.
        db.Entry(ticket).Property(t => t.RowVersion).OriginalValue = ParseRowVersion(request.RowVersion);

        var changed = false;

        if (request.Status is { } status && status != ticket.Status)
        {
            if (!TicketWorkflow.CanTransition(ticket.Status, status))
            {
                throw new UnprocessableException($"A ticket cannot move from {ticket.Status} to {status}.");
            }

            db.TicketEvents.Add(NewEvent(TicketEventType.StatusChanged, $"{ticket.Status} -> {status}", ticket.Id));
            ticket.Status = status;
            changed = true;
        }

        if (request.Priority is { } priority && priority != ticket.Priority)
        {
            db.TicketEvents.Add(NewEvent(TicketEventType.PriorityChanged, $"{ticket.Priority} -> {priority}", ticket.Id));
            ticket.Priority = priority;
            changed = true;
        }

        if (request.ClearAssignee && ticket.AssigneeId is not null)
        {
            db.TicketEvents.Add(NewEvent(TicketEventType.Unassigned, "Assignee removed", ticket.Id));
            ticket.AssigneeId = null;
            changed = true;
        }
        else if (request.AssigneeId is { } assigneeId && assigneeId != ticket.AssigneeId)
        {
            var assignee = await db.Users.SingleOrDefaultAsync(u => u.Id == assigneeId && u.IsActive, cancellationToken);
            if (assignee is null || !assignee.Role.IsStaff())
            {
                throw new UnprocessableException("The assignee must be an active agent or admin in your organisation.");
            }

            db.TicketEvents.Add(NewEvent(TicketEventType.Assigned, $"Assigned to {assignee.DisplayName}", ticket.Id));
            ticket.AssigneeId = assignee.Id;
            changed = true;
        }

        if (changed)
        {
            await db.SaveChangesAsync(cancellationToken);
        }

        var updated = await LoadDetailAsync(id, cancellationToken) ?? throw new NotFoundException("Ticket");
        if (changed)
        {
            await notifier.NotifyAsync("updated", ToSummary(updated), user.TenantId, updated.Requester!.Role.IsStaff(), cancellationToken);
        }

        return ToDetail(updated);
    }

    public async Task<CommentDto> AddCommentAsync(Guid id, AddCommentRequest request, CancellationToken cancellationToken)
    {
        var ticket = await Visible().SingleOrDefaultAsync(t => t.Id == id, cancellationToken)
            ?? throw new NotFoundException("Ticket");

        if (ticket.Status == TicketStatus.Closed)
        {
            throw new ConflictException("Closed tickets are read-only. Reopen the ticket to comment.");
        }

        var mentioned = await mentions.ResolveAsync(request.Body.Trim(), ticket.RequesterId, CommentMaxLength, cancellationToken);
        var comment = new Comment { TicketId = ticket.Id, AuthorId = user.Id, Body = mentioned.Text };
        var mentionRows = mentions.BuildRows(mentioned, ticket.Id, comment.Id).ToList();
        db.Comments.Add(comment);
        db.TicketMentions.AddRange(mentionRows);
        await db.SaveChangesAsync(cancellationToken);

        var updated = await LoadDetailAsync(id, cancellationToken) ?? throw new NotFoundException("Ticket");
        await notifier.NotifyAsync("commented", ToSummary(updated), user.TenantId, updated.Requester!.Role.IsStaff(), cancellationToken);
        await NotifyMentionsAsync(mentionRows, updated.Number, updated.Title, cancellationToken);

        return new CommentDto(comment.Id, new UserRef(user.Id, user.DisplayName), comment.Body, comment.CreatedUtc);
    }

    /// <summary>
    /// The kanban view: one column per status, each holding the highest-priority, most recently touched tickets.
    /// Cards carry their row version and legal next statuses, so a drag can become a single PATCH.
    /// </summary>
    public async Task<BoardDto> BoardAsync(BoardQuery query, CancellationToken cancellationToken)
    {
        var perColumn = Math.Clamp(query.PerColumn, 1, 50);
        var tickets = Visible().AsNoTracking();

        if (query.AssigneeId is { } assigneeId)
        {
            tickets = tickets.Where(t => t.AssigneeId == assigneeId);
        }

        if (query.Unassigned == true)
        {
            tickets = tickets.Where(t => t.AssigneeId == null);
        }

        tickets = ApplySearch(tickets, query.Search);

        var columns = new List<BoardColumnDto>();
        foreach (var status in Enum.GetValues<TicketStatus>())
        {
            var inColumn = tickets.Where(t => t.Status == status);
            var total = await inColumn.CountAsync(cancellationToken);

            var rows = await inColumn
                .OrderByDescending(t => t.Priority)
                .ThenByDescending(t => t.UpdatedUtc)
                .ThenBy(t => t.Id)
                .Take(perColumn)
                .Select(t => new
                {
                    t.Id,
                    t.Number,
                    t.Title,
                    t.Priority,
                    t.UpdatedUtc,
                    t.RowVersion,
                    Requester = new UserRef(t.Requester!.Id, t.Requester.DisplayName),
                    Assignee = t.Assignee == null ? null : new UserRef(t.Assignee.Id, t.Assignee.DisplayName),
                    CommentCount = t.Comments.Count,
                })
                .ToListAsync(cancellationToken);

            var allowed = TicketWorkflow.NextStatuses(status);
            columns.Add(new BoardColumnDto(
                status,
                total,
                [.. rows.Select(r => new BoardCardDto(
                    r.Id, r.Number, r.Title, status, r.Priority, r.Requester, r.Assignee, r.UpdatedUtc,
                    r.CommentCount, Convert.ToBase64String(r.RowVersion), allowed))]));
        }

        return new BoardDto(columns, user.IsStaff);
    }

    private static IQueryable<Ticket> ApplySearch(IQueryable<Ticket> tickets, string? search)
    {
        if (string.IsNullOrWhiteSpace(search))
        {
            return tickets;
        }

        var term = search.Trim();
        return int.TryParse(term.TrimStart('#'), out var number)
            ? tickets.Where(t => t.Number == number || t.Title.Contains(term))
            : tickets.Where(t => t.Title.Contains(term) || t.Description.Contains(term));
    }

    private Task NotifyMentionsAsync(IEnumerable<TicketMention> rows, int ticketNumber, string ticketTitle, CancellationToken cancellationToken) =>
        Task.WhenAll(rows.Select(row => notifier.MentionedAsync(
            row.UserId,
            new MentionDto(
                row.Id,
                row.TicketId,
                ticketNumber,
                ticketTitle,
                new UserRef(user.Id, user.DisplayName),
                row.Snippet,
                row.CreatedUtc,
                IsRead: false),
            cancellationToken)));

    private Task<Ticket?> LoadDetailAsync(Guid id, CancellationToken cancellationToken) =>
        Visible()
            .AsNoTracking()
            .AsSplitQuery()
            .Include(t => t.Requester)
            .Include(t => t.Assignee)
            .Include(t => t.Comments.OrderBy(c => c.CreatedUtc)).ThenInclude(c => c.Author)
            .Include(t => t.Events.OrderBy(e => e.CreatedUtc)).ThenInclude(e => e.Actor)
            .SingleOrDefaultAsync(t => t.Id == id, cancellationToken);

    private TicketEvent NewEvent(TicketEventType type, string detail, Guid? ticketId = null) =>
        new() { TicketId = ticketId ?? Guid.Empty, ActorId = user.Id, Type = type, Detail = detail };

    private static IQueryable<Ticket> Sort(IQueryable<Ticket> tickets, TicketQuery query)
    {
        // Id is the tie-breaker so paging is stable when the sort key has duplicates.
        return (query.Sort.ToLowerInvariant(), query.Descending) switch
        {
            ("created", true) => tickets.OrderByDescending(t => t.CreatedUtc).ThenBy(t => t.Id),
            ("created", false) => tickets.OrderBy(t => t.CreatedUtc).ThenBy(t => t.Id),
            ("priority", true) => tickets.OrderByDescending(t => t.Priority).ThenByDescending(t => t.UpdatedUtc).ThenBy(t => t.Id),
            ("priority", false) => tickets.OrderBy(t => t.Priority).ThenByDescending(t => t.UpdatedUtc).ThenBy(t => t.Id),
            ("number", true) => tickets.OrderByDescending(t => t.Number),
            ("number", false) => tickets.OrderBy(t => t.Number),
            (_, false) => tickets.OrderBy(t => t.UpdatedUtc).ThenBy(t => t.Id),
            _ => tickets.OrderByDescending(t => t.UpdatedUtc).ThenBy(t => t.Id),
        };
    }

    private static byte[] ParseRowVersion(string value)
    {
        try
        {
            return Convert.FromBase64String(value);
        }
        catch (FormatException)
        {
            throw new UnprocessableException("rowVersion is not valid. Use the value returned with the ticket.");
        }
    }

    private static UserRef Ref(User u) => new(u.Id, u.DisplayName);

    private static TicketSummaryDto ToSummary(Ticket t) =>
        new(t.Id, t.Number, t.Title, t.Status, t.Priority, Ref(t.Requester!),
            t.Assignee is null ? null : Ref(t.Assignee), t.CreatedUtc, t.UpdatedUtc, t.Comments.Count);

    private static TicketDetailDto ToDetail(Ticket t) =>
        new(
            t.Id,
            t.Number,
            t.Title,
            t.Description,
            t.Status,
            t.Priority,
            Ref(t.Requester!),
            t.Assignee is null ? null : Ref(t.Assignee),
            t.CreatedUtc,
            t.UpdatedUtc,
            Convert.ToBase64String(t.RowVersion),
            TicketWorkflow.NextStatuses(t.Status),
            [.. t.Comments.Select(c => new CommentDto(c.Id, Ref(c.Author!), c.Body, c.CreatedUtc))],
            [.. t.Events.Select(e => new TicketEventDto(e.Id, Ref(e.Actor!), e.Type, e.Detail, e.CreatedUtc))]);
}
