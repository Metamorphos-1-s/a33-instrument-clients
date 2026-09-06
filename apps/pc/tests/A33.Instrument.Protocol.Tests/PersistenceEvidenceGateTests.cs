using A33.Instrument.Core;
using A33.Instrument.HardwareValidation;
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
        await Assert.ThrowsAsync<IOException>(() => PreflightEvidenceStore.WriteAsync(created.Directory, created.Summary, Trace(), Report(), EnvironmentEvidence()));
    }

    private static async Task<CreatedEvidence> CreateEvidenceAsync(string root, Func<PreflightSummary, PreflightSummary>? transform = null)
    {
        var id = Guid.NewGuid().ToString(); var now = DateTimeOffset.Parse("2026-09-07T00:00:00Z");
        var directory = PersistenceEvidenceDirectory.CreateUnique(root, id, now);
        var report = Report(); var trace = Trace(); var requests = PreflightRequestStatistics.FromTrace(trace);
        var summary = new PreflightSummary(1, id, ClientCommit, "1.0.0", new string('E', 64),
            ConfigurationPersistenceService.FixedStm32Commit, Baseline().Manifest.BaselineId,
            Baseline().Manifest.ActiveArraySha256, Baseline().ManifestSha256, now, now.AddSeconds(1), 1000, 50, 1, 2,
            "192.168.1.100:502", 1, report.Identity, new(1, 1, 0, 1, 0), requests,
            report.Errors, report.Gates, "PASS", [], new Dictionary<string, string>());
        summary = transform?.Invoke(summary) ?? summary;
        var stored = await PreflightEvidenceStore.WriteAsync(directory, summary, trace, report, EnvironmentEvidence());
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
    private static PreflightEnvironmentEvidence EnvironmentEvidence() => new("test", ".NET", "x64", "test", "1.0.0");
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
