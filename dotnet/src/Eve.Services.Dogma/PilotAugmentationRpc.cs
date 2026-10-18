using Eve.Dogma;
using Eve.Macho;
using Eve.Marshal;
using Eve.Services.Ships;
using Eve.Services.Skills;
using Eve.Ships;
using Eve.Skills;

namespace Eve.Services.Dogma;

/// <summary>
/// What the client does with its pilot's implants and boosters: <c>dogmaIM.InjectImplant</c> (plug in), <c>DestroyImplant</c> and <c>UseBooster</c>
/// (consume). Each change is told in the order of the reference (EveJS <c>services/dogma/dogmaService/implantsAndBoosters.js</c>): the inventory
/// rows, the implant or booster list, then the character's attributes and brain, then the skill queue, which was rebased at the old rate.
/// </summary>
public sealed class PilotAugmentationRpc(
    PilotImplants implants,
    Func<long, long, long?, long?, long?, CancellationToken, Task<(IReadOnlyList<ItemChanged> Changes, TrainingUpdate? Training)>> useBooster,
    TrainingAnnouncer announcer,
    DogmaStatic statics)
{
    public async Task InjectImplantAsync(ClientSession session, long characterId, long itemId, CancellationToken cancellationToken)
    {
        var outcome = await implants.InjectAsync(characterId, itemId, session.GetInt("stationid"), session.GetInt("shipid"), cancellationToken);
        Refuse(outcome.Error);
        if (outcome.Before is { } before && outcome.After is { } after)
        {
            ShipNotifications.SendItemChange(session, new ItemChanged(after, before.LocationId, before.Flag, before.Quantity), RowOf);
        }
        TellImplantsChanged(session);
        await announcer.AnnounceLearningChangedAsync(characterId, outcome.Training);
    }

    public async Task DestroyImplantAsync(ClientSession session, long characterId, long itemId, CancellationToken cancellationToken)
    {
        var outcome = await implants.DestroyAsync(characterId, itemId, cancellationToken);
        Refuse(outcome.Error);
        TellImplantsChanged(session);
        await announcer.AnnounceLearningChangedAsync(characterId, outcome.Training);
    }

    public async Task UseBoosterAsync(ClientSession session, long characterId, long itemId, long? locationId, CancellationToken cancellationToken)
    {
        var (changes, training) = await useBooster(characterId, itemId, locationId, session.GetInt("stationid"), session.GetInt("shipid"), cancellationToken);
        foreach (var change in changes)
        {
            ShipNotifications.SendItemChange(session, change, RowOf);
        }
        session.SendNotification("OnServerBoostersChanged", "clientID", new PyList([]));
        await announcer.AnnounceLearningChangedAsync(characterId, training);
    }

    /// <summary>The implant list changed: the client reloads it, and its jump clones (which show the implants of each clone).</summary>
    private static void TellImplantsChanged(ClientSession session)
    {
        session.SendNotification("OnServerImplantsChanged", "clientID", new PyList([]));
        session.SendNotification("OnJumpCloneCacheInvalidated", "clientID", new PyList([]));
    }

    private ItemRow RowOf(Eve.Inventory.Item item) => DogmaInfo.RowOf(item, statics.Type(item.TypeId));

    /// <summary>A refused change is told to the client in its words (EveJS <c>_formatImplantUserError</c>).</summary>
    private static void Refuse(string? error)
    {
        if (error is null)
        {
            return;
        }
        var notify = error switch
        {
            "ITEM_NOT_FOUND" or "ITEM_NOT_OWNED" or "ITEM_LOCATION_MISMATCH" or "IMPLANT_NOT_FOUND" => "That implant is no longer available.",
            "NOT_AN_IMPLANT" => "That item cannot be plugged in as an implant.",
            "IMPLANT_SLOT_OCCUPIED" => "You already have an implant in that slot.",
            "SKILL_REQUIRED" => "You do not have the required skills to plug in that implant.",
            _ => "The implant could not be plugged in.",
        };
        throw MachoErrors.ThrowUserError("CustomNotify", ("notify", new PyStr(notify)));
    }
}
