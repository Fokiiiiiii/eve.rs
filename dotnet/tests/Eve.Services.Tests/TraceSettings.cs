namespace Eve.Services.Tests;

/// <summary>
/// The tests that set the process-wide RPC trace switch (EVE_TRACE_RPC) and Console.Error share one collection that does not run in parallel,
/// so one test's switch and error writer do not reach another's.
/// </summary>
public static class TraceSettings
{
    public const string Name = "rpc-trace-settings";
}

[CollectionDefinition(TraceSettings.Name, DisableParallelization = true)]
public sealed class TraceSettingsCollection;
