using System.Net.Sockets;
using CodeRank.Judge.Protocol;
using CodeRank.Judge.Sandbox;
using Xunit;

namespace CodeRank.Judge.Sandbox.Tests;

public sealed class SandboxIpcActivationTests
{
    public static bool IsLinux => OperatingSystem.IsLinux();

    [Fact]
    public void Socket_unit_activates_the_same_abstract_listener_used_by_the_binaries()
    {
        var unit = File.ReadAllLines(Path.Combine(AppContext.BaseDirectory, "coderank-judge-sandbox-worker.socket"));
        Assert.Equal('\0', JudgeIpc.SocketAddress[0]);
        Assert.Equal("ListenStream=@" + JudgeIpc.SocketAddress[1..],
            Assert.Single(unit, line => line.StartsWith("ListenStream=", StringComparison.Ordinal)));
        Assert.Equal("FileDescriptorName=" + JudgeIpc.SocketName,
            Assert.Single(unit, line => line.StartsWith("FileDescriptorName=", StringComparison.Ordinal)));
        Assert.Contains("Accept=no", unit);
    }

    [Theory]
    [InlineData("123", "1", JudgeIpc.SocketName, true)]
    [InlineData(null, "1", JudgeIpc.SocketName, false)]
    [InlineData("124", "1", JudgeIpc.SocketName, false)]
    [InlineData("123", null, JudgeIpc.SocketName, false)]
    [InlineData("123", "2", JudgeIpc.SocketName, false)]
    [InlineData("123", "1", null, false)]
    [InlineData("123", "1", "judge-sandbox:other", false)]
    public void Activation_requires_the_current_pid_and_one_named_fd(string? pid, string? count, string? name, bool valid) =>
        Assert.Equal(valid, SandboxIpcServer.HasActivationMetadata(123, pid, count, name));

    [Fact(Skip = "Linux abstract AF_UNIX and SO_PEERCRED are required.", SkipUnless = nameof(IsLinux))]
    public async Task Adopted_abstract_listener_accepts_and_exposes_kernel_peer_identity()
    {
        var address = TestAddress();
        using var original = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        original.Bind(address);
        original.Listen(1);
        using var listener = SandboxIpcServer.AdoptListener(new SafeSocketHandle(original.Handle, ownsHandle: false), address);
        using var client = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        using var deadline = JudgeIpc.Deadline(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        await client.ConnectAsync(address, deadline.Token);
        using var peer = await listener.AcceptAsync(deadline.Token);

        var uid = LinuxJudgeIdentity.GetEffectiveUid();
        Assert.True(LinuxJudgeIdentity.IsPeer(peer, uid));
        Assert.False(LinuxJudgeIdentity.IsPeer(peer, uid ^ 1));
        Assert.True(LinuxJudgeIdentity.IsPeer(client, uid, Environment.ProcessId));
        Assert.False(LinuxJudgeIdentity.IsPeer(client, uid, Environment.ProcessId + 1));
    }

    [Theory(Skip = "Linux abstract AF_UNIX is required.", SkipUnless = nameof(IsLinux))]
    [InlineData(AddressFamily.InterNetwork, SocketType.Stream, false, false)]
    [InlineData(AddressFamily.Unix, SocketType.Dgram, true, false)]
    [InlineData(AddressFamily.Unix, SocketType.Stream, false, true)]
    [InlineData(AddressFamily.Unix, SocketType.Stream, true, false)]
    public void Unexpected_socket_family_type_address_or_listening_state_is_rejected(
        AddressFamily family, SocketType type, bool bindExpectedAddress, bool listen)
    {
        var address = TestAddress();
        using var original = new Socket(family, type, ProtocolType.Unspecified);
        if (family == AddressFamily.Unix) original.Bind(bindExpectedAddress ? address : TestAddress());
        if (listen) original.Listen(1);
        using var handle = new SafeSocketHandle(original.Handle, ownsHandle: false);

        Assert.Throws<SandboxUnavailableException>(() => SandboxIpcServer.AdoptListener(handle, address));
        Assert.True(handle.IsClosed);
    }

    private static UnixDomainSocketEndPoint TestAddress() => new("\0coderank-ipc-test-" + Guid.NewGuid().ToString("N"));
}
