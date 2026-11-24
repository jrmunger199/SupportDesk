namespace SupportDesk.Api.Security;

/// <summary>
/// Rules for new passwords beyond the 10-character minimum enforced by validation attributes. Following current
/// NIST guidance, it blocks predictable choices rather than demanding arbitrary character mixes.
/// </summary>
public static class PasswordPolicy
{
    private const int MinimumDistinctCharacters = 5;
    private const int MinimumIdentifierLength = 4;

    // Common passwords that already satisfy the length rule, so the length check alone would let them through.
    private static readonly HashSet<string> Common = new(StringComparer.OrdinalIgnoreCase)
    {
        "password123", "password1234", "password12345", "passw0rd123", "1234567890", "12345678910",
        "123456789012", "0123456789", "qwertyuiop", "qwerty12345", "qwertyuiop123", "1q2w3e4r5t",
        "1qaz2wsx3edc", "iloveyou123", "welcome123", "welcome1234", "letmein1234", "administrator",
        "adminadmin", "admin12345", "abcdefghij", "abc1234567", "changeme123", "changemenow",
        "trustno1234", "monkey12345", "dragon12345", "football123", "baseball123", "supportdesk",
    };

    /// <summary>Returns a message explaining what is wrong, or null when the password is acceptable.</summary>
    public static string? Check(string password, string email, string displayName)
    {
        if (password.Length < 10)
        {
            return "Use at least 10 characters.";
        }

        if (Common.Contains(password))
        {
            return "That password is too common. Choose something less predictable.";
        }

        if (password.Distinct().Count() < MinimumDistinctCharacters)
        {
            return "That password repeats too few different characters.";
        }

        var lowered = password.ToLowerInvariant();
        var localPart = email.Split('@')[0].ToLowerInvariant();
        if (localPart.Length >= MinimumIdentifierLength && lowered.Contains(localPart, StringComparison.Ordinal))
        {
            return "Your password should not contain your email address.";
        }

        var nameParts = displayName.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (nameParts.Any(p => p.Length >= MinimumIdentifierLength && lowered.Contains(p.ToLowerInvariant(), StringComparison.Ordinal)))
        {
            return "Your password should not contain your name.";
        }

        return null;
    }
}
