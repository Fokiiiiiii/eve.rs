using Eve.Services;
using Eve.Services.Dogma;

namespace Eve.Services.Space;

/// <summary>
/// The superweapon activations the client sends to <c>superWeaponMgr</c> (its positional control): a point for a titan doomsday, a lance, a cone or the HOG,
/// and a slash for a reaper. The client sends a point, not a target id, and no package takes one yet, so the module's effect is started the way every module
/// activation is (<see cref="SpaceSessions.ActivateAsync"/> with no target): the handler refuses it by name (<c>Effects/SuperWeaponEffects.cs</c>), and the refusal
/// is shown as the same user error as any module refusal (<see cref="ModuleRefusals"/>). Nothing here reports a success.
/// </summary>
public sealed class SuperWeaponService(SpaceSessions spaces)
{
    /// <summary>The effect a slash is: the client names the module and the two points, and the effect of the module is this one.</summary>
    public const string SlashEffect = "doomsdaySlash";

    /// <summary>Activates a point-targeted superweapon effect of a module, named by its effect id in the static data.</summary>
    public ValueTask<bool> ActivatePointTargetedAsync(ClientSession session, long moduleId, long effectId, CancellationToken cancellationToken)
    {
        var name = spaces.EffectNameOf(effectId);
        if (name is null)
        {
            throw ModuleRefusals.Of(ModuleResult.Refusal("UNSUPPORTED_EFFECT"), starting: true);
        }
        return ActivateAsync(session, moduleId, name, cancellationToken);
    }

    /// <summary>Activates the slash (a reaper or Catmageddon) of a module.</summary>
    public ValueTask<bool> ActivateSlashAsync(ClientSession session, long moduleId, CancellationToken cancellationToken) =>
        ActivateAsync(session, moduleId, SlashEffect, cancellationToken);

    private async ValueTask<bool> ActivateAsync(ClientSession session, long moduleId, string effectName, CancellationToken cancellationToken)
    {
        var result = await spaces.ActivateAsync(session, moduleId, effectName, 0, 1, cancellationToken).ConfigureAwait(false);
        if (result is null)
        {
            throw MachoErrors.ThrowUserError("DeniedShipChanged");
        }
        if (!result.Ok)
        {
            throw ModuleRefusals.Of(result, starting: true);
        }
        // every superweapon handler refuses today, so no package can have been built: one that was would have run without the point the client sent
        throw new InvalidOperationException($"superweapon effect {effectName} was started without its point: no superweapon family has a body yet");
    }
}
