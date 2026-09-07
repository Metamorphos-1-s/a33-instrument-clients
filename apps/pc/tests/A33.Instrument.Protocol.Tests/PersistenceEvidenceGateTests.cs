using A33.Instrument.Core;
using A33.Instrument.HardwareValidation;
using System.Reflection;
using Xunit;

namespace A33.Instrument.Protocol.Tests;

public sealed class PersistenceEvidenceGateTests
{
    private const string ClientCommit = "89189fe037f9a38f9e2803389d7bd5f18e46f4df";

    [Fact] public void TrustedBaselineMatchesHistoricalSegmentedEvidence()
    {
        var baseline = Baseline();
        Assert.Equal(PersistenceBaselineContract.ActiveSha256, baseline.Manifest.ActiveArraySha256);
        Assert.Equal(PersistenceBaselineContract.ActiveSha256, PersistenceBaselineContract.ComputeActiveSha256(baseline.Manifest.ActiveRegisters));
        Assert.True(baseline.Manifest.ProvenBeforeFirstStage2BWrite);
        Assert.All(baseline.Manifest.ExcludedEvidence, item => Assert.Equal("PRESERVED_NON_AUTHORITATIVE_EVIDENCE", item.Classification));
    }

    [Fact] public void BaselineHashDetectsOrderAndSingleRegisterChanges()
    {
        var values = Baseline().Manifest.ActiveRegisters.ToArray();
        (values[0], values[1]) = (values[1], values[0]);
        Assert.NotEqual(PersistenceBaselineContract.ActiveSha256, PersistenceBaselineContract.ComputeActiveSha256(values));
        values = Baseline().Manifest.ActiveRegisters.ToArray(); values[63]++;
        Assert.NotEqual(PersistenceBaselineContract.ActiveSha256, PersistenceBaselineContract.ComputeActiveSha256(values));
    }

    [Fact] public void UnknownBaselineSourceIsRejected()
    {
        Assert.Throws<InvalidDataException>(() => PersistenceBaselineContract.Validate(Baseline().Manifest with { SourceEvidenceFile = "unknown.json" }));
    }

    [Fact] public async Task CompletePreflightEvidenceValidatesAndBinds()
    {
        using var files = new TempDirectory(); var created = await CreateEvidenceAsync(files.Root);
        var binding = ValidateEvidence(files.Root, created, created.Now.AddMinutes(1));
        Assert.Equal(created.WorkflowId, binding.WorkflowId); Assert.Equal(PersistenceBaselineContract.ActiveSha256, PersistenceBaselineContract.ComputeActiveSha256(binding.ActiveConfiguration));
    }

    [Theory] [InlineData("version")] [InlineData("hash")]
    public async Task EvidenceMustMatchCurrentToolBinary(string mismatch)
    {
        using var files = new TempDirectory(); var created = await CreateEvidenceAsync(files.Root);
        Assert.Throws<InvalidDataException>(() => PreflightEvidenceValidator.Validate(files.Root, created.WorkflowId, ClientCommit,
            Baseline(), created.Now.AddMinutes(1), mismatch == "version" ? "different" : created.Summary.ToolAssemblyVersion,
            mismatch == "hash" ? new string('F', 64) : created.Summary.ToolSha256));
    }

    [Fact] public async Task EnvironmentFileCarriesCompleteRunAndBuildIdentity()
    {
        using var files = new TempDirectory(); var created = await CreateEvidenceAsync(files.Root);
        var environment = AtomicJsonFile.Read<PreflightEnvironmentEvidence>(Path.Combine(created.Directory, "environment.json"));
        Assert.Equal(1, environment.SchemaVersion); Assert.Equal(created.WorkflowId, environment.WorkflowId);
        Assert.Equal(created.Summary.StartedAtUtc, environment.StartedAtUtc); Assert.Equal(created.Summary.CompletedAtUtc, environment.CompletedAtUtc);
        Assert.Equal(created.Summary.ClientCommit, environment.ClientCommit); Assert.Equal(created.Summary.ToolAssemblyVersion, environment.ToolAssemblyVersion);
        Assert.Equal(created.Summary.ToolSha256, environment.ToolSha256); Assert.Equal(TimeSpan.Zero, environment.StartedAtUtc.Offset); Assert.Equal(TimeSpan.Zero, environment.CompletedAtUtc.Offset);
    }

    [Theory][InlineData("strict")][InlineData("diagnostics")]
    public async Task DirtyWaitingSessionBlocksRecoveryBeforeNewConnection(string failure)
    {
        using var files=new TempDirectory();var assembly=typeof(PersistenceHardwareRunner).Assembly;var commit=assembly.GetCustomAttributes<AssemblyMetadataAttribute>().Single(x=>x.Key=="GitCommit").Value!;var version=assembly.GetName().Version!.ToString();var toolHash=PersistenceBaselineContract.ComputeFileSha256(assembly.Location);
        var preflight=await CreateEvidenceAsync(files.Root,s=>s with{ToolAssemblyVersion=version,ToolSha256=toolHash},clientCommit:commit);var hardware=new PersistenceTransport(Baseline().Manifest.ActiveRegisters);var calls=0;
        InstrumentMonitoringService Factory(){calls++;return new(new SingleTransportFactory(hardware));}
        Assert.Equal(20,await PersistenceHardwareRunner.RunAsync(AuthorizedArgs(preflight.WorkflowId),Factory,files.Root,()=>preflight.Now.AddSeconds(1)));
        var journalPath=Directory.GetFiles(files.Root,"persistence-journal.json",SearchOption.AllDirectories).Single();var journal=await PersistenceJournalStore.ReadAsync(journalPath);
        var summaryPath=Directory.GetFiles(Path.GetDirectoryName(journalPath)!,"persistence-session-*-summary.json").Single();var summary=AtomicJsonFile.Read<PersistenceSessionSummary>(summaryPath);
        summary=failure=="strict"?summary with{StrictFault=new("Decode",summary.StartedAtUtc,"failed")}:summary with{Diagnostics=summary.Diagnostics with{BadFrames=1}};
        File.Delete(summaryPath);await AtomicJsonFile.WriteAsync(summaryPath,summary);hardware.Reboot();
        await Assert.ThrowsAnyAsync<Exception>(()=>PersistenceHardwareRunner.RunAsync(AuthorizedRecoveryArgs(journal.WorkflowId),Factory,files.Root,()=>preflight.Now.AddSeconds(2)));
        Assert.Equal(1,calls);Assert.Equal(1,hardware.SaveCount);
    }

    [Fact] public async Task WaitingDiskValidationFailureReturnsDoNotReboot()
    {
        using var files=new TempDirectory();var assembly=typeof(PersistenceHardwareRunner).Assembly;var commit=assembly.GetCustomAttributes<AssemblyMetadataAttribute>().Single(x=>x.Key=="GitCommit").Value!;var version=assembly.GetName().Version!.ToString();var toolHash=PersistenceBaselineContract.ComputeFileSha256(assembly.Location);
        var preflight=await CreateEvidenceAsync(files.Root,s=>s with{ToolAssemblyVersion=version,ToolSha256=toolHash},clientCommit:commit);var hardware=new PersistenceTransport(Baseline().Manifest.ActiveRegisters);var output=new StringWriter();var error=new StringWriter();var oldOut=Console.Out;var oldError=Console.Error;
        try{Console.SetOut(output);Console.SetError(error);var exit=await PersistenceHardwareRunner.RunAsync(AuthorizedArgs(preflight.WorkflowId),()=>new(new SingleTransportFactory(hardware)),files.Root,()=>preflight.Now.AddSeconds(1),beforeWaitingValidation:directory=>{var summaryPath=Directory.GetFiles(directory,"persistence-session-*-summary.json").Single();var summary=AtomicJsonFile.Read<PersistenceSessionSummary>(summaryPath);var tracePath=Path.Combine(directory,summary.TraceFile);var trace=AtomicJsonFile.Read<ModbusOperationTrace[]>(tracePath);var save=Array.FindIndex(trace,x=>x.MailboxCommandId==13);trace[save]=WithWord(trace[save],0,99);File.Delete(tracePath);AtomicJsonFile.WriteAsync(tracePath,trace).GetAwaiter().GetResult();summary=summary with{TraceSha256=PersistenceBaselineContract.ComputeFileSha256(tracePath)};File.Delete(summaryPath);AtomicJsonFile.WriteAsync(summaryPath,summary).GetAwaiter().GetResult();});Assert.Equal(24,exit);}finally{Console.SetOut(oldOut);Console.SetError(oldError);}
        Assert.Contains("DO_NOT_REBOOT",error.ToString());Assert.DoesNotContain("MANUAL_REBOOT_REQUIRED",output.ToString());Assert.Equal(1,hardware.SaveCount);
    }

