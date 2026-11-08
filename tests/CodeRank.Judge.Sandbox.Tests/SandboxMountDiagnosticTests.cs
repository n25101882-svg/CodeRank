using System.Text;
using CodeRank.Judge.Sandbox;
using Xunit;

namespace CodeRank.Judge.Sandbox.Tests;

public sealed class SandboxMountDiagnosticTests
{
    private const string MountError = "SystemError stage=NsJailMount error=";
    private const string TreeFailure = "[E][2026-11-08T12:17:12+0000][22359] initCloneNs():270 Failed to build mount tree\n";

    [Theory]
    [InlineData("mount('/', MS_REC|MS_PRIVATE): Operation not permitted", "make_mount_private", "mount_tree", 1, "EPERM")]
    [InlineData("mount('/tmp/nsjail.110.root', tmpfs): No such device", "mount", "tmpfs", 19, "ENODEV")]
    [InlineData("mount('/tmp/nsjail.110.root', MS_REMOUNT|MS_BIND|MS_RDONLY): Read-only file system",
        "remount", "mount_tree", 30, "EROFS")]
    [InlineData("umount2('/tmp/nsjail.110.tmp', MNT_DETACH): Device or resource busy", "umount2", "mount_tree", 16, "EBUSY")]
    public void Build_tree_summary_retains_the_verified_legacy_operation_and_errno(
        string message, string operation, string target, int errno, string errnoName)
    {
        var stderr = Line('E', "buildMountTree", message) + TreeFailure;
        Assert.Equal($"{MountError}Native operation={operation} target={target} errno={errno} errno_name={errnoName}",
            Explain(stderr).ToLogLine());
    }

    [Theory]
    [InlineData("mount('/opt/coderank-judge-sandbox/compile-root' -> '/tmp/private-canary//'): Operation not permitted",
        "/", "rootfs", 1, "EPERM")]
    [InlineData("mount('none' -> '/tmp/private-canary//tmp'): Invalid argument", "/tmp", "tmpfs", 22, "EINVAL")]
    [InlineData("mount('/dev/null' -> '/tmp/private-canary//dev/null'): try 'chmod o+x' on source path: Permission denied",
        "/dev/null", "file", 13, "EACCES")]
    [InlineData("mount('/private/source: code-canary' -> '/tmp/private-canary//job/Main.cpp'): Not a directory",
        "/job/Main.cpp", "file", 20, "ENOTDIR")]
    public void Legacy_mandatory_mount_uses_fixed_target_codes_and_never_emits_paths(
        string message, string destination, string target, int errno, string errnoName)
    {
        var stderr = Line('W', "mountSinglePoint", message) + MandatoryFailure(destination) + TreeFailure;
        Assert.Equal($"{MountError}Native operation=mount target={target} errno={errno} errno_name={errnoName}",
            Explain(stderr).ToLogLine());
    }

    [Theory]
    [InlineData("/", " target=rootfs")]
    [InlineData("/tmp", " target=tmpfs")]
    [InlineData("/job/Main.py", " target=file")]
    [InlineData("/private/canary", "")]
    public void Mandatory_point_summary_can_identify_a_target_without_an_errno(string destination, string expected)
    {
        Assert.Equal(MountError + "Rejected operation=mount" + expected,
            Explain(MandatoryFailure(destination) + TreeFailure).ToLogLine());
    }

    [Fact]
    public void Build_tree_summary_without_a_cause_does_not_invent_an_errno()
    {
        Assert.Equal(MountError + "Rejected operation=build_mount_tree", Explain(TreeFailure).ToLogLine());
    }

    [Fact]
    public void Failed_work_directory_does_not_inherit_errno_from_a_retried_access_probe()
    {
        var stderr = Line('W', "tryCreateDir", "access('/private/canary', R_OK): Permission denied") +
            Line('E', "findWorkDir", "Failed to create work directory for 'root'") + TreeFailure;
        Assert.Equal(MountError + "Rejected operation=find_work_dir target=mount_tree", Explain(stderr).ToLogLine());
    }

