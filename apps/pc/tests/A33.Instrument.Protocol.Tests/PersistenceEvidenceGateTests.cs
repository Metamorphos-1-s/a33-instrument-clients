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
        var binding = PreflightEvidenceValidator.Validate(files.Root, created.WorkflowId, ClientCommit, Baseline(), created.Now.AddMinutes(1));
        Assert.Equal(created.WorkflowId, binding.WorkflowId); Assert.Equal(PersistenceBaselineContract.ActiveSha256, PersistenceBaselineContract.ComputeActiveSha256(binding.ActiveConfiguration));
    }

    [Theory] [InlineData("version")] [InlineData("hash")]
    public async Task EvidenceMustMatchCurrentToolBinary(string mismatch)
    {
        using var files = new TempDirectory(); var created = await CreateEvidenceAsync(files.Root);
        Assert.Throws<InvalidDataException>(() => PreflightEvidenceValidator.Validate(files.Root, created.WorkflowId, ClientCommit,
            Baseline(), created.Now.AddMinutes(1), currentToolVersion: mismatch == "version" ? "different" : created.Summary.ToolAssemblyVersion,
            currentToolSha256: mismatch == "hash" ? new string('F', 64) : created.Summary.ToolSha256));
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
        Assert.Throws<InvalidDataException>(() => PreflightEvidenceValidator.Validate(files.Root, created.WorkflowId, ClientCommit, Baseline(), created.Now.AddMinutes(1)));
    }

    [Theory]
    [InlineData("fail")] [InlineData("expired")] [InlineData("client")] [InlineData("firmware")]
    [InlineData("map")] [InlineData("baseline")] [InlineData("writes")] [InlineData("retry")]
    public async Task UnsafePreflightSummaryIsRejected(string failure)
    {
        using var files = new TempDirectory();
        var created = await CreateEvidenceAsync(files.Root, summary => failure switch
        {
            "fail" => summary with { FinalStatus = "FAIL" },
            "client" => summary with { ClientCommit = new string('B', 40) },
            "firmware" => summary with { Stm32Commit = new string('C', 40) },
            "map" => summary with { Identity = summary.Identity with { MapVersion = 0x0103 } },
            "baseline" => summary with { BaselineSha256 = new string('D', 64) },
            "writes" => summary with { Requests = summary.Requests with { Fc16 = 1 } },
            "retry" => summary with { Connections = summary.Connections with { AutomaticRetries = 1 } },
            _ => summary
        });
        var now = failure == "expired" ? created.Now.Add(PreflightEvidenceValidator.MaximumEvidenceAge).AddSeconds(1) : created.Now.AddMinutes(1);
        Assert.Throws<InvalidDataException>(() => PreflightEvidenceValidator.Validate(files.Root, created.WorkflowId, ClientCommit, Baseline(), now));
    }

    [Fact] public async Task TamperedOrDamagedEvidenceIsRejected()
    {
        using var files = new TempDirectory(); var created = await CreateEvidenceAsync(files.Root);
        await File.AppendAllTextAsync(Path.Combine(created.Directory, "active-snapshot-1.json"), " ");
        Assert.Throws<InvalidDataException>(() => PreflightEvidenceValidator.Validate(files.Root, created.WorkflowId, ClientCommit, Baseline(), created.Now));
        await File.WriteAllTextAsync(Path.Combine(created.Directory, "preflight-summary.json"), "{");
        Assert.Throws<InvalidDataException>(() => PreflightEvidenceValidator.Validate(files.Root, created.WorkflowId, ClientCommit, Baseline(), created.Now));
    }

    [Fact] public void MissingPreflightEvidenceIsRejected()
    {
        using var files = new TempDirectory();
        Assert.Throws<InvalidDataException>(() => PreflightEvidenceValidator.Validate(files.Root, Guid.NewGuid().ToString(), ClientCommit, Baseline(), DateTimeOffset.UtcNow));
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
        Assert.Throws<InvalidDataException>(() => PreflightEvidenceValidator.Validate(missingFiles.Root, missing.WorkflowId, ClientCommit, Baseline(), missing.Now));

        using var damagedFiles = new TempDirectory(); var damaged = await CreateEvidenceAsync(damagedFiles.Root);
        var environmentPath = Path.Combine(damaged.Directory, "environment.json"); await File.WriteAllTextAsync(environmentPath, "{");
        var hashes = damaged.Summary.EvidenceFileSha256.ToDictionary(x => x.Key, x => x.Value); hashes["environment.json"] = PersistenceBaselineContract.ComputeFileSha256(environmentPath);
        var summary = damaged.Summary with { EvidenceFileSha256 = hashes };
        await File.WriteAllTextAsync(Path.Combine(damaged.Directory, "preflight-summary.json"), System.Text.Json.JsonSerializer.Serialize(summary));
        Assert.Throws<InvalidDataException>(() => PreflightEvidenceValidator.Validate(damagedFiles.Root, damaged.WorkflowId, ClientCommit, Baseline(), damaged.Now));
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
        Assert.Throws<InvalidDataException>(() => PreflightEvidenceValidator.Validate(files.Root, created.WorkflowId, ClientCommit, Baseline(), created.Now));
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
    private static PreflightEnvironmentEvidence EnvironmentEvidence(PreflightSummary summary) => new(
        1, summary.WorkflowId, summary.StartedAtUtc, summary.CompletedAtUtc, summary.ClientCommit,
        summary.ToolAssemblyVersion, summary.ToolSha256, "test-os", ".NET", "x64", "test-machine");
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
    private sealed class TempDirectory : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "a33-evidence-tests-" + Guid.NewGuid().ToString("N"));
        public TempDirectory() => Directory.CreateDirectory(Root);
        public void Dispose() { if (Directory.Exists(Root)) Directory.Delete(Root, true); }
    }
}
