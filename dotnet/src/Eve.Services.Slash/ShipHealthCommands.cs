using System.Globalization;
using Eve.Dogma;
using Eve.Fitting;
using Eve.Inventory;
using Eve.Marshal;
using Eve.Services.Ships;
using Eve.Ships;

namespace Eve.Services.Slash;

/// <summary>
/// <c>/dmg [light|medium|heavy]</c> hurts the ship in the hangar (and its fitted modules) so that repairs can be tried, and <c>/heal</c> mends the ship, in
/// the hangar or in space. The client is told what the reference tells it: the ship's item and its modules as they are, and the numbers of the modules
/// that changed.
/// </summary>
public sealed class ShipHealthCommands(CharacterShips ships, DogmaStatic statics, Func<long> nowUnixMs, ISpaceHealth? space = null)
{
    private const int HullPoints = 9;
    private const int Damage = 3;
    private const int ShieldCapacity = 263;
    private const int ShieldCharge = 264;
    private const int ArmorHitPoints = 265;
    private const int ArmorDamage = 266;

    /// <summary>The heat of the slots: the values a heal in space sets to nothing.</summary>
    private static readonly int[] HeatAttributes = [1175, 1176, 1177];

    private static string Words(int count, string one, string many) => $"{count} {(count == 1 ? one : many)}";

    public async Task<string> DamageAsync(ClientSession session, string argument, CancellationToken cancellationToken)
    {
        if (session.CharacterId is not { } characterId)
        {
            return "Select a character before using /dmg.";
        }
        var preset = argument.Length == 0 ? HealthPresets.Medium : HealthPresets.Find(argument);
        if (preset is null)
        {
            return "Usage: /dmg [light|medium|heavy]";
        }
        if (session.GetInt("stationid") is null)
        {
            return "Dock first before using /dmg so the repair flow stays easy to test.";
        }
        if (await ships.DamageDockedAsync(characterId, preset, cancellationToken) is not { } changed)
        {
            return "Active docked ship not found.";
        }
        SendItems(session, changed, changed.Modules);
        var modules = changed.Modules.Count > 0 ? $" and {Words(changed.Modules.Count, "fitted module", "fitted modules")}" : "";
        return $"Applied {preset.Name} test damage to your active ship{modules}.";
    }

    public Task<string> HealAsync(ClientSession session, CancellationToken cancellationToken) =>
        HealAsync(session, "", cancellationToken);

