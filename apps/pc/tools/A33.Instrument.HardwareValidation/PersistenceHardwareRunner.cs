using A33.Instrument.Core;

namespace A33.Instrument.HardwareValidation;

public static class PersistenceHardwareRunner
{
    public static Task<int> RunAsync(
        string[] args, Func<InstrumentMonitoringService>? monitoringFactory = null,
        string? evidenceRootOverride = null, Func<DateTimeOffset>? utcNow = null) =>
        PersistenceHardwareAuthorizationGate.ExecuteAfterAuthorizationAsync(args,
            authorization => RunAuthorizedAsync(authorization, monitoringFactory ?? (() => new InstrumentMonitoringService()), evidenceRootOverride, utcNow ?? (() => DateTimeOffset.UtcNow)));

    private static async Task<int> RunAuthorizedAsync(
        PersistenceHardwareAuthorization authorization, Func<InstrumentMonitoringService> monitoringFactory,
        string? evidenceRootOverride, Func<DateTimeOffset> utcNow)
    {
        var repositoryRoot = RepositoryRoot.Find();
        var evidenceRoot = evidenceRootOverride ?? Path.Combine(repositoryRoot, "Results", "pc_stage2b_hw");
        var baseline = PersistenceBaselineContract.LoadFromRepository(repositoryRoot);
        var assembly = typeof(PersistenceHardwareRunner).Assembly;
        var clientCommit = ToolBuildIdentity.GetCommit(assembly);
        var toolVersion = assembly.GetName().Version?.ToString() ?? "unknown";
        var toolSha256 = ToolBuildIdentity.GetToolSha256(assembly);
        string journalPath;
        string workflowId;
        PersistenceJournal? existingJournal=null;
        ValidatedPreflightBinding binding;
        if (authorization.PreflightWorkflowId is not null)
        {
            binding = PreflightEvidenceValidator.Validate(evidenceRoot, authorization.PreflightWorkflowId, clientCommit, baseline, utcNow(),
                toolVersion, toolSha256);
            workflowId = Guid.NewGuid().ToString("D");
            var directory = PersistenceEvidenceDirectory.CreateUnique(evidenceRoot, workflowId, utcNow());
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
            existingJournal = await PersistenceJournalStore.ReadAsync(journalPath);
            if (existingJournal.WorkflowId != workflowId) { Console.Error.WriteLine("PERSISTENCE_JOURNAL_ID_MISMATCH"); return 14; }
            var existingStability=Path.Combine(Path.GetDirectoryName(journalPath)!,"final-stability.json");
            if(existingJournal.Phase==PersistencePhase.Complete&&File.Exists(existingStability))
            {
                var report=AtomicJsonFile.Read<FinalStabilityReport>(existingStability);
                if(report.Passed)FinalStabilityReportValidator.ValidatePass(report,baseline);
                return report.Passed?0:23;
            }
            binding = PreflightEvidenceValidator.Validate(evidenceRoot, existingJournal.BoundPreflightWorkflowId, clientCommit, baseline, utcNow(),
                toolVersion, toolSha256, requireFresh: false);
        }

        var safety = new PersistenceSafetyContext(baseline, binding, clientCommit);
        var sessionId = Guid.NewGuid().ToString("D");
        var sessionStarted = DateTimeOffset.UtcNow;
        var sessionDirectory = Path.GetDirectoryName(journalPath)!;
        var traceFile = $"persistence-session-{sessionId}-request-trace.json";
        var environmentFile = $"persistence-session-{sessionId}-environment.json";
        var summaryFile = $"persistence-session-{sessionId}-summary.json";
        var connectionAttempts = 0; var connectionSucceeded = 0; var connectionFailed = 0; var disconnects = 0;
        string phase = "NOT_STARTED"; string? sessionError = null;
        await using var monitoring = monitoringFactory();
        try
        {
            connectionAttempts++;
            try
            {
                await monitoring.ConnectAsync(new MonitoringOptions(TransportMode.Tcp, "192.168.1.100", 502, UnitId: 1, AutoReconnectAttempts: 0));
                connectionSucceeded++;
            }
            catch { connectionFailed++; throw; }
            await monitoring.StartMonitoringAsync();
            var persistenceDevice=new MonitoringConfigurationPersistenceDevice(monitoring);
            var persistence = new ConfigurationPersistenceService(persistenceDevice, safety);
            var journal = authorization.PreflightWorkflowId is not null
                ? await persistence.StartAsync(journalPath, workflowId, clientCommit)
                : await persistence.ResumeAfterManualRebootAsync(journalPath);
            phase = journal.Phase.ToString();
            Console.WriteLine($"WORKFLOW_ID={workflowId}");
            Console.WriteLine($"JOURNAL={journalPath}");
            Console.WriteLine($"PHASE={journal.Phase}");
            if (journal.Phase is PersistencePhase.WaitingForFirstReboot or PersistencePhase.WaitingForSecondReboot)
            {
                Console.WriteLine("MANUAL_REBOOT_REQUIRED; no automatic reboot or further write will occur.");
                return 20;
            }
            if(journal.Phase==PersistencePhase.Complete)
            {
                var stability=await new FinalPersistenceStabilityService(persistenceDevice,baseline).RunAsync();
                if(stability.Passed)FinalStabilityReportValidator.ValidatePass(stability,baseline);
                await AtomicJsonFile.WriteAsync(Path.Combine(sessionDirectory,"final-stability.json"),stability);
                phase=stability.Passed?"COMPLETE_STABILITY_PASS":"COMPLETE_STABILITY_FAIL";
                return stability.Passed?0:23;
            }
            return journal.Phase == PersistencePhase.Complete ? 0 : journal.Phase == PersistencePhase.ResultUncertain ? 21 : 22;
        }
        catch(Exception error)
        {
            sessionError = error.Message;
            phase = "FAILED_OR_UNCERTAIN";
            Console.Error.WriteLine(error.Message);
            return 22;
        }
        finally
        {
            await monitoring.StopMonitoringAsync();
            if(monitoring.State != MonitoringConnectionState.Disconnected){await monitoring.DisconnectAsync();disconnects++;}
            var completed = DateTimeOffset.UtcNow;
            var trace = monitoring.OperationTrace;
            var tracePath = Path.Combine(sessionDirectory, traceFile);
            var environmentPath = Path.Combine(sessionDirectory, environmentFile);
            await AtomicJsonFile.WriteAsync(tracePath, trace);
            var environment = PreflightEnvironment.Capture(assembly, workflowId, clientCommit, toolSha256, sessionStarted, completed);
            await AtomicJsonFile.WriteAsync(environmentPath, environment);
            var stabilityPath=Path.Combine(sessionDirectory,"final-stability.json");
            var hasStability=File.Exists(stabilityPath);
            var summary = new PersistenceSessionSummary(1, sessionId, workflowId, clientCommit, toolVersion, toolSha256,
                sessionStarted, completed, (completed-sessionStarted).TotalMilliseconds,
                new(connectionAttempts,connectionSucceeded,connectionFailed,disconnects,0),
                PersistenceTraceStatistics.FromTrace(trace),PersistenceTraceErrors.FromTrace(trace),monitoring.Diagnostics.Reconnects, phase, sessionError,
                traceFile, PersistenceBaselineContract.ComputeFileSha256(tracePath),
                environmentFile, PersistenceBaselineContract.ComputeFileSha256(environmentPath),
                hasStability?"final-stability.json":null,hasStability?PersistenceBaselineContract.ComputeFileSha256(stabilityPath):null);
            await AtomicJsonFile.WriteAsync(Path.Combine(sessionDirectory, summaryFile), summary);
        }
    }
}
