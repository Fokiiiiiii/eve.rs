namespace Eve.Host;

internal static class HostConfiguration
{
    internal static string Env(string name, string fallback) => Environment.GetEnvironmentVariable(name) is { Length: > 0 } value ? value : fallback;

    internal static bool Flag(string name, bool fallback) => Env(name, fallback ? "1" : "0") is "1" or "true" or "TRUE" or "yes";
}