    public async Task<string> HealAsync(ClientSession session, string argument, CancellationToken cancellationToken)
    {
        var text = argument.Trim();
        if (text.Length == 0 || text.Equals("me", StringComparison.OrdinalIgnoreCase))
            return await HealShipAsync(session, cancellationToken);

        var parts = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 2 && parts[1] == "0" && long.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var targetId) && targetId > 0)
            return "Targeted destruction via /heal <entityID> 0 is unavailable.";
        if (parts.Length != 1 || !long.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var moduleId) || moduleId <= 0)
            return "Usage: /heal [me|ownedModuleID]";
        if (session.CharacterId is not { } characterId)
            return "Select a character before using /heal.";
        long? activeShipId = null;
        if (session.GetInt("stationid") is null)
        {
            if (session.GetInt("solarsystemid") is null || session.GetInt("shipid") is not { } flyingShipId || flyingShipId <= 0)
                return "Dock before using /heal <moduleID>.";
            activeShipId = flyingShipId;
        }
        if (await ships.HealModuleAsync(characterId, moduleId, activeShipId, cancellationToken) is not { } changed)
            return "Usage: /heal [me|ownedModuleID]";

        var attributes = ModuleDamageAttributes(changed, statics.Type(changed.Module.TypeId));
        if (attributes.Count > 0)
            session.SendNotification("OnModuleAttributeChanges", "clientID", new PyList(attributes));
        return $"Repaired module {moduleId}.";
    }

    private async Task<string> HealShipAsync(ClientSession session, CancellationToken cancellationToken)
    {
        if (session.CharacterId is not { } characterId)
        {
            return "Select a character before using /heal.";
        }
        var inSpace = session.GetInt("stationid") is null && session.GetInt("solarsystemid") is not null;
        if (inSpace && (space is null || !await space.HealAsync(session, cancellationToken)))
        {
            return "Your ship is not loaded in space yet.";
        }
        var snapshot = await ships.LoadAsync(characterId, cancellationToken);
        if (snapshot is null || await ships.HealAsync(characterId, cancellationToken) is not { } changed)
        {
            return "Active ship not found.";
        }
        var fileTime = Py.FileTimeFromUnixMs(nowUnixMs());
        // a module that lost hull is told how much it has now; the reference tells each of them in a notification of its own
        foreach (var module in changed.Modules)
        {
            var hull = snapshot.World.Get(module.Module.Id, HullPoints);
            var (was, now) = (Math.Round(hull * module.Before.Damage, 6), Math.Round(hull * module.After.Damage, 6));
            if (was != now)
            {
                session.SendNotification("OnModuleAttributeChanges", "clientID", new PyList([ShipNotifications.AttributeChange(characterId, module.Module.Id, Damage, fileTime, was, now)]));
            }
        }
        if (inSpace)
        {
            session.SendNotification(
                "OnModuleAttributeChanges", "clientID",
                new PyList([.. HeatAttributes.Select(attribute => ShipNotifications.AttributeChange(characterId, changed.Ship.Id, attribute, fileTime, null, 0))]));
        }
        else
        {
            SendItems(session, changed, changed.Modules);
        }
        return "Restored full shields, armor, hull, capacitor, and fitted modules on your active ship.";
    }

    /// <summary>The V24 native module repair menu reports only health attributes backed by this type's SDE capacities.</summary>
    private IReadOnlyList<PyValue> ModuleDamageAttributes(ModuleConditionChanged changed, TypeDef? type)
    {
        if (type is null) return [];
        var attributes = new List<PyValue>();
        var fileTime = Py.FileTimeFromUnixMs(nowUnixMs());
        Add(ShieldCharge, ShieldCapacity, changed.Before.ShieldCharge, changed.After.ShieldCharge);
        Add(ArmorDamage, ArmorHitPoints, changed.Before.ArmorDamage, changed.After.ArmorDamage);
        Add(Damage, HullPoints, changed.Before.Damage, changed.After.Damage);
        return attributes;

        void Add(int attributeId, int capacityAttributeId, double beforeFraction, double afterFraction)
        {
            var capacity = Math.Max(0, type.Attributes.GetValueOrDefault(capacityAttributeId));
            var before = Math.Round(capacity * beforeFraction, 6);
            var after = Math.Round(capacity * afterFraction, 6);
            if (before != after)
                attributes.Add(ShipNotifications.AttributeChange(
                    changed.Module.OwnerId, changed.Module.Id, attributeId, fileTime, before, after));
        }
    }

    /// <summary>The ship's item and what the client knows of its name, then the items of the modules that changed.</summary>
    private void SendItems(ClientSession session, ConditionChanged changed, IReadOnlyList<ModuleConditionChanged> modules)
    {
        var ship = changed.Ship;
        var type = statics.Type(ship.TypeId);
        session.SendNotification("OnItemChange", "clientID", Row(ship, statics), new PyDict([]), PyNone.Instance);
        session.SendNotification(
            "OnCfgDataChanged", "charid", new PyStr("evelocations"),
            new PyList([InventoryWire.Id(ship.Id), new PyStr(ship.CustomName ?? type?.Name ?? ""), new PyInt(changed.SolarSystemId), new PyInt(0), new PyInt(0), new PyInt(0), PyNone.Instance]));
        foreach (var module in modules)
        {
            session.SendNotification("OnItemChange", "clientID", Row(module.Module, statics), new PyDict([]), PyNone.Instance);
        }
    }

    private static PyValue Row(Item item, DogmaStatic statics)
    {
        var type = statics.Type(item.TypeId);
        return InventoryWire.PackedRow(new ItemRow(item.Id, item.TypeId, item.OwnerId, item.LocationId, item.Flag, item.Singleton ? -1 : item.Quantity, type?.GroupId ?? 0, type?.CategoryId ?? 0));
    }
}
