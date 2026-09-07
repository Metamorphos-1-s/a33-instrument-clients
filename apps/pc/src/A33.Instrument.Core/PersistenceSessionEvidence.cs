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
    PersistenceTraceErrors Errors,CommunicationDiagnosticsSnapshot Diagnostics,StrictMonitoringFault? StrictFault,
    long AutomaticReconnects,long ConnectionGenerationStart,long ConnectionGenerationEnd,string Phase,string? Error,
    string TraceFile, string TraceSha256, string EnvironmentFile, string EnvironmentSha256,
    string? FinalStabilityFile,string? FinalStabilitySha256);

public static class PersistenceCompletionEvidenceValidator
{
    public static void ValidateWaitingSession(string journalPath,PersistencePhase waitingPhase,string clientCommit,string toolVersion,string toolSha256)
    {
        var directory=Path.GetDirectoryName(Path.GetFullPath(journalPath))??throw new InvalidDataException("Journal directory is missing.");
        var journal=PersistenceJournalStore.ReadAsync(journalPath).GetAwaiter().GetResult();PersistenceJournalStore.Validate(journal,journalPath);
        if(journal.Phase!=waitingPhase||journal.WorkflowId!=Path.GetFileName(directory).Split('_').Last())throw new InvalidDataException("Journal waiting phase or workflow directory does not match the completed session.");
        var matches=ReadSummaries(directory).Where(x=>x.Summary.Phase==waitingPhase.ToString()).ToArray();
        if(matches.Length!=1)throw new InvalidDataException("A unique clean waiting session is required before manual reboot.");
        ValidateCommon(directory,matches[0],journal.WorkflowId,clientCommit,toolVersion,toolSha256);
        ValidateCycleSession(directory,matches[0].Summary,journal,waitingPhase);
    }

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
        var all=ReadSummaries(directory);if(all.Length!=3)throw new InvalidDataException("Complete evidence requires exactly three formal sessions.");
        var cycleA=all.SingleOrDefault(x=>x.Summary.Phase==PersistencePhase.WaitingForFirstReboot.ToString());
        var cycleB=all.SingleOrDefault(x=>x.Summary.Phase==PersistencePhase.WaitingForSecondReboot.ToString());
        var final=all.SingleOrDefault(x=>x.Summary.Phase=="COMPLETE_STABILITY_PASS"&&x.Summary.FinalStabilityFile=="final-stability.json");
        if(cycleA==default||cycleB==default||final==default)throw new InvalidDataException("Cycle A, Cycle B and final sessions must each be unique.");
        foreach(var item in all)ValidateCommon(directory,item,workflowId,clientCommit,toolVersion,toolSha256);
        ValidateCycleSession(directory,cycleA.Summary,journal,PersistencePhase.WaitingForFirstReboot);
        ValidateCycleSession(directory,cycleB.Summary,journal,PersistencePhase.WaitingForSecondReboot);
        if(cycleA.Summary.CompletedAtUtc>cycleB.Summary.StartedAtUtc||cycleB.Summary.CompletedAtUtc>final.Summary.StartedAtUtc||
            cycleA.Summary.Requests.Save+cycleB.Summary.Requests.Save+final.Summary.Requests.Save!=2)
            throw new InvalidDataException("Formal persistence session order or SAVE total is invalid.");
        var summary=final.Summary;
        if(
            summary.Requests.Fc03Attempted<=0||summary.Requests.Fc03Attempted!=summary.Requests.Fc03Succeeded||summary.Requests.Fc03Failed!=0||
            summary.Requests.Fc06Attempted!=0||summary.Requests.Fc16Attempted!=0||summary.Requests.StagingWrites!=0||summary.Requests.MailboxWrites!=0||
            summary.Requests.Begin!=0||summary.Requests.Validate!=0||summary.Requests.Apply!=0||summary.Requests.Cancel!=0||summary.Requests.Save!=0||!summary.Errors.IsClean)
            throw new InvalidDataException("Final read-only session counters are not clean.");
        var tracePath=BoundPath(directory,summary.TraceFile);var environmentPath=BoundPath(directory,summary.EnvironmentFile);var boundStability=BoundPath(directory,summary.FinalStabilityFile!);
        if(PersistenceBaselineContract.ComputeFileSha256(tracePath)!=summary.TraceSha256||PersistenceBaselineContract.ComputeFileSha256(environmentPath)!=summary.EnvironmentSha256||
            PersistenceBaselineContract.ComputeFileSha256(boundStability)!=summary.FinalStabilitySha256)
            throw new InvalidDataException("Final session evidence hash mismatch.");
        var trace=AtomicJsonFile.Read<ModbusOperationTrace[]>(tracePath);
        if(PersistenceTraceStatistics.FromTrace(trace)!=summary.Requests||PersistenceTraceErrors.FromTrace(trace)!=summary.Errors||
            trace.Length!=summary.Requests.Fc03Attempted||trace.Any(x=>x.FunctionCode!=3||!x.Succeeded||x.ErrorCategory is not null||x.Error is not null||
                x.StartedAtUtc.Offset!=TimeSpan.Zero||x.CompletedAtUtc.Offset!=TimeSpan.Zero||x.StartedAtUtc<summary.StartedAtUtc||x.CompletedAtUtc>summary.CompletedAtUtc||x.CompletedAtUtc<x.StartedAtUtc))
            throw new InvalidDataException("Final session trace semantics mismatch.");
        if(stability.StartedAtUtc<summary.StartedAtUtc||stability.CompletedAtUtc>summary.CompletedAtUtc)
            throw new InvalidDataException("Final session environment or timing binding mismatch.");
    }
    private static (string Path,PersistenceSessionSummary Summary)[] ReadSummaries(string directory)=>Directory.GetFiles(directory,"persistence-session-*-summary.json").Select(path=>(path,AtomicJsonFile.Read<PersistenceSessionSummary>(path))).ToArray();
    private static void ValidateCycleSession(string directory,PersistenceSessionSummary summary,PersistenceJournal journal,PersistencePhase phase)
    {
        var r=summary.Requests;if(r.Begin!=1||r.Validate!=1||r.Apply!=1||r.Cancel!=0||r.Save!=1||r.StagingWrites!=1||r.MailboxWrites!=4||
            r.Fc03Attempted<=0||r.Fc03Attempted!=r.Fc03Succeeded||r.Fc03Failed!=0||r.Fc06Attempted!=0||r.Fc16Attempted!=5||r.Fc16Succeeded!=5||r.Fc16Failed!=0)
            throw new InvalidDataException("Cycle session command shape is invalid.");
        var cycle=phase==PersistencePhase.WaitingForFirstReboot?journal.CycleAEvidence:journal.CycleBEvidence??throw new InvalidDataException("Cycle B evidence is missing.");
        var expectedCycle=phase==PersistencePhase.WaitingForFirstReboot?PersistenceCycle.A:PersistenceCycle.B;var expectedStage=phase==PersistencePhase.WaitingForFirstReboot?PersistenceAuthorizationStage.WaitingForFirstReboot:PersistenceAuthorizationStage.WaitingForSecondReboot;
        var currentStateValid=journal.Phase==PersistencePhase.Complete
            ? journal.Cycle==PersistenceCycle.Complete&&journal.AuthorizationStage==PersistenceAuthorizationStage.Complete&&journal.ReservedSaveCount==2
            : journal.Phase==phase&&journal.Cycle==expectedCycle&&journal.AuthorizationStage==expectedStage&&journal.ReservedSaveCount==(phase==PersistencePhase.WaitingForFirstReboot?1:2);
        if(!currentStateValid||cycle.Cycle!=expectedCycle||cycle.SaveConfirmed is null||!cycle.SaveRequestMayHaveBeenSent||
            !cycle.SaveToken.HasValue||cycle.MailboxTokens is null||!cycle.MailboxTokens.Contains(cycle.SaveToken.Value)||
            journal.SaveTokens.Length!=journal.ReservedSaveCount||!journal.SaveTokens.Contains(cycle.SaveToken.Value))throw new InvalidDataException("Waiting journal SAVE budget or Cycle evidence is invalid.");
        var trace=AtomicJsonFile.Read<ModbusOperationTrace[]>(BoundPath(directory,summary.TraceFile));if(trace.Any(x=>x.FunctionCode is not(3 or 16)))throw new InvalidDataException("Cycle trace contains an unsupported function.");
        var writes=trace.Where(x=>x.FunctionCode==16).Select(ParseWrite).ToArray();var staging=writes.SingleOrDefault(x=>x.Address>=0x0140&&x.Address<=0x017F);var mailbox=writes.Where(x=>x.Address==0x0040).ToArray();
        var expectedBrightness=phase==PersistencePhase.WaitingForFirstReboot?(ushort)4:(ushort)3;
        if(staging is null||staging.Address!=0x0156||staging.Values.Length!=1||staging.Values[0]!=expectedBrightness||mailbox.Length!=4||mailbox.Any(x=>x.Values.Length!=12)||
            !mailbox.Select(x=>x.Values[1]).SequenceEqual(new ushort[]{9,10,11,13}))throw new InvalidDataException("Cycle FC16 payload shape is invalid.");
        var save=mailbox.Single(x=>x.Values[1]==13);if(save.Values[0]!=cycle.SaveToken.Value)throw new InvalidDataException("Cycle SAVE trace token conflicts with the journal.");
    }
    private static void ValidateCommon(string directory,(string Path,PersistenceSessionSummary Summary) item,string workflowId,string clientCommit,string toolVersion,string toolSha256)
    {
        var s=item.Summary;if(s.SchemaVersion!=2||s.WorkflowId!=workflowId||s.ClientCommit!=clientCommit||s.ToolAssemblyVersion!=toolVersion||s.ToolSha256!=toolSha256||
            Path.GetFileName(item.Path)!=$"persistence-session-{s.SessionId}-summary.json"||s.TraceFile!=$"persistence-session-{s.SessionId}-request-trace.json"||s.EnvironmentFile!=$"persistence-session-{s.SessionId}-environment.json"||
            s.StartedAtUtc.Offset!=TimeSpan.Zero||s.CompletedAtUtc.Offset!=TimeSpan.Zero||s.CompletedAtUtc<s.StartedAtUtc||Math.Abs((s.CompletedAtUtc-s.StartedAtUtc).TotalMilliseconds-s.DurationMs)>1||
            s.Error is not null||s.AutomaticReconnects!=0||s.StrictFault is not null||s.Diagnostics is null||!s.Diagnostics.IsClean||!s.Errors.IsClean||
            s.ConnectionGenerationStart<=0||s.ConnectionGenerationEnd!=s.ConnectionGenerationStart||s.Connections is not{ConnectionAttempts:1,ConnectionSucceeded:1,ConnectionFailed:0,Disconnects:1,AutomaticRetries:0})
            throw new InvalidDataException("Persistence session integrity is invalid.");
        var tracePath=BoundPath(directory,s.TraceFile);var environmentPath=BoundPath(directory,s.EnvironmentFile);
        if(PersistenceBaselineContract.ComputeFileSha256(tracePath)!=s.TraceSha256||PersistenceBaselineContract.ComputeFileSha256(environmentPath)!=s.EnvironmentSha256)throw new InvalidDataException("Persistence session evidence hash mismatch.");
        var trace=AtomicJsonFile.Read<ModbusOperationTrace[]>(tracePath);if(PersistenceTraceStatistics.FromTrace(trace)!=s.Requests||PersistenceTraceErrors.FromTrace(trace)!=s.Errors||trace.Any(x=>!x.Succeeded||x.ErrorCategory is not null||x.Error is not null||x.ModbusExceptionCode is not null||x.StartedAtUtc.Offset!=TimeSpan.Zero||x.CompletedAtUtc.Offset!=TimeSpan.Zero||x.CompletedAtUtc<x.StartedAtUtc||x.StartedAtUtc<s.StartedAtUtc||x.CompletedAtUtc>s.CompletedAtUtc))throw new InvalidDataException("Persistence session trace semantics mismatch.");
        EvidenceEnvironmentValidator.Validate(AtomicJsonFile.Read<PreflightEnvironmentEvidence>(environmentPath),workflowId,s.StartedAtUtc,s.CompletedAtUtc,s.DurationMs,clientCommit,toolVersion,toolSha256);
    }
    private static ParsedWrite ParseWrite(ModbusOperationTrace item)
    {
        var bytes=Convert.FromHexString(item.RequestHex);var offset=bytes.Length>7&&bytes[7]==16?7:0;if(bytes.Length<offset+6||bytes[offset]!=16)throw new InvalidDataException("FC16 request payload is malformed.");
        var address=(ushort)(bytes[offset+1]<<8|bytes[offset+2]);var quantity=(ushort)(bytes[offset+3]<<8|bytes[offset+4]);var byteCount=bytes[offset+5];if(quantity!=item.Quantity||address!=item.Address||byteCount!=quantity*2||bytes.Length<offset+6+byteCount)throw new InvalidDataException("FC16 trace metadata conflicts with its request payload.");
        var values=new ushort[quantity];for(var i=0;i<quantity;i++)values[i]=(ushort)(bytes[offset+6+i*2]<<8|bytes[offset+7+i*2]);if(item.MailboxCommandId!=(address==0x0040&&values.Length>1?values[1]:null))throw new InvalidDataException("FC16 command ID conflicts with its request payload.");return new(address,values);
    }
    private sealed record ParsedWrite(ushort Address,ushort[] Values);
    private static string BoundPath(string directory,string file){if(Path.GetFileName(file)!=file)throw new InvalidDataException("Evidence filename is not local.");var path=Path.Combine(directory,file);if(!File.Exists(path))throw new InvalidDataException($"Evidence file is missing: {file}");return path;}
}
