using System.Text.Json;
using Eve.Inventory;
using Eve.Sde;
using Eve.Universe;
using Eve.Wallet;
using Npgsql;

namespace Eve.Services.Space;

/// <summary>
/// What an undock or a stargate jump costs a pilot with contraband in the ship's cargo (EVE.js contrabandInspectionRuntime, its penalty settled in one transaction): the
/// fine charged to the pilot's wallet up to its balance, and the contraband confiscated (EVE.js moves it to void, so it is removed here).
/// </summary>
public sealed record ContrabandPenalty(decimal Fine, decimal Charged, IReadOnlyList<Item> Confiscated);

/// <summary>
/// Contraband in the ship's cargo at an undock or a stargate jump (EVE.js inspectCharacterContraband). The rules are the SDE contrabandTypes: per type and faction, the
/// confiscation threshold (the system's security must reach it) and the fine factor (fine = quantity x base price x fineByValue).
/// </summary>
public sealed class UndockContraband
{
    /// <summary>The wallet reason of the fine (EVE.js adjustCharacterBalance "Contraband fine").</summary>
    public const string FineReason = "Contraband fine";

    /// <summary>The journal ref type of the fine: the static data names none for it, so the journal keeps 0 (DEVIATIONS, ST-16 C).</summary>
    public const int FineRefType = 0;

    private readonly PgItemStore _items;
    private readonly SolarSystemCatalog _systems;
    private readonly SdeReader _sde;
    private readonly NpgsqlDataSource? _source;
    private readonly WalletTransactions? _wallets;
    private readonly Dictionary<(int TypeId, int FactionId), ContrabandRule> _rules;

    private sealed record ContrabandRule(double ConfiscateMinSecurity, double FineByValue);

    private UndockContraband(
        PgItemStore items,
        SolarSystemCatalog systems,
        SdeReader sde,
        NpgsqlDataSource? source,
        WalletTransactions? wallets,
        Dictionary<(int TypeId, int FactionId), ContrabandRule> rules)
    {
        _items = items;
        _systems = systems;
        _sde = sde;
        _source = source;
        _wallets = wallets;
        _rules = rules;
    }

    /// <summary>Loads the contraband rules. The database and the wallets are needed only for <see cref="ApplyAsync"/>.</summary>
    public static async Task<UndockContraband> LoadAsync(
        SdeReader sde,
        PgItemStore items,
        SolarSystemCatalog systems,
        NpgsqlDataSource? source = null,
        WalletTransactions? wallets = null,
        CancellationToken ct = default)
    {
        var rules = new Dictionary<(int TypeId, int FactionId), ContrabandRule>();
        foreach (var row in await sde.ReadAsync("contrabandTypes", cancellationToken: ct))
        {
            if (row.Key is <= 0 or > int.MaxValue
                || row.Doc.ValueKind != JsonValueKind.Object
                || !row.Doc.TryGetProperty("factions", out var factions)
                || factions.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var faction in factions.EnumerateArray())
            {
                if (faction.ValueKind != JsonValueKind.Object
                    || !faction.TryGetProperty("_key", out var factionIdValue)
                    || factionIdValue.ValueKind != JsonValueKind.Number
                    || !factionIdValue.TryGetInt32(out var factionId)
                    || factionId <= 0
                    || !faction.TryGetProperty("confiscateMinSec", out var minSecurityValue)
                    || minSecurityValue.ValueKind != JsonValueKind.Number
                    || !minSecurityValue.TryGetDouble(out var minSecurity)
                    || !double.IsFinite(minSecurity))
                {
                    continue;
                }

                var fineByValue = faction.TryGetProperty("fineByValue", out var fineValue)
                    && fineValue.ValueKind == JsonValueKind.Number && fineValue.TryGetDouble(out var fine) && double.IsFinite(fine) && fine >= 0
                        ? fine
                        : 0d;
                rules[((int)row.Key, factionId)] = new ContrabandRule(minSecurity, fineByValue);
            }
        }

        return new UndockContraband(items, systems, sde, source, wallets, rules);
    }