    [Theory][InlineData("missing-a")][InlineData("missing-b")][InlineData("second-save-a")][InlineData("save-final")][InlineData("time-order")]
    public async Task ThreeSessionIntegrityConflictFailsCompleteOffline(string failure)
    {
        using var files=new TempDirectory();var complete=await CreateCompleteEvidenceAsync(files.Root);
        var summaries=Directory.GetFiles(complete.Directory,"persistence-session-*-summary.json").Select(path=>(path,summary:AtomicJsonFile.Read<PersistenceSessionSummary>(path))).ToArray();
        var a=summaries.Single(x=>x.summary.Phase==PersistencePhase.WaitingForFirstReboot.ToString());var b=summaries.Single(x=>x.summary.Phase==PersistencePhase.WaitingForSecondReboot.ToString());var final=summaries.Single(x=>x.summary.Phase=="COMPLETE_STABILITY_PASS");
        if(failure=="missing-a")File.Delete(a.path);else if(failure=="missing-b")File.Delete(b.path);else
        {
            var target=failure=="second-save-a"?a:failure=="save-final"?final:a;var changed=target.summary;
            if(failure=="second-save-a")changed=changed with{Requests=changed.Requests with{Save=2}};
            if(failure=="save-final")changed=changed with{Requests=changed.Requests with{Save=1}};
            if(failure=="time-order")changed=changed with{CompletedAtUtc=b.summary.StartedAtUtc.AddSeconds(1),DurationMs=(b.summary.StartedAtUtc.AddSeconds(1)-changed.StartedAtUtc).TotalMilliseconds};
            File.Delete(target.path);await AtomicJsonFile.WriteAsync(target.path,changed);
        }
        var calls=0;await Assert.ThrowsAnyAsync<Exception>(()=>PersistenceHardwareRunner.RunAsync(AuthorizedRecoveryArgs(complete.WorkflowId),()=>{calls++;return new();},files.Root,()=>complete.Now.AddHours(1)));Assert.Equal(0,calls);
    }

    [Theory]
    [InlineData("save-token")][InlineData("save-address")][InlineData("save-command")][InlineData("save-zero")][InlineData("save-two")]
    [InlineData("staging-address")][InlineData("staging-value")][InlineData("fc03-zero")][InlineData("fc03-failed")][InlineData("unknown-function")]
    [InlineData("trace-non-utc")][InlineData("trace-reverse")][InlineData("trace-outside")][InlineData("success-error")]
    public async Task CycleTraceSemanticConflictFailsCompleteOffline(string failure)
    {
        using var files=new TempDirectory();var complete=await CreateCompleteEvidenceAsync(files.Root);var summaryPath=Directory.GetFiles(complete.Directory,"persistence-session-*-summary.json").Single(path=>AtomicJsonFile.Read<PersistenceSessionSummary>(path).Phase==PersistencePhase.WaitingForFirstReboot.ToString());
        var summary=AtomicJsonFile.Read<PersistenceSessionSummary>(summaryPath);var tracePath=Path.Combine(complete.Directory,summary.TraceFile);var trace=AtomicJsonFile.Read<ModbusOperationTrace[]>(tracePath).ToList();var save=trace.FindIndex(x=>x.MailboxCommandId==13);var staging=trace.FindIndex(x=>x.FunctionCode==16&&x.Address==0x0156);var read=trace.FindIndex(x=>x.FunctionCode==3);
        if(failure=="save-token")trace[save]=WithWord(trace[save],0,99);
        if(failure=="save-address")trace[save]=WithAddress(trace[save],0x0041);
        if(failure=="save-command")trace[save]=WithWord(trace[save],1,12) with{MailboxCommandId=12};
        if(failure=="save-zero")trace.RemoveAt(save);
        if(failure=="save-two")trace.Add(trace[save] with{StartedAtUtc=trace[save].StartedAtUtc.AddMilliseconds(10),CompletedAtUtc=trace[save].CompletedAtUtc.AddMilliseconds(10)});
        if(failure=="staging-address")trace[staging]=WithAddress(trace[staging],0x0157);
        if(failure=="staging-value")trace[staging]=WithWord(trace[staging],0,5);
        if(failure=="fc03-zero")trace.RemoveAll(x=>x.FunctionCode==3);
        if(failure=="fc03-failed")trace[read]=trace[read] with{Succeeded=false,ErrorCategory="Timeout",Error="timeout"};
        if(failure=="unknown-function")trace.Add(trace[read] with{FunctionCode=4});
        if(failure=="trace-non-utc")trace[read]=trace[read] with{StartedAtUtc=trace[read].StartedAtUtc.ToOffset(TimeSpan.FromHours(8))};
        if(failure=="trace-reverse")trace[read]=trace[read] with{CompletedAtUtc=trace[read].StartedAtUtc.AddMilliseconds(-1)};
        if(failure=="trace-outside")trace[read]=trace[read] with{StartedAtUtc=summary.StartedAtUtc.AddSeconds(-1)};
        if(failure=="success-error")trace[read]=trace[read] with{ErrorCategory="BadFrame",Error="stale",ModbusExceptionCode=2};
        File.Delete(tracePath);await AtomicJsonFile.WriteAsync(tracePath,trace);summary=summary with{Requests=PersistenceTraceStatistics.FromTrace(trace),Errors=PersistenceTraceErrors.FromTrace(trace),TraceSha256=PersistenceBaselineContract.ComputeFileSha256(tracePath)};File.Delete(summaryPath);await AtomicJsonFile.WriteAsync(summaryPath,summary);
        var calls=0;await Assert.ThrowsAnyAsync<Exception>(()=>PersistenceHardwareRunner.RunAsync(AuthorizedRecoveryArgs(complete.WorkflowId),()=>{calls++;return new();},files.Root,()=>complete.Now.AddHours(1)));Assert.Equal(0,calls);
    }

    [Theory][InlineData("mailbox-token")][InlineData("journal-token")][InlineData("reserved")][InlineData("missing")][InlineData("damaged")]
    public async Task JournalWaitingBindingConflictFailsCompleteOffline(string failure)
    {
        using var files=new TempDirectory();var complete=await CreateCompleteEvidenceAsync(files.Root);var path=Path.Combine(complete.Directory,"persistence-journal.json");var journal=await PersistenceJournalStore.ReadAsync(path);
        if(failure=="missing"){File.Delete(path);}
        else if(failure=="damaged"){File.Delete(path);File.WriteAllText(path,"{");}
        else
        {
            if(failure=="mailbox-token")journal=journal with{CycleAEvidence=journal.CycleAEvidence with{MailboxTokens=[1,2,3]}};
            if(failure=="journal-token")journal=journal with{SaveTokens=[5,6]};
            if(failure=="reserved")journal=journal with{ReservedSaveCount=1};
            File.Delete(path);await AtomicJsonFile.WriteAsync(path,journal);
        }
        var calls=0;if(failure=="missing")Assert.NotEqual(0,await PersistenceHardwareRunner.RunAsync(AuthorizedRecoveryArgs(complete.WorkflowId),()=>{calls++;return new();},files.Root,()=>complete.Now.AddHours(1)));else await Assert.ThrowsAnyAsync<Exception>(()=>PersistenceHardwareRunner.RunAsync(AuthorizedRecoveryArgs(complete.WorkflowId),()=>{calls++;return new();},files.Root,()=>complete.Now.AddHours(1)));Assert.Equal(0,calls);
    }

