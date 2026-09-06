using System.Text.Json;
using A33.Instrument.Core;
using A33.Instrument.Protocol;

namespace A33.Instrument.HardwareValidation;

public static class StrictPreflightRunner
{
    public static async Task<int> RunAsync(string[] args)
    {
        var root = Get(args, "output-root", "Results/pc_stage2b_hw");
        var workflowId = Guid.NewGuid().ToString("D");
        var outputDirectory = PersistenceEvidenceDirectory.CreateUnique(root, workflowId, DateTimeOffset.UtcNow);
        var output = Path.Combine(outputDirectory, "preflight.json");
        StrictPreflightReport? report = null;
        string? failure = null;
        var connectionCount = 0;
        await using var access = new TcpPreflightRegisterAccess("192.168.1.100", 502, 1, TimeSpan.FromSeconds(2));
        try
        {
            connectionCount++;
            await access.OpenAsync(CancellationToken.None);
            report = await new StrictPreflightService(access).RunAsync();
        }
        catch (Exception error) { failure = error.Message; }

        var result = new
        {
            schema_version = 2,
            tool_version = "strict-2",
            workflow_id = workflowId,
            client_commit = "BUILD_FROM_CURRENT_CHECKOUT",
            stm32_commit = ConfigurationPersistenceService.FixedStm32Commit,
            command = "preflight",
            transport = "tcp",
            endpoint = "192.168.1.100:502",
            connection_count = connectionCount,
            fc03_requests = access.Fc03Requests,
            fc06_requests = access.Fc06Requests,
            fc16_requests = access.Fc16Requests,
            mailbox_write_requests = access.MailboxWriteRequests,
            save_requests = access.SaveRequests,
            errors = access.Errors,
            final_status = report?.Passed == true ? "PASS" : "FAIL",
            failure_reason = failure ?? report?.FailureReason,
            report
        };
        var json = JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true });
        Console.WriteLine(json);
        await File.WriteAllTextAsync(output, json);
        return report?.Passed == true ? 0 : 6;
    }

    private static string Get(string[] args, string key, string fallback)
    {
        for (var i = 0; i < args.Length - 1; i++)
            if (args[i].Equals("--" + key, StringComparison.OrdinalIgnoreCase)) return args[i + 1];
        return fallback;
    }

    private sealed class TcpPreflightRegisterAccess(string host, int port, byte unitId, TimeSpan timeout) : ITrackedReadOnlyRegisterAccess, IAsyncDisposable
    {
        private readonly ModbusTcpTransport transport = new(host, port);
        private ReadOnlyModbusClient? client;
        private long timeouts, crcErrors, mbapErrors, tidErrors, unitErrors, exceptions, badFrames;

        public WordOrder WordOrder { get; private set; } = WordOrder.HighWordFirst;
        public byte UnitId => unitId;
        public long Fc03Requests { get; private set; }
        public long Fc06Requests { get; private set; }
        public long Fc16Requests { get; private set; }
        public long MailboxWriteRequests { get; private set; }
        public long SaveRequests { get; private set; }
        public PreflightErrorCounters Errors => new(timeouts, crcErrors, mbapErrors, tidErrors, unitErrors, exceptions, badFrames);

        public async Task OpenAsync(CancellationToken cancellationToken)
        {
            await transport.OpenAsync(cancellationToken);
            client = new ReadOnlyModbusClient(transport, unitId, timeout);
            var order = await ReadHoldingAsync(0x0103, 1, cancellationToken);
            WordOrder = order[0] switch { 0 => WordOrder.HighWordFirst, 1 => WordOrder.LowWordFirst, _ => throw new InvalidDataException("Unknown Modbus word order.") };
        }

        public async Task<ushort[]> ReadHoldingAsync(ushort address, ushort count, CancellationToken cancellationToken = default)
        {
            Fc03Requests++;
            try { return await (client ?? throw new InvalidOperationException("Preflight transport is not open.")).ReadHoldingAsync(address, count, cancellationToken); }
            catch (Exception error)
            {
                switch (error)
                {
                    case OperationCanceledException: timeouts++; break;
                    case ModbusExceptionResponse: exceptions++; break;
                    case ModbusFrameException frame when frame.Error == ModbusFrameError.Crc: crcErrors++; break;
                    case ModbusFrameException frame when frame.Error == ModbusFrameError.TransactionId: tidErrors++; break;
                    case ModbusFrameException frame when frame.Error == ModbusFrameError.ProtocolId: mbapErrors++; break;
                    case ModbusFrameException frame when frame.Error == ModbusFrameError.UnitId: unitErrors++; break;
                    default: badFrames++; break;
                }
                throw;
            }
        }

        public async ValueTask DisposeAsync()
        {
            await transport.CloseAsync();
            await transport.DisposeAsync();
        }
    }
}
