using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using A33.Instrument.Core;
using A33.Instrument.Protocol;

namespace A33.Instrument.HardwareValidation;

public interface IStrictPreflightSession : ITrackedReadOnlyRegisterAccess, IAsyncDisposable
{
    bool IsOpen { get; }
    Task OpenAsync(CancellationToken cancellationToken);
    Task CloseAsync();
    void RecordConnectionError(Exception error);
}

public static class StrictPreflightRunner
{
    public static async Task<int> RunAsync(string[] args, Func<IPersistenceClock, IStrictPreflightSession>? sessionFactory = null)
    {
        var repositoryRoot = RepositoryRoot.Find();
        var root = Get(args, "output-root", Path.Combine(repositoryRoot, "Results", "pc_stage2b_hw"));
        var workflowId = Guid.NewGuid().ToString("D");
        var outputDirectory = PersistenceEvidenceDirectory.CreateUnique(root, workflowId, DateTimeOffset.UtcNow);
        var assembly = typeof(StrictPreflightRunner).Assembly;
        var clientCommit = ToolBuildIdentity.GetCommit(assembly);
        var baseline = PersistenceBaselineContract.LoadFromRepository(repositoryRoot);
        var environment = PreflightEnvironment.Capture(assembly);
        var clock = new SystemPersistenceClock();
        var started = clock.UtcNow;
        var access = sessionFactory?.Invoke(clock) ?? new TcpPreflightRegisterAccess("192.168.1.100", 502, 1, TimeSpan.FromSeconds(2), clock);
        StrictPreflightReport? report = null;
        Exception? failure = null;
        var attempts = 0; var succeeded = 0; var failed = 0; var disconnects = 0;
        try
        {
            attempts++;
            using var connectTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            await access.OpenAsync(connectTimeout.Token);
            succeeded++;
            report = await new StrictPreflightService(access, baseline, clock).RunAsync();
        }
        catch (Exception error)
        {
            failure = error;
            if (succeeded == 0) { failed++; access.RecordConnectionError(error); }
        }
        finally
        {
            if (access.IsOpen)
            {
                try { await access.CloseAsync(); disconnects++; }
                catch (Exception error) { failure ??= error; access.RecordConnectionError(error); }
            }
            try { await access.DisposeAsync(); }
            catch (Exception error) { failure ??= error; access.RecordConnectionError(error); }
        }

        var completed = clock.UtcNow;
        var requests = PreflightRequestStatistics.FromTrace(access.Trace);
        var connections = new PreflightConnectionStatistics(attempts, succeeded, failed, disconnects, 0);
        var failureReasons = report?.FailureReasons ?? (failure is null ? [] : [$"{failure.GetType().Name}: {failure.Message}"]);
        var gates = report?.Gates ?? new Dictionary<string, bool> { ["execution_completed"] = false };
        var summary = new PreflightSummary(1, workflowId, clientCommit,
            environment.ToolAssemblyVersion, ComputeToolHash(assembly), ConfigurationPersistenceService.FixedStm32Commit,
            baseline.Manifest.BaselineId, baseline.Manifest.ActiveArraySha256, baseline.ManifestSha256,
            started, completed, (completed - started).TotalMilliseconds, report?.Timing.FreshnessWaitMs ?? 0,
            report?.Timing.InitialSampleSequence ?? 0, report?.Timing.FinalSampleSequence ?? 0,
            "192.168.1.100:502", 1, report?.Identity ?? new DeviceIdentity(0, 0, 0, 1), connections, requests, access.Errors, gates,
            report?.Passed == true && failure is null ? "PASS" : "FAIL", failureReasons,
            new Dictionary<string, string>());
        var stored = await PreflightEvidenceStore.WriteAsync(outputDirectory, summary, access.Trace, report, environment);
        Console.WriteLine($"WORKFLOW_ID={workflowId}");
        Console.WriteLine($"EVIDENCE_DIRECTORY={outputDirectory}");
        Console.WriteLine($"FC03_ATTEMPTED={stored.Requests.Fc03Attempted}");
        Console.WriteLine($"FC03_SUCCEEDED={stored.Requests.Fc03Succeeded}");
        Console.WriteLine($"FINAL_STATUS={stored.FinalStatus}");
        return stored.FinalStatus == "PASS" ? 0 : 6;
    }

