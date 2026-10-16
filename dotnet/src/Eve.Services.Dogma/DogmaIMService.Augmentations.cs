using Eve.Macho;
using Eve.Marshal;

namespace Eve.Services.Dogma;

public sealed partial class DogmaIMService
{
    /// <summary><c>InjectImplant(itemID)</c>: plugs an implant into the pilot's slot (the client's Plug In).</summary>
    [Method("InjectImplant")]
    public async ValueTask<PyValue> InjectImplant(ServiceCall call)
    {
        await AugmentationsOf(call).InjectImplantAsync(call.Session, CharacterOf(call), IdOf(call.Arg(0)) ?? 0, call.Cancellation);
        return None;
    }

    /// <summary><c>DestroyImplant(itemID)</c>: removes an implant the pilot wears (the client's Unplug, which destroys it).</summary>
    [Method("DestroyImplant")]
    public async ValueTask<PyValue> DestroyImplant(ServiceCall call)
    {
        await AugmentationsOf(call).DestroyImplantAsync(call.Session, CharacterOf(call), IdOf(call.Arg(0)) ?? 0, call.Cancellation);
        return None;
    }

    /// <summary><c>UseBooster(itemID, locationID)</c>: consumes a booster (the client's Consume).</summary>
    [Method("UseBooster")]
    public async ValueTask<PyValue> UseBooster(ServiceCall call)
    {
        var location = IdOf(call.Arg(1)) ?? 0;
        await AugmentationsOf(call).UseBoosterAsync(call.Session, CharacterOf(call), IdOf(call.Arg(0)) ?? 0, location > 0 ? location : null, call.Cancellation);
        return None;
    }

    private PilotAugmentationRpc AugmentationsOf(ServiceCall call) =>
        augmentations ?? throw MachoErrors.ThrowUserError("CustomNotify", ("notify", new PyStr("Implants and boosters are not available on this server.")));

    private static long CharacterOf(ServiceCall call) =>
        call.Session.CharacterId ?? throw MachoErrors.ThrowUserError("CustomNotify", ("notify", new PyStr("No character is selected.")));
}
