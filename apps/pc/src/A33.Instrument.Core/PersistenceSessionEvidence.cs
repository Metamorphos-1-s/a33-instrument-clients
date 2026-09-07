namespace A33.Instrument.Core;

public sealed record PersistenceTraceStatistics(
    int Fc03Attempted, int Fc03Succeeded, int Fc03Failed,
    int Fc06Attempted,
    int Fc16Attempted, int Fc16Succeeded, int Fc16Failed,
    int StagingWrites, int MailboxWrites, int Begin, int Validate,
    int Apply, int Cancel, int Save)
{
    public static PersistenceTraceStatistics FromTrace(IReadOnlyList<ModbusOperationTrace> trace) => new(
        trace.Count(x => x.FunctionCode == 3), trace.Count(x => x.FunctionCode == 3 && x.Succeeded), trace.Count(x => x.FunctionCode == 3 && !x.Succeeded),trace.Count(x=>x.FunctionCode==6),
        trace.Count(x => x.FunctionCode == 16), trace.Count(x => x.FunctionCode == 16 && x.Succeeded), trace.Count(x => x.FunctionCode == 16 && !x.Succeeded),
        trace.Count(x => x.FunctionCode == 16 && x.Address >= 0x0140 && x.Address <= 0x017F),
        trace.Count(x => x.FunctionCode == 16 && x.Address == 0x0040),
        trace.Count(x => x.MailboxCommandId == 9), trace.Count(x => x.MailboxCommandId == 10),
        trace.Count(x => x.MailboxCommandId == 11), trace.Count(x => x.MailboxCommandId == 12),
        trace.Count(x => x.MailboxCommandId == 13));
}

public sealed record PersistenceTraceErrors(int Timeouts,int CrcErrors,int MbapErrors,int TidErrors,int UnitErrors,int ModbusExceptions,int BadFrames,int TransportErrors)
{
    public bool IsClean=>Timeouts==0&&CrcErrors==0&&MbapErrors==0&&TidErrors==0&&UnitErrors==0&&ModbusExceptions==0&&BadFrames==0&&TransportErrors==0;
    public static PersistenceTraceErrors FromTrace(IReadOnlyList<ModbusOperationTrace> trace)=>new(
        trace.Count(x=>x.ErrorCategory=="Timeout"),trace.Count(x=>x.ErrorCategory=="CRC"),trace.Count(x=>x.ErrorCategory=="MBAP"),
        trace.Count(x=>x.ErrorCategory=="TID"),trace.Count(x=>x.ErrorCategory=="Unit"),trace.Count(x=>x.ErrorCategory=="ModbusException"),
        trace.Count(x=>x.ErrorCategory=="BadFrame"),trace.Count(x=>x.ErrorCategory=="Transport"));
}

public sealed record PersistenceSessionSummary(
    int SchemaVersion, string SessionId, string WorkflowId, string ClientCommit,
    string ToolAssemblyVersion, string ToolSha256,
    DateTimeOffset StartedAtUtc, DateTimeOffset CompletedAtUtc, double DurationMs,
    PreflightConnectionStatistics Connections, PersistenceTraceStatistics Requests,
    PersistenceTraceErrors Errors,long AutomaticReconnects, string Phase, string? Error,
    string TraceFile, string TraceSha256, string EnvironmentFile, string EnvironmentSha256,
    string? FinalStabilityFile,string? FinalStabilitySha256);