    [Theory]
    [InlineData("commit")] [InlineData("start-time")] [InlineData("end-time")] [InlineData("reversed-time")] [InlineData("non-utc")]
    [InlineData("version")] [InlineData("tool-hash")] [InlineData("missing-os")] [InlineData("workflow")]
    public async Task EnvironmentSemanticConflictIsRejected(string failure)
    {
        using var files = new TempDirectory();
        var created = await CreateEvidenceAsync(files.Root, environmentTransform: environment => failure switch
        {
            "commit" => environment with { ClientCommit = new string('A', 40) },
            "start-time" => environment with { StartedAtUtc = environment.StartedAtUtc.AddSeconds(1) },
            "end-time" => environment with { CompletedAtUtc = environment.CompletedAtUtc.AddSeconds(1) },
            "reversed-time" => environment with { CompletedAtUtc = environment.StartedAtUtc.AddSeconds(-1) },
            "non-utc" => environment with { StartedAtUtc = environment.StartedAtUtc.ToOffset(TimeSpan.FromHours(8)) },
            "version" => environment with { ToolAssemblyVersion = "other" },
            "tool-hash" => environment with { ToolSha256 = new string('F', 64) },
            "missing-os" => environment with { OsDescription = "" },
            "workflow" => environment with { WorkflowId = Guid.NewGuid().ToString() },
            _ => environment
        });
        Assert.Throws<InvalidDataException>(() => ValidateEvidence(files.Root, created, created.Now.AddMinutes(1)));
    }

    [Theory]
    [InlineData("fail")] [InlineData("expired")] [InlineData("schema")] [InlineData("client")] [InlineData("firmware")]
    [InlineData("map")] [InlineData("baseline")] [InlineData("writes")] [InlineData("retry")]
    public async Task UnsafePreflightSummaryIsRejected(string failure)
    {
        using var files = new TempDirectory();
        var created = await CreateEvidenceAsync(files.Root, summary => failure switch
        {
            "fail" => summary with { FinalStatus = "FAIL" },
            "schema" => summary with { SchemaVersion = 1 },
            "client" => summary with { ClientCommit = new string('B', 40) },
            "firmware" => summary with { Stm32Commit = new string('C', 40) },
            "map" => summary with { Identity = summary.Identity with { MapVersion = 0x0103 } },
            "baseline" => summary with { BaselineSha256 = new string('D', 64) },
            "writes" => summary with { Requests = summary.Requests with { Fc16 = 1 } },
            "retry" => summary with { Connections = summary.Connections with { AutomaticRetries = 1 } },
            _ => summary
        });
        var now = failure == "expired" ? created.Now.Add(PreflightEvidenceValidator.MaximumEvidenceAge).AddSeconds(1) : created.Now.AddMinutes(1);
        Assert.Throws<InvalidDataException>(() => ValidateEvidence(files.Root, created, now));
    }

    [Fact] public async Task TamperedOrDamagedEvidenceIsRejected()
    {
        using var files = new TempDirectory(); var created = await CreateEvidenceAsync(files.Root);
        await File.AppendAllTextAsync(Path.Combine(created.Directory, "active-snapshot-1.json"), " ");
        Assert.Throws<InvalidDataException>(() => ValidateEvidence(files.Root, created, created.Now));
        await File.WriteAllTextAsync(Path.Combine(created.Directory, "preflight-summary.json"), "{");
        Assert.Throws<InvalidDataException>(() => ValidateEvidence(files.Root, created, created.Now));
    }

    [Fact] public void MissingPreflightEvidenceIsRejected()
    {
        using var files = new TempDirectory();
        Assert.Throws<InvalidDataException>(() => PreflightEvidenceValidator.Validate(files.Root, Guid.NewGuid().ToString(), ClientCommit, Baseline(), DateTimeOffset.UtcNow, "1.0.0", new string('E',64)));
    }

    [Fact] public async Task PersistenceRunnerRejectsMissingEvidenceBeforeConnectionFactory()
    {
        var calls = 0; var id = Guid.NewGuid().ToString();
        var args = AuthorizedArgs(id);
        await Assert.ThrowsAsync<InvalidDataException>(() => PersistenceHardwareRunner.RunAsync(args, () => { calls++; return new InstrumentMonitoringService(); }));
        Assert.Equal(0, calls);
    }

    [Fact] public async Task EvidenceFilesAreAtomicUniqueAndCannotOverwrite()
    {
        using var files = new TempDirectory(); var created = await CreateEvidenceAsync(files.Root);
        Assert.Equal(PreflightEvidenceStore.RequiredFiles.Order(), Directory.GetFiles(created.Directory).Select(Path.GetFileName).Order());
        await Assert.ThrowsAsync<IOException>(() => PreflightEvidenceStore.WriteAsync(created.Directory, created.Summary, Trace(), Report(), EnvironmentEvidence(created.Summary)));
    }

    [Fact] public async Task MissingOrDamagedEnvironmentIsRejected()
    {
        using var missingFiles = new TempDirectory(); var missing = await CreateEvidenceAsync(missingFiles.Root);
        File.Delete(Path.Combine(missing.Directory, "environment.json"));
        Assert.Throws<InvalidDataException>(() => ValidateEvidence(missingFiles.Root, missing, missing.Now));

        using var damagedFiles = new TempDirectory(); var damaged = await CreateEvidenceAsync(damagedFiles.Root);
        var environmentPath = Path.Combine(damaged.Directory, "environment.json"); await File.WriteAllTextAsync(environmentPath, "{");
        var hashes = damaged.Summary.EvidenceFileSha256.ToDictionary(x => x.Key, x => x.Value); hashes["environment.json"] = PersistenceBaselineContract.ComputeFileSha256(environmentPath);
        var summary = damaged.Summary with { EvidenceFileSha256 = hashes };
        await File.WriteAllTextAsync(Path.Combine(damaged.Directory, "preflight-summary.json"), System.Text.Json.JsonSerializer.Serialize(summary));
        Assert.Throws<InvalidDataException>(() => ValidateEvidence(damagedFiles.Root, damaged, damaged.Now));
    }

    [Fact] public async Task SyntacticallyValidEnvironmentWithMissingFieldIsRejectedSemantically()
    {
        using var files = new TempDirectory(); var created = await CreateEvidenceAsync(files.Root);
        var environment = AtomicJsonFile.Read<PreflightEnvironmentEvidence>(Path.Combine(created.Directory, "environment.json"));
        var withoutCommit = new
        {
            environment.SchemaVersion, environment.WorkflowId, environment.StartedAtUtc, environment.CompletedAtUtc,
            environment.ToolAssemblyVersion, environment.ToolSha256, environment.OsDescription,
            environment.FrameworkDescription, environment.ProcessArchitecture, environment.MachineName
        };
        var environmentPath = Path.Combine(created.Directory, "environment.json");
        await File.WriteAllTextAsync(environmentPath, System.Text.Json.JsonSerializer.Serialize(withoutCommit));
        var hashes = created.Summary.EvidenceFileSha256.ToDictionary(x => x.Key, x => x.Value);
        hashes["environment.json"] = PersistenceBaselineContract.ComputeFileSha256(environmentPath);
        var summary = created.Summary with { EvidenceFileSha256 = hashes };
        await File.WriteAllTextAsync(Path.Combine(created.Directory, "preflight-summary.json"), System.Text.Json.JsonSerializer.Serialize(summary));
        Assert.Throws<InvalidDataException>(() => ValidateEvidence(files.Root, created, created.Now));
    }

