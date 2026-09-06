using System.Reflection;
using A33.Instrument.Core;

namespace A33.Instrument.HardwareValidation;

public static class PersistenceHardwareRunner
{
    private const string EvidenceRoot = "Results/pc_stage2b_hw";

    public static Task<int> RunAsync(string[] args, Func<InstrumentMonitoringService>? monitoringFactory = null) =>
        PersistenceHardwareAuthorizationGate.ExecuteAfterAuthorizationAsync(args,
            authorization => RunAuthorizedAsync(authorization, monitoringFactory ?? (() => new InstrumentMonitoringService())));

    private static async Task<int> RunAuthorizedAsync(PersistenceHardwareAuthorization authorization, Func<InstrumentMonitoringService> monitoringFactory)
    {
        string journalPath;
        string workflowId;
        if (authorization.WorkflowId is null)
        {
            workflowId = Guid.NewGuid().ToString("D");
            var directory = PersistenceEvidenceDirectory.CreateUnique(EvidenceRoot, workflowId, DateTimeOffset.UtcNow);
            journalPath = Path.Combine(directory, "persistence-journal.json");
        }
        else
        {
            workflowId = authorization.WorkflowId;
            var matches = Directory.Exists(EvidenceRoot)
                ? Directory.GetFiles(EvidenceRoot, "persistence-journal.json", SearchOption.AllDirectories)
                    .Where(path => Path.GetFileName(Path.GetDirectoryName(path)!).EndsWith("_" + workflowId, StringComparison.OrdinalIgnoreCase)).ToArray()
                : [];
            if (matches.Length != 1) { Console.Error.WriteLine("PERSISTENCE_JOURNAL_NOT_UNIQUE"); return 14; }
            journalPath = matches[0];
        }

        await using var monitoring = monitoringFactory();
        try
        {
            await monitoring.ConnectAsync(new MonitoringOptions(TransportMode.Tcp, "192.168.1.100", 502, UnitId: 1));
            await monitoring.StartMonitoringAsync();
            var persistence = new ConfigurationPersistenceService(new MonitoringConfigurationPersistenceDevice(monitoring));
            var commit = typeof(ConfigurationPersistenceService).Assembly
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown";
            var journal = authorization.WorkflowId is null
                ? await persistence.StartAsync(journalPath, workflowId, commit)
                : await persistence.ResumeAfterManualRebootAsync(journalPath);
            Console.WriteLine($"WORKFLOW_ID={workflowId}");
            Console.WriteLine($"JOURNAL={journalPath}");
            Console.WriteLine($"PHASE={journal.Phase}");
            if (journal.Phase is PersistencePhase.WaitingForFirstReboot or PersistencePhase.WaitingForSecondReboot)
            {
                Console.WriteLine("MANUAL_REBOOT_REQUIRED; no automatic reboot or further write will occur.");
                return 20;
            }
            return journal.Phase == PersistencePhase.Complete ? 0 : journal.Phase == PersistencePhase.ResultUncertain ? 21 : 22;
        }
        finally
        {
            await monitoring.StopMonitoringAsync();
            await monitoring.DisconnectAsync();
        }
    }
}
