using System.Text.Json;
using Eve.Dogma;
using Eve.Npc;
using Eve.Space;

namespace Eve.Npc.Tests;

/// <summary>
/// The weapon a hull carries is the one the reference's npcEquipment made of it, for every NPC ship type of the static data
/// (<c>oracle/fixtures/npc/hull_weapons.json</c>, made by <c>tools/dev/gen-npc-armament-fixture.js</c> with the attributes and effects it worked from): its turret or, where it
/// has none, the missile it launches (<c>getNpcEntityMissileWeaponSource</c>). A hull that has both is armed with its turret: an NPC's weapons are effects of its ship, and the
/// simulation runs one effect of the ship at a time.
/// </summary>
public sealed class HullWeaponFixtureTests
{
    private const long ShipId = 4_300_000_000_000_001;

    private static JsonDocument Fixture() => JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", "npc", "hull_weapons.json")));

    [Fact]
    public void Every_hull_has_the_weapon_the_reference_made_of_it()
    {
        using var fixture = Fixture();
        var effects = fixture.RootElement.GetProperty("effects").EnumerateObject().ToDictionary(
            e => int.Parse(e.Name),
            e => new EffectDef(int.Parse(e.Name), e.Value.GetProperty("name").GetString()!, e.Value.GetProperty("category").GetInt32(), [])
            {
                Guid = e.Value.GetProperty("guid").GetString()!,
            });
        var missileTypes = fixture.RootElement.GetProperty("missileTypes").EnumerateObject().Select(t => new TypeDef(
            int.Parse(t.Name), t.Value.GetProperty("groupID").GetInt32(), 8,
            t.Value.GetProperty("attributes").EnumerateObject().ToDictionary(a => int.Parse(a.Name), a => a.Value.GetDouble()), [], t.Value.GetProperty("name").GetString()!)).ToList();
        var statics = new DogmaStatic([new AttributeDef(247, "entityAttackRange", 15_000, true, false, 0)], effects.Values, missileTypes);
        var missiles = 0;
        var cases = fixture.RootElement.GetProperty("cases").EnumerateArray().ToList();
        Assert.True(cases.Count > 4_000, $"{cases.Count} cases");

        var wrong = new List<string>();
        var armed = 0;
        foreach (var c in cases)
        {
            var typeId = c.GetProperty("typeID").GetInt32();
            var attributes = c.GetProperty("attributes").EnumerateObject().ToDictionary(a => int.Parse(a.Name), a => a.Value.GetDouble());
            var hull = new TypeDef(typeId, 0, 11, attributes, [.. c.GetProperty("effectIds").EnumerateArray().Select(e => e.GetInt32())]);

            // the simulation knows one weapon to an NPC ship (its module is the ship): the first turret the reference made, and none when it made none
            // (a vorton projector is not a turret)
            var theirs = c.GetProperty("weapons").EnumerateArray().FirstOrDefault(w => w.GetProperty("family").GetString() is not "vortonProjector");
            var mine = NpcArmament.HullWeapon(hull, ShipId, statics);
            if (theirs.ValueKind == JsonValueKind.Undefined && c.GetProperty("missile") is { ValueKind: JsonValueKind.Object } launched)
            {
                missiles++;
                if (!SameMissileWeapon(mine, hull, launched, effects))
                {
                    wrong.Add($"type {typeId}: mine {mine}, the reference's missile {launched}");
                }
                continue;
            }
            if (theirs.ValueKind == JsonValueKind.Undefined)
            {
                if (mine is not null)
                {
                    wrong.Add($"type {typeId}: a weapon where the reference made none");
                }
                continue;
            }
            armed++;
            if (mine is null)
            {
                wrong.Add($"type {typeId}: no weapon where the reference made one");
                continue;
            }
            var damage = theirs.GetProperty("rawShotDamage").EnumerateArray().Select(d => d.GetDouble()).ToArray();
            var same = mine.ModuleId == ShipId
                && mine.EffectId == theirs.GetProperty("effectID").GetInt32()
                && mine.WeaponTypeId == typeId
                && mine.Fx == theirs.GetProperty("effectGUID").GetString()
                && mine.CycleMs == theirs.GetProperty("durationMs").GetDouble()
                && mine.CapacitorNeed == theirs.GetProperty("capNeed").GetDouble()
                && mine.OptimalRange == theirs.GetProperty("optimalRange").GetDouble()
                && mine.Falloff == theirs.GetProperty("falloff").GetDouble()
                && mine.TrackingSpeed == theirs.GetProperty("trackingSpeed").GetDouble()
                && mine.SignatureResolution == theirs.GetProperty("optimalSigRadius").GetDouble()
                && mine.RawDamage.SequenceEqual(damage);
            if (!same)
            {
                wrong.Add($"type {typeId}: mine {mine}, the reference's {theirs}");
            }
        }
        Assert.True(armed > 4_000, $"{armed} armed types");
        Assert.True(missiles > 500, $"{missiles} hulls that launch missiles and have no turret");
        Assert.True(wrong.Count == 0, $"{wrong.Count} of {cases.Count} differ:\n{string.Join("\n", wrong.Take(20))}");
    }

    /// <summary>Whether the weapon of a hull is the missile launcher the reference made of it: the effect the hull has, the cycle, the damage, and the missile's speed, flight and explosion.</summary>
    private static bool SameMissileWeapon(SimNpcWeapon? mine, TypeDef hull, JsonElement theirs, Dictionary<int, EffectDef> effects)
    {
        if (mine?.Missile is not { } missile)
        {
            return false;
        }
        var launcher = hull.EffectIds.Select(id => effects.GetValueOrDefault(id)).FirstOrDefault(e => e is not null && e.Name.Equals("missileLaunchingForEntity", StringComparison.OrdinalIgnoreCase));
        return launcher is not null
            && mine.ModuleId == ShipId && mine.EffectId == launcher.Id && mine.WeaponTypeId == hull.TypeId && mine.Charge is null
            && mine.CycleMs == theirs.GetProperty("durationMs").GetDouble()
            && mine.OptimalRange == theirs.GetProperty("approxRange").GetDouble()
            && mine.RawDamage.SequenceEqual(theirs.GetProperty("rawShotDamage").EnumerateArray().Select(d => d.GetDouble()))
            && (missile.ChargeTypeId, missile.MaxVelocity, missile.FlightTimeMs, missile.ExplosionRadius, missile.ExplosionVelocity, missile.DamageReductionFactor, missile.DamageReductionSensitivity)
            == (theirs.GetProperty("typeID").GetInt32(), theirs.GetProperty("maxVelocity").GetDouble(), theirs.GetProperty("flightTimeMs").GetDouble(), theirs.GetProperty("explosionRadius").GetDouble(),
                theirs.GetProperty("explosionVelocity").GetDouble(), theirs.GetProperty("damageReductionFactor").GetDouble(), theirs.GetProperty("damageReductionSensitivity").GetDouble());
    }
}
