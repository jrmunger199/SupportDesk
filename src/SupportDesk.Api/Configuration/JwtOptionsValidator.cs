using Microsoft.Extensions.Options;

namespace SupportDesk.Api.Configuration;

/// <summary>
/// A signing key that is long enough but publicly known is worse than no key: the dev key is in the repository and
/// the example env file used to carry a placeholder. Outside Development, refuse to start with either.
/// </summary>
public sealed class JwtOptionsValidator(IHostEnvironment environment) : IValidateOptions<JwtOptions>
{
    private static readonly string[] KnownPlaceholders = ["development-only", "replace-with", "change-me", "changeme"];

    public ValidateOptionsResult Validate(string? name, JwtOptions options)
    {
        if (environment.IsDevelopment())
        {
            return ValidateOptionsResult.Success;
        }

        return KnownPlaceholders.Any(p => options.Key.Contains(p, StringComparison.OrdinalIgnoreCase))
            ? ValidateOptionsResult.Fail("Jwt:Key looks like a placeholder or the published development key. Generate a real secret, for example with: openssl rand -base64 48")
            : ValidateOptionsResult.Success;
    }
}