    [Fact] public async Task EnvironmentConflictBlocksPersistenceRunnerBeforeConnectionFactory()
    {
        using var files = new TempDirectory();
        var assembly = typeof(PersistenceHardwareRunner).Assembly;
        var assemblyCommit = assembly.GetCustomAttributes<AssemblyMetadataAttribute>().Single(x => x.Key == "GitCommit").Value!;
        var toolVersion = assembly.GetName().Version!.ToString(); var toolSha256 = PersistenceBaselineContract.ComputeFileSha256(assembly.Location);
        var created = await CreateEvidenceAsync(files.Root,
            summary => summary with { ToolAssemblyVersion = toolVersion, ToolSha256 = toolSha256 }, clientCommit: assemblyCommit,
            environmentTransform: environment => environment with { ToolAssemblyVersion = "conflict" });
        var calls = 0;
        await Assert.ThrowsAsync<InvalidDataException>(() => PersistenceHardwareRunner.RunAsync(AuthorizedArgs(created.WorkflowId),
            () => { calls++; return new InstrumentMonitoringService(); }, files.Root, () => created.Now.AddMinutes(1)));
        Assert.Equal(0, calls);
    }

    [Fact] public async Task ExpiredEvidenceBlocksPersistenceRunnerBeforeConnectionFactory()
    {
        using var files = new TempDirectory();var assembly=typeof(PersistenceHardwareRunner).Assembly;
        var commit=assembly.GetCustomAttributes<AssemblyMetadataAttribute>().Single(x=>x.Key=="GitCommit").Value!;
        var version=assembly.GetName().Version!.ToString();var hash=PersistenceBaselineContract.ComputeFileSha256(assembly.Location);
        var created=await CreateEvidenceAsync(files.Root,summary=>summary with{ToolAssemblyVersion=version,ToolSha256=hash},clientCommit:commit);
        var calls=0;
        await Assert.ThrowsAsync<InvalidDataException>(()=>PersistenceHardwareRunner.RunAsync(AuthorizedArgs(created.WorkflowId),
            ()=>{calls++;return new InstrumentMonitoringService();},files.Root,()=>created.Now.Add(PreflightEvidenceValidator.MaximumEvidenceAge).AddSeconds(1)));
        Assert.Equal(0,calls);
    }

    [Fact] public async Task CompleteEvidenceAllowsIdempotentReturnWithoutConnectionFactory()
    {
        using var files=new TempDirectory();var complete=await CreateCompleteEvidenceAsync(files.Root);var calls=0;
        var exit=await PersistenceHardwareRunner.RunAsync(AuthorizedRecoveryArgs(complete.WorkflowId),()=>{calls++;return new InstrumentMonitoringService();},files.Root,()=>complete.Now.AddHours(1));
        Assert.Equal(0,exit);Assert.Equal(0,calls);
    }

    [Fact] public async Task FakeRunnerCompletesBothRebootsFinalStabilityAndOfflineReplay()
    {
        using var files=new TempDirectory();var assembly=typeof(PersistenceHardwareRunner).Assembly;
        var commit=assembly.GetCustomAttributes<AssemblyMetadataAttribute>().Single(x=>x.Key=="GitCommit").Value!;
        var version=assembly.GetName().Version!.ToString();var toolHash=PersistenceBaselineContract.ComputeFileSha256(assembly.Location);
        var preflight=await CreateEvidenceAsync(files.Root,s=>s with{ToolAssemblyVersion=version,ToolSha256=toolHash},clientCommit:commit);
        var clock=new RunnerClock(DateTimeOffset.UtcNow);var hardware=new PersistenceTransport(Baseline().Manifest.ActiveRegisters){HoldFirstRealtime=true};
        var factoryCalls=0;InstrumentMonitoringService Factory(){factoryCalls++;return new InstrumentMonitoringService(new SingleTransportFactory(hardware));}
        var policy=new FinalStabilityPolicy(TimeSpan.FromSeconds(600),TimeSpan.FromSeconds(1));

        var firstRun=PersistenceHardwareRunner.RunAsync(AuthorizedArgs(preflight.WorkflowId),Factory,files.Root,()=>preflight.Now.AddSeconds(1),clock,policy);
        await WaitUntilAsync(()=>hardware.RealtimeAttempts>0);Assert.Empty(Directory.GetFiles(files.Root,"persistence-journal.json",SearchOption.AllDirectories));Assert.Equal(0,hardware.SaveCount);
        hardware.ReleaseFirstRealtime();Assert.Equal(20,await firstRun);
        var journalPath=Directory.GetFiles(files.Root,"persistence-journal.json",SearchOption.AllDirectories).Single();
        var workflowId=(await PersistenceJournalStore.ReadAsync(journalPath)).WorkflowId;
        Assert.Equal(1,hardware.SaveCount);hardware.Reboot();clock.SynchronizeForward();
        Assert.Equal(20,await PersistenceHardwareRunner.RunAsync(AuthorizedRecoveryArgs(workflowId),Factory,files.Root,()=>preflight.Now.AddSeconds(1),clock,policy));
        Assert.Equal(2,hardware.SaveCount);hardware.Reboot();clock.SynchronizeForward();
        Assert.Equal(0,await PersistenceHardwareRunner.RunAsync(AuthorizedRecoveryArgs(workflowId),Factory,files.Root,()=>preflight.Now.AddSeconds(1),clock,policy));
        Assert.Equal(3,factoryCalls);Assert.Equal(2,hardware.SaveCount);Assert.Equal((ushort)3,hardware.Active[ConfigurationPersistenceService.BrightnessOffset]);
        Assert.Equal(0,await PersistenceHardwareRunner.RunAsync(AuthorizedRecoveryArgs(workflowId),Factory,files.Root,()=>preflight.Now.AddSeconds(1),clock,policy));
        Assert.Equal(3,factoryCalls);Assert.Equal(2,hardware.SaveCount);
    }

    [Fact] public async Task RunnerFreshSnapshotTimeoutWritesFailureEvidenceWithoutCycleOrWrite()
    {
        using var files=new TempDirectory();var assembly=typeof(PersistenceHardwareRunner).Assembly;
        var commit=assembly.GetCustomAttributes<AssemblyMetadataAttribute>().Single(x=>x.Key=="GitCommit").Value!;
        var version=assembly.GetName().Version!.ToString();var toolHash=PersistenceBaselineContract.ComputeFileSha256(assembly.Location);
        var preflight=await CreateEvidenceAsync(files.Root,s=>s with{ToolAssemblyVersion=version,ToolSha256=toolHash},clientCommit:commit);
        var hardware=new PersistenceTransport(Baseline().Manifest.ActiveRegisters){HoldFirstRealtime=true};var calls=0;
        var exit=await PersistenceHardwareRunner.RunAsync(AuthorizedArgs(preflight.WorkflowId),()=>{calls++;return new InstrumentMonitoringService(new SingleTransportFactory(hardware));},
            files.Root,()=>preflight.Now.AddSeconds(1),readinessTimeout:TimeSpan.FromMilliseconds(20));
        Assert.Equal(22,exit);Assert.Equal(1,calls);Assert.Equal(0,hardware.SaveCount);Assert.Empty(Directory.GetFiles(files.Root,"persistence-journal.json",SearchOption.AllDirectories));
        var summaryPath=Directory.GetFiles(files.Root,"persistence-session-*-summary.json",SearchOption.AllDirectories).Single();var summary=AtomicJsonFile.Read<PersistenceSessionSummary>(summaryPath);
        Assert.Equal("FAILED_OR_UNCERTAIN",summary.Phase);Assert.Equal("SnapshotTimeout",summary.StrictFault?.Category);Assert.Equal(0,summary.Requests.Fc16Attempted);
        Assert.True(File.Exists(Path.Combine(Path.GetDirectoryName(summaryPath)!,summary.TraceFile)));Assert.True(File.Exists(Path.Combine(Path.GetDirectoryName(summaryPath)!,summary.EnvironmentFile)));
    }

