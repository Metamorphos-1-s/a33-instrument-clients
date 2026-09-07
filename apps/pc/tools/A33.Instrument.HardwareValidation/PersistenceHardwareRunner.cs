using A33.Instrument.Core;

namespace A33.Instrument.HardwareValidation;

public static class PersistenceHardwareRunner
{
    public static Task<int> RunAsync(
        string[] args, Func<InstrumentMonitoringService>? monitoringFactory = null,
        string? evidenceRootOverride = null, Func<DateTimeOffset>? utcNow = null,
        IPersistenceClock? workflowClock = null, FinalStabilityPolicy? stabilityPolicy = null,TimeSpan? readinessTimeout = null) =>
        PersistenceHardwareAuthorizationGate.ExecuteAfterAuthorizationAsync(args,
            authorization => RunAuthorizedAsync(authorization, monitoringFactory ?? (() => new InstrumentMonitoringService()), evidenceRootOverride,
                utcNow ?? (() => DateTimeOffset.UtcNow), workflowClock ?? new SystemPersistenceClock(), stabilityPolicy ?? FinalStabilityPolicy.Fixed600Seconds,
                readinessTimeout ?? TimeSpan.FromSeconds(5)));

    private static async Task<int> RunAuthorizedAsync(
        PersistenceHardwareAuthorization authorization, Func<InstrumentMonitoringService> monitoringFactory,
        string? evidenceRootOverride, Func<DateTimeOffset> utcNow, IPersistenceClock workflowClock, FinalStabilityPolicy stabilityPolicy,TimeSpan readinessTimeout)
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
            binding = PreflightEvidenceValidator.Validate(evidenceRoot, existingJournal.BoundPreflightWorkflowId, clientCommit, baseline, utcNow(),
                toolVersion, toolSha256, requireFresh: false);
        }

        var workflowDirectory=Path.GetDirectoryName(journalPath)!;
        if(existingJournal?.Phase==PersistencePhase.Complete)
        {
            PersistenceCompletionEvidenceValidator.Validate(workflowDirectory,workflowId,existingJournal,baseline,clientCommit,toolVersion,toolSha256);
            return 0;
        }

        var safety = new PersistenceSafetyContext(baseline, binding, clientCommit);
        var sessionId = Guid.NewGuid().ToString("D");
        var sessionStarted = DateTimeOffset.UtcNow;
        var sessionDirectory = workflowDirectory;
        var traceFile = $"persistence-session-{sessionId}-request-trace.json";
        var environmentFile = $"persistence-session-{sessionId}-environment.json";
        var summaryFile = $"persistence-session-{sessionId}-summary.json";
        var connectionAttempts = 0; var connectionSucceeded = 0; var connectionFailed = 0; var disconnects = 0;long generationStart=0;long generationEnd=0;
        string phase = "NOT_STARTED"; string? sessionError = null;var exitCode=22;DateTimeOffset? stabilityCompleted=null;
        await using var monitoring = monitoringFactory();
        try
        {
            connectionAttempts++;
            try
            {
                await monitoring.ConnectAsync(new MonitoringOptions(TransportMode.Tcp, "192.168.1.100", 502, UnitId: 1, AutoReconnectAttempts: 0,StrictSession:true));
                connectionSucceeded++;
                generationStart=monitoring.ConnectionGeneration;
            }
            catch { connectionFailed++; throw; }
            await monitoring.StartMonitoringAsync();
            await monitoring.WaitForFreshSnapshotAsync(readinessTimeout);
            var persistenceDevice=new MonitoringConfigurationPersistenceDevice(monitoring);
            var persistence = new ConfigurationPersistenceService(persistenceDevice, safety, workflowClock);
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
                exitCode=20;
            }
            else if(journal.Phase==PersistencePhase.Complete)
            {
                var stability=await new FinalPersistenceStabilityService(persistenceDevice,baseline,FinalPersistenceExpectation.FromCycleB(journal),workflowClock,stabilityPolicy).RunAsync();
                stabilityCompleted=stability.CompletedAtUtc;
                if(stability.Passed)FinalStabilityReportValidator.ValidatePass(stability,baseline);
                await AtomicJsonFile.WriteAsync(Path.Combine(sessionDirectory,"final-stability.json"),stability);
                phase=stability.Passed?"COMPLETE_STABILITY_PASS":"COMPLETE_STABILITY_FAIL";
                exitCode=stability.Passed?0:23;
            }
            else exitCode=journal.Phase==PersistencePhase.ResultUncertain?21:22;
        }
        catch(Exception error)
        {
            sessionError = error.Message;
            phase = "FAILED_OR_UNCERTAIN";
            Console.Error.WriteLine(error.Message);
            exitCode=22;
        }
        finally
        {
            await monitoring.StopMonitoringAsync();
            generationEnd=monitoring.ConnectionGeneration;
            var diagnostics=monitoring.Diagnostics.Snapshot();var strictFault=monitoring.StrictFault;
            if(monitoring.State != MonitoringConnectionState.Disconnected){await monitoring.DisconnectAsync();disconnects++;}
            var completed = DateTimeOffset.UtcNow;
            if(stabilityCompleted>completed)completed=stabilityCompleted.Value;
            var trace = monitoring.OperationTrace;
            var tracePath = Path.Combine(sessionDirectory, traceFile);
            var environmentPath = Path.Combine(sessionDirectory, environmentFile);
            await AtomicJsonFile.WriteAsync(tracePath, trace);
            var environment = PreflightEnvironment.Capture(assembly, workflowId, clientCommit, toolSha256, sessionStarted, completed);
            await AtomicJsonFile.WriteAsync(environmentPath, environment);
            var stabilityPath=Path.Combine(sessionDirectory,"final-stability.json");
            var hasStability=File.Exists(stabilityPath);
            var summary = new PersistenceSessionSummary(2, sessionId, workflowId, clientCommit, toolVersion, toolSha256,
                sessionStarted, completed, (completed-sessionStarted).TotalMilliseconds,
                new(connectionAttempts,connectionSucceeded,connectionFailed,disconnects,0),
                PersistenceTraceStatistics.FromTrace(trace),PersistenceTraceErrors.FromTrace(trace),diagnostics,strictFault,
                monitoring.Diagnostics.Reconnects,generationStart,generationEnd,phase,sessionError,
                traceFile, PersistenceBaselineContract.ComputeFileSha256(tracePath),
                environmentFile, PersistenceBaselineContract.ComputeFileSha256(environmentPath),
                hasStability?"final-stability.json":null,hasStability?PersistenceBaselineContract.ComputeFileSha256(stabilityPath):null);
            await AtomicJsonFile.WriteAsync(Path.Combine(sessionDirectory, summaryFile), summary);
        }
        if(exitCode==0)
        {
            var completedJournal=await PersistenceJournalStore.ReadAsync(journalPath);
            PersistenceCompletionEvidenceValidator.Validate(sessionDirectory,workflowId,completedJournal,baseline,clientCommit,toolVersion,toolSha256);
        }
        return exitCode;
    }
}
