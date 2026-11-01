using System.ComponentModel.DataAnnotations;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CodeRank.Api.Validation;

[AttributeUsage(AttributeTargets.Property)]
public sealed class UnicodeNameAttribute : ValidationAttribute
{
    public UnicodeNameAttribute() => ErrorMessage = "Ім’я та прізвище повинні містити принаймні одну літеру Unicode.";

    public override bool IsValid(object? value) => value is null || value is string text
        && text.EnumerateRunes().Any(rune => rune.Value != 0x02BC && Rune.IsLetter(rune));
}

[AttributeUsage(AttributeTargets.Property)]
public sealed class RegistrationPasswordAttribute : ValidationAttribute
{
    public RegistrationPasswordAttribute() => ErrorMessage = "Пароль занадто передбачуваний. Оберіть інший пароль або довшу фразу.";
    public override bool IsValid(object? value) => value is null || value is string text && RegistrationPasswordPolicy.IsAcceptable(text);
}

public static class RegistrationPasswordPolicy
{
    private static readonly HashSet<string> CommonHashes;
    private static readonly int[] DecimalDigitStarts;
    private static readonly char[] ComparisonTrimCharacters;

    static RegistrationPasswordPolicy()
    {
        using var stream = typeof(RegistrationPasswordPolicy).Assembly
            .GetManifestResourceStream("CodeRank.RegistrationPolicy.json")
            ?? throw new InvalidOperationException("Registration policy resource is missing.");
        using var policy = JsonDocument.Parse(stream);
        CommonHashes = policy.RootElement.GetProperty("commonPasswordSha256").EnumerateArray()
            .Select(item => item.GetString()!).ToHashSet(StringComparer.Ordinal);
        DecimalDigitStarts = policy.RootElement.GetProperty("decimalDigitStarts").EnumerateArray()
            .Select(item => item.GetInt32()).ToArray();
        ComparisonTrimCharacters = policy.RootElement.GetProperty("comparisonTrimCodePoints").EnumerateArray()
            .Select(item => (char)item.GetInt32()).ToArray();
    }

    public static bool IsAcceptable(string password)
    {
        if (password.Length is < 12 or > 128) return false;
        string comparison;
        try
        {
            comparison = string.Concat(password.Normalize(NormalizationForm.FormKC).Trim(ComparisonTrimCharacters)
                .Select(character => character is >= 'A' and <= 'Z' ? (char)(character + 32) : character));
        }
        catch (ArgumentException) { return false; }
        if (comparison.Length == 0) return false;
        var runes = comparison.EnumerateRunes().ToArray();
        // Reject whole-password repetitions of a short motif, not all repeated characters.
        for (var period = 1; period <= Math.Min(8, runes.Length / 2); period++)
        {
            if (runes.Length % period == 0 && runes.Where((rune, i) => rune != runes[i % period]).Any() == false)
                return false;
        }
        var digits = runes.Select(DigitValue).ToArray();
        if (digits.Length >= 3 && digits.All(digit => digit >= 0))
        {
            var step = (digits[1] - digits[0] + 10) % 10;
            if (digits.Skip(1).Where((digit, i) => (digit - digits[i] + 10) % 10 != step).Any() == false)
                return false;
        }
        // SHA-256 is solely a public denylist lookup. Actual storage still uses PasswordHasher<User>.
        var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(comparison))).ToLowerInvariant();
        return !CommonHashes.Contains(digest);
    }

    private static int DigitValue(Rune rune)
    {
        foreach (var start in DecimalDigitStarts)
            if (rune.Value >= start && rune.Value <= start + 9) return rune.Value - start;
        return -1;
    }
}