    [Theory]
    [InlineData("missing-summary")][InlineData("missing-final")][InlineData("final-damaged")][InlineData("final-tamper")][InlineData("final-semantic")]
    [InlineData("trace-hash")][InlineData("trace-write")][InlineData("phase")][InlineData("environment")]
    [InlineData("diagnostics")][InlineData("strict-latch")][InlineData("generation")]
    [InlineData("environment-schema")][InlineData("environment-empty")][InlineData("environment-utc")][InlineData("environment-time")]
    [InlineData("environment-commit")][InlineData("environment-version")][InlineData("environment-tool-sha")][InlineData("environment-damaged")]
    [InlineData("reconnect")][InlineData("fc03")][InlineData("fc06")][InlineData("staging")][InlineData("mailbox")]
    [InlineData("begin")][InlineData("validate")][InlineData("apply")][InlineData("cancel")][InlineData("save")][InlineData("error")]
    public async Task CompleteEvidenceMissingTamperedOrConflictingIsRejectedOffline(string failure)
    {
        using var files=new TempDirectory();var complete=await CreateCompleteEvidenceAsync(files.Root);var summaryPath=complete.SessionSummaryPath;
        if(failure=="missing-summary")File.Delete(summaryPath);
        else if(failure=="missing-final")File.Delete(Path.Combine(complete.Directory,"final-stability.json"));
        else if(failure=="final-damaged"){File.Delete(Path.Combine(complete.Directory,"final-stability.json"));File.WriteAllText(Path.Combine(complete.Directory,"final-stability.json"),"{");}
        else if(failure=="final-tamper")File.AppendAllText(Path.Combine(complete.Directory,"final-stability.json")," ");
        else if(failure=="final-semantic")
        {
            var finalPath=Path.Combine(complete.Directory,"final-stability.json");var report=AtomicJsonFile.Read<FinalStabilityReport>(finalPath);
            var changed=report.Samples[1].ActiveRegisters.ToArray();changed[0]++;
            report=report with{Samples=[report.Samples[0],report.Samples[1] with{ActiveRegisters=changed}]};File.Delete(finalPath);await AtomicJsonFile.WriteAsync(finalPath,report);
            var summary=AtomicJsonFile.Read<PersistenceSessionSummary>(summaryPath) with{FinalStabilitySha256=PersistenceBaselineContract.ComputeFileSha256(finalPath)};
            File.Delete(summaryPath);await AtomicJsonFile.WriteAsync(summaryPath,summary);
        }
        else if(failure.StartsWith("environment-",StringComparison.Ordinal))
        {
            var summary=AtomicJsonFile.Read<PersistenceSessionSummary>(summaryPath);var environmentPath=Path.Combine(complete.Directory,summary.EnvironmentFile);
            if(failure=="environment-damaged"){File.Delete(environmentPath);File.WriteAllText(environmentPath,"{");}
            else
            {
                var environment=AtomicJsonFile.Read<PreflightEnvironmentEvidence>(environmentPath);
                if(failure=="environment-schema")environment=environment with{SchemaVersion=2};
                if(failure=="environment-empty")environment=environment with{OsDescription=""};
                if(failure=="environment-utc")environment=environment with{StartedAtUtc=environment.StartedAtUtc.ToOffset(TimeSpan.FromHours(8))};
                if(failure=="environment-time")environment=environment with{CompletedAtUtc=environment.CompletedAtUtc.AddSeconds(1)};
                if(failure=="environment-commit")environment=environment with{ClientCommit=new string('A',40)};
                if(failure=="environment-version")environment=environment with{ToolAssemblyVersion="9.9.9.9"};
                if(failure=="environment-tool-sha")environment=environment with{ToolSha256=new string('B',64)};
                File.Delete(environmentPath);await AtomicJsonFile.WriteAsync(environmentPath,environment);
                summary=summary with{EnvironmentSha256=PersistenceBaselineContract.ComputeFileSha256(environmentPath)};
                File.Delete(summaryPath);await AtomicJsonFile.WriteAsync(summaryPath,summary);
            }
        }
        else
        {
            var summary=AtomicJsonFile.Read<PersistenceSessionSummary>(summaryPath);
            if(failure=="trace-hash")summary=summary with{TraceSha256=new string('A',64)};
            if(failure=="trace-write")summary=summary with{Requests=summary.Requests with{Fc16Attempted=1}};
            if(failure=="phase")summary=summary with{Phase="WaitingForSecondReboot"};
            if(failure=="environment")summary=summary with{EnvironmentSha256=new string('B',64)};
            if(failure=="reconnect")summary=summary with{AutomaticReconnects=1};
            if(failure=="fc03")summary=summary with{Requests=summary.Requests with{Fc03Succeeded=0,Fc03Failed=1}};
            if(failure=="fc06")summary=summary with{Requests=summary.Requests with{Fc06Attempted=1}};
            if(failure=="staging")summary=summary with{Requests=summary.Requests with{StagingWrites=1}};
            if(failure=="mailbox")summary=summary with{Requests=summary.Requests with{MailboxWrites=1}};
            if(failure=="begin")summary=summary with{Requests=summary.Requests with{Begin=1}};
            if(failure=="validate")summary=summary with{Requests=summary.Requests with{Validate=1}};
            if(failure=="apply")summary=summary with{Requests=summary.Requests with{Apply=1}};
            if(failure=="cancel")summary=summary with{Requests=summary.Requests with{Cancel=1}};
            if(failure=="save")summary=summary with{Requests=summary.Requests with{Save=1}};
            if(failure=="error")summary=summary with{Errors=summary.Errors with{TransportErrors=1}};
            if(failure=="diagnostics")summary=summary with{Diagnostics=summary.Diagnostics with{BadFrames=1}};
            if(failure=="strict-latch")summary=summary with{StrictFault=new("Decode",summary.StartedAtUtc,"decoder failed")};
            if(failure=="generation")summary=summary with{ConnectionGenerationEnd=summary.ConnectionGenerationStart+1};
            File.Delete(summaryPath);await AtomicJsonFile.WriteAsync(summaryPath,summary);
        }
        var calls=0;
        await Assert.ThrowsAnyAsync<Exception>(()=>PersistenceHardwareRunner.RunAsync(AuthorizedRecoveryArgs(complete.WorkflowId),()=>{calls++;return new InstrumentMonitoringService();},files.Root,()=>complete.Now.AddHours(1)));
        Assert.Equal(0,calls);
    }

