namespace A33.Instrument.Core;

public sealed record PersistenceHardwareAuthorization(bool Authorized, string? WorkflowId, string? Error);

public static class PersistenceHardwareAuthorizationGate
{
    public const int DeniedExitCode = 13;
    private const string Confirmation = "A33_STAGE2B_BRIGHTNESS_3_TO_4_TO_3_TWO_SAVES";
    private static readonly HashSet<string> AllowedFlags = new(StringComparer.OrdinalIgnoreCase)
    {
        "--authorize-stage2b-persistence", "--confirmation", "--acknowledge-manual-reboots",
        "--acknowledge-result-uncertain-lockout", "--workflow-id"
    };

    public static PersistenceHardwareAuthorization Validate(string[] args)
    {
        var options = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 1; i < args.Length; i++)
        {
            var key = args[i];
            if (!key.StartsWith("--", StringComparison.Ordinal) || !AllowedFlags.Contains(key))
                return new(false, null, $"Unsupported persistence option: {key}");
            if (options.ContainsKey(key)) return new(false, null, $"Duplicate persistence option: {key}");
            if (key is "--authorize-stage2b-persistence" or "--acknowledge-manual-reboots" or "--acknowledge-result-uncertain-lockout")
            {
                options[key] = "true";
                continue;
            }
            if (++i >= args.Length || args[i].StartsWith("--", StringComparison.Ordinal))
                return new(false, null, $"Missing value for {key}.");
            options[key] = args[i];
        }
        if (!options.ContainsKey("--authorize-stage2b-persistence") ||
            !options.ContainsKey("--acknowledge-manual-reboots") ||
            !options.ContainsKey("--acknowledge-result-uncertain-lockout") ||
            options.GetValueOrDefault("--confirmation") != Confirmation)
            return new(false, null, "Complete Stage 2B persistence authorization is required.");
        var workflowId = options.GetValueOrDefault("--workflow-id");
        if (workflowId is not null && !Guid.TryParse(workflowId, out _))
            return new(false, null, "Workflow ID must be a GUID.");
        return new(true, workflowId, null);
    }

    public static async Task<int> ExecuteAfterAuthorizationAsync(string[] args, Func<PersistenceHardwareAuthorization, Task<int>> authorizedAction)
    {
        var authorization = Validate(args);
        if (!authorization.Authorized) return DeniedExitCode;
        return await authorizedAction(authorization);
    }
}