    [Fact]
    public void Parent_directory_preparation_keeps_its_errno_through_both_wrappers()
    {
        var stderr = Line('W', "createDirRecursively", "openat('7', 'private-canary', O_DIRECTORY | O_CLOEXEC): Not a directory") +
            Line('W', "mountSinglePoint", "Failed to create parent directories for '/tmp/private-canary//job/Main.cpp'") +
            MandatoryFailure("/job/Main.cpp") + TreeFailure;
        Assert.Equal(MountError + "Native operation=prepare_mount_target target=file errno=20 errno_name=ENOTDIR",
            Explain(stderr).ToLogLine());
    }

    [Fact]
    public void Pivot_root_accepts_the_quoted_log_format_and_decorated_envelope()
    {
        var stderr = "\u001b[1;31m[E][[Time conv error]][22359] initCloneNs():285 " +
            @"pivot_root('/tmp/private\'canary', '/tmp/private\'canary'): Invalid argument" + "\u001b[0m\r\n";
        Assert.Equal(MountError + "Native operation=pivot_root target=mount_tree errno=22 errno_name=EINVAL",
            Explain(stderr).ToLogLine());
    }

    [Theory]
    [InlineData("fsopen('tmpfs'): Function not implemented", "fsopen", 38, "ENOSYS")]
    [InlineData("fsconfig(size=16777216): Invalid argument", "fsconfig", 22, "EINVAL")]
    [InlineData("fsconfig(CMD_CREATE): No space left on device", "fsconfig", 28, "ENOSPC")]
    [InlineData("fsmount('tmpfs'): Cannot allocate memory", "fsmount", 12, "ENOMEM")]
    public void Detached_tmpfs_failures_report_the_new_api_syscall(string message, string syscall, int errno, string name)
    {
        var stderr = Line('W', "createDetachedTmpfs", message) +
            Line('E', "buildMountTree", "Failed to create root tmpfs") + TreeFailure;
        Assert.Equal($"{MountError}Native operation={syscall} target=tmpfs errno={errno} errno_name={name}",
            Explain(stderr).ToLogLine());
    }

    [Theory]
    [InlineData("doBindMountAt", "open_tree('/opt/coderank-judge-sandbox/runtime-root'): Permission denied",
        "/", "open_tree", "rootfs", 13, "EACCES")]
    [InlineData("doBindMountAt", "open_tree('/private/payload-canary'): No such file or directory",
        "/job/program", "open_tree", "file", 2, "ENOENT")]
    [InlineData("openMountForRemount", "open_tree(root_fd, 'tmp'): Bad file descriptor",
        "/tmp", "open_tree", "tmpfs", 9, "EBADF")]
    [InlineData("doBindMountAt", "move_mount('/private/payload-canary' -> 'job/Main.py'): Invalid cross-device link",
        "/job/Main.py", "move_mount", "file", 18, "EXDEV")]
    [InlineData("mountSinglePointAt", "move_mount() for 'tmp': Invalid argument",
        "/tmp", "move_mount", "tmpfs", 22, "EINVAL")]
    [InlineData("createFilesystemMount", "fsconfig(key='private-canary', value='private: canary'): Invalid argument",
        "/tmp", "fsconfig", "tmpfs", 22, "EINVAL")]
    public void New_api_point_failures_preserve_syscall_and_target_through_summaries(
        string site, string message, string destination, string syscall, string target, int errno, string name)
    {
        var stderr = Line('W', site, message) + MandatoryFailure(destination) + TreeFailure;
        Assert.Equal($"{MountError}Native operation={syscall} target={target} errno={errno} errno_name={name}",
            Explain(stderr).ToLogLine());
    }

    [Theory]
    [InlineData("move_mount(root_mfd -> '/tmp/private-canary'): Invalid argument", "move_mount", 22, "EINVAL")]
    [InlineData("mount_setattr(root_fd, MOUNT_ATTR_RDONLY): Operation not supported", "mount_setattr", 95, "EOPNOTSUPP")]
    public void New_api_tree_failures_use_the_fatal_operation(string message, string syscall, int errno, string name)
    {
        var stderr = Line('E', "buildMountTree", message) + TreeFailure;
        Assert.Equal($"{MountError}Native operation={syscall} target=mount_tree errno={errno} errno_name={name}",
            Explain(stderr).ToLogLine());
    }

