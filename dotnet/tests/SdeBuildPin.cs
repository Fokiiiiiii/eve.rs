using System.Globalization;
using System.Text.Json;

namespace Eve.Testing;

internal static class SdeBuildPin
{
    private static readonly Lazy<(long Standard, long Missions, long Reference)> Builds = new(ReadBuilds);

    public static long Standard => Builds.Value.Standard;
    public static long Missions => Builds.Value.Missions;
    public static long Reference => Builds.Value.Reference;

    private static (long Standard, long Missions, long Reference) ReadBuilds()
    {
        using var stream = typeof(SdeBuildPin).Assembly.GetManifestResourceStream("Eve.Testing.SdeLock.json")
            ?? throw new InvalidOperationException("The test assembly is missing sde/lock.json.");
        using var lockFile = JsonDocument.Parse(stream);
        return (Read("pinned"), Read("missionPinned"), Read("referencePinned"));

        long Read(string key) => long.Parse(lockFile.RootElement.GetProperty(key).GetString()!, CultureInfo.InvariantCulture);
    }
}
