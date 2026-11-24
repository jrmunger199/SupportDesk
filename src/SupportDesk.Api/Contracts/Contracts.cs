using System.ComponentModel.DataAnnotations;

using SupportDesk.Domain;

namespace SupportDesk.Api.Contracts;

// ---- Requests ----

public sealed record RegisterOrganizationRequest(
    [Required, StringLength(120, MinimumLength = 2)] string OrganizationName,
    [Required, StringLength(120, MinimumLength = 2)] string AdminName,
    [Required, EmailAddress, StringLength(256)] string Email,
    [Required, StringLength(100, MinimumLength = 10)] string Password);

public sealed record LoginRequest(
    [Required, EmailAddress] string Email,
    [Required] string Password);

public sealed record CreateUserRequest(
    [Required, StringLength(120, MinimumLength = 2)] string DisplayName,
    [Required, EmailAddress, StringLength(256)] string Email,
    [Required, StringLength(100, MinimumLength = 10)] string Password,
    Role Role);

public sealed record SetUserActiveRequest(bool IsActive);

public sealed record CreateTicketRequest(
    [Required, StringLength(200, MinimumLength = 3)] string Title,
    [Required, StringLength(10_000, MinimumLength = 3)] string Description,
    TicketPriority? Priority);

/// <summary>Partial update. Omitted fields are left alone; <see cref="ClearAssignee"/> removes the assignee.</summary>
public sealed record UpdateTicketRequest(
    TicketStatus? Status,
    TicketPriority? Priority,
    Guid? AssigneeId,
    bool ClearAssignee,
    [Required] string RowVersion);

public sealed record AddCommentRequest(
    [Required, StringLength(4_000, MinimumLength = 1)] string Body);

/// <summary>Query-string filters for the ticket list.</summary>
public sealed record TicketQuery(
    TicketStatus? Status = null,
    TicketPriority? Priority = null,
    Guid? AssigneeId = null,
    bool? Unassigned = null,
    string? Search = null,
    string Sort = "updated",
    bool Descending = true,
    int Page = 1,
    int PageSize = 20);

// ---- Responses ----

public sealed record UserDto(Guid Id, string Email, string DisplayName, Role Role, bool IsActive);

public sealed record AuthResponse(string Token, DateTime ExpiresUtc, UserDto User, string Organization);

public sealed record UserRef(Guid Id, string DisplayName);

public sealed record TicketSummaryDto(
    Guid Id,
    int Number,
    string Title,
    TicketStatus Status,
    TicketPriority Priority,
    UserRef Requester,
    UserRef? Assignee,
    DateTime CreatedUtc,
    DateTime UpdatedUtc,
    int CommentCount);

public sealed record CommentDto(Guid Id, UserRef Author, string Body, DateTime CreatedUtc);

public sealed record TicketEventDto(Guid Id, UserRef Actor, TicketEventType Type, string Detail, DateTime CreatedUtc);

public sealed record TicketDetailDto(
    Guid Id,
    int Number,
    string Title,
    string Description,
    TicketStatus Status,
    TicketPriority Priority,
    UserRef Requester,
    UserRef? Assignee,
    DateTime CreatedUtc,
    DateTime UpdatedUtc,
    string RowVersion,
    IReadOnlyList<TicketStatus> AllowedTransitions,
    IReadOnlyList<CommentDto> Comments,
    IReadOnlyList<TicketEventDto> Events);

public sealed record PagedResult<T>(IReadOnlyList<T> Items, int Total, int Page, int PageSize);

public sealed record TicketStatsDto(IReadOnlyDictionary<TicketStatus, int> ByStatus, int Unassigned, int AssignedToMe);

/// <summary>Pushed over SignalR whenever a ticket changes. Kind is created, updated or commented.</summary>
public sealed record TicketChange(string Kind, TicketSummaryDto Ticket);

// ---- Mentions ----

/// <summary>Someone who can be @mentioned in a particular piece of text.</summary>
public sealed record MentionCandidateDto(Guid Id, string DisplayName, Role Role);

/// <summary>One entry in a person's mentions inbox. Also the payload of the live "mentioned" push.</summary>
public sealed record MentionDto(
    Guid Id,
    Guid TicketId,
    int TicketNumber,
    string TicketTitle,
    UserRef Actor,
    string Snippet,
    DateTime CreatedUtc,
    bool IsRead);

public sealed record MentionInboxDto(IReadOnlyList<MentionDto> Items, int UnreadCount);

// ---- Board ----

/// <summary>Filters for the kanban board. Each status column returns at most <see cref="PerColumn"/> cards.</summary>
public sealed record BoardQuery(
    Guid? AssigneeId = null,
    bool? Unassigned = null,
    string? Search = null,
    int PerColumn = 25);

/// <summary>Everything the board needs to show a card and to move it without another round trip.</summary>
public sealed record BoardCardDto(
    Guid Id,
    int Number,
    string Title,
    TicketStatus Status,
    TicketPriority Priority,
    UserRef Requester,
    UserRef? Assignee,
    DateTime UpdatedUtc,
    int CommentCount,
    string RowVersion,
    IReadOnlyList<TicketStatus> AllowedTransitions);

public sealed record BoardColumnDto(TicketStatus Status, int Total, IReadOnlyList<BoardCardDto> Cards);

/// <summary><see cref="CanMove"/> is false for requesters, whose board is read-only.</summary>
public sealed record BoardDto(IReadOnlyList<BoardColumnDto> Columns, bool CanMove);
