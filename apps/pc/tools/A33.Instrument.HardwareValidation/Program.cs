using System.Text.Json;
using A33.Instrument.Core;

var command = args.FirstOrDefault(a => !a.StartsWith("--"))?.ToLowerInvariant() ?? "help";
if (command == "apply-restore-test") return await A33.Instrument.HardwareValidation.ApplyRestoreRunner.RunAsync(args);
if (command == "recovery-cancel") return await A33.Instrument.HardwareValidation.RecoveryCancelRunner.RunAsync(args);
if (command == "preflight") return await A33.Instrument.HardwareValidation.StrictPreflightRunner.RunAsync(args);
if (command == "persistence-brightness-cycle") return await A33.Instrument.HardwareValidation.PersistenceHardwareRunner.RunAsync(args);
var options = Parse(args);
if (command is "help" or "--help") { Console.WriteLine("preflight | cancel-test | apply-restore-test | save-test (not authorized) | persistence-brightness-cycle (explicit authorization required)"); return 0; }
if (command == "save-test") { Console.Error.WriteLine("SAVE_NOT_AUTHORIZED"); return 12; }
if (!A33.Instrument.HardwareValidation.HardwareValidationCommandPolicy.UsesLegacyAuthorizedCancelPath(command)) { Console.Error.WriteLine("UNKNOWN_OR_UNAVAILABLE_COMMAND"); return 2; }
var output = Get("output", "");
if (!Has("allow-ram-write") || Get("confirm-map", "") != "0x0104" || Get("confirmation", "") != "APPLY_RAM_ONLY" || Get("field", "") != "brightness" || Get("expect-original", "") != "3" || Get("test-value", "") != "4") { Console.Error.WriteLine("RAM_WRITE_AUTHORIZATION_REQUIRED"); return 3; }
var settings = new MonitoringOptions(TransportMode.Tcp, Get("host", "192.168.1.100"), int.Parse(Get("port", "502")), "", 115200, (byte)int.Parse(Get("unit-id", "1")), 3000, 1000, 200);
await using var monitoring = new InstrumentMonitoringService(); var result = new Dictionary<string, object?> { ["command"] = command, ["clientCommit"] = "f136663", ["firmwareCommit"] = "71a6124", ["endpoint"] = $"{settings.Host}:{settings.Port}", ["unitId"] = settings.UnitId, ["save"] = false };
var exitCode = 0; try { await monitoring.ConnectAsync(settings); await monitoring.StartMonitoringAsync(); var configuration = new ConfigurationTransactionService(monitoring); var snapshot = await configuration.RefreshAsync(); result["map"]=$"0x{monitoring.MapVersion:X4}"; result["brightness"] = snapshot.Fields.Single(f=>f.Key=="brightness").Current[0]; configuration.Edit("brightness",4); await configuration.ValidateAsync(); await configuration.CancelAsync(); result["status"]="PASS"; result["operation"]="BEGIN -> STAGING 0x0156=4 -> VALIDATE -> CANCEL";} catch(Exception error){result["status"]="FAIL";result["error"]=error.Message;exitCode=6;} finally {try{await monitoring.StopMonitoringAsync();await monitoring.DisconnectAsync();}catch(Exception error){result["cleanupError"]=error.Message;if(exitCode==0)exitCode=6;}}
var json=JsonSerializer.Serialize(result,new JsonSerializerOptions{WriteIndented=true});Console.WriteLine(json);if(!string.IsNullOrWhiteSpace(output))await File.WriteAllTextAsync(output,json);return result["status"]?.ToString()=="PASS"?0:exitCode == 0 ? 1 : exitCode;
Dictionary<string,string> Parse(string[] values){var map=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);for(var i=0;i<values.Length;i++)if(values[i].StartsWith("--"))map[values[i][2..]]=i+1<values.Length&&!values[i+1].StartsWith("--")?values[++i]:"true";return map;} string Get(string key,string fallback)=>options.GetValueOrDefault(key,fallback); bool Has(string key)=>options.ContainsKey(key);
