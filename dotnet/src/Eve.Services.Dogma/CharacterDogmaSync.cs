using System.Collections.Concurrent;
using Eve.Dogma;
using Eve.Marshal;
using Eve.Services.Ships;
using Eve.Services.Skills;
using Eve.Ships;
using Eve.Skills;

namespace Eve.Services.Dogma;

/// <summary>
/// What a client is told about its character's dogma when it changes: the character's attributes and its brain (which <c>GetAllInfo</c> sends
/// too), and, when skills changed, the attributes of the ship that changed with them. The brain carries a version that counts every telling.
/// </summary>
public sealed class CharacterDogmaSync(DogmaStatic statics, CharacterShips ships, Func<long> nowUnixMs) : ICharacterDogmaSync, Eve.Services.ICharacterEntryHook
{
    /// <summary>How many times each character's brain has been told to the client.</summary>
    private readonly ConcurrentDictionary<long, int> _brainVersions = new();

    /// <summary>The service's own way of putting dogma in the shapes the client reads.</summary>
    public DogmaInfo Info { get; } = new(statics);

    /// <summary>The next version of a character's brain.</summary>
    public int NextBrainVersion(long characterId) => _brainVersions.AddOrUpdate(characterId, 1, (_, version) => version + 1);

    /// <summary>The character's attributes as two batches of changes, then the brain: what the client is sent after it asks for the character.</summary>
    public void SendCharacter(ClientSession session, ShipSnapshot snapshot, long fileTime, int version, Brain? brain = null)
    {
        foreach (var events in Info.CharacterAttributeEvents(snapshot, fileTime))
        {
            session.SendNotification("OnModuleAttributeChanges", "clientID", events);
        }
        session.SendNotification("OnServerBrainUpdated", "clientID", Info.BrainUpdate(snapshot, version, brain));
    }

    private const string FittingBootstrapShipId = "loginFittingBootstrapShipId";

    public ValueTask OnCharacterLeavingAsync(ClientSession session, long characterId, CancellationToken cancellationToken)
    {
        session.Set(FittingBootstrapShipId, Eve.Marshal.PyNone.Instance);
        return ValueTask.CompletedTask;
    }

    public ValueTask OnCharacterEnteredAsync(ClientSession session, long characterId, CancellationToken cancellationToken)
    {
        var shipId = session.GetInt("shipid");
        if (shipId is > 0)
            session.Set(FittingBootstrapShipId, new Eve.Marshal.PyLong(shipId.Value));
        else
            session.Set(FittingBootstrapShipId, Eve.Marshal.PyNone.Instance);
        return ValueTask.CompletedTask;
    }

    public async ValueTask OnInventoryRpcAsync(
        ClientSession session, string method, bool stationHangar, bool activeSpaceShipInventoryList, CancellationToken cancellationToken)
    {
        if (method is not ("List" or "GetSelfInvItem" or "GetInventoryFromId") ||
            session.Get(FittingBootstrapShipId) is not Eve.Marshal.PyLong pendingShip ||
            session.CharacterId is not { } characterId)
            return;
        // docked: the fitting bootstrap runs on the hangar list, when the ship's items are listed; in space on the active ship's list
        if (stationHangar ? method != "List" : !activeSpaceShipInventoryList)
            return;

        session.Set(FittingBootstrapShipId, Eve.Marshal.PyNone.Instance);
        if (await ships.LoadAsync(characterId, cancellationToken) is not { } snapshot ||
            snapshot.Ship.Id != pendingShip.Value)
            return;

        var fileTime = Py.FileTimeFromUnixMs(nowUnixMs());
        var changes = Info.ShipAttributes(snapshot)
            .Where(attribute => attribute.Key != DogmaInfo.Quantity)
            .Select(attribute => ShipNotifications.AttributeChange(
                characterId,
                new Eve.Marshal.PyLong(snapshot.Ship.Id),
                attribute.Key,
                fileTime,
                null,
                attribute.Value))
            .Concat(snapshot.Fitted.SelectMany(module =>
                Info.ModuleAttributes(snapshot, module.Id)
                    .Where(attribute => attribute.Key != DogmaInfo.Quantity)
                    .Select(attribute =>
                    ShipNotifications.AttributeChange(
                        characterId,
                        new Eve.Marshal.PyLong(module.Id),
                        attribute.Key,
                        fileTime,
                        null,
                        attribute.Value))))
            .ToArray();
        var onlineModules = snapshot.Fitted
            .Where(module => snapshot.World.Item(module.Id)?.State >= ItemState.Online)
            .ToArray();
        if (changes.Length > 0 || onlineModules.Length > 0)
        {
            session.AfterResponse(() =>
            {
                foreach (var module in onlineModules)
                {
                    var effect = (Eve.Marshal.PyTuple)ShipNotifications.ShipEffect(
                        characterId, snapshot.Ship.Id, module.Id, 16, fileTime, true);
                    session.SendNotification("OnGodmaShipEffect", "clientID", [.. effect.Items.Skip(1)]);
                }
                if (changes.Length > 0)
                    session.SendNotification("OnModuleAttributeChanges", "clientID", new Eve.Marshal.PyList(changes));
            });
        }
    }

