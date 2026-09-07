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