public static class PersistenceCompletionEvidenceValidator
{
    public static void Validate(string directory,string workflowId,PersistenceJournal journal,TrustedPersistenceBaseline baseline,string clientCommit,string toolVersion,string toolSha256)
    {
        PersistenceJournalStore.Validate(journal,Path.Combine(directory,"persistence-journal.json"));
        if(journal.WorkflowId!=workflowId||journal.ClientCommit!=clientCommit||journal.Phase!=PersistencePhase.Complete||journal.FinalConfigurationMatches64Of64!=true||
            journal.Stm32Commit!=ConfigurationPersistenceService.FixedStm32Commit||journal.BaselineId!=baseline.Manifest.BaselineId||
            journal.BaselineSha256!=baseline.Manifest.ActiveArraySha256||journal.BaselineManifestSha256!=baseline.ManifestSha256)
            throw new InvalidDataException("Complete journal binding is invalid.");
        var stabilityPath=Path.Combine(directory,"final-stability.json");
        if(!File.Exists(stabilityPath))throw new InvalidDataException("Final stability evidence is missing.");
        var stability=AtomicJsonFile.Read<FinalStabilityReport>(stabilityPath);
        FinalStabilityReportValidator.ValidatePass(stability,baseline);
        var expected=FinalPersistenceExpectation.FromCycleB(journal);
        if(stability.Expectation!=expected||stability.StartedAtUtc<journal.CycleBEvidence!.RebootEvidence!.CapturedAtUtc)
            throw new InvalidDataException("Final stability is not bound to the Cycle B second-reboot result.");
        var summaries=Directory.GetFiles(directory,"persistence-session-*-summary.json")
            .Select(path=>(Path:path,Summary:AtomicJsonFile.Read<PersistenceSessionSummary>(path)))
            .Where(x=>x.Summary.Phase=="COMPLETE_STABILITY_PASS"&&x.Summary.FinalStabilityFile=="final-stability.json").ToArray();
        if(summaries.Length!=1)throw new InvalidDataException("A unique final read-only session summary is required.");
        var summary=summaries[0].Summary;
        if(summary.SchemaVersion!=1||summary.WorkflowId!=workflowId||summary.ClientCommit!=clientCommit||summary.ToolAssemblyVersion!=toolVersion||summary.ToolSha256!=toolSha256||
            Path.GetFileName(summaries[0].Path)!=$"persistence-session-{summary.SessionId}-summary.json"||summary.StartedAtUtc.Offset!=TimeSpan.Zero||
            summary.CompletedAtUtc.Offset!=TimeSpan.Zero||summary.CompletedAtUtc<summary.StartedAtUtc||Math.Abs((summary.CompletedAtUtc-summary.StartedAtUtc).TotalMilliseconds-summary.DurationMs)>0.01||
            summary.Error is not null||summary.AutomaticReconnects!=0||summary.Connections is not{ConnectionAttempts:1,ConnectionSucceeded:1,ConnectionFailed:0,Disconnects:1,AutomaticRetries:0}||
            summary.Requests.Fc03Attempted<=0||summary.Requests.Fc03Attempted!=summary.Requests.Fc03Succeeded||summary.Requests.Fc03Failed!=0||
            summary.Requests.Fc06Attempted!=0||summary.Requests.Fc16Attempted!=0||summary.Requests.StagingWrites!=0||summary.Requests.MailboxWrites!=0||
            summary.Requests.Begin!=0||summary.Requests.Validate!=0||summary.Requests.Apply!=0||summary.Requests.Cancel!=0||summary.Requests.Save!=0||!summary.Errors.IsClean)
            throw new InvalidDataException("Final read-only session counters are not clean.");
        if(summary.TraceFile!=$"persistence-session-{summary.SessionId}-request-trace.json"||summary.EnvironmentFile!=$"persistence-session-{summary.SessionId}-environment.json")
            throw new InvalidDataException("Final session filenames are not bound to its session ID.");
        var tracePath=BoundPath(directory,summary.TraceFile);var environmentPath=BoundPath(directory,summary.EnvironmentFile);var boundStability=BoundPath(directory,summary.FinalStabilityFile!);
        if(PersistenceBaselineContract.ComputeFileSha256(tracePath)!=summary.TraceSha256||PersistenceBaselineContract.ComputeFileSha256(environmentPath)!=summary.EnvironmentSha256||
            PersistenceBaselineContract.ComputeFileSha256(boundStability)!=summary.FinalStabilitySha256)
            throw new InvalidDataException("Final session evidence hash mismatch.");
        var trace=AtomicJsonFile.Read<ModbusOperationTrace[]>(tracePath);
        if(PersistenceTraceStatistics.FromTrace(trace)!=summary.Requests||PersistenceTraceErrors.FromTrace(trace)!=summary.Errors||
            trace.Length!=summary.Requests.Fc03Attempted||trace.Any(x=>x.FunctionCode!=3||!x.Succeeded||x.ErrorCategory is not null||x.Error is not null||
                x.StartedAtUtc.Offset!=TimeSpan.Zero||x.CompletedAtUtc.Offset!=TimeSpan.Zero||x.StartedAtUtc<summary.StartedAtUtc||x.CompletedAtUtc>summary.CompletedAtUtc||x.CompletedAtUtc<x.StartedAtUtc))
            throw new InvalidDataException("Final session trace semantics mismatch.");
        var environment=AtomicJsonFile.Read<PreflightEnvironmentEvidence>(environmentPath);
        if(environment.WorkflowId!=workflowId||environment.ClientCommit!=clientCommit||environment.ToolAssemblyVersion!=toolVersion||environment.ToolSha256!=toolSha256||
            environment.StartedAtUtc!=summary.StartedAtUtc||environment.CompletedAtUtc!=summary.CompletedAtUtc||environment.StartedAtUtc.Offset!=TimeSpan.Zero||environment.CompletedAtUtc.Offset!=TimeSpan.Zero||
            stability.StartedAtUtc<summary.StartedAtUtc||stability.CompletedAtUtc>summary.CompletedAtUtc)
            throw new InvalidDataException("Final session environment or timing binding mismatch.");
    }
    private static string BoundPath(string directory,string file){if(Path.GetFileName(file)!=file)throw new InvalidDataException("Evidence filename is not local.");var path=Path.Combine(directory,file);if(!File.Exists(path))throw new InvalidDataException($"Evidence file is missing: {file}");return path;}
}
