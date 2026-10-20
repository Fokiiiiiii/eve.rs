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
/// </summary>
public sealed class ItemCommands(ItemTypeNames names, CharacterShips ships, DogmaStatic statics, Func<long> nowUnixMs, ISpaceModules? modules = null)
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
    /// and into the station hangar when docked. shortcut: EveJS's per-operation amount limit and its cargo volume check are not applied.
    /// </summary>
    public Task<string> CreateAsync(ClientSession session, string argument, CancellationToken cancellationToken) =>
        GiveCoreAsync(session, argument, cancellationToken, create: true);

    /// <summary>An item a name (or a typeID) and an optional amount name: the type, the amount, or the words of why there is none.</summary>
    private async Task<(ItemTypeEntry? Type, long Amount, string? Error)> ItemAmountAsync(string text, CancellationToken cancellationToken)
    {
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
                    return (null, 0, "Item amount must be a positive whole number.");
                }
                amount = (long)Math.Min(parsed.Value, long.MaxValue);
            }
        }
        if (found.Type is not { } type)
        {
            return (null, 0, (found.Error == "ITEM_NOT_FOUND" ? $"Item not found: {text}.{Suggestions(found.Suggestions)}" : $"Item name is ambiguous: {text}.{Suggestions(found.Suggestions)}").Trim());
        }
        return (type, amount, null);
    }

    private async Task<string> GiveCoreAsync(ClientSession session, string argument, CancellationToken cancellationToken, bool create)
    {
        var command = create ? "/createitem" : "/giveitem";
        var text = argument.Trim();
        if (text.Length == 0)
        {
            return $"Usage: {command} <item name|typeID> [amount]";
        }
        var (type, amount, error) = await ItemAmountAsync(text, cancellationToken);
        if (error is not null)
        {
            return error;
        }
        if (type is null)
        {
            return "Item not found.";
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
    /// <summary>
    /// <c>/online &lt;me|shipID&gt;</c>: every fitted module of the ship that has the online effect is put online, as the client's own online-all does
    /// (the loop of <c>cmd_online</c>). A module that is already online is left as it is; a module that cannot go online says why.
    /// In space the simulation is told (<see cref="ISpaceModules.RefreshShipAsync"/>), as a module switch tells it.
    /// </summary>
    public async Task<string> OnlineAsync(ClientSession session, string argument, CancellationToken cancellationToken)
    {
        if (session.CharacterId is not { } character) return "Select a character before using /online.";
        var target = argument.Trim();
        if (target.Length == 0) return "Usage: /online <me|shipID>";
        if (await ships.LoadAsync(character, cancellationToken) is not { } snapshot) return "Active ship not found for this character.";
        if (!target.Equals("me", StringComparison.OrdinalIgnoreCase) && target != snapshot.Ship.Id.ToString(CultureInfo.InvariantCulture))
            return $"Ship not found: {target}.";

        var candidates = snapshot.Fitted.Where(module => statics.Type(module.TypeId)?.EffectIds.Contains(BuiltInEffects.Online) == true).ToList();
        if (candidates.Count == 0) return "No fitted module of the ship has an online effect.";
        var online = new List<string>();
        var refused = new List<string>();
        foreach (var module in candidates)
        {
            if (snapshot.World.Item(module.Id) is { State: >= ItemState.Online }) continue;
            var name = statics.Type(module.TypeId)?.Name ?? module.TypeId.ToString(CultureInfo.InvariantCulture);
            switch (await ships.SetOnlineAsync(character, snapshot.Ship.Id, module.Id, true, cancellationToken))
            {
                case ShipChanged changed:
                    ShipNotifications.Send(session, character, changed, item => DogmaInfoRow(item), Py.FileTimeFromUnixMs(nowUnixMs()));
                    online.Add(name);
                    break;
                case ShipRefused refusal:
                    refused.Add($"{name}: {refusal.Reason.ToString().ToUpperInvariant()}");
                    break;
            }
        }
        if (online.Count > 0 && modules is not null)
            await modules.RefreshShipAsync(session, cancellationToken);

        var lines = new List<string>();
        if (online.Count > 0) lines.Add($"Online: {string.Join(", ", online)}.");
        if (refused.Count > 0) lines.Add($"Not online: {string.Join("; ", refused)}.");
        return lines.Count == 0 ? "Every fitted module with an online effect is online already." : string.Join(" ", lines);
    }

    private const int ChargeCategory = 8;

    /// <summary>
    /// <c>/load me &lt;item name|typeID&gt; [amount]</c>: a charge is made in the station hangar and loaded into the fitted modules that take it (the client's own
    /// create-then-load); any other item is made as <c>/createitem</c> makes it. A charge is loaded docked only (the reload in space is the simulation's).
    /// </summary>
    public async Task<string> LoadAsync(ClientSession session, string argument, CancellationToken cancellationToken)
    {
        var parts = argument.Trim().Split((char[]?)null, 2, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2 || !parts[0].Equals("me", StringComparison.OrdinalIgnoreCase)) return "Usage: /load me <item name|typeID> [amount]";
        var text = parts[1].Trim();
        if (session.CharacterId is not { } character) return "Select a character before using /load.";
        var (type, amount, error) = await ItemAmountAsync(text, cancellationToken);
        if (error is not null || type is null) return error ?? "Item not found.";
        if (type.CategoryId != ChargeCategory) return await GiveCoreAsync(session, text, cancellationToken, create: true);
        if (session.GetInt("stationid") is null) return "Loading a charge in space is not served yet. Dock to load it.";
        await statics.EnsureTypesAsync([type.TypeId], cancellationToken);
        if (await ships.GrantAsync(character, type.TypeId, amount, singleton: false, cancellationToken) is not { } hangar)
            return "You must be docked before using /load.";
        foreach (var change in hangar)
        {
            ShipNotifications.SendItemChange(session, change, item => DogmaInfoRow(item));
        }
        if (await ships.LoadAsync(character, cancellationToken) is not { } snapshot) return "Active ship not found for this character.";
        var charges = hangar.Where(change => change.After.TypeId == type.TypeId).Select(change => change.After.Id).ToList();
        var modules = snapshot.Fitted.Select(module => module.Id).ToList();
        switch (await ships.LoadAmmoAsync(character, snapshot.Ship.Id, modules, charges, snapshot.Ship.LocationId, cancellationToken))
        {
            case AmmoChanged ammo:
                TellAmmo(session, ammo);
                return $"Loaded {Count(amount)}x {type.Name} into your ship's modules.";
            case ShipRefused refused:
                return $"Not loaded: {refused.Reason.ToString().ToUpperInvariant()}. The {type.Name} stays in your station hangar.";
            default:
                return $"No fitted module takes {type.Name}. It stays in your station hangar.";
        }
    }

    /// <summary>
    /// <c>/unload me &lt;all|module itemID|charge itemID|charge name|typeID&gt;</c>: the rounds the chosen modules hold go to the station hangar. Docked only (the
    /// unload in space is the simulation's).
    /// </summary>
    public async Task<string> UnloadAsync(ClientSession session, string argument, CancellationToken cancellationToken)
    {
        var parts = argument.Trim().Split((char[]?)null, 2, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2 || !parts[0].Equals("me", StringComparison.OrdinalIgnoreCase)) return "Usage: /unload me <all|item name|typeID|itemID>";
        if (session.CharacterId is not { } character) return "Select a character before using /unload.";
        if (session.GetInt("stationid") is null) return "Unloading a charge in space is not served yet. Dock to unload it.";
        if (await ships.LoadAsync(character, cancellationToken) is not { } snapshot) return "Active ship not found for this character.";
        var query = parts[1].Trim();
        var loaded = snapshot.Charges.ToList();
        IEnumerable<Item> chosen = loaded;
        if (!query.Equals("all", StringComparison.OrdinalIgnoreCase))
        {
            if (long.TryParse(query, System.Globalization.NumberStyles.None, CultureInfo.InvariantCulture, out var itemId))
            {
                chosen = loaded.Where(charge => charge.Id == itemId || snapshot.Fitted.Any(module => module.Id == itemId && module.Flag == charge.Flag));
            }
            else
            {
                var (type, _, error) = await ItemAmountAsync(query, cancellationToken);
                if (error is not null || type is null) return error ?? "Item not found.";
                chosen = loaded.Where(charge => charge.TypeId == type.TypeId);
            }
        }
        var chosenFlags = chosen.Select(charge => charge.Flag).ToHashSet();
        var moduleIds = snapshot.Fitted.Where(module => chosenFlags.Contains(module.Flag)).Select(module => module.Id).ToList();
        if (moduleIds.Count == 0) return "Nothing to unload.";
        switch (await ships.UnloadAmmoAsync(character, snapshot.Ship.Id, moduleIds, snapshot.Ship.LocationId, ItemFlags.Hangar, null, cancellationToken))
        {
            case AmmoChanged ammo:
                TellAmmo(session, ammo);
                return $"Unloaded {moduleIds.Count} {(moduleIds.Count == 1 ? "module" : "modules")} to your station hangar.";
            case ShipRefused refused:
                return $"Not unloaded: {refused.Reason.ToString().ToUpperInvariant()}.";
            default:
                return "Nothing to unload.";
        }
    }

    /// <summary>
    /// What a change of ammunition tells a docked pilot: the item changes of the ship's stacks. shortcut: the module's charge notifications the dogma service
    /// sends (<c>OnChargeBeingLoadedToModule</c>, quantity changes) are not repeated here; the pilot sees the charges on the next fitting refresh.
    /// </summary>
    private void TellAmmo(ClientSession session, AmmoChanged ammo)
    {
        foreach (var item in ammo.Events.OfType<ItemChanged>())
        {
            ShipNotifications.SendItemChange(session, item, row => DogmaInfoRow(row));
        }
    }

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
