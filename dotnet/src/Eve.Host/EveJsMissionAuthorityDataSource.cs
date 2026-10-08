using System.Text.Json;
using System.Text.Json.Nodes;
using Eve.Services.Core;

namespace Eve.Host;

/// <summary>Reads the same EveJS authority tables directly from its GameStore data directory.</summary>
public sealed class EveJsMissionAuthorityDataSource
{
    private static readonly Lazy<EveJsMissionAuthorityDataSource> Current = new(LoadFromEnvironment);
    private readonly JsonObject? _agentAuthority;
    private readonly JsonObject? _missionAuthority;
    private readonly JsonObject? _productionMissionPolicy;

    private EveJsMissionAuthorityDataSource(
        JsonObject? agentAuthority,
        JsonObject? missionAuthority,
        JsonObject? productionMissionPolicy)
    {
        _agentAuthority = agentAuthority;
        _missionAuthority = missionAuthority;
        _productionMissionPolicy = productionMissionPolicy;
    }

    public static EveJsMissionAuthorityDataSource Shared => Current.Value;

    public IReadOnlyList<long> DisabledMissionIds =>
        AgentMissionStaticAuthority.DisabledMissionIds(_productionMissionPolicy);

    public IReadOnlyList<long> PlausibleMissionIds(long agentId) =>
        AgentMissionStaticAuthority.PlausibleMissionIds(_agentAuthority, _missionAuthority, agentId);

    public JsonObject EpicArcMessageMaps =>
        AgentMissionStaticAuthority.EpicArcMessageMaps(_missionAuthority);

    public static EveJsMissionAuthorityDataSource LoadFromPaths(
        string? dataDirectory,
        string? serverRoot,
        string? productionMissionPolicyPath = null)
    {
        var dataDir = string.IsNullOrWhiteSpace(dataDirectory) ? null : Path.GetFullPath(dataDirectory);
        var normalizedServerRoot = NormalizeServerRoot(serverRoot) ?? FindServerRoot(dataDir);
        var policyPath = productionMissionPolicyPath ?? Environment.GetEnvironmentVariable("EVEJS_PRODUCTION_MISSION_POLICY_PATH");
        if (string.IsNullOrWhiteSpace(policyPath) && normalizedServerRoot is not null)
            policyPath = Path.Combine(normalizedServerRoot, "src", "config", "productionMissionPolicy.json");

        return new EveJsMissionAuthorityDataSource(
            ReadObject(dataDir is null ? null : Path.Combine(dataDir, "agentAuthority", "data.json")),
            ReadObject(dataDir is null ? null : Path.Combine(dataDir, "missionAuthority", "data.json")),
            ReadObject(policyPath));
    }

    private static EveJsMissionAuthorityDataSource LoadFromEnvironment()
    {
        var dataDirectory = Environment.GetEnvironmentVariable("EVEJS_GAMESTORE_DATA_DIR");
        var serverRoot = Environment.GetEnvironmentVariable("EVEJS_SERVER_ROOT");
        if (string.IsNullOrWhiteSpace(dataDirectory))
        {
            var discovered = DiscoverInstalledEveJs();
            dataDirectory = discovered.DataDirectory;
            serverRoot ??= discovered.ServerRoot;
        }
        return LoadFromPaths(dataDirectory, serverRoot);
    }

    private static (string? DataDirectory, string? ServerRoot) DiscoverInstalledEveJs()
    {
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrWhiteSpace(userProfile)) return (null, null);
        var runtimeRoot = Path.Combine(userProfile, "evejs-launcher", "runtime");
        if (!Directory.Exists(runtimeRoot)) return (null, null);

        foreach (var runtime in Directory.EnumerateDirectories(runtimeRoot, "EveJS-v*", SearchOption.TopDirectoryOnly)
                     .OrderByDescending(RuntimeVersion))
        {
            var server = Path.Combine(runtime, "server");
            var candidates = new[]
            {
                Path.Combine(runtime, "_local", "gameStore", "data"),
                Path.Combine(server, "src", "gameStore", "data"),
            };
            var data = candidates.FirstOrDefault(path =>
                File.Exists(Path.Combine(path, "agentAuthority", "data.json")) ||
                File.Exists(Path.Combine(path, "missionAuthority", "data.json")));
            if (data is not null) return (data, server);
        }
        return (null, null);
    }

    private static Version RuntimeVersion(string path)
    {
        var name = Path.GetFileName(path);
        return name.StartsWith("EveJS-v", StringComparison.OrdinalIgnoreCase) &&
               Version.TryParse(name[7..], out var version)
            ? version
            : new Version(0, 0);
    }

    private static string? NormalizeServerRoot(string? candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate)) return null;
        var path = Path.GetFullPath(candidate);
        if (File.Exists(Path.Combine(path, "src", "config", "productionMissionPolicy.json")))
            return path;
        var nested = Path.Combine(path, "server");
        return File.Exists(Path.Combine(nested, "src", "config", "productionMissionPolicy.json"))
            ? nested
            : null;
    }

    private static string? FindServerRoot(string? dataDirectory)
    {
        if (dataDirectory is null) return null;
        for (var current = new DirectoryInfo(dataDirectory); current is not null; current = current.Parent)
        {
            var nested = Path.Combine(current.FullName, "server");
            if (File.Exists(Path.Combine(nested, "src", "config", "productionMissionPolicy.json")))
                return nested;
            if (File.Exists(Path.Combine(current.FullName, "src", "config", "productionMissionPolicy.json")))
                return current.FullName;
        }
        return null;
    }

    private static JsonObject? ReadObject(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return null;
        try
        {
            return JsonNode.Parse(File.ReadAllText(path)) as JsonObject;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }
}
