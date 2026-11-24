using Microsoft.AspNetCore.Identity;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

using SupportDesk.Api.Configuration;
using SupportDesk.Api.Contracts;
using SupportDesk.Api.Infrastructure;
using SupportDesk.Api.Security;
using SupportDesk.Data;
using SupportDesk.Domain;

namespace SupportDesk.Api.Services;

public sealed partial class AuthService(
    AppDbContext db,
    IPasswordHasher<User> hasher,
    TokenService tokens,
    ICurrentUser currentUser,
    IOptions<AccountLockoutOptions> lockoutOptions,
    TimeProvider clock,
    ILogger<AuthService> logger)
{
    // Verified against when the email is unknown, so "no such user" and "wrong password" take similar time.
    private static readonly User Decoy = new() { Email = "decoy", DisplayName = "decoy", PasswordHash = string.Empty };
    private static readonly string DecoyHash = new PasswordHasher<User>().HashPassword(Decoy, "decoy-password");

    // One message for every failure (unknown email, wrong password, locked, deactivated) so responses reveal nothing.
    private const string InvalidCredentials = "The email or password is incorrect.";

    public async Task<AuthResponse> RegisterOrganizationAsync(RegisterOrganizationRequest request, CancellationToken cancellationToken)
    {
        var email = NormalizeEmail(request.Email);

        if (PasswordPolicy.Check(request.Password, email, request.AdminName) is { } weakness)
        {
            throw new UnprocessableException(weakness);
        }

        // Users are looked up across tenants here on purpose: emails are globally unique.
        if (await db.Users.IgnoreQueryFilters().AnyAsync(u => u.Email == email, cancellationToken))
        {
            throw new ConflictException("An account with this email already exists.");
        }

        var tenant = new Tenant { Name = request.OrganizationName.Trim() };
        var admin = new User
        {
            TenantId = tenant.Id,
            Email = email,
            DisplayName = request.AdminName.Trim(),
            PasswordHash = string.Empty,
            Role = Role.Admin,
        };
        admin.PasswordHash = hasher.HashPassword(admin, request.Password);

        db.Tenants.Add(tenant);
        db.Users.Add(admin);
        await SaveUniqueAsync(cancellationToken);

        return Issue(admin, tenant.Name);
    }

    public async Task<AuthResponse> LoginAsync(LoginRequest request, CancellationToken cancellationToken)
    {
        var email = NormalizeEmail(request.Email);
        var now = clock.GetUtcNow().UtcDateTime;
        var user = await db.Users.IgnoreQueryFilters().SingleOrDefaultAsync(u => u.Email == email, cancellationToken);

        if (user is null)
        {
            hasher.VerifyHashedPassword(Decoy, DecoyHash, request.Password);
            throw new UnauthorizedAppException(InvalidCredentials);
        }

        if (user.LockoutEndUtc is { } lockedUntil && lockedUntil > now)
        {
            // Do the same hashing work as a real attempt so a locked account is not distinguishable by timing.
            hasher.VerifyHashedPassword(user, user.PasswordHash, request.Password);
            LogLockedAttempt(user.Id);
            throw new UnauthorizedAppException(InvalidCredentials);
        }

        var result = hasher.VerifyHashedPassword(user, user.PasswordHash, request.Password);
        if (result == PasswordVerificationResult.Failed)
        {
            await RecordFailureAsync(user.Id, now, cancellationToken);
            throw new UnauthorizedAppException(InvalidCredentials);
        }

        if (!user.IsActive)
        {
            throw new UnauthorizedAppException(InvalidCredentials);
        }

        var changed = false;
        if (result == PasswordVerificationResult.SuccessRehashNeeded)
        {
            // The hasher's work factor has been raised since this hash was made: upgrade it transparently.
            user.PasswordHash = hasher.HashPassword(user, request.Password);
            changed = true;
        }

        if (user.FailedLoginCount != 0 || user.LockoutEndUtc is not null)
        {
            user.FailedLoginCount = 0;
            user.LockoutEndUtc = null;
            changed = true;
        }

        if (changed)
        {
            await db.SaveChangesAsync(cancellationToken);
        }

        var organization = await db.Tenants
            .Where(t => t.Id == user.TenantId)
            .Select(t => t.Name)
            .SingleAsync(cancellationToken);

        return Issue(user, organization);
    }

    public async Task<AuthResponse> RefreshProfileAsync(CancellationToken cancellationToken)
    {
        var id = currentUser.Id;
        var user = await db.Users.SingleOrDefaultAsync(u => u.Id == id && u.IsActive, cancellationToken)
            ?? throw new UnauthorizedAppException("This account is no longer active.");
        var organization = await db.Tenants
            .Where(t => t.Id == user.TenantId)
            .Select(t => t.Name)
            .SingleAsync(cancellationToken);

        return Issue(user, organization);
    }

    internal static string NormalizeEmail(string email) => email.Trim().ToLowerInvariant();

    internal static UserDto ToDto(User user) =>
        new(user.Id, user.Email, user.DisplayName, user.Role, user.IsActive);

    /// <summary>
    /// Counts a failed attempt with atomic SQL, so parallel guesses cannot lose increments, and starts a lockout
    /// once the threshold is reached.
    /// </summary>
    private async Task RecordFailureAsync(Guid userId, DateTime now, CancellationToken cancellationToken)
    {
        var settings = lockoutOptions.Value;
        var users = db.Users.IgnoreQueryFilters().Where(u => u.Id == userId);

        await users.ExecuteUpdateAsync(
            s => s.SetProperty(u => u.FailedLoginCount, u => u.FailedLoginCount + 1),
            cancellationToken);
        var failures = await users.Select(u => u.FailedLoginCount).SingleAsync(cancellationToken);
        LogFailedSignIn(userId, failures);

        if (failures >= settings.MaxFailedAttempts)
        {
            var until = now.AddMinutes(settings.DurationMinutes);
            await users.ExecuteUpdateAsync(
                s => s.SetProperty(u => u.LockoutEndUtc, (DateTime?)until).SetProperty(u => u.FailedLoginCount, 0),
                cancellationToken);
            LogLockout(userId, until);
        }
    }

    private AuthResponse Issue(User user, string organization)
    {
        var (token, expires) = tokens.Issue(user);
        return new AuthResponse(token, expires, ToDto(user), organization);
    }

    private async Task SaveUniqueAsync(CancellationToken cancellationToken)
    {
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (ex.InnerException is SqlException { Number: 2601 or 2627 })
        {
            // Two registrations raced past the AnyAsync check; the unique index is the real guarantee.
            throw new ConflictException("An account with this email already exists.");
        }
    }

    // Security events are logged by user id, never by password or by the (possibly mistyped) email.
    [LoggerMessage(Level = LogLevel.Warning, Message = "Failed sign-in for user {UserId} (consecutive failures: {Failures})")]
    private partial void LogFailedSignIn(Guid userId, int failures);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Account {UserId} locked until {Until:u} after repeated failed sign-ins")]
    private partial void LogLockout(Guid userId, DateTime until);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Sign-in attempt refused for locked account {UserId}")]
    private partial void LogLockedAttempt(Guid userId);
}
