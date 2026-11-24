using System.ComponentModel.DataAnnotations;

namespace SupportDesk.Api.Configuration;

public sealed class JwtOptions
{
    public const string SectionName = "Jwt";

    /// <summary>HMAC signing key. Must be supplied by the environment in production (never committed).</summary>
    [Required, MinLength(32)]
    public string Key { get; set; } = string.Empty;

    [Required]
    public string Issuer { get; set; } = "SupportDesk";

    [Required]
    public string Audience { get; set; } = "SupportDesk";

    [Range(1, 1440)]
    public int ExpiryMinutes { get; set; } = 60;
}

/// <summary>Per-account protection against password guessing, on top of the per-IP rate limit.</summary>
public sealed class AccountLockoutOptions
{
    public const string SectionName = "Lockout";

    [Range(1, 100)]
    public int MaxFailedAttempts { get; set; } = 5;

    [Range(1, 1440)]
    public int DurationMinutes { get; set; } = 15;
}

public sealed class ThrottlingOptions
{
    public const string SectionName = "Throttling";

    /// <summary>Requests per minute, per client IP, allowed on the sign-in and registration endpoints.</summary>
    [Range(1, 100_000)]
    public int AuthPermitLimit { get; set; } = 10;
}
