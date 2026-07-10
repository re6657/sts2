namespace LocalCoop.Mod.Runtime;

public sealed record BrokerModeSettings(
    bool Enabled,
    BrokerClientConfig? Config,
    string ClientId,
    string EventLogPath,
    string? FailureReason)
{
    public const string MarkerFileName = "enable-local-broker.txt";
    public const string ConfigDirectoryEnvironmentVariable = "LOCALCOOP_CONFIG_DIR";
    public const string RoleEnvironmentVariable = "TOKENSPIRE2_ROLE";

    public static BrokerModeSettings LoadFromDirectory(string modDirectory)
    {
        return Load(modDirectory, Environment.GetEnvironmentVariable);
    }

    public static BrokerModeSettings Load(
        string modDirectory,
        Func<string, string?> getEnvironmentVariable)
    {
        if (string.IsNullOrWhiteSpace(modDirectory))
        {
            throw new ArgumentException("Mod directory must not be blank.", nameof(modDirectory));
        }

        var configDirectory = ResolveConfigDirectory(modDirectory, getEnvironmentVariable);

        // Resolve the marker file path, preferring a per-instance marker when
        // TOKENSPIRE2_ROLE is set. This prevents the dual-instance race where
        // the client overwrites the host's shared marker file.
        var envRole = getEnvironmentVariable(RoleEnvironmentVariable);
        var markerPath = ResolveMarkerPath(configDirectory, envRole);
        if (markerPath is null || !File.Exists(markerPath))
        {
            return Disabled(modDirectory, failureReason: null);
        }

        try
        {
            var config = BrokerClientConfig.Parse(File.ReadAllText(markerPath));

            // Override role from TOKENSPIRE2_ROLE env var if present.
            // This is the authoritative role source in dual-instance LAN mode.
            if (!string.IsNullOrWhiteSpace(envRole))
            {
                var role = envRole.Equals("client", StringComparison.OrdinalIgnoreCase)
                    ? BrokerClientRole.Client
                    : BrokerClientRole.Host;
                if (role != config.Role)
                {
                    config = config with { Role = role };
                }
            }

            var clientId = $"client-{config.ClientIndex}";
            return new BrokerModeSettings(
                Enabled: true,
                config,
                clientId,
                EventLogPathFor(modDirectory, config),
                FailureReason: null);
        }
        catch (Exception exception) when (exception is FormatException or IOException or UnauthorizedAccessException)
        {
            return Disabled(modDirectory, exception.Message);
        }
    }

    private static string ResolveConfigDirectory(
        string modDirectory,
        Func<string, string?> getEnvironmentVariable)
    {
        var configuredDirectory = getEnvironmentVariable(ConfigDirectoryEnvironmentVariable);
        return string.IsNullOrWhiteSpace(configuredDirectory) ? modDirectory : configuredDirectory;
    }

    /// <summary>
    /// Resolves the marker file path. Prefers a per-instance marker
    /// (<c>enable-local-broker-host.txt</c> or <c>enable-local-broker-client.txt</c>)
    /// so that dual instances don't overwrite each other's config.
    /// Falls back to the shared marker file, then to ANY per-instance marker
    /// found in the directory.
    /// </summary>
    private static string? ResolveMarkerPath(string configDirectory, string? envRole)
    {
        // 1) Per-instance marker based on TOKENSPIRE2_ROLE
        if (!string.IsNullOrWhiteSpace(envRole))
        {
            var role = envRole.Equals("client", StringComparison.OrdinalIgnoreCase)
                ? "client"
                : "host";
            var perInstancePath = Path.Combine(configDirectory, $"enable-local-broker-{role}.txt");
            if (File.Exists(perInstancePath))
            {
                return perInstancePath;
            }
        }

        // 2) Shared marker (legacy / single-instance)
        var sharedPath = Path.Combine(configDirectory, MarkerFileName);
        if (File.Exists(sharedPath))
        {
            return sharedPath;
        }

        // 3) Fallback: scan for ANY per-instance marker in the directory.
        //    This handles the case where the launcher writes per-instance
        //    markers but TOKENSPIRE2_ROLE env var is not inherited.
        try
        {
            var candidates = Directory.GetFiles(configDirectory, "enable-local-broker-*.txt");
            if (candidates.Length > 0)
            {
                return candidates[0];
            }
        }
        catch (IOException)
        {
            // Directory enumeration failed — fall through to disabled.
        }

        return null;
    }

    private static BrokerModeSettings Disabled(string modDirectory, string? failureReason)
    {
        return new BrokerModeSettings(
            Enabled: false,
            Config: null,
            ClientId: "client-0",
            Path.Combine(modDirectory, "localcoop-events.txt"),
            failureReason);
    }

    private static string EventLogPathFor(string modDirectory, BrokerClientConfig config)
    {
        var role = config.Role.ToString().ToLowerInvariant();
        return Path.Combine(modDirectory, $"localcoop-{role}-{config.ClientIndex}-events.txt");
    }
}
