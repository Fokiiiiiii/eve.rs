using Eve.Marshal;
using Eve.Services;

namespace Eve.Services.CharacterSelect.Tests;

/// <summary>The name-validation toggle (per session, by role) and the lapsed-Omega answer of the selection screen.</summary>
public class ValidationAndOmegaTests
{
    /// <summary>The GML role bit (EveJS ROLE_GML); the toggle is open to this role.</summary>
    private const ulong RoleGml = 1UL << 54;

    [PgFact]
    public async Task A_session_without_the_toggle_roles_keeps_name_validation_on()
    {
        await using var h = await Harness.CreateAsync();
        var (session, _) = await h.LoginAsync("plain");

        Assert.Equal(PyBool.True, await h.CallAsync(session, "ToggleValidation"));
        Assert.Equal(PyBool.True, await h.CallAsync(session, "ToggleValidation"));
    }

    [PgFact]
    public async Task A_gml_session_switches_name_validation_off_and_back_on()
    {
        await using var h = await Harness.CreateAsync();
        var (session, _) = await h.LoginAsync("gml", RoleGml);

        Assert.Equal(PyBool.False, await h.CallAsync(session, "ToggleValidation"));
        Assert.Equal(PyBool.True, await h.CallAsync(session, "ToggleValidation"));
    }

    [PgFact]
    public async Task The_omega_lapse_answer_is_false_because_every_account_is_open_ended()
    {
        await using var h = await Harness.CreateAsync();
        var (session, _) = await h.LoginAsync("lapse");

        Assert.Equal(PyBool.False, await h.CallAsync(session, "GetCharOmegaDowngradeStatus"));
    }
}
