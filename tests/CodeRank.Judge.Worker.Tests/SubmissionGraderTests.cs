using CodeRank.Judge.Protocol;
using CodeRank.Judge.Worker;
using Xunit;

namespace CodeRank.Judge.Worker.Tests;

public sealed class SubmissionGraderTests
{
    [Theory]
    [InlineData("cpp17")]
    [InlineData("python3")]
    public async Task Correct_output_is_accepted_and_only_inputs_are_passed_to_runner(string language)
    {
        var payload = Payload(language, "expected-one\n", "expected-two");
        var runner = new StubRunner((actualLanguage, code, inputs, _) =>
        {
            Assert.Equal(language, actualLanguage);
            Assert.Equal(payload.SourceCode, code);
            Assert.Equal(["input-1", "input-2"], inputs);
            return Task.FromResult<IReadOnlyList<IsolatedCaseResult>>([Exited("expected-one"), Exited("expected-two\r\n")]);
        });

        Assert.Equal("Accepted", await new SubmissionGrader(runner).GradeAsync(payload, Token));
    }

    [Theory]
    [InlineData("line\nnext", "line\r\nnext\r\n", "Accepted")]
    [InlineData("text\n", "text \n", "WrongAnswer")]
    [InlineData("text", "text\n\n", "WrongAnswer")]
    [InlineData("one\ntwo", "one\rtwo", "WrongAnswer")]
    [InlineData("привіт", "Привіт", "WrongAnswer")]
    public async Task Output_comparison_is_ordinal_and_preserves_spaces_and_extra_lines(string expected, string actual, string verdict)
    {
        var runner = FromResults(Exited(actual));
        Assert.Equal(verdict, await new SubmissionGrader(runner).GradeAsync(Payload("python3", expected), Token));
    }

    [Theory]
    [InlineData("CompilationError", "CompilationError")]
    [InlineData("RuntimeError", "RuntimeError")]
    [InlineData("TimeLimitExceeded", "TimeLimitExceeded")]
    [InlineData("MemoryLimitExceeded", "MemoryLimitExceeded")]
    [InlineData("OutputLimitExceeded", "RuntimeError")]
    [InlineData("SystemError", "SystemError")]
    public async Task Sandbox_status_maps_to_existing_submission_verdict(string status, string verdict)
    {
        var runner = FromResults(new IsolatedCaseResult { Status = Enum.Parse<IsolatedCaseStatus>(status), StandardOutput = "" });
        Assert.Equal(verdict, await new SubmissionGrader(runner).GradeAsync(Payload("cpp17", "expected"), Token));
    }

    [Fact]
    public async Task First_student_failure_wins_unless_infrastructure_failed()
    {
        var payload = Payload("python3", "one", "two");
        var studentFailure = new IsolatedCaseResult { Status = IsolatedCaseStatus.TimeLimitExceeded, StandardOutput = "" };
        var infrastructureFailure = new IsolatedCaseResult { Status = IsolatedCaseStatus.SystemError, StandardOutput = "" };
        Assert.Equal("WrongAnswer",
            await new SubmissionGrader(FromResults(Exited("wrong"), studentFailure)).GradeAsync(payload, Token));
        Assert.Equal("SystemError",
            await new SubmissionGrader(FromResults(Exited("wrong"), infrastructureFailure)).GradeAsync(payload, Token));
    }

    [Fact]
    public async Task Unavailable_runner_or_incomplete_results_fail_closed()
    {
        var payload = Payload("python3", "one");
        var unavailable = new StubRunner((_, _, _, _) => throw new IOException("private diagnostic canary"));
        Assert.Equal("SystemError", await new SubmissionGrader(unavailable).GradeAsync(payload, Token));
        Assert.Equal("SystemError", await new SubmissionGrader(FromResults()).GradeAsync(payload, Token));
    }

    internal static JudgePayload Payload(string language, params string[] expected) => new()
    {
        Id = 4,
        ProblemId = 8,
        Language = language,
        SourceCode = language == "cpp17" ? "int main() {}" : "print('synthetic')",
        TestCases = expected.Select((output, index) => new JudgePayloadTest
        {
            Id = index + 1, Position = index + 1, Input = $"input-{index + 1}", ExpectedOutput = output
        }).ToArray()
    };

    internal static IsolatedCaseResult Exited(string output) => new()
    {
        Status = IsolatedCaseStatus.Exited,
        StandardOutput = output
    };

    private static StubRunner FromResults(params IsolatedCaseResult[] results) =>
        new((_, _, _, _) => Task.FromResult<IReadOnlyList<IsolatedCaseResult>>(results));

    internal sealed class StubRunner(Func<string, string, IReadOnlyList<string>, CancellationToken,
        Task<IReadOnlyList<IsolatedCaseResult>>> run) : IIsolatedJudgeRunner
    {
        public Task<IReadOnlyList<IsolatedCaseResult>> RunAsync(string language, string sourceCode,
            IReadOnlyList<string> inputs, CancellationToken token) => run(language, sourceCode, inputs, token);
    }

    private static CancellationToken Token => TestContext.Current.CancellationToken;
}
