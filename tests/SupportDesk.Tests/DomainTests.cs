using SupportDesk.Domain;

namespace SupportDesk.Tests;

public class DomainTests
{
    public static TheoryData<TicketStatus, TicketStatus, bool> Transitions()
    {
        var legal = new HashSet<(TicketStatus, TicketStatus)>
        {
            (TicketStatus.Open, TicketStatus.InProgress),
            (TicketStatus.Open, TicketStatus.Resolved),
            (TicketStatus.Open, TicketStatus.Closed),
            (TicketStatus.InProgress, TicketStatus.Open),
            (TicketStatus.InProgress, TicketStatus.Resolved),
            (TicketStatus.InProgress, TicketStatus.Closed),
            (TicketStatus.Resolved, TicketStatus.Open),
            (TicketStatus.Resolved, TicketStatus.Closed),
            (TicketStatus.Closed, TicketStatus.Open),
        };

        var data = new TheoryData<TicketStatus, TicketStatus, bool>();
        foreach (var from in Enum.GetValues<TicketStatus>())
        {
            foreach (var to in Enum.GetValues<TicketStatus>())
            {
                data.Add(from, to, legal.Contains((from, to)));
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Transitions))]
    public void Workflow_allows_exactly_the_documented_transitions(TicketStatus from, TicketStatus to, bool expected) =>
        Assert.Equal(expected, TicketWorkflow.CanTransition(from, to));

    [Fact]
    public void A_status_cannot_transition_to_itself() =>
        Assert.All(Enum.GetValues<TicketStatus>(), s => Assert.False(TicketWorkflow.CanTransition(s, s)));

    [Fact]
    public void Next_statuses_agree_with_CanTransition() =>
        Assert.All(Enum.GetValues<TicketStatus>(), from =>
            Assert.All(TicketWorkflow.NextStatuses(from), to => Assert.True(TicketWorkflow.CanTransition(from, to))));

    [Theory]
    [InlineData(Role.Requester, false)]
    [InlineData(Role.Agent, true)]
    [InlineData(Role.Admin, true)]
    public void Only_agents_and_admins_are_staff(Role role, bool expected) =>
        Assert.Equal(expected, role.IsStaff());
}
