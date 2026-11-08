using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CodeRank.Judge.Protocol;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed class JudgePayload
{
    public required long Id { get; init; }
    public required long ProblemId { get; init; }
    public required string Language { get; init; }
    public required string SourceCode { get; init; }
    public required IReadOnlyList<JudgePayloadTest> TestCases { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed class JudgePayloadTest
{
    public required long Id { get; init; }
    public required int Position { get; init; }
    public required string Input { get; init; }
    public required string ExpectedOutput { get; init; }
}

internal static class JudgePayloadContract
{
    public const string LeaseHeader = "X-CodeRank-Lease";
    public const string LeaseLostCode = "lease_lost";
    public const string TestsUnavailableCode = "judge_tests_unavailable";
    public const string InvalidPayloadCode = "judge_payload_invalid";
    public const int MaxSourceBytes = 64 * 1024;
    public const int MaxTests = 32;
    public const int MaxTestFieldBytes = 8 * 1024;
    public const int MaxTestSetBytes = 64 * 1024;
    // JSON escaping may expand a source or test byte up to sixfold.
    public const int MaxSnapshotCharacters = MaxTestSetBytes * 6 + 8 * 1024;
    public const int MaxResponseBytes = 800 * 1024;

    private static readonly Encoding StrictUtf8 = new UTF8Encoding(false, true);
    public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        AllowDuplicateProperties = false,
        NumberHandling = JsonNumberHandling.Strict,
        PropertyNameCaseInsensitive = false,
        MaxDepth = 8
    };

    public static bool IsValid(JudgePayload? payload) =>
        payload is { Id: > 0, ProblemId: > 0, Language: "cpp17" or "python3" } &&
        !string.IsNullOrWhiteSpace(payload.SourceCode) && Utf8Size(payload.SourceCode) is > 0 and <= MaxSourceBytes &&
        AreValidTests(payload.TestCases);

    public static bool AreValidTests(IReadOnlyList<JudgePayloadTest>? tests)
    {
        if (tests is not { Count: > 0 and <= MaxTests }) return false;

        var ids = new HashSet<long>();
        var previousPosition = 0;
        var totalBytes = 0;
        foreach (var test in tests)
        {
            if (test is null || test.Id <= 0 || !ids.Add(test.Id) ||
                test.Position <= previousPosition || test.Position > MaxTests)
                return false;

            var inputBytes = Utf8Size(test.Input);
            var outputBytes = Utf8Size(test.ExpectedOutput);
            if (inputBytes is < 0 or > MaxTestFieldBytes || outputBytes is < 0 or > MaxTestFieldBytes)
                return false;

            totalBytes += inputBytes + outputBytes;
            if (totalBytes > MaxTestSetBytes) return false;
            previousPosition = test.Position;
        }
        return true;
    }

    private static int Utf8Size(string? value)
    {
        if (value is null) return -1;
        try { return StrictUtf8.GetByteCount(value); }
        catch (EncoderFallbackException) { return -1; }
    }
}
