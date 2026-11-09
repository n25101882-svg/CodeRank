using System.Globalization;
using CodeRank.Judge.Worker;

namespace CodeRank.Judge.Sandbox;

internal static class LinuxSmokeTests
{
    private static readonly string HostCanary = SandboxLayout.Smoke.Scratch + "/smoke-host-canary";
    private const string EnvCanary = "CODERANK_SMOKE_CANARY";

    public static async Task<int> RunAsync(IIsolatedJudgeRunner runner, SandboxLimits limits, CancellationToken token)
    {
        // The only external inputs to this harness are the reviewed numeric limits.
        var created = false;
        var previous = Environment.GetEnvironmentVariable(EnvCanary);
        try
        {
            await using (var file = new FileStream(HostCanary, FileMode.CreateNew, FileAccess.Write))
            {
                created = true;
                await file.WriteAsync("synthetic-canary"u8.ToArray(), token);
            }
            Environment.SetEnvironmentVariable(EnvCanary, "synthetic-canary");
            var passed = 0;
            var total = 0;
            async Task Check(string name, string language, string code, IsolatedCaseStatus expected,
                string[]? inputs = null, string[]? outputs = null)
            {
                var result = await runner.RunAsync(language, code, inputs ?? [""], token);
                var ok = result.Count == (inputs?.Length ?? 1) && result.All(item => item.Status == expected) &&
                    (outputs is null || result.Select(item => item.StandardOutput).SequenceEqual(outputs)) &&
                    Clean();
                Console.WriteLine($"{(ok ? "PASS" : "FAIL")} {name} ({string.Join(", ", result.Select(item => item.Status))})");
                if (ok) passed++;
                total++;
            }

            await Check("C++17 compile once, separate cases", "cpp17", """
                #include <cerrno>
                #include <fcntl.h>
                #include <iostream>
                int main() {
                    if (fcntl(3, F_GETFD) != -1 || errno != EBADF) return 2;
                    int value;
                    if (!(std::cin >> value)) return 3;
                    std::cout << value + 1 << '\n';
                }
                """, IsolatedCaseStatus.Exited, ["8\n", "20\n"], ["9\n", "21\n"]);
            await Check("C++17 syntax error", "cpp17", "int main( {", IsolatedCaseStatus.CompilationError);
            await Check("Python stdin/stdout", "python3", "print(input()[::-1])", IsolatedCaseStatus.Exited,
                ["coderank\n"], ["knaredoc\n"]);
            await Check("Python exception", "python3", "raise ValueError('synthetic')", IsolatedCaseStatus.RuntimeError);
            await Check("wall deadline", "python3", "while True: pass", IsolatedCaseStatus.TimeLimitExceeded);
            var memory = (limits.TestMemoryMiB + 32).ToString(CultureInfo.InvariantCulture);
            await Check("cgroup memory limit", "python3", $$"""
                data = bytearray({{memory}} * 1024 * 1024)
                for i in range(0, len(data), 4096):
                    data[i] = 1
                """, IsolatedCaseStatus.MemoryLimitExceeded);
            await Check("stdout cap", "python3", "import os\nwhile True: os.write(1, b'x' * 4096)",
                IsolatedCaseStatus.OutputLimitExceeded);
            await Check("stderr cap", "python3", "import os\nwhile True: os.write(2, b'x' * 4096)",
                IsolatedCaseStatus.OutputLimitExceeded);
            await Check("child processes denied", "python3", """
                import errno, os
                try:
                    os.fork()
                except OSError as error:
                    assert error.errno == errno.EPERM
                else:
                    raise AssertionError('fork allowed')
                print('blocked')
                """, IsolatedCaseStatus.Exited, outputs: ["blocked\n"]);
            await Check("local HTTPS tunnel inaccessible", "python3", """
                import errno, socket
                try:
                    with socket.socket(socket.AF_INET, socket.SOCK_STREAM) as client:
                        client.settimeout(0.25)
                        client.connect(('127.0.0.1', 7160))
                except OSError as error:
                    assert error.errno == errno.EPERM
                else:
                    raise AssertionError('network allowed')
                print('blocked')
                """, IsolatedCaseStatus.Exited, outputs: ["blocked\n"]);
            await Check("student NETLINK_ROUTE and other sockets denied", "cpp17", """
                #include <cerrno>
                #include <cstdio>
                #include <linux/netlink.h>
                #include <sys/socket.h>
                #include <sys/syscall.h>
                #include <unistd.h>
                int main() {
                    if (syscall(SYS_socket, AF_NETLINK, SOCK_RAW, NETLINK_ROUTE) != -1 || errno != EPERM)
                        return 1;
                    if (syscall(SYS_socket, AF_INET6, SOCK_STREAM, 0) != -1 || errno != EPERM)
                        return 2;
                    int pair[2];
                    if (syscall(SYS_socketpair, AF_UNIX, SOCK_STREAM, 0, pair) != -1 || errno != EPERM)
                        return 3;
                    std::puts("blocked");
                }
                """, IsolatedCaseStatus.Exited, outputs: ["blocked\n"]);
            await Check("host paths and environment inaccessible", "python3", $$"""
                import os
                assert '{{EnvCanary}}' not in os.environ
                for path in ('{{HostCanary}}', '/proc/self/environ', '/sys/fs/cgroup/cgroup.procs'):
                    try:
                        with open(path, 'rb'):
                            raise AssertionError('host file reachable')
                    except OSError:
                        pass
                print('isolated')
                """, IsolatedCaseStatus.Exited, outputs: ["isolated\n"]);
            await Check("bounded tmpfs and read-only root", "python3", $$"""
                import errno, os
                stats = os.statvfs('/tmp')
                assert 0 < stats.f_blocks * stats.f_frsize <= {{limits.TemporaryMiB * SandboxLimits.MiB}}
                assert stats.f_files <= 4096
                root_required = os.ST_RDONLY | os.ST_NOSUID | os.ST_NODEV
                payload_required = root_required | os.ST_NOEXEC
                tmp_required = os.ST_NOSUID | os.ST_NODEV | os.ST_NOEXEC
                assert (os.statvfs('/').f_flag & root_required) == root_required, 'root mount flags'
                assert (os.statvfs('/job/Main.py').f_flag & payload_required) == payload_required, 'payload mount flags'
                assert (stats.f_flag & (tmp_required | os.ST_RDONLY)) == tmp_required, 'tmpfs mount flags'
                try:
                    os.open('/job/write-test', os.O_WRONLY | os.O_CREAT, 0o600)
                except OSError as error:
                    assert error.errno in (errno.EROFS, errno.EACCES)
                else:
                    raise AssertionError('root is writable')
                print('bounded')
                """, IsolatedCaseStatus.Exited, outputs: ["bounded\n"]);
            var cancelled = await CancelAttestedRunAsync(runner, token);
            Console.WriteLine(cancelled ? "PASS cancellation and cgroup cleanup" : "FAIL cancellation and cgroup cleanup");
            if (cancelled) passed++;
            total++;
            Console.WriteLine($"{passed}/{total} Linux sandbox checks passed.");
            return passed == total ? 0 : 1;
        }
        finally
        {
            Environment.SetEnvironmentVariable(EnvCanary, previous);
            if (created) File.Delete(HostCanary);
        }
    }

    private static bool Clean() =>
        !LinuxSandboxHost.HasScratchFiles(SandboxLayout.Smoke) &&
        !Directory.EnumerateDirectories(SandboxLayout.Smoke.Cgroup, "job-*").Any();

    private static async Task<bool> CancelAttestedRunAsync(IIsolatedJudgeRunner runner, CancellationToken token)
    {
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(token);
        var run = runner.RunAsync("python3", "while True: pass", [""], cancel.Token);
        while (!run.IsCompleted && !token.IsCancellationRequested)
        {
            if (Directory.EnumerateDirectories(SandboxLayout.Smoke.Scratch, "job-*").Any(HasReceipt))
            {
                cancel.Cancel();
                break;
            }
            await Task.Delay(10);
        }
        try
        {
            await run;
            return false;
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested && !token.IsCancellationRequested)
        {
            return Clean();
        }
    }

    private static bool HasReceipt(string directory)
    {
        try { return File.ReadAllText(Path.Combine(directory, "status")) == "R"; }
        catch (IOException) { return false; }
    }
}
