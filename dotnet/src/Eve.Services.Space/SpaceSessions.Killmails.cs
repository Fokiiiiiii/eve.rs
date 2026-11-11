using System.Collections.Concurrent;
using Eve.Marshal;
using Eve.Macho;
using Eve.Npc;
using Eve.Space;

namespace Eve.Services.Space;

public sealed partial class SpaceSessions
{
    private const long KillmailDamageLedgerTtlMs = 30 * 60 * 1000;
    private const int KillmailDamageLedgerCapacity = 16_384;

    private sealed class DamageContribution(
        long? characterId, long corporationId, long allianceId, long factionId,
        long sourceShipId, int shipTypeId, int weaponTypeId, double? securityStatus)
    {
        public long? CharacterId { get; } = characterId;
        public long CorporationId { get; } = corporationId;
        public long AllianceId { get; } = allianceId;
        public long FactionId { get; } = factionId;
        public long SourceShipId { get; } = sourceShipId;
        public int ShipTypeId { get; } = shipTypeId;
        public int WeaponTypeId { get; } = weaponTypeId;
        public double? SecurityStatus { get; } = securityStatus;
        public double DamageDone { get; set; }
        public long LastHitOrder { get; set; }
    }

    private sealed class DamageLedger
    {
        public long LastUpdatedMs { get; set; }
        public int SolarSystemId { get; set; }
        public double DamageTaken { get; set; }
        public Dictionary<(long OwnerId, long SourceShipId, int WeaponTypeId), DamageContribution> Attackers { get; } = [];
    }

    private readonly ConcurrentDictionary<long, DamageLedger> _killmailDamage = new();
    private long _killmailDamageOrder;
    private int _killmailDamageCleanupCounter;

    private void RecordKillmailDamage(SimNotify notify, Pilot pilot)
    {
        if (notify.Name != "OnDamageMessage" ||
            MarshalCodec.Decode(notify.Args.ToArray()) is not PyTuple { Items.Count: 1 } args ||
            args.Items[0] is not PyDict damage)
            return;

        static string? Text(PyValue value) => JsSemantics.StringValue(value);
        PyValue? Value(string name) => damage.Entries
            .Where(entry => Text(entry.Key) == name)
            .Select(entry => entry.Value)
            .FirstOrDefault();
        long Integer(string name) => Value(name) is { } value && JsSemantics.IsNumber(value)
            ? (long)JsSemantics.NumberValue(value)
            : Value(name) is PyLong integer ? integer.Value : 0;
        double Number(string name) => Value(name) is { } value && JsSemantics.IsNumber(value)
            ? JsSemantics.NumberValue(value)
            : 0;
        long OwnerId()
        {
            if (Value("owner") is not PyTuple { Items.Count: >= 2 } owner)
                return 0;
            return owner.Items[1] is PyInt number ? number.Value
                : owner.Items[1] is PyLong big ? big.Value
                : 0;
        }

        var attackType = Value("attackType") is { } attackTypeValue ? Text(attackTypeValue) : null;
        var targetShipId = Integer("target");
        var sourceShipId = Integer("source");
        var weaponTypeId = checked((int)Integer("weapon"));
        var amount = Number("damage");
        if (targetShipId <= 0 || !double.IsFinite(amount) || amount <= 0)
            return;

        // A pilot receives damage messages for their own ship; an attacker receives
        // the same message with attackType=me when the target is a tracked NPC.
        var trackedNpcVictim = _npcShips.TryGetValue(targetShipId, out var trackedNpc);
        var targetIsPlayerShip = _pilots.Values.Any(candidate => candidate.ShipId == targetShipId);
        var nativeNpcVictim = attackType == "me" && sourceShipId == pilot.ShipId && !targetIsPlayerShip;
        var npcVictim = trackedNpcVictim || nativeNpcVictim;
        var victimIsThisPilot = targetShipId == pilot.ShipId;
        if (!victimIsThisPilot && !npcVictim)
            return;

        var sourcePilot = _pilots.Values.FirstOrDefault(candidate => candidate.ShipId == sourceShipId);
        var ownerId = OwnerId();
        var npc = _npcShips.TryGetValue(sourceShipId, out var npcShip) ? npcShip.Profile : null;
        long? characterId = npc is null
            ? sourcePilot?.CharacterId
            : null;
        // The wire's UE_OWNERID is an owner fallback, not proof that this is a
        // character. Match EveJS: identified pilots use their character row;
        // otherwise retain the owner as corporation identity without inventing
        // a character ID for an NPC or other untracked entity.
        var corporationId = npc is { CorporationId: > 0 }
            ? npc.CorporationId
            : sourcePilot is null && ownerId > 0 ? ownerId : 0;
        var allianceId = npc?.AllianceId ?? 0;
        var factionId = npc?.FactionId ?? 0;
        var shipTypeId = npc?.ShipTypeId ?? sourcePilot?.TypeId ?? 0;
        var securityStatus = npc?.SecurityStatus;
        var ledgerOwnerId = corporationId > 0 ? corporationId : characterId ?? ownerId;

        var now = _nowUnixMs();
        var solarSystemId = trackedNpcVictim ? trackedNpc!.SolarSystemId : pilot.SolarSystemId;
        var ledger = _killmailDamage.GetOrAdd(targetShipId, _ => new DamageLedger
        {
            LastUpdatedMs = now,
            SolarSystemId = solarSystemId,
        });
        lock (ledger)
        {
            if (now - ledger.LastUpdatedMs > KillmailDamageLedgerTtlMs)
            {
                ledger.Attackers.Clear();
                ledger.DamageTaken = 0;
            }
            ledger.LastUpdatedMs = now;
            ledger.SolarSystemId = solarSystemId;
            ledger.DamageTaken += amount;
            if (ledgerOwnerId > 0 && sourceShipId > 0)
            {
                var key = (ledgerOwnerId, sourceShipId, weaponTypeId);
                if (!ledger.Attackers.TryGetValue(key, out var attacker))
                {
                    attacker = new DamageContribution(
                        characterId, corporationId, allianceId, factionId,
                        sourceShipId, shipTypeId, weaponTypeId, securityStatus);
                    ledger.Attackers.Add(key, attacker);
                }
                attacker.DamageDone += amount;
                attacker.LastHitOrder = Interlocked.Increment(ref _killmailDamageOrder);
            }
        }

        if (Interlocked.Increment(ref _killmailDamageCleanupCounter) % 256 == 0)
            CleanupKillmailDamage(now);
    }

