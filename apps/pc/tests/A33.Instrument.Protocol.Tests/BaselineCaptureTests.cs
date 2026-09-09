using A33.Instrument.Core;
using A33.Instrument.HardwareValidation;
using A33.Instrument.Protocol;
using Xunit;

namespace A33.Instrument.Protocol.Tests;

public sealed class BaselineCaptureTests
{
    [Fact]
    public async Task ValidFirmware050bBrightness3CapturePasses()
    {
        using var temp = new CaptureTempDirectory();
        var fake = new CaptureSession();
        var calls = 0;
        var exit = await BaselineCaptureRunner.RunAsync(Args(temp.Root), _ => { calls++; return fake; });
        Assert.Equal(0, exit);
        Assert.Equal(1, calls);
        Assert.Equal(17, fake.Trace.Count);
        Assert.All(fake.Trace, item => Assert.Equal(3, item.FunctionCode));
        Assert.Equal(0, fake.WriteCount);
        var directory = Assert.Single(Directory.GetDirectories(temp.Root));
        Assert.All(BaselineCaptureRunner.RequiredFiles, name => Assert.True(File.Exists(Path.Combine(directory, name))));
        var summary = AtomicJsonFile.Read<BaselineCaptureSummary>(Path.Combine(directory, "baseline-capture-summary.json"));
        Assert.Equal("PASS", summary.FinalStatus);
        Assert.Equal(Stage2BDeviceContract.PcJsonActiveSha256, summary.PcJsonActiveSha256);
        Assert.Equal(Stage2BDeviceContract.Stm32BinaryActiveSha256, summary.Stm32BinaryActiveSha256);
        Assert.Equal(7, summary.EvidenceFileSha256.Count);
    }

    [Theory]
    [InlineData("firmware")]
    [InlineData("brightness")]
    [InlineData("active-diff")]
    [InlineData("fc03")]
    [InlineData("dirty")]
    [InlineData("revision")]
    [InlineData("mailbox")]
    [InlineData("slot")]
    [InlineData("sequence")]
    public async Task UnsafeCaptureFailsWithoutWrites(string failure)
    {
        using var temp = new CaptureTempDirectory();
        var fake = new CaptureSession { Failure = failure };
        var exit = await BaselineCaptureRunner.RunAsync(Args(temp.Root), _ => fake);
        Assert.NotEqual(0, exit);
        Assert.Equal(0, fake.WriteCount);
        var directory = Assert.Single(Directory.GetDirectories(temp.Root));
        var summary = AtomicJsonFile.Read<BaselineCaptureSummary>(Path.Combine(directory, "baseline-capture-summary.json"));
        Assert.Equal("FAIL", summary.FinalStatus);
    }

    [Fact]
    public async Task MissingAuthorizationRejectsBeforeFactory()
    {
        var calls = 0;
        Assert.Equal(2, await BaselineCaptureRunner.RunAsync(["capture-persistence-baseline"], _ => { calls++; return new CaptureSession(); }));
        Assert.Equal(2, await BaselineCaptureRunner.RunAsync(["capture-persistence-baseline", "--confirmation", BaselineCaptureRunner.Confirmation, "--unknown"], _ => { calls++; return new CaptureSession(); }));
        Assert.Equal(0, calls);
    }

    private static string[] Args(string root) => ["capture-persistence-baseline", "--confirmation", BaselineCaptureRunner.Confirmation, "--output-root", root];

    private sealed class CaptureSession : IStrictPreflightSession
    {
        private static readonly ushort[] Active =
        [0,1,7,0,0,0,45776,24064,0,0,15,16960,0,0,0,1,3,1,2,1,3,1,3,0,0,0,0,0,0,0,0,0,0,3,2,1,8,500,0,0,30,33920,0,0,61,2304,1,3,0,0,8,500,0,0,30,33920,0,0,61,2304,1,0,2,0];
        private readonly List<PreflightRequestTrace> trace = [];
        private int activeRead;
        public string? Failure { get; init; }
        public int WriteCount => 0;
        public bool IsOpen { get; private set; }
        public WordOrder WordOrder { get; set; }
        public byte UnitId => 1;
        public IReadOnlyList<PreflightRequestTrace> Trace => trace;
        public PreflightErrorCounters Errors => Failure == "fc03" ? new(0,0,0,0,0,0,0,1) : new(0,0,0,0,0,0,0,0);
        public Task OpenAsync(CancellationToken cancellationToken) { IsOpen = true; return Task.CompletedTask; }
        public Task CloseAsync() { IsOpen = false; return Task.CompletedTask; }
        public void RecordConnectionError(Exception error) { }
        public ValueTask DisposeAsync() { IsOpen = false; return ValueTask.CompletedTask; }
        public Task<ushort[]> ReadHoldingAsync(ushort address, ushort count, CancellationToken cancellationToken = default) => ReadForPreflightAsync(address, count, PreflightReadPurpose.ConfigStore, cancellationToken);

        public Task<ushort[]> ReadForPreflightAsync(ushort address, ushort count, PreflightReadPurpose purpose, CancellationToken cancellationToken = default)
        {
            var started = DateTimeOffset.UtcNow;
            ushort[] values;
            if (address == 0x0103 && count == 1) values = [0];
            else if (address == 14 && count == 2) values = [0x0104, Failure == "firmware" ? (ushort)0x050A : (ushort)0x050B];
            else if (address is >= 0x0100 and <= 0x0130)
            {
                values = Active.Skip(address - 0x0100).Take(count).ToArray();
                if (address == 0x0110 && Failure == "brightness") values[6] = 4;
                if (address == 0x0130 && ++activeRead == 2 && Failure == "active-diff") values[0] ^= 1;
            }
            else if (address is >= 0x0140 and <= 0x0170) values = new ushort[count];
            else if (address == 0x004C) { values = new ushort[12]; if (Failure == "mailbox") values[2] = 1; }
            else if (address == 0x0030)
            {
                values = [0,0,Failure == "dirty" ? (ushort)1 : (ushort)0,0,19,0,Failure == "revision" ? (ushort)18 : (ushort)19];
            }
            else if (address == 0x01C0)
                values = [2, Failure == "slot" ? (ushort)2 : (ushort)1,0,Failure == "sequence" ? (ushort)18 : (ushort)19,0];
            else throw new InvalidOperationException("Unexpected read plan.");
            var success = Failure != "fc03" || trace.Count != 2;
            trace.Add(new(trace.Count + 1, started, started.AddMilliseconds(1), 1, 3, address, count, "0103", success ? "0103" : "", success ? values.Length : 0, success, success ? null : "IOException", null, purpose, null));
            if (!success) throw new IOException("Injected FC03 failure.");
            return Task.FromResult(values);
        }
    }

    private sealed class CaptureTempDirectory : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "a33-capture-" + Guid.NewGuid().ToString("N"));
        public CaptureTempDirectory() => Directory.CreateDirectory(Root);
        public void Dispose() { try { Directory.Delete(Root, true); } catch { } }
    }
}
