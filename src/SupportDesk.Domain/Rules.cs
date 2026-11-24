namespace SupportDesk.Domain;

/// <summary>Resolves the tenant for the current operation. Null means "no tenant": the data layer then fails closed.</summary>
public interface ITenantContext
{
    Guid? TenantId { get; }
}

/// <summary>Which status changes are legal. Kept in the domain so it is trivially unit-testable.</summary>
public static class TicketWorkflow
{
    private static readonly Dictionary<TicketStatus, TicketStatus[]> Allowed = new()
    {
        [TicketStatus.Open] = [TicketStatus.InProgress, TicketStatus.Resolved, TicketStatus.Closed],
        [TicketStatus.InProgress] = [TicketStatus.Open, TicketStatus.Resolved, TicketStatus.Closed],
        [TicketStatus.Resolved] = [TicketStatus.Open, TicketStatus.Closed],
        [TicketStatus.Closed] = [TicketStatus.Open],
    };

    /// <summary>True when moving from <paramref name="from"/> to a different status <paramref name="to"/> is allowed.</summary>
    public static bool CanTransition(TicketStatus from, TicketStatus to) =>
        from != to && Allowed[from].Contains(to);

    public static IReadOnlyList<TicketStatus> NextStatuses(TicketStatus from) => Allowed[from];
}

public static class RoleExtensions
{
    /// <summary>Agents and admins work tickets; requesters only raise and follow their own.</summary>
    public static bool IsStaff(this Role role) => role is Role.Agent or Role.Admin;
}