    private static async Task<CreatedEvidence> CreateEvidenceAsync(
        string root, Func<PreflightSummary, PreflightSummary>? transform = null,
        Func<PreflightEnvironmentEvidence, PreflightEnvironmentEvidence>? environmentTransform = null,
        string clientCommit = ClientCommit)
    {
        var id = Guid.NewGuid().ToString(); var now = DateTimeOffset.Parse("2026-09-07T00:00:00Z");
        var directory = PersistenceEvidenceDirectory.CreateUnique(root, id, now);
        var report = Report(); var trace = Trace(); var requests = PreflightRequestStatistics.FromTrace(trace);
        var summary = new PreflightSummary(2, id, clientCommit, "1.0.0", new string('E', 64),
            ConfigurationPersistenceService.FixedStm32Commit, Baseline().Manifest.BaselineId,
            Baseline().Manifest.ActiveArraySha256, Baseline().ManifestSha256, now, now.AddSeconds(1), 1000, 50, 1, 2,
            "192.168.1.100:502", 1, report.Identity, new(1, 1, 0, 1, 0), requests,
            report.Errors, report.Gates, "PASS", [], new Dictionary<string, string>());
        summary = transform?.Invoke(summary) ?? summary;
        var environment = environmentTransform?.Invoke(EnvironmentEvidence(summary)) ?? EnvironmentEvidence(summary);
        var stored = await PreflightEvidenceStore.WriteAsync(directory, summary, trace, report, environment);
        return new(id, directory, now.AddSeconds(1), stored);
    }

    private static async Task<CompleteEvidence> CreateCompleteEvidenceAsync(string root)
    {
        var assembly=typeof(PersistenceHardwareRunner).Assembly;var commit=assembly.GetCustomAttributes<AssemblyMetadataAttribute>().Single(x=>x.Key=="GitCommit").Value!;
        var version=assembly.GetName().Version!.ToString();var toolHash=PersistenceBaselineContract.ComputeFileSha256(assembly.Location);
        var preflight=await CreateEvidenceAsync(root,s=>s with{ToolAssemblyVersion=version,ToolSha256=toolHash},clientCommit:commit);
        var workflowId=Guid.NewGuid().ToString();var directory=PersistenceEvidenceDirectory.CreateUnique(root,workflowId,preflight.Now.AddMinutes(1));var journalPath=Path.Combine(directory,"persistence-journal.json");
        var baseline=Baseline();var active3=baseline.Manifest.ActiveRegisters.ToArray();var active4=active3.ToArray();active4[ConfigurationPersistenceService.BrightnessOffset]=4;
        var storeA0=Store() with{ConfigDirty=true,CurrentRevision=11,SavedRevision=10};var storeA1=Store() with{ActiveSlot=2,ActiveSequence=8,CurrentRevision=11,SavedRevision=11};
        var storeB0=storeA1 with{ConfigDirty=true,CurrentRevision=12,SavedRevision=11};var storeB1=storeA1 with{ActiveSlot=1,ActiveSequence=9,CurrentRevision=12,SavedRevision=12};
        var mailbox=new MailboxSnapshot(0,0,0,0,new ushort[12]);var identity=new DeviceIdentity(0x050A,2,0x0104,1);var at=preflight.Now.AddMinutes(2);
        var rebootA=new PersistenceRebootEvidence(at,identity,mailbox,storeA1,active4,PersistenceBaselineContract.ComputeActiveSha256(active4));
        var rebootB=new PersistenceRebootEvidence(at.AddMinutes(1),identity,mailbox,storeB1,active3,baseline.Manifest.ActiveArraySha256);
        var cycleA=new PersistenceCycleEvidence{Cycle=PersistenceCycle.A,ExpectedActiveConfiguration=active4,ActiveBeforeApply=active3,ActiveAfterApply=active4,SaveBefore=storeA0,SaveConfirmed=storeA1,MailboxTokens=[1,2,3,4],SaveToken=4,SaveReservedAtUtc=at.AddSeconds(-2),SaveRequestMayHaveBeenSent=true,RebootEvidence=rebootA};
        var cycleB=new PersistenceCycleEvidence{Cycle=PersistenceCycle.B,ExpectedActiveConfiguration=active3,ActiveBeforeApply=active4,ActiveAfterApply=active3,SaveBefore=storeB0,SaveConfirmed=storeB1,MailboxTokens=[1,2,3,5],SaveToken=5,SaveReservedAtUtc=at.AddSeconds(58),SaveRequestMayHaveBeenSent=true,RebootEvidence=rebootB};
        var journal=new PersistenceJournal{WorkflowId=workflowId,ClientCommit=commit,Stm32Commit=ConfigurationPersistenceService.FixedStm32Commit,BaselineId=baseline.Manifest.BaselineId,BaselineSha256=baseline.Manifest.ActiveArraySha256,BaselineManifestSha256=baseline.ManifestSha256,BoundPreflightWorkflowId=preflight.WorkflowId,PreflightSummarySha256=PersistenceBaselineContract.ComputeFileSha256(Path.Combine(preflight.Directory,"preflight-summary.json")),CreatedAt=at.AddMinutes(-1),UpdatedAt=rebootB.CapturedAtUtc,Phase=PersistencePhase.Complete,Cycle=PersistenceCycle.Complete,AuthorizationStage=PersistenceAuthorizationStage.Complete,OriginalActiveConfiguration=active3,ExpectedActiveConfiguration=active3,ReservedSaveCount=2,SaveTokens=[4,5],LastMailboxToken=0,MailboxTokenBeforeFirstReboot=4,MailboxTokenAfterFirstReboot=0,MailboxTokenBeforeSecondReboot=5,MailboxTokenAfterSecondReboot=0,PollSnapshots=[],WaitingForFirstReboot=false,WaitingForSecondReboot=false,FinalConfigurationMatches64Of64=true,Events=[new(at.AddMinutes(-1),PersistenceEventKind.Information,"start"),new(rebootB.CapturedAtUtc,PersistenceEventKind.Verified,"complete")],CycleAEvidence=cycleA,CycleBEvidence=cycleB};
        await PersistenceJournalStore.WriteAtomicAsync(journalPath,journal);
        var expectation=FinalPersistenceExpectation.FromCycleB(journal);var stabilityStart=rebootB.CapturedAtUtc.AddSeconds(1);
        var samples=Enumerable.Range(0,601).Select(i=>new FinalStabilitySample(stabilityStart.AddSeconds(i),active3.ToArray(),baseline.Manifest.ActiveArraySha256,3,storeB1,mailbox)).ToArray();
        var stability=new FinalStabilityReport(stabilityStart,stabilityStart.AddSeconds(600),600,true,expectation,[],samples);var stabilityPath=Path.Combine(directory,"final-stability.json");await AtomicJsonFile.WriteAsync(stabilityPath,stability);
        await WriteCycleSessionAsync(directory,workflowId,commit,version,toolHash,PersistencePhase.WaitingForFirstReboot,at.AddSeconds(-30),4);
        await WriteCycleSessionAsync(directory,workflowId,commit,version,toolHash,PersistencePhase.WaitingForSecondReboot,at.AddSeconds(10),5);
        var sessionId=Guid.NewGuid().ToString();var traceFile=$"persistence-session-{sessionId}-request-trace.json";var environmentFile=$"persistence-session-{sessionId}-environment.json";var summaryFile=$"persistence-session-{sessionId}-summary.json";
        var trace=new[]{new ModbusOperationTrace(stabilityStart,stabilityStart,3,0,1,null,true,"AA","BB",null,null,null)};var tracePath=Path.Combine(directory,traceFile);await AtomicJsonFile.WriteAsync(tracePath,trace);
        var environment=new PreflightEnvironmentEvidence(1,workflowId,stabilityStart.AddSeconds(-1),stability.CompletedAtUtc.AddSeconds(1),commit,version,toolHash,"test-os",".NET","x64","test");var environmentPath=Path.Combine(directory,environmentFile);await AtomicJsonFile.WriteAsync(environmentPath,environment);
        var stats=PersistenceTraceStatistics.FromTrace(trace);var errors=PersistenceTraceErrors.FromTrace(trace);var diagnostics=new CommunicationDiagnosticsSnapshot(1,1,0,0,0,0,0,0,0,0,0);var sessionSummary=new PersistenceSessionSummary(2,sessionId,workflowId,commit,version,toolHash,environment.StartedAtUtc,environment.CompletedAtUtc,(environment.CompletedAtUtc-environment.StartedAtUtc).TotalMilliseconds,new(1,1,0,1,0),stats,errors,diagnostics,null,0,1,1,"COMPLETE_STABILITY_PASS",null,traceFile,PersistenceBaselineContract.ComputeFileSha256(tracePath),environmentFile,PersistenceBaselineContract.ComputeFileSha256(environmentPath),"final-stability.json",PersistenceBaselineContract.ComputeFileSha256(stabilityPath));var sessionSummaryPath=Path.Combine(directory,summaryFile);await AtomicJsonFile.WriteAsync(sessionSummaryPath,sessionSummary);
        return new(workflowId,directory,preflight.Now,sessionSummaryPath);
    }
    private static async Task WriteCycleSessionAsync(string directory,string workflowId,string commit,string version,string toolHash,PersistencePhase phase,DateTimeOffset started,ushort saveToken)
    {
        var id=Guid.NewGuid().ToString();var traceFile=$"persistence-session-{id}-request-trace.json";var environmentFile=$"persistence-session-{id}-environment.json";
        var brightness=phase==PersistencePhase.WaitingForFirstReboot?(ushort)4:(ushort)3;
        var trace=new[]{new ModbusOperationTrace(default,default,3,0,1,null,true,"0300000001","03020000",null,null,null),Trace16(0x0040,12,9,1),Trace16(0x0156,1,null,brightness),Trace16(0x0040,12,10,2),Trace16(0x0040,12,11,3),Trace16(0x0040,12,13,saveToken)}.Select((x,i)=>x with{StartedAtUtc=started.AddMilliseconds(i),CompletedAtUtc=started.AddMilliseconds(i+1)}).ToArray();
        var tracePath=Path.Combine(directory,traceFile);await AtomicJsonFile.WriteAsync(tracePath,trace);var completed=started.AddSeconds(1);
        var environment=new PreflightEnvironmentEvidence(1,workflowId,started,completed,commit,version,toolHash,"test-os",".NET","x64","test");var environmentPath=Path.Combine(directory,environmentFile);await AtomicJsonFile.WriteAsync(environmentPath,environment);
        var summary=new PersistenceSessionSummary(2,id,workflowId,commit,version,toolHash,started,completed,1000,new(1,1,0,1,0),PersistenceTraceStatistics.FromTrace(trace),PersistenceTraceErrors.FromTrace(trace),new(6,6,0,0,0,0,0,0,0,0,0),null,0,1,1,phase.ToString(),null,traceFile,PersistenceBaselineContract.ComputeFileSha256(tracePath),environmentFile,PersistenceBaselineContract.ComputeFileSha256(environmentPath),null,null);
        await AtomicJsonFile.WriteAsync(Path.Combine(directory,$"persistence-session-{id}-summary.json"),summary);
    }
    private static ModbusOperationTrace Trace16(ushort address,ushort quantity,ushort? command,ushort token){var bytes=new byte[6+quantity*2];bytes[0]=16;bytes[1]=(byte)(address>>8);bytes[2]=(byte)address;bytes[3]=(byte)(quantity>>8);bytes[4]=(byte)quantity;bytes[5]=(byte)(quantity*2);if(quantity>0){bytes[6]=(byte)(token>>8);bytes[7]=(byte)token;}if(command.HasValue){bytes[8]=(byte)(command.Value>>8);bytes[9]=(byte)command.Value;}return new(default,default,16,address,quantity,command,true,Convert.ToHexString(bytes),"BB",null,null,null);}
    private static ModbusOperationTrace WithWord(ModbusOperationTrace item,int index,ushort value){var bytes=Convert.FromHexString(item.RequestHex);var pdu=bytes.Length>7&&bytes[7]==16?7:0;bytes[pdu+6+index*2]=(byte)(value>>8);bytes[pdu+7+index*2]=(byte)value;return item with{RequestHex=Convert.ToHexString(bytes)};}
    private static ModbusOperationTrace WithAddress(ModbusOperationTrace item,ushort address){var bytes=Convert.FromHexString(item.RequestHex);var pdu=bytes.Length>7&&bytes[7]==16?7:0;bytes[pdu+1]=(byte)(address>>8);bytes[pdu+2]=(byte)address;return item with{Address=address,RequestHex=Convert.ToHexString(bytes)};}