    public async Task CharacterChangedAsync(ClientSession session, long characterId, CancellationToken cancellationToken = default)
    {
        if (await ships.LoadAsync(characterId, cancellationToken) is not { } snapshot)
        {
            return;
        }
        SendCharacter(session, snapshot, Py.FileTimeFromUnixMs(nowUnixMs()), NextBrainVersion(characterId));
    }

    public async Task ExpertGrantsChangedAsync(ClientSession session, long characterId, IReadOnlyList<CharacterSkill> trainedSkills, IReadOnlyDictionary<int, int> grantsBefore, CancellationToken cancellationToken = default)
    {
        if (await ships.LoadAsync(characterId, cancellationToken) is not { } snapshot)
        {
            return;
        }
        var fileTime = Py.FileTimeFromUnixMs(nowUnixMs());
        var changes = await ships.SkillChangesAsync(characterId, trainedSkills, grantsBefore, cancellationToken);
        if (changes.Count > 0)
        {
            session.SendNotification(
                "OnModuleAttributeChanges", "clientID",
                new PyList([.. changes.Select(c => ShipNotifications.AttributeChange(characterId, c.ItemId, c.AttributeId, fileTime, c.Old, c.New))]));
        }
        SendCharacter(session, snapshot, fileTime, NextBrainVersion(characterId));
    }

    public async Task ShipAttributesChangedAsync(ClientSession session, long characterId, CancellationToken cancellationToken = default)
    {
        if (await ships.LoadAsync(characterId, cancellationToken) is not { } snapshot) return;
        var fileTime = Py.FileTimeFromUnixMs(nowUnixMs());
        var changes = Info.ShipAttributes(snapshot)
            .Where(attribute => attribute.Key != DogmaInfo.Quantity)
            .Select(attribute => ShipNotifications.AttributeChange(characterId, new Eve.Marshal.PyLong(snapshot.Ship.Id),
                attribute.Key, fileTime, null, attribute.Value))
            .Concat(snapshot.Fitted.SelectMany(module => Info.ModuleAttributes(snapshot, module.Id)
                .Where(attribute => attribute.Key != DogmaInfo.Quantity)
                .Select(attribute => ShipNotifications.AttributeChange(characterId, new Eve.Marshal.PyLong(module.Id),
                    attribute.Key, fileTime, null, attribute.Value))))
            .ToArray();
        if (changes.Length > 0)
            session.SendNotification("OnModuleAttributeChanges", "clientID", new Eve.Marshal.PyList(changes));
    }

    public async Task SkillsChangedAsync(ClientSession session, long characterId, IReadOnlyList<CharacterSkill> before, CancellationToken cancellationToken = default)
    {
        if (await ships.LoadAsync(characterId, cancellationToken) is not { } snapshot)
        {
            return;
        }
        var fileTime = Py.FileTimeFromUnixMs(nowUnixMs());
        var changes = await ships.SkillChangesAsync(characterId, before, cancellationToken: cancellationToken);
        if (changes.Count > 0)
        {
            session.SendNotification(
                "OnModuleAttributeChanges", "clientID",
                new PyList([.. changes.Select(c => ShipNotifications.AttributeChange(characterId, c.ItemId, c.AttributeId, fileTime, c.Old, c.New))]));
        }
        SendCharacter(session, snapshot, fileTime, NextBrainVersion(characterId));
    }
}
