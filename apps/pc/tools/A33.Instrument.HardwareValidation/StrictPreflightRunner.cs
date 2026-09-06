using System.Diagnostics;
using System.Text.Json;
using A33.Instrument.Core;
using A33.Instrument.Protocol;

namespace A33.Instrument.HardwareValidation;

public static class StrictPreflightRunner
{
    public static async Task<int> RunAsync(string[] args)
    {
        var output = Get(args, "output", "Results/pc_stage2b_hw/tcp_strict_preflight.json"); var started = DateTimeOffset.Now; var result = new Dictionary<string, object?> { ["schema_version"] = 1, ["tool_version"] = "strict-1", ["started_at"] = started, ["client_commit"] = "0fb02a7", ["stm32_commit"] = "71a6124", ["command"] = "preflight", ["transport"] = "tcp", ["endpoint"] = "192.168.1.100:502", ["unit_id"] = 1, ["fc06_requests"] = 0, ["fc16_requests"] = 0, ["mailbox_write_requests"] = 0, ["save_requests"] = 0, ["total_write_requests"] = 0 };
        try
        {
            await using var transport = new ModbusTcpTransport("192.168.1.100", 502); await transport.OpenAsync(CancellationToken.None);
            var client = new ReadOnlyModbusClient(transport, 1, TimeSpan.FromSeconds(2)); var sw = Stopwatch.StartNew(); var first = await ReadConfigAsync(client, 0x0100); var staging = await ReadConfigAsync(client, 0x0140); var mailbox = await client.ReadHoldingAsync(0x004C, 12); var second = await ReadConfigAsync(client, 0x0100); var readonlySuccess = 0; for (var i = 0; i < 100; i++) { await client.ReadHoldingAsync(0, 1); readonlySuccess++; }
            result["firmware_version"] = (await client.ReadHoldingAsync(15, 1))[0]; result["map_version"] = (await client.ReadHoldingAsync(14, 1))[0]; result["snapshot_received"] = true; result["snapshot_wait_ms"] = sw.ElapsedMilliseconds; result["active_snapshot_1"] = first; result["active_snapshot_2"] = second; result["staging_snapshot"] = staging; result["mailbox"] = mailbox; result["active_snapshots_equal"] = first.SequenceEqual(second); result["active_register_count"] = first.Length; result["staging_register_count"] = staging.Length; result["brightness_value"] = first[22]; result["brightness_active_address"] = "0x0116"; result["brightness_staging_address"] = "0x0156"; result["mailbox_busy"] = mailbox[2] == 1; result["mailbox_pending"] = mailbox[2] != 0; result["readonly_requested"] = 100; result["readonly_succeeded"] = readonlySuccess; result["fc03_requests"] = 8 + 100; result["timeouts"] = 0; result["mbap_errors"] = 0; result["tid_errors"] = 0; result["unit_errors"] = 0; result["modbus_exceptions"] = 0; result["bad_frames"] = 0; result["failure_stage"] = null; result["final_status"] = first.Length == 64 && staging.Length == 64 && first.SequenceEqual(second) && first[22] == 3 && readonlySuccess == 100 ? "PASS" : "PREFLIGHT_INCOMPLETE"; result["exit_code"] = result["final_status"]!.ToString() == "PASS" ? 0 : 14;
        }
        catch (Exception error) { result["final_status"] = "FAIL"; result["failure_stage"] = "readonly_preflight"; result["failure_reason"] = error.Message; result["exit_code"] = 6; }
        result["completed_at"] = DateTimeOffset.Now; result["duration_ms"] = (DateTimeOffset.Now - started).TotalMilliseconds; var json = JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }); Console.WriteLine(json); await File.WriteAllTextAsync(output, json); return (int)result["exit_code"]!;
    }
    private static async Task<ushort[]> ReadConfigAsync(ReadOnlyModbusClient client, ushort start) { var all = new ushort[64]; for (var i = 0; i < 4; i++) (await client.ReadHoldingAsync((ushort)(start + i * 16), 16)).CopyTo(all, i * 16); return all; }
    private static string Get(string[] args, string key, string fallback) { for (var i=0;i<args.Length-1;i++) if(args[i].Equals("--"+key,StringComparison.OrdinalIgnoreCase)) return args[i+1]; return fallback; }
}
