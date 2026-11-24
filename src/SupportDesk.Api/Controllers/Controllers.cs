using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

using SupportDesk.Api.Contracts;
using SupportDesk.Api.Services;

namespace SupportDesk.Api.Controllers;

public static class Policies
{
    public const string Staff = "Staff";
    public const string Admin = "Admin";
    public const string AuthRateLimit = "auth";
}

[ApiController]
[Route("api/auth")]
[Produces("application/json")]
public sealed class AuthController(AuthService auth) : ControllerBase
{
    /// <summary>Creates a new organisation (tenant) and its first admin, and signs them in.</summary>
    [HttpPost("register")]
    [AllowAnonymous]
    [EnableRateLimiting(Policies.AuthRateLimit)]
    [ProducesResponseType<AuthResponse>(StatusCodes.Status201Created)]
    public async Task<ActionResult<AuthResponse>> Register(RegisterOrganizationRequest request, CancellationToken cancellationToken) =>
        StatusCode(StatusCodes.Status201Created, await auth.RegisterOrganizationAsync(request, cancellationToken));

    [HttpPost("login")]
    [AllowAnonymous]
    [EnableRateLimiting(Policies.AuthRateLimit)]
    public async Task<ActionResult<AuthResponse>> Login(LoginRequest request, CancellationToken cancellationToken) =>
        await auth.LoginAsync(request, cancellationToken);

    /// <summary>Returns the signed-in user with a fresh token, which is also how the client revalidates a stored session.</summary>
    [HttpGet("me")]
    [Authorize]
    public async Task<ActionResult<AuthResponse>> Me(CancellationToken cancellationToken) =>
        await auth.RefreshProfileAsync(cancellationToken);
}

[ApiController]
[Route("api/users")]
[Authorize(Policy = Policies.Staff)]
public sealed class UsersController(UserService users) : ControllerBase
{
    /// <summary>People in your organisation. Staff need this to pick an assignee.</summary>
    [HttpGet]
    public async Task<IReadOnlyList<UserDto>> List(CancellationToken cancellationToken) =>
        await users.ListAsync(cancellationToken);

    [HttpPost]
    [Authorize(Policy = Policies.Admin)]
    [ProducesResponseType<UserDto>(StatusCodes.Status201Created)]
    public async Task<ActionResult<UserDto>> Create(CreateUserRequest request, CancellationToken cancellationToken) =>
        StatusCode(StatusCodes.Status201Created, await users.CreateAsync(request, cancellationToken));

    [HttpPut("{id:guid}/active")]
    [Authorize(Policy = Policies.Admin)]
    public async Task<ActionResult<UserDto>> SetActive(Guid id, SetUserActiveRequest request, CancellationToken cancellationToken) =>
        await users.SetActiveAsync(id, request.IsActive, cancellationToken);
}

[ApiController]
[Route("api/tickets")]
[Authorize]
public sealed class TicketsController(TicketService tickets) : ControllerBase
{
    [HttpGet]
    public async Task<PagedResult<TicketSummaryDto>> List([FromQuery] TicketQuery query, CancellationToken cancellationToken) =>
        await tickets.ListAsync(query, cancellationToken);

    /// <summary>The kanban board: tickets grouped into one column per status.</summary>
    [HttpGet("board")]
    public async Task<BoardDto> Board([FromQuery] BoardQuery query, CancellationToken cancellationToken) =>
        await tickets.BoardAsync(query, cancellationToken);

    [HttpGet("stats")]
    public async Task<TicketStatsDto> Stats(CancellationToken cancellationToken) =>
        await tickets.StatsAsync(cancellationToken);

    [HttpGet("{id:guid}")]
    public async Task<TicketDetailDto> Get(Guid id, CancellationToken cancellationToken) =>
        await tickets.GetAsync(id, cancellationToken);

    [HttpPost]
    [ProducesResponseType<TicketDetailDto>(StatusCodes.Status201Created)]
    public async Task<ActionResult<TicketDetailDto>> Create(CreateTicketRequest request, CancellationToken cancellationToken)
    {
        var ticket = await tickets.CreateAsync(request, cancellationToken);
        return CreatedAtAction(nameof(Get), new { id = ticket.Id }, ticket);
    }

    /// <summary>Agents and admins change status, priority and assignee. Send back the rowVersion you received (409 if stale).</summary>
    [HttpPatch("{id:guid}")]
    public async Task<TicketDetailDto> Update(Guid id, UpdateTicketRequest request, CancellationToken cancellationToken) =>
        await tickets.UpdateAsync(id, request, cancellationToken);

    [HttpPost("{id:guid}/comments")]
    [ProducesResponseType<CommentDto>(StatusCodes.Status201Created)]
    public async Task<ActionResult<CommentDto>> AddComment(Guid id, AddCommentRequest request, CancellationToken cancellationToken) =>
        StatusCode(StatusCodes.Status201Created, await tickets.AddCommentAsync(id, request, cancellationToken));
}

[ApiController]
[Route("api/mentions")]
[Authorize]
public sealed class MentionsController(MentionQueryService mentions) : ControllerBase
{
    /// <summary>The signed-in person's mentions, newest first, with the unread count for the notification bell.</summary>
    [HttpGet]
    public async Task<MentionInboxDto> Inbox([FromQuery] bool unreadOnly = false, [FromQuery] int limit = 30, CancellationToken cancellationToken = default) =>
        await mentions.InboxAsync(unreadOnly, limit, cancellationToken);

    [HttpPost("{id:guid}/read")]
    public async Task<IActionResult> MarkRead(Guid id, CancellationToken cancellationToken)
    {
        await mentions.MarkReadAsync(id, cancellationToken);
        return NoContent();
    }

    [HttpPost("read-all")]
    public async Task<IActionResult> MarkAllRead(CancellationToken cancellationToken)
    {
        await mentions.MarkAllReadAsync(cancellationToken);
        return NoContent();
    }

    /// <summary>People who can be @mentioned, optionally in the context of one ticket.</summary>
    [HttpGet("candidates")]
    public async Task<IReadOnlyList<MentionCandidateDto>> Candidates([FromQuery] Guid? ticketId, [FromQuery] string? q, CancellationToken cancellationToken) =>
        await mentions.CandidatesAsync(ticketId, q, cancellationToken);
}
