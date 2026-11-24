using Microsoft.AspNetCore.Identity;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

using SupportDesk.Api.Contracts;
using SupportDesk.Api.Infrastructure;
using SupportDesk.Api.Security;
using SupportDesk.Data;
using SupportDesk.Domain;

namespace SupportDesk.Api.Services;

/// <summary>Manages the people in the caller's own organisation. Tenant filters keep it from seeing anyone else's.</summary>
public sealed class UserService(
    AppDbContext db,
    IPasswordHasher<User> hasher,
    ICurrentUser currentUser,
    SessionDirectory sessions,
    HubConnections connections)
{
    public async Task<IReadOnlyList<UserDto>> ListAsync(CancellationToken cancellationToken) =>
        await db.Users
            .AsNoTracking()
            .OrderBy(u => u.DisplayName)
            .Select(u => new UserDto(u.Id, u.Email, u.DisplayName, u.Role, u.IsActive))
            .ToListAsync(cancellationToken);

    public async Task<UserDto> CreateAsync(CreateUserRequest request, CancellationToken cancellationToken)
    {
        var email = AuthService.NormalizeEmail(request.Email);

        if (PasswordPolicy.Check(request.Password, email, request.DisplayName) is { } weakness)
        {
            throw new UnprocessableException(weakness);
        }

        if (await db.Users.IgnoreQueryFilters().AnyAsync(u => u.Email == email, cancellationToken))
        {
            throw new ConflictException("An account with this email already exists.");
        }

        var user = new User
        {
            Email = email,
            DisplayName = request.DisplayName.Trim(),
            PasswordHash = string.Empty,
            Role = request.Role,
        };
        user.PasswordHash = hasher.HashPassword(user, request.Password);

        db.Users.Add(user); // TenantId is stamped from the caller's token by the DbContext
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (ex.InnerException is SqlException { Number: 2601 or 2627 })
        {
            throw new ConflictException("An account with this email already exists.");
        }

        return AuthService.ToDto(user);
    }

    public async Task<UserDto> SetActiveAsync(Guid id, bool isActive, CancellationToken cancellationToken)
    {
        if (id == currentUser.Id && !isActive)
        {
            throw new UnprocessableException("You cannot deactivate your own account.");
        }

        var user = await db.Users.SingleOrDefaultAsync(u => u.Id == id, cancellationToken)
            ?? throw new NotFoundException("User");

        user.IsActive = isActive;
        await db.SaveChangesAsync(cancellationToken);

        // Make the change bite now: forget the cached session so the next request is re-checked,
        // and cut any live SignalR connections, which are only authenticated when they open.
        sessions.Invalidate(user.Id);
        if (!isActive)
        {
            connections.AbortUser(user.Id);
        }

        return AuthService.ToDto(user);
    }
}
