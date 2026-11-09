using System.Buffers.Binary;
using CodeRank.Judge.Protocol;
using CodeRank.Judge.Worker;
using Xunit;

namespace CodeRank.Judge.Worker.Tests;

public sealed class JudgeIpcClientTests
{
    [Fact]
    public async Task Sends_only_source_and_current_inputs_in_separate_bounded_frames()
    {
        using var replies = new MemoryStream();
        await JudgeIpc.WriteAsync(replies, JudgeIpcKind.Ready, ReadOnlyMemory<byte>.Empty, Token);
        await ReplyAsync(replies, IsolatedCaseStatus.Exited, "first");
        await ReplyAsync(replies, IsolatedCaseStatus.Exited, "second");
        using var channel = new ScriptedChannel(replies.ToArray());

        var results = await UnixJudgeRunner.ExchangeAsync(channel, "cpp17", "synthetic source", ["input one", "input two"], Token);
        Assert.Equal(["first", "second"], results.Select(result => result.StandardOutput));

        using var sent = channel.WrittenFrames();
        var start = JudgeIpc.DecodeStart(await JudgeIpc.ReadAsync(sent, JudgeIpc.MaxStartBytes, Token));
        Assert.Equal(("cpp17", "synthetic source"), start);
        foreach (var input in new[] { "input one", "input two" })
        {
            var frame = await JudgeIpc.ReadAsync(sent, JudgePayloadContract.MaxTestFieldBytes, Token);
            Assert.Equal(JudgeIpcKind.Input, frame.Kind);
            Assert.Equal(input, JudgeIpc.DecodeText(frame.Payload));
        }
        var end = await JudgeIpc.ReadAsync(sent, 0, Token);
        Assert.Equal(JudgeIpcKind.End, end.Kind);
        Assert.Equal(sent.Length, sent.Position);
    }

    [Fact]
    public async Task Failed_preparation_does_not_send_private_inputs()
    {
        using var replies = new MemoryStream();
        await ReplyAsync(replies, IsolatedCaseStatus.CompilationError, "");
        using var channel = new ScriptedChannel(replies.ToArray());

        var results = await UnixJudgeRunner.ExchangeAsync(channel, "cpp17", "synthetic source", ["input one", "input two"], Token);
        Assert.All(results, result => Assert.Equal(IsolatedCaseStatus.CompilationError, result.Status));
        using var sent = channel.WrittenFrames();
        Assert.Equal(JudgeIpcKind.Start, (await JudgeIpc.ReadAsync(sent, JudgeIpc.MaxStartBytes, Token)).Kind);
        Assert.Equal(sent.Length, sent.Position);
    }

    [Fact]
    public async Task Runner_system_error_stops_the_session_before_the_next_input()
    {
        using var replies = new MemoryStream();
        await JudgeIpc.WriteAsync(replies, JudgeIpcKind.Ready, ReadOnlyMemory<byte>.Empty, Token);
        await ReplyAsync(replies, IsolatedCaseStatus.SystemError, "");
        using var channel = new ScriptedChannel(replies.ToArray());

        var results = await UnixJudgeRunner.ExchangeAsync(channel, "python3", "synthetic source", ["one", "two"], Token);
        Assert.All(results, result => Assert.Equal(IsolatedCaseStatus.SystemError, result.Status));
        using var sent = channel.WrittenFrames();
        Assert.Equal(JudgeIpcKind.Start, (await JudgeIpc.ReadAsync(sent, JudgeIpc.MaxStartBytes, Token)).Kind);
        Assert.Equal("one", JudgeIpc.DecodeText((await JudgeIpc.ReadAsync(sent, JudgePayloadContract.MaxTestFieldBytes, Token)).Payload));
        Assert.Equal(sent.Length, sent.Position);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(JudgePayloadContract.MaxTestFieldBytes + 1)]
    [InlineData(int.MaxValue)]
    public async Task Invalid_size_is_rejected_from_header_without_reading_a_body(int size)
    {
        var header = new byte[5];
        header[0] = (byte)JudgeIpcKind.Input;
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(1), size);
        using var frame = new MemoryStream(header);
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            JudgeIpc.ReadAsync(frame, JudgePayloadContract.MaxTestFieldBytes, Token).AsTask());
    }

    [Fact]
    public void Malformed_output_and_status_are_rejected()
    {
        Assert.Throws<InvalidDataException>(() => JudgeIpc.DecodeResult(new(JudgeIpcKind.Result, [0, 0xff])));
        Assert.Throws<InvalidDataException>(() => JudgeIpc.DecodeResult(new(JudgeIpcKind.Result, [255])));
        Assert.Throws<InvalidDataException>(() => JudgeIpc.DecodeResult(new(JudgeIpcKind.Result, [1, 65])));
        Assert.Throws<InvalidDataException>(() => JudgeIpc.DecodeStart(new(JudgeIpcKind.Start, [2, 1, 65])));
    }

    private static ValueTask ReplyAsync(Stream stream, IsolatedCaseStatus status, string output) =>
        JudgeIpc.WriteAsync(stream, JudgeIpcKind.Result,
            JudgeIpc.EncodeResult(new() { Status = status, StandardOutput = output }), Token);

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private sealed class ScriptedChannel(byte[] replies) : Stream
    {
        private readonly MemoryStream input = new(replies, writable: false);
        private readonly MemoryStream output = new();

        public Stream WrittenFrames() => new MemoryStream(output.ToArray(), writable: false);
        public override bool CanRead => true;
        public override bool CanWrite => true;
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => output.Flush();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => input.Read(buffer, offset, count);
        public override void Write(byte[] buffer, int offset, int count) => output.Write(buffer, offset, count);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default) =>
            input.ReadAsync(buffer, token);
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken token = default) =>
            output.WriteAsync(buffer, token);

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                input.Dispose();
                output.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
