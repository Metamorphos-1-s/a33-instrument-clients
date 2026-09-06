using A33.Instrument.Core;

namespace A33.Instrument.HardwareValidation;

public static class PersistenceHardwareRunner
{
    public static Task<int> RunAsync(string[] args, Func<InstrumentMonitoringService>? monitoringFactory = null) =>
        PersistenceHardwareAuthorizationGate.ExecuteAfterAuthorizationAsync(args,
            authorization => RunAuthorizedAsync(authorization, monitoringFactory ?? (() => new InstrumentMonitoringService())));

    private static async Task<int> RunAuthorizedAsync(PersistenceHardwareAuthorization authorization, Func<InstrumentMonitoringService> monitoringFactory)
    {
        var repositoryRoot = RepositoryRoot.Find();
        var evidenceRoot = Path.Combine(repositoryRoot, "Results", "pc_stage2b_hw");
        var baseline = PersistenceBaselineContract.LoadFromRepository(repositoryRoot);
        var clientCommit = ToolBuildIdentity.GetCommit(typeof(PersistenceHardwareRunner).Assembly);
        string journalPath;
        string workflowId;
        ValidatedPreflightBinding binding;
        if (authorization.PreflightWorkflowId is not null)
        {
            binding = PreflightEvidenceValidator.Validate(evidenceRoot, authorization.PreflightWorkflowId, clientCommit, baseline, DateTimeOffset.UtcNow);
            workflowId = Guid.NewGuid().ToString("D");
            var directory = PersistenceEvidenceDirectory.CreateUnique(evidenceRoot, workflowId, DateTimeOffset.UtcNow);
            journalPath = Path.Combine(directory, "persistence-journal.json");
        }
        else
        {
            workflowId = authorization.WorkflowId!;
            var matches = Directory.Exists(evidenceRoot)
                ? Directory.GetFiles(evidenceRoot, "persistence-journal.json", SearchOption.AllDirectories)
                    .Where(path => Path.GetFileName(Path.GetDirectoryName(path)!).EndsWith("_" + workflowId, StringComparison.OrdinalIgnoreCase)).ToArray()
                : [];
            if (matches.Length != 1) { Console.Error.WriteLine("PERSISTENCE_JOURNAL_NOT_UNIQUE"); return 14; }
            journalPath = matches[0];
            var journal = await PersistenceJournalStore.ReadAsync(journalPath);
            if (journal.WorkflowId != workflowId) { Console.Error.WriteLine("PERSISTENCE_JOURNAL_ID_MISMATCH"); return 14; }
            binding = PreflightEvidenceValidator.Validate(evidenceRoot, journal.BoundPreflightWorkflowId, clientCommit, baseline, DateTimeOffset.UtcNow, requireFresh: false);
        }

        var safety = new PersistenceSafetyContext(baseline, binding, clientCommit);
        await using var monitoring = monitoringFactory();
        try
        {
            await monitoring.ConnectAsync(new MonitoringOptions(TransportMode.Tcp, "192.168.1.100", 502, UnitId: 1));
            await monitoring.StartMonitoringAsync();
            var persistence = new ConfigurationPersistenceService(new MonitoringConfigurationPersistenceDevice(monitoring), safety);
            var journal = authorization.PreflightWorkflowId is not null
                ? await persistence.StartAsync(journalPath, workflowId, clientCommit)
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
