using System.Globalization;
using System.Text;

namespace CodeRank.Judge.Sandbox;

internal static partial class SandboxReceipt
{
    public static string? Read(string path)
    {
        try
        {
            using var file = File.OpenRead(path);
            Span<byte> bytes = stackalloc byte[9];
            var count = file.ReadAtLeast(bytes, bytes.Length, false);
            return count == bytes.Length ? "?" : Encoding.ASCII.GetString(bytes[..count]);
        }
        catch (FileNotFoundException) { return null; }
    }

    internal static SandboxFailure? Explain(string? receipt, ReadOnlySpan<byte> stderr, bool timedOut,
        SandboxStderrStatus stderrStatus = SandboxStderrStatus.Complete)
    {
        if (receipt == "R") return null;
        if (receipt is null)
            return new(new(SandboxStage.LauncherBootstrap, timedOut ? SandboxFault.Timeout : SandboxFault.Rejected));
        if (receipt.Length == 0)
            return new(ExplainNsJailSetup(stderr, timedOut, stderrStatus));
        if (receipt == "RF") return new(new(SandboxStage.JailProgramStart));

        var ready = receipt.StartsWith('R');
        var value = ready ? receipt.AsSpan(1) : receipt.AsSpan();
        if (value.Length is < 4 or > 7 || value[0] != 'F' || value[2] != ':' ||
            !int.TryParse(value[3..], NumberStyles.None, CultureInfo.InvariantCulture, out var errno) || errno > 4095)
            return new(new(SandboxStage.Receipt, SandboxFault.InvalidData));

        var stage = (ready, value[1]) switch
        {
            (false, 'L') => SandboxStage.LauncherBootstrap,
            (false, 'C') => SandboxStage.LauncherCgroup,
            (false, 'A') => SandboxStage.JailAttestation,
            (false, 'M') => SandboxStage.JailMounts,
            (false, 'S') => SandboxStage.JailSeccomp,
            (true, 'E') => SandboxStage.JailProgramStart,
            _ => SandboxStage.Receipt
        };
        if (stage == SandboxStage.Receipt) return new(new(stage, SandboxFault.InvalidData));
        return new(new(stage, errno == 0 ? SandboxFault.Rejected : SandboxFault.Native, errno));
    }

    private static SandboxDiagnostic ExplainNsJailSetup(ReadOnlySpan<byte> stderr, bool timedOut,
        SandboxStderrStatus stderrStatus)
    {
        // An empty receipt means the native entrypoint never attested and never ran the program.
        // After an interrupted read, only newline-terminated records can supply an errno.
        var completeLength = stderrStatus == SandboxStderrStatus.Complete
            ? stderr.Length : stderr.LastIndexOf((byte)'\n') + 1;
        var text = Encoding.UTF8.GetString(stderr[..completeLength]);
        var failure = ClassifyNsJailSetup(text);
        if (failure is null && completeLength < stderr.Length &&
            CloneCandidate().IsMatch(Encoding.UTF8.GetString(stderr[completeLength..])))
            failure = new(SandboxStage.NsJailNamespace, NamespaceDetail: NsJailNamespaceDetail.CloneTruncated);

        var diagnostic = failure ??
            new(SandboxStage.NsJailSetup, timedOut ? SandboxFault.Timeout : SandboxFault.Rejected);
        return stderrStatus != SandboxStderrStatus.Complete || diagnostic.NamespaceDetail.HasValue
            ? diagnostic with { StderrStatus = stderrStatus }
            : diagnostic;
    }

    private static SandboxDiagnostic? ClassifyNsJailSetup(string text)
    {
        if (text.Contains("Couldn't prepare sandboxing policy", StringComparison.Ordinal) ||
            text.Contains("seccomp(SECCOMP_SET_MODE_FILTER", StringComparison.Ordinal) ||
            text.Contains("prctl(PR_SET_SECCOMP, SECCOMP_MODE_FILTER)", StringComparison.Ordinal))
            return new(SandboxStage.NsJailSeccomp);
        if (ExplainMountFailure(text) is { } mountFailure) return mountFailure;
        if (text.Contains("Failed to build mount tree", StringComparison.Ordinal) ||
            text.Contains("Failed to mount mandatory point", StringComparison.Ordinal) ||
            text.Contains("pivot_root(", StringComparison.Ordinal))
            return new(SandboxStage.NsJailMount);
        return ExplainNamespaceFailure(text);
    }
}
