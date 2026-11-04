using Xunit;

namespace Eve.Host.Tests;

/// <summary>Explicitly requires the test database and a caller-supplied headless simulation binary.</summary>
public sealed class RealAbyssalSimPgFactAttribute : FactAttribute
{
    public RealAbyssalSimPgFactAttribute()
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("EVE_PG_URL")))
            Skip = "Set EVE_PG_URL to the dedicated test database.";
        else if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("EVE_ABYSSAL_SIM_BINARY")) ||
                 string.IsNullOrEmpty(Environment.GetEnvironmentVariable("EVE_ABYSSAL_SIM_SDE")))
            Skip = "Set EVE_ABYSSAL_SIM_BINARY and EVE_ABYSSAL_SIM_SDE for the headless Rust simulation.";
    }
}
