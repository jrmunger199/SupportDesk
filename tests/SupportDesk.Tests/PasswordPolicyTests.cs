using SupportDesk.Api.Security;

namespace SupportDesk.Tests;

public class PasswordPolicyTests
{
    [Theory]
    [InlineData("CorrectHorse!123")]
    [InlineData("tr0ub4dor&3-extended")]
    [InlineData("a-very-long-passphrase-of-words")]
    public void Reasonable_passwords_are_accepted(string password) =>
        Assert.Null(PasswordPolicy.Check(password, "someone@example.test", "Some One"));

    [Theory]
    [InlineData("short")]
    [InlineData("123456789")]
    public void Short_passwords_are_refused(string password) =>
        Assert.NotNull(PasswordPolicy.Check(password, "someone@example.test", "Some One"));

    [Theory]
    [InlineData("password123")]
    [InlineData("PASSWORD123")]
    [InlineData("QwertyUiop")]
    [InlineData("1234567890")]
    public void Common_passwords_are_refused_regardless_of_case(string password) =>
        Assert.Contains("too common", PasswordPolicy.Check(password, "x@example.test", "Some One"), StringComparison.Ordinal);

    [Fact]
    public void Repetitive_passwords_are_refused() =>
        Assert.NotNull(PasswordPolicy.Check("abababababab", "x@example.test", "Some One"));

    [Fact]
    public void Passwords_containing_the_email_name_are_refused() =>
        Assert.Contains("email", PasswordPolicy.Check("!!janedoe!!77", "JaneDoe@example.test", "Someone"), StringComparison.Ordinal);

    [Fact]
    public void Passwords_containing_the_display_name_are_refused() =>
        Assert.Contains("name", PasswordPolicy.Check("My-Priya-2026!", "p@example.test", "Priya Nair"), StringComparison.Ordinal);

    [Fact]
    public void Very_short_name_parts_do_not_cause_false_positives() =>
        Assert.Null(PasswordPolicy.Check("Mr-Lee-has-a-long-password", "x@example.test", "Al Li"));
}
