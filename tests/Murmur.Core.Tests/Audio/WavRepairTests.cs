using Murmur.Core.Audio;
using Murmur.Core.Tests.Fakes;

namespace Murmur.Core.Tests.Audio;

/// <summary>How <see cref="WavRepair.Repair(string, out TimeSpan)"/> classifies failures (via an injected opener).</summary>
public sealed class WavRepairTests : IDisposable
{
    private const int SharingViolation = unchecked((int)0x80070020);
    private const int LockViolation = unchecked((int)0x80070021);
    private const int DiskFull = unchecked((int)0x80070070);
    private const int NotReady = unchecked((int)0x80070017);

    private readonly TempDirectory _dir = new();

    public void Dispose() => _dir.Dispose();

    private static int Ewouldblock => OperatingSystem.IsLinux() ? 11 : 35;

    private static WavRepairResult ExpectedForLock => OperatingSystem.IsWindows() ? WavRepairResult.Busy : WavRepairResult.Invalid;

    [Theory]
    [InlineData(SharingViolation)]
    [InlineData(LockViolation)]
    public void OpenThrowingALockViolation_IsBusyOnWindowsOnly(int hresult)
    {
        var result = WavRepair.Repair(ExistingFile(), _ => throw new IOException("locked", hresult), out var duration);

        Assert.Equal(ExpectedForLock, result);
        Assert.Equal(TimeSpan.Zero, duration);
    }

    [Fact]
    public void OpenThrowingAnotherWindowsError_IsInvalid()
    {
        Assert.Equal(WavRepairResult.Invalid, WavRepair.Repair(ExistingFile(), _ => throw new IOException("x", NotReady), out _));
        Assert.Equal(WavRepairResult.Invalid, WavRepair.Repair(ExistingFile(), _ => throw new IOException("x", DiskFull), out _));
    }

    [Fact]
    public void OpenThrowingEio_IsInvalid() =>
        Assert.Equal(WavRepairResult.Invalid, WavRepair.Repair(ExistingFile(), _ => throw new IOException("x", 5), out _));

    [Fact]
    public void OpenThrowingEwouldblock_IsBusyOnUnixOnly()
    {
        var expected = OperatingSystem.IsWindows() ? WavRepairResult.Invalid : WavRepairResult.Busy;

        Assert.Equal(expected, WavRepair.Repair(ExistingFile(), _ => throw new IOException("x", Ewouldblock), out _));
    }

    [Fact]
    public void OpenThrowingUnauthorizedAccess_IsInvalid() =>
        Assert.Equal(WavRepairResult.Invalid, WavRepair.Repair(ExistingFile(), _ => throw new UnauthorizedAccessException(), out _));

    [Fact]
    public void OpenThrowingFileNotFound_IsMissing() =>
        Assert.Equal(WavRepairResult.Missing, WavRepair.Repair(ExistingFile(), p => throw new FileNotFoundException("gone", p), out _));

    [Fact]
    public void MissingFile_IsMissingWithoutOpening()
    {
        var result = WavRepair.Repair(_dir.File("none.wav"), _ => throw new InvalidOperationException("must not open"), out _);

        Assert.Equal(WavRepairResult.Missing, result);
    }

    [Fact]
    public void LockViolationDuringSetLength_IsBusyOnWindowsOnly()
    {
        var result = WavRepair.Repair(ExistingFile(), _ => CrashedWav(failSetLength: SharingViolation), out _);

        Assert.Equal(ExpectedForLock, result);
    }

    [Fact]
    public void LockViolationDuringWrite_IsBusyOnWindowsOnly()
    {
        var result = WavRepair.Repair(ExistingFile(), _ => CrashedWav(failWrite: LockViolation), out _);

        Assert.Equal(ExpectedForLock, result);
    }

    [Fact]
    public void OtherIoErrorDuringWrite_IsInvalid()
    {
        var result = WavRepair.Repair(ExistingFile(), _ => CrashedWav(failWrite: NotReady), out _);

        Assert.Equal(WavRepairResult.Invalid, result);
    }

    [Fact]
    public void InjectedStream_IsRepaired()
    {
        var result = WavRepair.Repair(ExistingFile(), _ => CrashedWav(), out var duration);

        Assert.Equal(WavRepairResult.Repaired, result);
        Assert.Equal(TimeSpan.FromSeconds(1500 / 16_000.0), duration);
    }

    private string ExistingFile()
    {
        var path = _dir.File(Guid.NewGuid().ToString("N") + ".wav");
        File.WriteAllBytes(path, [0]);
        return path;
    }

    /// <summary>A WAV like a process that died between two flushes (stale header, extra data, half a sample).</summary>
    private static FaultStream CrashedWav(int? failSetLength = null, int? failWrite = null)
    {
        var memory = new MemoryStream();
        var writer = new WavPcm16Writer(memory, leaveOpen: true);
        writer.Append(new float[1_000]);
        writer.Flush();
        writer.Append(new float[500]);
        memory.WriteByte(1);
        memory.Position = 0;
        return new FaultStream(memory, failSetLength, failWrite);
    }

    private sealed class FaultStream(MemoryStream inner, int? failSetLength, int? failWrite) : Stream
    {
        public override bool CanRead => true;

        public override bool CanSeek => true;

        public override bool CanWrite => true;

        public override long Length => inner.Length;

        public override long Position
        {
            get => inner.Position;
            set => inner.Position = value;
        }

        public override void Flush() => inner.Flush();

        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);

        public override int Read(Span<byte> buffer) => inner.Read(buffer);

        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);

        public override void SetLength(long value)
        {
            if (failSetLength is { } hresult)
            {
                throw new IOException("locked", hresult);
            }

            inner.SetLength(value);
        }

        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            if (failWrite is { } hresult)
            {
                throw new IOException("locked", hresult);
            }

            inner.Write(buffer);
        }
    }
}
