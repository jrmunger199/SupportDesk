using System.Text.RegularExpressions;

namespace SupportDesk.Api.Services;

/// <summary>
/// The wire format for @mentions inside ticket text: <c>@[Display Name](user-id)</c>. The id is what the server trusts;
/// the name is only a readable fallback that is rewritten to the person's current name on save, so a client can never
/// make a mention say one thing and point at someone else.
/// </summary>
public static partial class MentionTokens
{
    public const int MaxMentionsPerText = 10;

    // The timeout guards against pathological input; the pattern itself is linear.
    [GeneratedRegex(
        @"@\[(?<name>[^\]\r\n]{1,120})\]\((?<id>[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12})\)",
        RegexOptions.CultureInvariant,
        250)]
    private static partial Regex Token();

    public static IReadOnlyList<Guid> ExtractIds(string text) =>
        [.. Token().Matches(text).Select(m => Guid.Parse(m.Groups["id"].Value)).Distinct()];

    public static string Format(Guid id, string displayName) => $"@[{Clean(displayName)}]({id})";

    /// <summary>Rewrites every token whose id is known so its name is the person's real, current name.</summary>
    public static string Normalize(string text, IReadOnlyDictionary<Guid, string> names) =>
        Token().Replace(text, match =>
        {
            var id = Guid.Parse(match.Groups["id"].Value);
            return names.TryGetValue(id, out var name) ? Format(id, name) : match.Value;
        });

    /// <summary>Plain text with tokens shown as <c>@Name</c>, for snippets and notifications.</summary>
    public static string Flatten(string text) => Token().Replace(text, match => "@" + match.Groups["name"].Value);

    // Brackets and control characters would break the token's own syntax.
    private static string Clean(string name) =>
        new string([.. name.Where(c => c is not ('[' or ']') && !char.IsControl(c))]).Trim();
}