    public async Task<IReadOnlyList<int>> InspectAsync(
        long characterId,
        long shipId,
        int solarSystemId,
        CancellationToken ct = default)
    {
        var system = _systems.Get(solarSystemId);
        if (characterId <= 0
            || shipId <= 0
            || system is null
            || system.FactionId <= 0
            || !double.IsFinite(system.Security))
        {
            return Array.Empty<int>();
        }

        var ship = await _items.GetAsync(shipId, ct);
        if (ship is null || ship.OwnerId != characterId)
        {
            return Array.Empty<int>();
        }

        var contraband = new List<int>();
        var seen = new HashSet<int>();
        foreach (var item in await _items.InLocationAsync(shipId, characterId, ct))
        {
            if (item.Flag != ItemFlags.Cargo
                || item.TypeId <= 0
                || !_rules.TryGetValue((item.TypeId, system.FactionId), out var rule)
                || system.Security < rule.ConfiscateMinSecurity
                || !seen.Add(item.TypeId))
            {
                continue;
            }

            contraband.Add(item.TypeId);
        }

        return contraband;
    }

    /// <summary>
    /// Applies the penalty of the contraband in the ship's cargo at <paramref name="solarSystemId"/> (EVE.js applyContrabandPenalties): the fine is charged to the pilot's
    /// wallet up to its balance and the contraband items are removed, in one transaction. Null when there is nothing to penalise. The inspection roll is not applied:
    /// the smuggling chance (attribute 445 of the implants and skills) is not read, so the contraband is always detected (DEVIATIONS, ST-16 C).
    /// </summary>
    public async Task<ContrabandPenalty?> ApplyAsync(long characterId, long shipId, int solarSystemId, CancellationToken ct = default)
    {
        if (_source is null || _wallets is null)
        {
            throw new InvalidOperationException("The contraband penalty needs the database and the wallets.");
        }

        var system = _systems.Get(solarSystemId);
        if (characterId <= 0 || shipId <= 0 || system is null || system.FactionId <= 0 || !double.IsFinite(system.Security))
        {
            return null;
        }
        var ship = await _items.GetAsync(shipId, ct);
        if (ship is null || ship.OwnerId != characterId)
        {
            return null;
        }

        var entries = new List<(Item Item, double FineByValue)>();
        foreach (var item in await _items.InLocationAsync(shipId, characterId, ct))
        {
            if (item.Flag != ItemFlags.Cargo
                || item.TypeId <= 0
                || !_rules.TryGetValue((item.TypeId, system.FactionId), out var rule)
                || system.Security < rule.ConfiscateMinSecurity)
            {
                continue;
            }
            entries.Add((item, rule.FineByValue));
        }
        if (entries.Count == 0)
        {
            return null;
        }

        // the base prices of the contraband types (EVE.js getItemBasePrice, the SDE types)
        var prices = new Dictionary<long, decimal>();
        foreach (var row in await _sde.ReadAsync("types", entries.Select(entry => (long)entry.Item.TypeId).Distinct(), ct))
        {
            if (row.Doc.ValueKind == JsonValueKind.Object
                && row.Doc.TryGetProperty("basePrice", out var price)
                && price.TryGetDecimal(out var value))
            {
                prices[row.Key] = value;
            }
        }

        var priced = entries
            .Select(entry => (entry.Item, Fine: Math.Round(entry.Item.Quantity * prices.GetValueOrDefault(entry.Item.TypeId) * (decimal)entry.FineByValue,
                2, MidpointRounding.AwayFromZero)))
            .ToList();
        var total = priced.Sum(entry => entry.Fine);
        var balance = await _wallets.AccountBalanceAsync(new WalletAccount(characterId), ct) ?? 0m;
        var charged = Math.Min(balance, total);

        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var key = $"contraband-penalty:{characterId}:{shipId}:{solarSystemId}:{now}";
        await using var connection = await _source.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        if (charged > 0)
        {
            // a fine is burnt: no payee is named by the static data (as the gate toll is, DEVIATIONS, slice 3)
            await _wallets.AdjustAsync(connection, transaction, new WalletAccount(characterId), -charged, 0, FineRefType, FineReason,
                now, key, "contraband_fine", ct);
        }
        foreach (var (item, _) in priced)
        {
            await _items.DeleteAsync(connection, transaction, item.Id, ct);
        }
        await transaction.CommitAsync(ct);
        return new ContrabandPenalty(total, charged, priced.Select(entry => entry.Item).ToArray());
    }
}
