using System.Reflection;
using A33.Instrument.Core;
using A33.Instrument.Protocol;

namespace A33.Instrument.HardwareValidation;

public sealed record BaselineCaptureSummary(
    int SchemaVersion, string WorkflowId, string ClientCommit, string ToolAssemblyVersion,
    string ToolSha256, DateTimeOffset StartedAtUtc, DateTimeOffset CompletedAtUtc,
    string Endpoint, byte UnitId, DeviceIdentity Identity, string FinalStatus,
    string[] FailureReasons, string PcJsonActiveSha256, string Stm32BinaryActiveSha256,
    PreflightConnectionStatistics Connections, PreflightRequestStatistics Requests,
    PreflightErrorCounters Errors, ConfigStoreSnapshot? ConfigStore,
    IReadOnlyDictionary<string, string> EvidenceFileSha256);

public static class BaselineCaptureRunner
{
    public const string Confirmation = "READ_ONLY_BASELINE_CAPTURE";
    public static readonly string[] RequiredFiles =
    [
        "baseline-capture-summary.json", "request-trace.json", "active-snapshot-1.json",
        "active-snapshot-2.json", "staging-snapshot.json", "mailbox-snapshot.json",
        "config-store-snapshot.json", "environment.json"
    ];

    public static async Task<int> RunAsync(string[] args,
        Func<IPersistenceClock, IStrictPreflightSession>? sessionFactory = null)
    {
        if (!TryParse(args, out var outputRoot))
        {
            Console.Error.WriteLine("BASELINE_CAPTURE_READ_ONLY_CONFIRMATION_REQUIRED");
            return 2;
        }
        var repositoryRoot = RepositoryRoot.Find();
        var root = outputRoot ?? Path.Combine(repositoryRoot, Stage2BDeviceContract.BaselineRoot);
        var workflowId = Guid.NewGuid().ToString("D");
        var directory = PersistenceEvidenceDirectory.CreateUnique(root, workflowId, DateTimeOffset.UtcNow);
        var assembly = typeof(BaselineCaptureRunner).Assembly;
        var commit = ToolBuildIdentity.GetCommit(assembly);
        var toolHash = ToolBuildIdentity.GetToolSha256(assembly);
        var clock = new SystemPersistenceClock();
        var started = clock.UtcNow;
        var access = sessionFactory?.Invoke(clock) ?? new StrictPreflightRunner.TcpPreflightRegisterAccess(
            Stage2BDeviceContract.TcpHost, Stage2BDeviceContract.TcpPort,
            Stage2BDeviceContract.UnitId, TimeSpan.FromSeconds(2), clock);
        ushort[]? active1 = null, active2 = null, staging = null, mailboxRaw = null;
        ConfigStorePreflightEvidence? storeEvidence = null;
        DeviceIdentity identity = new(0, 0, 0, Stage2BDeviceContract.UnitId);
        Exception? failure = null;
        var attempts = 0; var opened = 0; var failed = 0; var disconnects = 0;
        try
        {
            attempts++;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            await access.OpenAsync(timeout.Token); opened++;
            var order = (await access.ReadForPreflightAsync(0x0103, 1, PreflightReadPurpose.Identity))[0];
            access.WordOrder = order switch
            {
                0 => WordOrder.HighWordFirst,
                1 => WordOrder.LowWordFirst,
                _ => throw new InvalidDataException("Unknown word order.")
            };
            var identityRaw = await access.ReadForPreflightAsync(14, 2, PreflightReadPurpose.Identity);
            active1 = await Read64(access, 0x0100, PreflightReadPurpose.Active);
            active2 = await Read64(access, 0x0100, PreflightReadPurpose.Active);
            staging = await Read64(access, 0x0140, PreflightReadPurpose.Staging);
            mailboxRaw = await access.ReadForPreflightAsync(0x004C, 12, PreflightReadPurpose.Mailbox);
            var diagnostics = await access.ReadForPreflightAsync(0x0030, 7, PreflightReadPurpose.ConfigStore);
            var storage = await access.ReadForPreflightAsync(0x01C0, 5, PreflightReadPurpose.ConfigStore);
            var store = ConfigStoreContract.Decode(diagnostics, storage, access.WordOrder);
            storeEvidence = new(access.WordOrder, diagnostics, storage, store);
            identity = new(identityRaw[1], store.SchemaVersion, identityRaw[0], access.UnitId);
        }
        catch (Exception error)
        {
            failure = error;
            if (opened == 0) { failed++; access.RecordConnectionError(error); }
        }
        finally
        {
            if (access.IsOpen)
            {
                try { await access.CloseAsync(); disconnects++; }
                catch (Exception error) { failure ??= error; access.RecordConnectionError(error); }
            }
            await access.DisposeAsync();
        }

        var completed = clock.UtcNow;
        var requests = PreflightRequestStatistics.FromTrace(access.Trace);
        var mailbox = mailboxRaw is { Length: 12 } ? new MailboxSnapshot(
            mailboxRaw[0], mailboxRaw[1], mailboxRaw[2], mailboxRaw[3], mailboxRaw) : null;
        var storeSnapshot = storeEvidence?.Parsed;
        var pcHash = active1 is { Length: 64 } ? PersistenceBaselineContract.ComputeActiveSha256(active1) : "";
        var binaryHash = active1 is { Length: 64 } ? ComputeBinarySha256(active1) : "";
        var failures = new List<string>();
        if (failure is not null) failures.Add($"{failure.GetType().Name}: {failure.Message}");
        if (!Stage2BDeviceContract.Matches(identity)) failures.Add("Device identity mismatch.");
        if (active1 is not { Length: 64 } || active2 is not { Length: 64 } || !active1.SequenceEqual(active2)) failures.Add("Active snapshots are incomplete or differ.");
        if (active1 is { Length: 64 } && active1[22] != Stage2BDeviceContract.OriginalBrightness) failures.Add("Brightness is not 3.");
        if (pcHash != Stage2BDeviceContract.PcJsonActiveSha256 ||
            binaryHash != Stage2BDeviceContract.Stm32BinaryActiveSha256) failures.Add("Active baseline hash mismatch.");
        if (mailbox is null || mailbox.Busy || mailbox.Pending) failures.Add("Mailbox is not idle.");
        if (storeSnapshot is null || !storeSnapshot.StatesKnown || !storeSnapshot.StatesConsistent ||
            storeSnapshot.State != ConfigStoreState.Idle || storeSnapshot.ConfigDirty ||
            storeSnapshot.CurrentRevision != storeSnapshot.SavedRevision ||
            storeSnapshot.ActiveSlot != Stage2BDeviceContract.ExpectedBaselineSlot ||
            storeSnapshot.ActiveSequence != Stage2BDeviceContract.ExpectedBaselineSequence) failures.Add("ConfigStore baseline state mismatch.");
        if (requests.Fc03Failed != 0 || requests.Fc03Attempted != requests.Fc03Succeeded ||
            requests is not { Fc06: 0, Fc16: 0, MailboxWrites: 0, Begin: 0, Validate: 0, Apply: 0, Cancel: 0, Save: 0, Reboots: 0 } ||
            access.Trace.Any(x => x.FunctionCode != 3 || !x.Succeeded) || !access.Errors.IsClean) failures.Add("Trace is not clean FC03-only evidence.");
        if (attempts != 1 || opened != 1 || failed != 0 || disconnects != 1) failures.Add("Connection lifecycle mismatch.");

        var environment = PreflightEnvironment.Capture(assembly, workflowId, commit, toolHash, started, completed);
        var values = new Dictionary<string, object?>
        {
            ["request-trace.json"] = access.Trace,
            ["active-snapshot-1.json"] = active1,
            ["active-snapshot-2.json"] = active2,
            ["staging-snapshot.json"] = staging,
            ["mailbox-snapshot.json"] = mailbox,
            ["config-store-snapshot.json"] = storeEvidence,
            ["environment.json"] = environment
        };
        var hashes = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var item in values)
        {
            var path = Path.Combine(directory, item.Key);
            await AtomicJsonFile.WriteAsync(path, item.Value);
            hashes[item.Key] = PersistenceBaselineContract.ComputeFileSha256(path);
        }
        var summary = new BaselineCaptureSummary(1, workflowId, commit,
            environment.ToolAssemblyVersion, toolHash, started, completed,
            Stage2BDeviceContract.TcpEndpoint, Stage2BDeviceContract.UnitId, identity,
            failures.Count == 0 ? "PASS" : "FAIL", failures.ToArray(), pcHash, binaryHash,
            new(attempts, opened, failed, disconnects, 0), requests, access.Errors,
            storeSnapshot, hashes);
        await AtomicJsonFile.WriteAsync(Path.Combine(directory, "baseline-capture-summary.json"), summary);
        Console.WriteLine($"WORKFLOW_ID={workflowId}");
        Console.WriteLine($"EVIDENCE_DIRECTORY={directory}");
        Console.WriteLine($"PC_JSON_SHA256={pcHash}");
        Console.WriteLine($"STM32_BINARY_SHA256={binaryHash}");
        Console.WriteLine($"FINAL_STATUS={summary.FinalStatus}");
        return failures.Count == 0 ? 0 : 6;
    }

    public static string ComputeBinarySha256(IReadOnlyList<ushort> values)
    {
        if (values.Count != 64) throw new InvalidDataException("Active configuration must contain 64 registers.");
        var bytes = new byte[128];
        for (var i = 0; i < values.Count; i++)
        {
            bytes[i * 2] = (byte)(values[i] >> 8);
            bytes[i * 2 + 1] = (byte)values[i];
        }
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes));
    }

    private static async Task<ushort[]> Read64(ITrackedReadOnlyRegisterAccess access, ushort start, PreflightReadPurpose purpose)
    {
        var values = new ushort[64];
        for (var i = 0; i < 4; i++)
            (await access.ReadForPreflightAsync((ushort)(start + i * 16), 16, purpose)).CopyTo(values, i * 16);
        return values;
    }

    private static bool TryParse(string[] args, out string? outputRoot)
    {
        outputRoot = null;
        var allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "capture-persistence-baseline", "--confirmation", "--output-root" };
        for (var i = 0; i < args.Length; i++)
        {
            if (!allowed.Contains(args[i])) return false;
            if (args[i].Equals("--confirmation", StringComparison.OrdinalIgnoreCase))
            {
                if (++i >= args.Length || args[i] != Confirmation) return false;
            }
            else if (args[i].Equals("--output-root", StringComparison.OrdinalIgnoreCase))
            {
                if (++i >= args.Length || string.IsNullOrWhiteSpace(args[i])) return false;
                outputRoot = args[i];
            }
        }
        return args.Contains("--confirmation", StringComparer.OrdinalIgnoreCase) &&
            args.Contains(Confirmation, StringComparer.Ordinal);
    }
}