    private static StrictPreflightReport Report()
    {
        var baseline = Baseline(); var active = baseline.Manifest.ActiveRegisters.ToArray(); var now = DateTimeOffset.Parse("2026-09-07T00:00:00Z");
        var requests = PreflightRequestStatistics.FromTrace(Trace()); var errors = new PreflightErrorCounters(0, 0, 0, 0, 0, 0, 0, 0);
        return new StrictPreflightReport(true, [], new(0x050A, 2, 0x0104, 1),
            new(now, now, 0, now, now, 0, 1, 2), new ushort[34], active, active.ToArray(), active.ToArray(),
            baseline.Manifest.ActiveArraySha256, baseline.Manifest.ActiveArraySha256, [], new(0, 0, 0, 0, new ushort[12]),
            new ConfigStorePreflightEvidence(A33.Instrument.Protocol.WordOrder.HighWordFirst,[0,1,0,0,10,0,10],[2,1,0,7,0],Store()), baseline.Manifest.BaselineId, baseline.Manifest.ActiveArraySha256, baseline.ManifestSha256,
            requests, errors, RequiredGates());
    }

    private static PreflightRequestTrace[] Trace()
    {
        var plan = new List<(ushort Address, ushort Count, PreflightReadPurpose Purpose)>
        {
            (0x0103,1,PreflightReadPurpose.Identity),(14,2,PreflightReadPurpose.Identity),
            (0x0020,2,PreflightReadPurpose.Freshness),(0x0020,2,PreflightReadPurpose.Freshness),
            (0,16,PreflightReadPurpose.Realtime),(0x10,16,PreflightReadPurpose.Realtime),(0x20,2,PreflightReadPurpose.Realtime)
        };
        for(var pass=0;pass<2;pass++) for(var i=0;i<4;i++) plan.Add(((ushort)(0x0100+i*16),16,PreflightReadPurpose.Active));
        for(var i=0;i<4;i++) plan.Add(((ushort)(0x0140+i*16),16,PreflightReadPurpose.Staging));
        plan.Add((0x004C,12,PreflightReadPurpose.Mailbox)); plan.Add((0x0030,7,PreflightReadPurpose.ConfigStore)); plan.Add((0x01C0,5,PreflightReadPurpose.ConfigStore));
        var at=DateTimeOffset.Parse("2026-09-07T00:00:00Z");
        return plan.Select((x,i)=>new PreflightRequestTrace(i+1,at,at,0,3,x.Address,x.Count,"AA","BB",x.Count,true,null,null,x.Purpose,null)).ToArray();
    }

