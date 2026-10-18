using System.Globalization;
using Eve.Dogma;
using Eve.Fitting;
using Eve.Inventory;
using Eve.Services.Ships;
using Eve.Ships;

namespace Eve.Services.Slash;

/// <summary>
/// The game master's item commands of a docked pilot: <c>/giveitem &lt;item name|typeID&gt; [amount]</c> (items into the station hangar), <c>/fit me &lt;module&gt;</c> (a module,
/// made if the cargo hold has none, fitted to the first slot that suits) and <c>/unfit me &lt;module name|typeID|itemID|all&gt;</c> (fitted modules back to the hangar).
/// Their words are the reference's (<c>services/chat/commands/itemSeeding.js</c> and <c>fitModule.js</c>, ADR 0009). The reference also serves a pilot in space; here the
/// pilot must be docked (<c>docs/reference/compat/DEVIATIONS.md</c>).
/// </summary>
public sealed class ItemCommands(ItemTypeNames names, CharacterShips ships, DogmaStatic statics, Func<long> nowUnixMs)
{
    /// <summary>The categories the command makes one item of for each in the amount: ships and blueprints.</summary>
    private static readonly HashSet<int> SingletonCategories = [6, 9];

    private const int ShipCategory = 6;
    private const int ModuleCategory = 7;
    private const int SubsystemCategory = 32;

    private static string Suggestions(IReadOnlyList<string> suggestions) => suggestions.Count == 0 ? "" : $" Suggestions: {string.Join(", ", suggestions)}";

    private static string Count(long number) => number.ToString("N0", CultureInfo.InvariantCulture);

    /// <summary><c>splitTrailingAmount</c>: "Antimatter Charge S 500" is the name "Antimatter Charge S" and the amount 500.</summary>
    private static (string Name, double? Amount) SplitAmount(string text)
    {
        var at = text.LastIndexOfAny([' ', '\t']);
        if (at > 0 && double.TryParse(text[(at + 1)..].Replace(",", ""), NumberStyles.Float, CultureInfo.InvariantCulture, out var amount))
        {
            return (text[..at].Trim(), amount);
        }
        return (text, null);
    }

    public Task<string> GiveAsync(ClientSession session, string argument, CancellationToken cancellationToken) =>
        GiveCoreAsync(session, argument, cancellationToken, create: false);

    /// <summary>
    /// <c>/createitem &lt;item name|typeID&gt; [amount]</c> (EveJS <c>handleCreateItemCommand</c>): the items go into the cargo hold of the pilot's ship when it is in space,
    /// and into the station hangar when docked. shortcut: EveJS's per-operation amount limit and its cargo volume check are not applied.
    /// </summary>
    public Task<string> CreateAsync(ClientSession session, string argument, CancellationToken cancellationToken) =>
        GiveCoreAsync(session, argument, cancellationToken, create: true);

    private async Task<string> GiveCoreAsync(ClientSession session, string argument, CancellationToken cancellationToken, bool create)
    {
        var command = create ? "/createitem" : "/giveitem";
        var text = argument.Trim();
        if (text.Length == 0)
        {
            return $"Usage: {command} <item name|typeID> [amount]";
        }
        var found = await names.FindAsync(text, cancellationToken);
        long amount = 1;
        if (found.Type is null)
        {
            var (name, parsed) = SplitAmount(text);
            if (name.Length > 0 && parsed is not null && await names.FindAsync(name, cancellationToken) is { Type: not null } split)
            {
                found = split;
                if (parsed != Math.Floor(parsed.Value) || parsed < 1)
                {
                    return "Item amount must be a positive whole number.";
                }
                amount = (long)Math.Min(parsed.Value, long.MaxValue);
            }
        }
        if (found.Type is not { } type)
        {
            return (found.Error == "ITEM_NOT_FOUND" ? $"Item not found: {text}.{Suggestions(found.Suggestions)}" : $"Item name is ambiguous: {text}.{Suggestions(found.Suggestions)}").Trim();
        }
        if (amount > 9_007_199_254_740_991)
        {
            return "Item amount is too large. Use a positive whole number up to 9,007,199,254,740,991.";
        }
        if (session.CharacterId is not { } character)
        {
            return $"Select a character before using {command}.";
        }
        await statics.EnsureTypesAsync([type.TypeId], cancellationToken);
        var singleton = SingletonCategories.Contains(type.CategoryId);
        if (create && session.GetInt("stationid") is null)
        {
            if (session.GetInt("shipid") is not { } shipId)
            {
                return $"You must be docked or in space before using {command}.";
            }
            var made = new List<ItemChanged>();
            for (var done = 0L; done < (singleton ? amount : 1); done++)
            {
                if (await ships.CreateInShipCargoAsync(character, shipId, type.TypeId, singleton ? 1 : amount, cancellationToken) is not { } changes)
                {
                    return $"Your current ship could not be found for {command}.";
                }
                made.AddRange(changes);
            }
            foreach (var change in made)
            {
                ShipNotifications.SendItemChange(session, change, item => DogmaInfoRow(item));
            }
            return $"{Count(amount)}x {type.Name}{(type.Published ? "" : " [unpublished]")} was added to your ship's cargo hold.";
        }
        if (session.GetInt("stationid") is null && !create)
        {
            return "You must be docked before using /giveitem.";
        }
        if (await ships.GrantAsync(character, type.TypeId, amount, singleton, cancellationToken) is not { } hangar)
        {
            return create ? "You must be docked or in space before using /createitem." : "You must be docked before using /giveitem.";
        }
        foreach (var change in hangar)
        {
            ShipNotifications.SendItemChange(session, change, item => DogmaInfoRow(item));
        }
        var summary = !singleton ? $"{Count(amount)}x {type.Name}" : $"{type.Name} x{Count(amount)}";
        return $"{summary}{(type.Published ? "" : " [unpublished]")} was added to your station hangar.";
    }

