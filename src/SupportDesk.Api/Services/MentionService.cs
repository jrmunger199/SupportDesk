using Microsoft.EntityFrameworkCore;

using SupportDesk.Api.Contracts;
using SupportDesk.Api.Infrastructure;
using SupportDesk.Api.Security;
using SupportDesk.Data;
using SupportDesk.Domain;

namespace SupportDesk.Api.Services;

public sealed record MentionedPerson(Guid Id, string DisplayName);

public sealed record ResolvedMentions(string Text, IReadOnlyList<MentionedPerson> People);

/// <summary>Validates the @mentions in a piece of text and turns them into inbox rows.</summary>
public sealed class MentionService(AppDbContext db, ICurrentUser user)
{
    private const int SnippetLength = 140;

    /// <summary>
    /// Checks every mentioned id and returns the text with canonical names. A person can be mentioned only if they exist
    /// in this organisation, are active, and could actually open the ticket: staff, or the ticket's requester.
    /// </summary>
    public async Task<ResolvedMentions> ResolveAsync(string text, Guid ticketRequesterId, int maxLength, CancellationToken cancellationToken)
    {
        var ids = MentionTokens.ExtractIds(text).ToList();
        if (ids.Count == 0)
        {
            return new ResolvedMentions(text, []);
        }

        if (ids.Count > MentionTokens.MaxMentionsPerText)
        {
            throw new UnprocessableException($"You can mention at most {MentionTokens.MaxMentionsPerText} people at once.");
        }

        // Tenant filters apply, so an id from another organisation is simply "not found".
        var found = await db.Users
            .Where(u => ids.Contains(u.Id) && u.IsActive)
            .Select(u => new { u.Id, u.DisplayName, u.Role })
            .ToListAsync(cancellationToken);

        foreach (var id in ids)
        {
            var person = found.FirstOrDefault(p => p.Id == id)
                ?? throw new UnprocessableException("One of the people you mentioned could not be found.");

            if (!person.Role.IsStaff() && person.Id != ticketRequesterId)
            {
                throw new UnprocessableException($"{person.DisplayName} cannot see this ticket, so they cannot be mentioned in it.");
            }
        }

        var normalized = MentionTokens.Normalize(text, found.ToDictionary(p => p.Id, p => p.DisplayName));
        if (normalized.Length > maxLength)
        {
            throw new UnprocessableException("That text is too long once the mentions are included.");
        }

        return new ResolvedMentions(normalized, [.. found.Select(p => new MentionedPerson(p.Id, p.DisplayName))]);
    }

    /// <summary>Inbox rows for everyone mentioned except the author: nobody needs a notification about themselves.</summary>
    public IEnumerable<TicketMention> BuildRows(ResolvedMentions resolved, Guid ticketId, Guid? commentId)
    {
        var snippet = Snippet(resolved.Text);
        return resolved.People
            .Where(p => p.Id != user.Id)
            .Select(p => new TicketMention
            {
                TicketId = ticketId,
                CommentId = commentId,
                UserId = p.Id,
                ActorId = user.Id,
                Snippet = snippet,
            });
    }

    internal static string Snippet(string text)
    {
        var flat = string.Join(' ', MentionTokens.Flatten(text).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return flat.Length <= SnippetLength ? flat : flat[..(SnippetLength - 1)].TrimEnd() + "\u2026";
    }
}

/// <summary>Reads for the mentions feature: a person's inbox and the people who can be mentioned.</summary>
public sealed class MentionQueryService(AppDbContext db, ICurrentUser user)
{
    private const int MaxInbox = 50;
    private const int MaxCandidates = 50;

    public async Task<MentionInboxDto> InboxAsync(bool unreadOnly, int limit, CancellationToken cancellationToken)
    {
        var mine = db.TicketMentions.AsNoTracking().Where(m => m.UserId == user.Id);
        var unread = await mine.CountAsync(m => !m.IsRead, cancellationToken);

        var items = await (unreadOnly ? mine.Where(m => !m.IsRead) : mine)
            .OrderByDescending(m => m.CreatedUtc)
            .Take(Math.Clamp(limit, 1, MaxInbox))
            .Select(m => new MentionDto(
                m.Id,
                m.TicketId,
                m.Ticket!.Number,
                m.Ticket.Title,
                new UserRef(m.Actor!.Id, m.Actor.DisplayName),
                m.Snippet,
                m.CreatedUtc,
                m.IsRead))
            .ToListAsync(cancellationToken);

        return new MentionInboxDto(items, unread);
    }

    public async Task MarkReadAsync(Guid id, CancellationToken cancellationToken)
    {
        var mention = await db.TicketMentions.SingleOrDefaultAsync(m => m.Id == id && m.UserId == user.Id, cancellationToken)
            ?? throw new NotFoundException("Mention");

        if (!mention.IsRead)
        {
            mention.IsRead = true;
            await db.SaveChangesAsync(cancellationToken);
        }
    }

    public Task<int> MarkAllReadAsync(CancellationToken cancellationToken) =>
        db.TicketMentions
            .Where(m => m.UserId == user.Id && !m.IsRead)
            .ExecuteUpdateAsync(s => s.SetProperty(m => m.IsRead, true), cancellationToken);

    /// <summary>
    /// People the caller may mention. With a ticket id: staff plus that ticket's requester (the people who can open it).
    /// Without one, as when writing a new ticket: staff only, since the only requester involved is the author.
    /// </summary>
    public async Task<IReadOnlyList<MentionCandidateDto>> CandidatesAsync(Guid? ticketId, string? search, CancellationToken cancellationToken)
    {
        var requesterId = user.Id;
        if (ticketId is { } id)
        {
            var tickets = user.IsStaff ? db.Tickets : db.Tickets.Where(t => t.RequesterId == user.Id);
            requesterId = await tickets
                .Where(t => t.Id == id)
                .Select(t => (Guid?)t.RequesterId)
                .SingleOrDefaultAsync(cancellationToken)
                ?? throw new NotFoundException("Ticket");
        }

        var people = db.Users.AsNoTracking()
            .Where(u => u.IsActive && u.Id != user.Id && (u.Role == Role.Agent || u.Role == Role.Admin || u.Id == requesterId));

        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim();
            people = people.Where(u => u.DisplayName.Contains(term));
        }

        return await people
            .OrderBy(u => u.DisplayName)
            .Take(MaxCandidates)
            .Select(u => new MentionCandidateDto(u.Id, u.DisplayName, u.Role))
            .ToListAsync(cancellationToken);
    }
}