    private static string[] AuthorizedArgs(string id) => ["persistence-brightness-cycle", "--authorize-stage2b-persistence", "--confirmation",
        "A33_STAGE2B_BRIGHTNESS_3_TO_4_TO_3_TWO_SAVES", "--acknowledge-manual-reboots", "--acknowledge-result-uncertain-lockout", "--preflight-workflow-id", id];
    private static string[] AuthorizedRecoveryArgs(string id)=>["persistence-brightness-cycle","--authorize-stage2b-persistence","--confirmation","A33_STAGE2B_BRIGHTNESS_3_TO_4_TO_3_TWO_SAVES","--acknowledge-manual-reboots","--acknowledge-result-uncertain-lockout","--workflow-id",id];
    private static PreflightEnvironmentEvidence EnvironmentEvidence(PreflightSummary summary) => new(
        1, summary.WorkflowId, summary.StartedAtUtc, summary.CompletedAtUtc, summary.ClientCommit,
        summary.ToolAssemblyVersion, summary.ToolSha256, "test-os", ".NET", "x64", "test-machine");
    private static ValidatedPreflightBinding ValidateEvidence(string root, CreatedEvidence created, DateTimeOffset now) =>
        PreflightEvidenceValidator.Validate(root, created.WorkflowId, ClientCommit, Baseline(), now,
            created.Summary.ToolAssemblyVersion, created.Summary.ToolSha256);
    private static IReadOnlyDictionary<string,bool> RequiredGates() => new Dictionary<string,bool>
    {
        ["identity"]=true,["fresh_sample_sequence"]=true,["active_complete"]=true,["active_stable"]=true,
        ["active_matches_baseline"]=true,["active_hash_matches_baseline"]=true,["brightness"]=true,["staging_complete"]=true,
        ["mailbox_idle"]=true,["config_store_known_consistent_idle"]=true,["config_store_clean"]=true,
        ["request_trace_consistent"]=true,["errors_clean"]=true,["read_only"]=true
    };
    private static ConfigStoreSnapshot Store() => new(0, 0, ConfigStoreState.Idle, ConfigStoreState.Idle, false, 10, 10, 2, 1, 7);
    private static TrustedPersistenceBaseline Baseline() => PersistenceBaselineContract.LoadFromRepository(RepositoryRoot());
    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, PersistenceBaselineContract.RelativeManifestPath))) directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException();
    }
    private sealed record CreatedEvidence(string WorkflowId, string Directory, DateTimeOffset Now, PreflightSummary Summary);
    private sealed record CompleteEvidence(string WorkflowId,string Directory,DateTimeOffset Now,string SessionSummaryPath);
    private static async Task WaitUntilAsync(Func<bool> condition){var deadline=DateTime.UtcNow.AddSeconds(2);while(!condition()&&DateTime.UtcNow<deadline)await Task.Delay(5);Assert.True(condition());}
    private sealed class RunnerClock(DateTimeOffset now):IPersistenceClock
    {
        public DateTimeOffset UtcNow{get;private set;}=now;
        public void SynchronizeForward(){var current=DateTimeOffset.UtcNow;if(current>UtcNow)UtcNow=current;}
        public Task DelayAsync(TimeSpan delay,CancellationToken cancellationToken){cancellationToken.ThrowIfCancellationRequested();UtcNow+=delay;return Task.CompletedTask;}
    }
    private sealed class SingleTransportFactory(IModbusTransport transport):IModbusTransportFactory
    {
        public IModbusTransport Create(MonitoringOptions options)=>transport;
    }
    private sealed class PersistenceTransport(IEnumerable<ushort> baseline):IModbusTransport
    {
        private readonly TaskCompletionSource firstRealtime=new(TaskCreationOptions.RunContinuationsAsynchronously);
        private ushort[] staging=baseline.ToArray();private ushort responseToken;private ushort lastCommand;
        private bool dirty;private uint currentRevision=10;private uint savedRevision=10;private ushort activeSlot=1;private uint activeSequence=7;
        public ushort[] Active{get;private set;}=baseline.ToArray();public int SaveCount{get;private set;}public int RealtimeAttempts{get;private set;}public bool HoldFirstRealtime{get;init;}public bool IsOpen{get;private set;}public string Endpoint=>"memory://persistence";
        public Task OpenAsync(CancellationToken cancellationToken){cancellationToken.ThrowIfCancellationRequested();IsOpen=true;return Task.CompletedTask;}
        public Task CloseAsync(){IsOpen=false;return Task.CompletedTask;}
        public ValueTask DisposeAsync(){IsOpen=false;return ValueTask.CompletedTask;}
        public void Reboot(){responseToken=0;lastCommand=0;staging=Active.ToArray();}
        public void ReleaseFirstRealtime()=>firstRealtime.TrySetResult();
        public async Task<ModbusExchangeResult> ExchangeAsync(byte unitId,ReadOnlyMemory<byte> pdu,CancellationToken cancellationToken)
        {
            await Task.Yield();cancellationToken.ThrowIfCancellationRequested();var request=pdu.ToArray();
            if(request[0]==3)
            {
                var address=(ushort)(request[1]<<8|request[2]);var count=(ushort)(request[3]<<8|request[4]);
                if(address==0&&++RealtimeAttempts==1&&HoldFirstRealtime)await firstRealtime.Task.WaitAsync(cancellationToken);
                var response=ReadResponse(Read(address,count));return new ModbusExchangeResult(response,request,response);
            }
            if(request[0]==16)
            {
                var address=(ushort)(request[1]<<8|request[2]);var quantity=(ushort)(request[3]<<8|request[4]);
                var values=new ushort[quantity];for(var i=0;i<quantity;i++)values[i]=(ushort)(request[6+i*2]<<8|request[7+i*2]);
                if(address>=0x0140&&address+quantity<=0x0180)Array.Copy(values,0,staging,address-0x0140,quantity);
                else if(address==0x0040){responseToken=values[0];lastCommand=values[1];Execute(lastCommand);}
                else throw new InvalidOperationException("Unexpected fake write.");
                var response=new[]{(byte)16,request[1],request[2],request[3],request[4]};return new ModbusExchangeResult(response,request,response);
            }
            throw new InvalidOperationException("Unexpected fake function.");
        }
        private void Execute(ushort command)
        {
            if(command==11){Active=staging.ToArray();dirty=true;currentRevision++;}
            else if(command==12)staging=Active.ToArray();
            else if(command==13){SaveCount++;dirty=false;savedRevision=currentRevision;activeSlot=ConfigStoreContract.NextSlot(activeSlot);activeSequence=ConfigStoreContract.NextSequence(activeSequence);}
            else if(command is not (9 or 10))throw new InvalidOperationException("Unexpected fake command.");
        }
        private ushort[] Read(ushort address,ushort count)
        {
            ushort[] source=address switch
            {
                0=>Realtime(),14=>[0x0104,0x050A],0x0030=>Diagnostics(),0x004C=>Mailbox(),
                >=0x0100 and <=0x013F=>Active.Skip(address-0x0100).Take(count).ToArray(),
                >=0x0140 and <=0x017F=>staging.Skip(address-0x0140).Take(count).ToArray(),
                0x01C0=>Storage(),_=>new ushort[count]
            };
            return source.Take(count).Concat(Enumerable.Repeat((ushort)0,Math.Max(0,count-source.Length))).ToArray();
        }
        private ushort[] Realtime(){var values=new ushort[34];values[14]=0x0104;values[15]=0x050A;values[32]=0;values[33]=1;return values;}
        private ushort[] Diagnostics()=>[0,0,dirty?(ushort)1:(ushort)0,(ushort)(currentRevision>>16),(ushort)currentRevision,(ushort)(savedRevision>>16),(ushort)savedRevision];
        private ushort[] Storage()=>[2,activeSlot,(ushort)(activeSequence>>16),(ushort)activeSequence,0];
        private ushort[] Mailbox()=>[responseToken,lastCommand==13?(ushort)1:(ushort)0,0,lastCommand,0,0,0,0,0,0,0,0];
        private static byte[] ReadResponse(ushort[] values){var response=new byte[2+values.Length*2];response[0]=3;response[1]=(byte)(values.Length*2);for(var i=0;i<values.Length;i++){response[2+i*2]=(byte)(values[i]>>8);response[3+i*2]=(byte)values[i];}return response;}
    }
    private sealed class TempDirectory : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "a33-evidence-tests-" + Guid.NewGuid().ToString("N"));
        public TempDirectory() => Directory.CreateDirectory(Root);
        public void Dispose() { if (Directory.Exists(Root)) Directory.Delete(Root, true); }
    }
}