    /// <summary><c>/ship &lt;ship name|typeID&gt;</c> (EveJS <c>handleShipSpawn</c>): one ship hull of the pilot's, made in the station hangar. Only ships are made.</summary>
    public async Task<string> ShipAsync(ClientSession session, string argument, CancellationToken cancellationToken, string command = "ship")
    {
        var text = argument.Trim();
        if (text.Length == 0)
        {
            return "Usage: /ship <ship name|typeID>";
        }
        var found = await names.FindAsync(text, cancellationToken);
        if (found.Type is not { CategoryId: ShipCategory } type)
        {
            return found.Type is null
                ? (found.Error == "ITEM_NOT_FOUND" ? $"Ship not found: {text}.{Suggestions(found.Suggestions)}" : $"Ship name is ambiguous: {text}.{Suggestions(found.Suggestions)}").Trim()
                : $"Ship not found: {text}.";
        }
        if (session.CharacterId is not { } character)
        {
            return "Select a character before spawning ships.";
        }
        if (session.GetInt("stationid") is null)
        {
            return "You must be docked before spawning ships into your hangar.";
        }
        await statics.EnsureTypesAsync([type.TypeId], cancellationToken);
        if (await ships.GrantAsync(character, type.TypeId, 1, singleton: true, cancellationToken) is not { } changes)
        {
            return "You must be docked before spawning ships into your hangar.";
        }
        foreach (var change in changes)
        {
            ShipNotifications.SendItemChange(session, change, item => DogmaInfoRow(item));
        }
        return $"{type.Name}{(type.Published ? "" : " [unpublished]")} was added to your ship hangar. /{command} only spawns the hull for now; board it manually from the hangar.";
    }

    private Eve.Services.ItemRow DogmaInfoRow(Item item)
    {
        var type = statics.Type(item.TypeId);
        return new Eve.Services.ItemRow(item.Id, item.TypeId, item.OwnerId, item.LocationId, item.Flag, item.Singleton ? -1 : item.Quantity, type?.GroupId ?? 0, type?.CategoryId ?? 0);
    }

    private static string FamilyName(SlotFamily family) => family switch
    {
        SlotFamily.High => "high",
        SlotFamily.Medium => "medium",
        SlotFamily.Low => "low",
        SlotFamily.Rig => "rig",
        _ => family.ToString().ToLowerInvariant(),
    };

