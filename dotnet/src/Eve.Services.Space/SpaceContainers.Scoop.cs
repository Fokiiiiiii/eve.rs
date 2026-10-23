using Eve.Inventory;
using Eve.Macho;
using Eve.Marshal;
using Eve.Space;
using Eve.Services.Ships;
using Eve.Structures;
using Eve.Ships;

namespace Eve.Services.Space;

public sealed partial class SpaceContainers
{
    /// <summary>Handles the V24 <c>ship.Scoop(itemID, password)</c> call for an unanchored structure hull or its fittings container.</summary>
    public async Task ScoopAsync(ClientSession session, long itemId, string? password, CancellationToken cancellationToken)
    {
        // Unanchored structure artifacts are not secure containers; accept the V24 optional password argument without inventing a password rule.
        _ = password;
        if (session.CharacterId is not { } characterId || session.GetInt("shipid") is not { } shipId ||
            !_space.IsInSpace(session) || _space.SolarSystemOf(session) is not { } systemId)
        {
            return;
        }

        var item = await _items.GetAsync(itemId, cancellationToken);
        if (item is null)
        {
            throw MachoErrors.ThrowUserError("FakeItemNotFound");
        }

        await _statics.EnsureTypesAsync([item.TypeId], cancellationToken);
        var type = _statics.Type(item.TypeId)
            ?? throw new InvalidOperationException($"SDE type {item.TypeId} for scoop target {itemId} is missing.");
        var isFittings = StructureUnanchorDrop.IsFittingsContainer(item.TypeId, item.CustomInfo);

        var isHull = StructureUnanchorDrop.IsHullArtifact(item.CustomInfo) && _statics.CategoryName(type.CategoryId) is "Structure" or "Starbase";
        if (!isFittings && !isHull)
        {
            throw MachoErrors.ThrowUserError("CustomNotify", ("notify", new PyStr("Unable to scoop this item.")));
        }

        if (item.LocationId != systemId || item.Flag != CharacterShips.InSpaceFlag)
        {
            throw MachoErrors.ThrowUserError("FakeItemNotFound");
        }

        var spaceItem = await _spaceItems.GetAsync(itemId, cancellationToken);
        if (spaceItem is null || spaceItem.SolarSystemId != systemId || spaceItem.GroupId != type.GroupId)
        {
            throw MachoErrors.ThrowUserError("FakeItemNotFound");
        }
        if (isFittings && !await MayTakeFromAsync(characterId, item, cancellationToken, spaceItem))
        {
            throw MachoErrors.ThrowUserError("SafetyActivated");
        }
        var pilot = await _ships.CharacterAsync(characterId, cancellationToken);
        if (pilot is null || (item.OwnerId != characterId &&
            (item.OwnerId != pilot.CorporationId || spaceItem.CorporationId != pilot.CorporationId)))
        {
            throw MachoErrors.ThrowUserError("FakeItemNotFound");
        }

        CommandReply reply;
        try
        {
            reply = await _sim.CommandAsync((ulong)session.SessionId, SimCommands.Measure, [itemId], cancellationToken);
        }
        catch (SimUnavailableException error)
        {
            _log?.Invoke($"scooping {itemId} for session {session.SessionId}: {error.Message}");
            throw Unavailable();
        }

        if (!reply.Accepted || reply.Distance is not { } distance)
        {
            throw MachoErrors.ThrowUserError("FakeItemNotFound");
        }
        if (distance > OpenRange)
        {
            throw MachoErrors.ThrowUserError("TargetTooFar");
        }

        var outcome = await _ships.ScoopSpaceArtifactAsync(
            characterId, pilot.CorporationId, shipId, systemId, itemId, item.CustomInfo!, _spaceItems, cancellationToken);
        switch (outcome)
        {
            case ShipRefused refused:
                throw ShipErrors.Of(refused, _statics);
            case ShipChanged changed:
                // Persistence and custody have committed. Removing a scene ball is then a one-way presentation update.
                _sim.RemoveWreck(itemId, 0);
                _placed.TryRemove(itemId, out _);
                ShipNotifications.Send(session, characterId, changed, RowOf, Py.FileTimeFromUnixMs(_nowUnixMs()));
                return;
            default:
                throw MachoErrors.ThrowUserError("FakeItemNotFound");
        }
    }
}