    [Theory]
    [InlineData("tryMountRW", "mount('/private/canary' -> '/private/canary'): RW failed, falling back to RO")]
    [InlineData("applyMountFlags", "mount_setattr(fd=7, flags=0x8): Permission denied")]
    [InlineData("mountSinglePointAt", "openat(root_fd, 'job/Main.cpp', O_CREAT): Permission denied")]
    [InlineData("unknown", "open_tree('/private/canary'): Permission denied")]
    public void Recoverable_or_unverified_warnings_do_not_replace_the_fatal_summary(string site, string warning)
    {
        var stderr = Line('W', site, warning) + MandatoryFailure("/job/Main.cpp") + TreeFailure;
        Assert.Equal(MountError + "Rejected operation=mount target=file", Explain(stderr).ToLogLine());
    }

    [Fact]
    public void Warning_from_another_process_cannot_supply_the_summary_errno()
    {
        var stderr = Line('W', "doBindMountAt", "open_tree('/private/payload-canary'): Permission denied", pid: 22358) +
            MandatoryFailure("/job/Main.cpp") + TreeFailure;
        Assert.Equal(MountError + "Rejected operation=mount target=file", Explain(stderr).ToLogLine());
    }

    [Theory]
    [InlineData("Unknown error 234", "Rejected operation=mount target=tmpfs")]
    [InlineData("Invalid argument /private/canary", "Rejected operation=mount target=tmpfs")]
    [InlineData("other reason: Permission denied", "Rejected operation=build_mount_tree")]
    public void Unknown_errno_is_never_guessed_or_printed(string error, string expected)
    {
        var stderr = Line('E', "buildMountTree", "mount('/tmp/private-canary', tmpfs): " + error) + TreeFailure;
        Assert.Equal(MountError + expected, Explain(stderr).ToLogLine());
    }

    [Fact]
    public void Incomplete_stderr_never_supplies_an_errno_from_a_cut_record()
    {
        var stderr = MandatoryFailure("/tmp") + Line('E', "buildMountTree",
            "mount('/tmp/private-canary', tmpfs): Permission denied").TrimEnd('\n');
        Assert.Equal(MountError + "Rejected operation=mount target=tmpfs stderr=Interrupted",
            Explain(stderr, SandboxStderrStatus.Interrupted).ToLogLine());
    }

    [Fact]
    public void A_complete_mount_line_survives_a_later_capture_limit()
    {
        var stderr = Line('E', "initCloneNs", "pivot_root('/private/canary', '/private/canary'): Invalid argument") +
            "truncated /private/source-canary";
        Assert.Equal(MountError + "Native operation=pivot_root target=mount_tree errno=22 errno_name=EINVAL stderr=LimitExceeded",
            Explain(stderr, SandboxStderrStatus.LimitExceeded).ToLogLine());
    }

    [Fact]
    public void Mount_stderr_cannot_override_ready_or_native_failure_receipts()
    {
        var stderr = Encoding.UTF8.GetBytes(Line('E', "initCloneNs",
            "pivot_root('/private/canary', '/private/canary'): Invalid argument"));
        Assert.Null(SandboxReceipt.Explain("R", stderr, false));
        var native = Assert.IsType<SandboxFailure>(SandboxReceipt.Explain("FM:13", stderr, false));
        Assert.Equal("SystemError stage=JailMounts error=Native errno=13", native.ToLogLine());
    }

    private static SandboxFailure Explain(string stderr, SandboxStderrStatus status = SandboxStderrStatus.Complete) =>
        Assert.IsType<SandboxFailure>(SandboxReceipt.Explain("", Encoding.UTF8.GetBytes(stderr), false, status));

    private static string MandatoryFailure(string target) =>
        Line('E', "buildMountTree", $"Failed to mount mandatory point: '{target}'");

    private static string Line(char level, string site, string body, int pid = 22359) =>
        $"[{level}][2026-11-08T12:17:12+0000][{pid}] {site}():328 {body}\n";
}
