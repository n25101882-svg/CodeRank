using System.Text.RegularExpressions;

namespace CodeRank.Judge.Protocol;

internal static partial class WorkerCredentials
{
    public const string Scheme = "CodeRankJudge";
    public const string WorkerIdHeader = "X-CodeRank-Worker";
    public const int KeyLength = 32;

    public static bool IsValidId(string? id) => id is not null && WorkerIdPattern().IsMatch(id);

    public static bool TryDecodeKey(string? encoded, Span<byte> key) =>
        encoded is { Length: 44 } && Convert.TryFromBase64String(encoded, key, out var written) &&
        written == KeyLength && encoded == Convert.ToBase64String(key[..written]);

    [GeneratedRegex(@"\A[a-z0-9]{1,64}\z")]
    private static partial Regex WorkerIdPattern();
}
