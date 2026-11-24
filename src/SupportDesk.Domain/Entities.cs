namespace SupportDesk.Domain;

public enum Role
{
    Requester = 0,
    Agent = 1,
    Admin = 2,
}

public enum TicketStatus
{
    Open = 0,
    InProgress = 1,
    Resolved = 2,
    Closed = 3,
}

public enum TicketPriority
{
    Low = 0,
    Normal = 1,
    High = 2,
    Urgent = 3,
}

public enum TicketEventType
{
    Created = 0,
    StatusChanged = 1,
    PriorityChanged = 2,
    Assigned = 3,
    Unassigned = 4,
}

/// <summary>Implemented by every entity that belongs to exactly one tenant.</summary>
public interface ITenantOwned
{
    Guid TenantId { get; set; }
}

/// <summary>An organisation using the helpdesk. Tenants are the isolation boundary.</summary>
public sealed class Tenant
{
    public Guid Id { get; set; } = Guid.CreateVersion7();

    public required string Name { get; set; }

    public DateTime CreatedUtc { get; set; }

    /// <summary>Next human-friendly ticket number; incremented under a row lock when a ticket is created.</summary>
    public int NextTicketNumber { get; set; } = 1;
}

public sealed class User : ITenantOwned
{
    public Guid Id { get; set; } = Guid.CreateVersion7();

    public Guid TenantId { get; set; }

    /// <summary>Normalised to lower case. Unique across all tenants so users can sign in with just an email.</summary>
    public required string Email { get; set; }

    public required string DisplayName { get; set; }

    public required string PasswordHash { get; set; }

    public Role Role { get; set; }

    public bool IsActive { get; set; } = true;

    public DateTime CreatedUtc { get; set; }

    /// <summary>Consecutive failed sign-ins since the last success. Reset on success and when a lockout starts.</summary>
    public int FailedLoginCount { get; set; }

    /// <summary>While in the future, sign-in is refused whatever the password.</summary>
    public DateTime? LockoutEndUtc { get; set; }
}

public sealed class Ticket : ITenantOwned
{
    public Guid Id { get; set; } = Guid.CreateVersion7();

    public Guid TenantId { get; set; }

    /// <summary>Sequential per tenant (#1, #2, ...), shown to people instead of the GUID.</summary>
    public int Number { get; set; }

    public required string Title { get; set; }

    public required string Description { get; set; }

    public TicketStatus Status { get; set; } = TicketStatus.Open;

    public TicketPriority Priority { get; set; } = TicketPriority.Normal;

    public Guid RequesterId { get; set; }

    public User? Requester { get; set; }

    public Guid? AssigneeId { get; set; }

    public User? Assignee { get; set; }

    public DateTime CreatedUtc { get; set; }

    public DateTime UpdatedUtc { get; set; }

    /// <summary>SQL Server rowversion used for optimistic concurrency.</summary>
    public byte[] RowVersion { get; set; } = [];

    public List<Comment> Comments { get; set; } = [];

    public List<TicketEvent> Events { get; set; } = [];
}

public sealed class Comment : ITenantOwned
{
    public Guid Id { get; set; } = Guid.CreateVersion7();

    public Guid TenantId { get; set; }

    public Guid TicketId { get; set; }

    public Guid AuthorId { get; set; }

    public User? Author { get; set; }

    public required string Body { get; set; }

    public DateTime CreatedUtc { get; set; }
}

/// <summary>Append-only audit trail entry for a ticket.</summary>
public sealed class TicketEvent : ITenantOwned
{
    public Guid Id { get; set; } = Guid.CreateVersion7();

    public Guid TenantId { get; set; }

    public Guid TicketId { get; set; }

    public Guid ActorId { get; set; }

    public User? Actor { get; set; }

    public TicketEventType Type { get; set; }

    public required string Detail { get; set; }

    public DateTime CreatedUtc { get; set; }
}

/// <summary>
/// Someone was @mentioned in a ticket description or a comment. Drives the mentions inbox and the live notification.
/// </summary>
public sealed class TicketMention : ITenantOwned
{
    public Guid Id { get; set; } = Guid.CreateVersion7();

    public Guid TenantId { get; set; }

    public Guid TicketId { get; set; }

    public Ticket? Ticket { get; set; }

    /// <summary>The comment containing the mention, or null when it was in the ticket description.</summary>
    public Guid? CommentId { get; set; }

    /// <summary>The person who was mentioned.</summary>
    public Guid UserId { get; set; }

    public User? User { get; set; }

    /// <summary>The person who wrote the mention.</summary>
    public Guid ActorId { get; set; }

    public User? Actor { get; set; }

    /// <summary>A short plain-text excerpt shown in the inbox.</summary>
    public required string Snippet { get; set; }

    public DateTime CreatedUtc { get; set; }

    public bool IsRead { get; set; }
}