    /// <summary><c>/fit me &lt;module&gt;</c>.</summary>
    public async Task<string> FitAsync(ClientSession session, string argument, CancellationToken cancellationToken)
    {
        var text = argument.Trim();
        var me = text.Split((char[]?)null, 2, StringSplitOptions.RemoveEmptyEntries);
        if (me.Length < 2 || !me[0].Trim('"', '\'').Equals("me", StringComparison.OrdinalIgnoreCase))
        {
            return "Usage: /fit me <module name|typeID>";
        }
        if (session.CharacterId is not { } character)
        {
            return "Select a character before using /fit me.";
        }
        var query = me[1].Trim().Trim('"', '\'').Trim();
        if (await ships.LoadAsync(character, cancellationToken) is not { } snapshot)
        {
            return "Board a ship before using /fit me.";
        }
        if (session.GetInt("stationid") is null || snapshot.Ship.Flag != ItemFlags.Hangar)
        {
            return "Your active ship must be docked before using /fit me.";
        }
        var found = await names.FindAsync(query, cancellationToken);
        if (found.Type is not { } type)
        {
            return $"Module lookup failed: {query}.{Suggestions(found.Suggestions)}";
        }
        await statics.EnsureTypesAsync([type.TypeId], cancellationToken);
        if (type.CategoryId is not (ModuleCategory or SubsystemCategory) || statics.Type(type.TypeId) is not { } def || Slots.FamilyOfType(def) is not { } family)
        {
            return $"{type.Name} is not a fittable module.";
        }
        if (await ships.GrantAsync(character, type.TypeId, 1, singleton: true, cancellationToken) is not { Count: 1 } granted)
        {
            return "Module fitting failed: NOT_DOCKED.";
        }
        var module = granted[0].After;
        var outcome = await ships.MoveAsync(character, module.Id, snapshot.Ship.LocationId, snapshot.Ship.Id, null, null, cancellationToken);
        switch (outcome)
        {
            case ShipChanged changed:
                ShipNotifications.Send(session, character, changed, item => DogmaInfoRow(item), Py.FileTimeFromUnixMs(nowUnixMs()));
                var flag = changed.Items.Count > 0 ? changed.Items[0].After.Flag : 0;
                // the module is put online when the ship has the power and the processing for it (resolveFitOnlineState); else it stays offline and the reply says why
                var offline = "";
                switch (await ships.SetOnlineAsync(character, snapshot.Ship.Id, module.Id, true, cancellationToken))
                {
                    case ShipChanged online:
                        ShipNotifications.Send(session, character, online, item => DogmaInfoRow(item), Py.FileTimeFromUnixMs(nowUnixMs()));
                        break;
                    case ShipRefused { Reason: ShipRefusal.NotEnoughCpu }:
                        offline = " Fitted offline: NOT_ENOUGH_CPU.";
                        break;
                    case ShipRefused { Reason: ShipRefusal.NotEnoughPower }:
                        offline = " Fitted offline: NOT_ENOUGH_POWER.";
                        break;
                }
                return $"Created and fitted {type.Name} to {snapshot.Ship.CustomName ?? statics.Type(snapshot.Ship.TypeId)?.Name ?? "your ship"} ({FamilyName(family)} slot, flag {flag}).{offline}";
            case ShipRefused { Reason: ShipRefusal.NoSuitableSlot }:
                await ships.DeleteItemAsync(character, module.Id, cancellationToken);
                return $"No free compatible {FamilyName(family)} slot for {type.Name}.";
            case ShipRefused refused:
                await ships.DeleteItemAsync(character, module.Id, cancellationToken);
                return $"Cannot fit {type.Name}: {(refused.Fit?.Error.ToString() ?? refused.Reason.ToString()).ToUpperInvariant()}.";
            default:
                await ships.DeleteItemAsync(character, module.Id, cancellationToken);
                return $"Module fitting failed: NOTHING_HAPPENED.";
        }
    }

    /// <summary><c>/unfit me &lt;module name|typeID|itemID|all&gt;</c>.</summary>
    public async Task<string> UnfitAsync(ClientSession session, string argument, CancellationToken cancellationToken)
    {
        var text = argument.Trim();
        var me = text.Split((char[]?)null, 2, StringSplitOptions.RemoveEmptyEntries);
        if (me.Length < 2 || !me[0].Trim('"', '\'').Equals("me", StringComparison.OrdinalIgnoreCase))
        {
            return "Usage: /unfit me <module name|typeID|itemID|all>";
        }
        if (session.CharacterId is not { } character)
        {
            return "Select a character before using /unfit me.";
        }
        if (await ships.LoadAsync(character, cancellationToken) is not { } snapshot)
        {
            return "Board a ship before using /unfit me.";
        }
        if (session.GetInt("stationid") is null || snapshot.Ship.Flag != ItemFlags.Hangar)
        {
            return "Your active ship must be docked before using /unfit me.";
        }
        var query = me[1].Trim().Trim('"', '\'').Trim();
        List<Item> targets;
        if (query.Equals("all", StringComparison.OrdinalIgnoreCase))
        {
            targets = [.. snapshot.Fitted];
        }
        else if (long.TryParse(query, out var number) && snapshot.Fitted.FirstOrDefault(m => m.Id == number) is { } byItem)
        {
            targets = [byItem];
        }
        else
        {
            var found = await names.FindAsync(query, cancellationToken);
            if (found.Type is not { } type)
            {
                return $"Module lookup failed: {query}.{Suggestions(found.Suggestions)}";
            }
            targets = [.. snapshot.Fitted.Where(m => m.TypeId == type.TypeId).Take(1)];
            if (targets.Count == 0)
            {
                return $"{type.Name} is not fitted to your ship.";
            }
        }
        if (targets.Count == 0)
        {
            return "Nothing is fitted to your ship.";
        }
        var done = new List<string>();
        foreach (var module in targets)
        {
            var outcome = await ships.MoveAsync(character, module.Id, snapshot.Ship.Id, snapshot.Ship.LocationId, null, null, cancellationToken);
            if (outcome is ShipChanged changed)
            {
                ShipNotifications.Send(session, character, changed, item => DogmaInfoRow(item), Py.FileTimeFromUnixMs(nowUnixMs()));
                done.Add($"{statics.Type(module.TypeId)?.Name ?? module.TypeId.ToString(CultureInfo.InvariantCulture)} from slot {module.Flag}");
            }
        }
        return done.Count == 0 ? "Nothing was unfitted." : $"Unfitted {string.Join(", ", done)} to your hangar.";
    }
}