    private static string ComputeToolHash(Assembly assembly) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(assembly.Location)));

    private static string Get(string[] args, string key, string fallback)
    {
        for (var i = 0; i < args.Length - 1; i++)
            if (args[i].Equals("--" + key, StringComparison.OrdinalIgnoreCase)) return args[i + 1];
        return fallback;
    }

    private sealed class TcpPreflightRegisterAccess(
        string host, int port, byte unitId, TimeSpan timeout, IPersistenceClock clock) : IStrictPreflightSession
    {
        private readonly ModbusTcpTransport transport = new(host, port);
        private readonly List<PreflightRequestTrace> trace = [];
        private ReadOnlyModbusClient? client;
        private long timeouts, crcErrors, mbapErrors, tidErrors, unitErrors, exceptions, badFrames, transportErrors;

        public bool IsOpen => transport.IsOpen;
        public WordOrder WordOrder { get; set; } = WordOrder.HighWordFirst;
        public byte UnitId => unitId;
        public IReadOnlyList<PreflightRequestTrace> Trace => trace;
        public PreflightErrorCounters Errors => new(timeouts, crcErrors, mbapErrors, tidErrors, unitErrors, exceptions, badFrames, transportErrors);

        public async Task OpenAsync(CancellationToken cancellationToken)
        {
            await transport.OpenAsync(cancellationToken);
            client = new ReadOnlyModbusClient(transport, unitId, timeout);
        }

        public async Task CloseAsync() => await transport.CloseAsync();

        public Task<ushort[]> ReadHoldingAsync(ushort address, ushort count, CancellationToken cancellationToken = default) =>
            ReadForPreflightAsync(address, count, PreflightReadPurpose.ConfigStore, cancellationToken);

        public async Task<ushort[]> ReadForPreflightAsync(
            ushort address, ushort count, PreflightReadPurpose purpose,
            CancellationToken cancellationToken = default)
        {
            var started = clock.UtcNow;
            var requestPdu = ModbusRequestCodec.ReadHoldingPdu(address, count);
            try
            {
                var values = await (client ?? throw new InvalidOperationException("Preflight transport is not open.")).ReadHoldingAsync(address, count, cancellationToken);
                var completed = clock.UtcNow;
                var exchange = client.LastExchange ?? throw new InvalidDataException("Successful FC03 has no exchange evidence.");
                trace.Add(new(trace.Count + 1, started, completed, (completed - started).TotalMilliseconds, 3, address, count,
                    Convert.ToHexString(exchange.RequestAdu), Convert.ToHexString(exchange.ResponseAdu), values.Length,
                    true, null, null, purpose, null));
                return values;
            }
            catch (Exception error)
            {
                CountError(error);
                var completed = clock.UtcNow;
                trace.Add(new(trace.Count + 1, started, completed, (completed - started).TotalMilliseconds, 3, address, count,
                    Convert.ToHexString(requestPdu), "", 0, false, error.GetType().Name,
                    error is ModbusExceptionResponse modbus ? modbus.ExceptionCode : null, purpose, null));
                throw;
            }
        }

        private void CountError(Exception error)
        {
            switch (error)
            {
                case OperationCanceledException: timeouts++; break;
                case ModbusExceptionResponse: exceptions++; break;
                case ModbusFrameException frame when frame.Error == ModbusFrameError.Crc: crcErrors++; break;
                case ModbusFrameException frame when frame.Error == ModbusFrameError.TransactionId: tidErrors++; break;
                case ModbusFrameException frame when frame.Error == ModbusFrameError.ProtocolId: mbapErrors++; break;
                case ModbusFrameException frame when frame.Error == ModbusFrameError.UnitId: unitErrors++; break;
                case IOException or System.Net.Sockets.SocketException: transportErrors++; break;
                default: badFrames++; break;
            }
        }

        public void RecordConnectionError(Exception error) => CountError(error);

        public async ValueTask DisposeAsync() => await transport.DisposeAsync();
    }
}

internal static class ToolBuildIdentity
{
    public static string GetCommit(Assembly assembly)
    {
        var value = assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .SingleOrDefault(x => x.Key == "GitCommit")?.Value;
        if (value is null || value.Length != 40 || value.Any(x => !Uri.IsHexDigit(x)) || value == "BUILD_FROM_CURRENT_CHECKOUT")
            throw new InvalidDataException("Tool assembly does not contain a reliable Git commit.");
        return value.ToLowerInvariant();
    }
}

internal static class RepositoryRoot
{
    public static string Find()
    {
        foreach (var start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        {
            var current = new DirectoryInfo(start);
            while (current is not null)
            {
                if (File.Exists(Path.Combine(current.FullName, PersistenceBaselineContract.RelativeManifestPath))) return current.FullName;
                current = current.Parent;
            }
        }
        throw new DirectoryNotFoundException("A33 repository root was not found.");
    }
}
