using Eve.Marshal;
using Eve.Services;
using Eve.Services.Dogma;
using Eve.Services.Space.Effects;

namespace Eve.Services.Space;

/// <summary>
/// The superweapon activations the client sends to <c>superWeaponMgr</c> (its positional control): a point for a titan doomsday, a lance, a cone or the HOG,
/// the point itself. The module's effect is started the way every module activation is (<see cref="SpaceSessions.ActivateAsync"/>, with the aim): its handler
/// builds the package, or refuses it by name (<c>Effects/SuperWeaponEffects.cs</c>, <c>Effects/DoomsdayConeEffect.cs</c>), and a refusal is shown as the same user
/// error as any module refusal (<see cref="ModuleRefusals"/>). A success is reported only once the simulation has accepted the start.
/// </summary>
public sealed class SuperWeaponService(SpaceSessions spaces)
{
    /// <summary>The effect a slash is: the client names the module and the two points, and the effect of the module is this one.</summary>
    public const string SlashEffect = "doomsdaySlash";

    /// <summary>Activates a point-targeted superweapon effect of a module, named by its effect id in the static data, aimed at the client's point.</summary>
    public ValueTask<bool> ActivatePointTargetedAsync(ClientSession session, long moduleId, long effectId, PyValue destination, CancellationToken cancellationToken)
    {
        var name = spaces.EffectNameOf(effectId);
        if (name is null)
        {
            throw ModuleRefusals.Of(ModuleResult.Refusal("UNSUPPORTED_EFFECT"), starting: true);
        }
        if (AimOf(destination) is not { } aim)
        {
            throw ModuleRefusals.Of(ModuleResult.Refusal("TARGET_POINT_REQUIRED"), starting: true);
        }
        return ActivateAsync(session, moduleId, name, aim, cancellationToken);
    }

    /// <summary>
    /// Activates the slash of a module (a reaper, <c>ActivateSlashModule</c>): the client's two points are the arc's start and end, both relative to the ship.
    /// The handler checks the arc against the client's fixed geometry and refuses it by name (<c>SLASH_POINTS_*</c>), or builds the sweep.
    /// </summary>
    public ValueTask<bool> ActivateSlashAsync(ClientSession session, long moduleId, PyValue startPoint, PyValue endPoint, CancellationToken cancellationToken)
    {
        if (AimOf(startPoint) is not { } start || AimOf(endPoint) is not { } end)
        {
            throw ModuleRefusals.Of(ModuleResult.Refusal("SLASH_POINTS_REQUIRED"), starting: true);
        }
        return ActivateAsync(session, moduleId, SlashEffect, start, cancellationToken, end);
    }

    private async ValueTask<bool> ActivateAsync(ClientSession session, long moduleId, string effectName, double[]? aim, CancellationToken cancellationToken, double[]? aimEnd = null)
    {
        var result = await spaces.ActivateAsync(session, moduleId, effectName, 0, 1, cancellationToken, aim, aimEnd).ConfigureAwait(false);
        if (result is null)
        {
            throw MachoErrors.ThrowUserError("DeniedShipChanged");
        }
        if (!result.Ok)
        {
            throw ModuleRefusals.Of(result, starting: true);
        }
        return true;
    }

    /// <summary>The client's point (a tuple of three numbers, from the ship), or null when it is not one.</summary>
    public static double[]? AimOf(PyValue value)
    {
        if (value is not PyTuple { Items.Count: >= 3 } tuple)
        {
            return null;
        }
        var numbers = tuple.Items.Take(3).Select(Number).ToArray();
        return numbers.All(n => n is not null) ? [numbers[0]!.Value, numbers[1]!.Value, numbers[2]!.Value] : null;
    }

    private static double? Number(PyValue value) => value switch
    {
        PyInt number => number.Value,
        PyLong number => number.Value,
        PyReal number => number.Value,
        _ => null,
    };
}