    private IReadOnlyList<ShipKillmailAttacker> TakeKillmailAttackers(
        long victimShipId, long killerShipId, out double damageTaken, out int solarSystemId)
    {
        damageTaken = 0;
        solarSystemId = 0;
        if (!_killmailDamage.TryRemove(victimShipId, out var ledger))
            return DescribeFinalAttacker(killerShipId) is { } finalOnly ? [finalOnly] : [];

        List<ShipKillmailAttacker> attackers;
        lock (ledger)
        {
            var now = _nowUnixMs();
            if (now - ledger.LastUpdatedMs > KillmailDamageLedgerTtlMs)
                return DescribeFinalAttacker(killerShipId) is { } finalOnly ? [finalOnly] : [];
            solarSystemId = ledger.SolarSystemId;
            damageTaken = ledger.DamageTaken;
            attackers = ledger.Attackers.Values.Select(attacker => new ShipKillmailAttacker(
                    attacker.CharacterId, attacker.CorporationId, attacker.AllianceId, attacker.FactionId,
                    attacker.SourceShipId, attacker.ShipTypeId, attacker.WeaponTypeId,
                    attacker.DamageDone, attacker.SecurityStatus, attacker.LastHitOrder))
                .ToList();
        }

        if (killerShipId > 0 && attackers.All(attacker => attacker.SourceShipId != killerShipId) &&
            DescribeFinalAttacker(killerShipId) is { } finalAttacker)
            attackers.Add(finalAttacker);
        return attackers.OrderByDescending(attacker => attacker.DamageDone)
            .ThenBy(attacker => attacker.LastHitOrder)
            .ToArray();
    }

    private ShipKillmailAttacker? DescribeFinalAttacker(long shipId)
    {
        if (shipId <= 0)
            return null;
        if (_npcShips.TryGetValue(shipId, out var npc) && npc.Profile is { } profile)
            return new ShipKillmailAttacker(null, profile.CorporationId, profile.AllianceId, profile.FactionId,
                shipId, profile.ShipTypeId, 0, 0, profile.SecurityStatus, long.MaxValue);
        if (_pilots.Values.FirstOrDefault(candidate => candidate.ShipId == shipId) is { } pilot)
            return new ShipKillmailAttacker(pilot.CharacterId, 0, 0, 0, shipId, pilot.TypeId, 0, 0, null, long.MaxValue);
        return null;
    }

    private void CleanupKillmailDamage(long nowMs)
    {
        foreach (var (shipId, ledger) in _killmailDamage)
        {
            lock (ledger)
            {
                if (nowMs - ledger.LastUpdatedMs > KillmailDamageLedgerTtlMs)
                    _killmailDamage.TryRemove(new KeyValuePair<long, DamageLedger>(shipId, ledger));
            }
        }
        while (_killmailDamage.Count > KillmailDamageLedgerCapacity)
        {
            var oldest = _killmailDamage.OrderBy(pair => pair.Value.LastUpdatedMs).FirstOrDefault();
            if (oldest.Key <= 0 || !_killmailDamage.TryRemove(oldest))
                break;
        }
    }
}
