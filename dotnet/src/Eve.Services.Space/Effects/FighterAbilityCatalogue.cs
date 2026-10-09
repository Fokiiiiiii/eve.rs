using System.Reflection;
using System.Text.Json;

namespace Eve.Services.Space.Effects;

/// <summary>
/// An ability a fighter carries: its effect family (the EVE.js name; the AttackM family is <c>fighterAbilityAttackMissile</c> there and the SDE effect
/// <c>fighterAbilityAttackM</c>) and the dogma attributes its effect reads (ids of the SDE attributes).
/// </summary>
public sealed record FighterAbility(
    int AbilityId,
    string EffectFamily,
    string TargetMode,
    bool IsOffensive,
    bool Published,
    int? DogmaEffectId,
    int? DurationAttributeId,
    int? RangeAttributeId,
    int? FalloffAttributeId,
    int? TrackingSpeedAttributeId,
    bool DisallowInHighSec,
    bool DisallowInLowSec);

/// <summary>A slot's charges: how many uses it has, and how long one charge takes to rearm.</summary>
public sealed record FighterCharges(int ChargeCount, double RearmTimeSeconds);

/// <summary>One slot of a fighter type: the ability it carries, its cooldown in seconds and its charges (null when the slot has none).</summary>
public sealed record FighterAbilitySlot(int SlotId, int AbilityId, double? CooldownSeconds, FighterCharges? Charges);

/// <summary>The fighter types and the ability each of their slots carries.</summary>
public sealed record FighterTypeAbilities(int TypeId, IReadOnlyList<FighterAbilitySlot> Slots);

/// <summary>
/// The ability slot table of the fighter types (slot 0, 1 and 2 of each of the 94 published fighter types), with the ability facts it refers to.
/// The data is the EVE.js table (<c>fighter-abilities.json</c>, an embedded resource); its families and slot 0 agree with the pinned SDE, and the
/// slot order, cooldowns and charges come from the EVE.js table alone (ADR 0015, decision FIG-D-2 as amended).
/// </summary>
public sealed class FighterAbilityCatalogue
{
    /// <summary>The resource name of the table as the assembly embeds it.</summary>
    public const string ResourceName = "fighter-abilities.json";

    private readonly IReadOnlyDictionary<int, FighterAbility> _abilities;
    private readonly IReadOnlyDictionary<int, FighterTypeAbilities> _types;

    private FighterAbilityCatalogue(IEnumerable<FighterAbility> abilities, IEnumerable<FighterTypeAbilities> types)
    {
        _abilities = abilities.ToDictionary(a => a.AbilityId);
        _types = types.ToDictionary(t => t.TypeId);
        foreach (var slot in _types.Values.SelectMany(t => t.Slots))
        {
            if (!_abilities.ContainsKey(slot.AbilityId))
            {
                throw new InvalidDataException($"The fighter ability table names ability {slot.AbilityId}, which it does not define.");
            }
        }
    }

    /// <summary>The table the assembly carries.</summary>
    public static FighterAbilityCatalogue Default { get; } = Load(typeof(FighterAbilityCatalogue).Assembly);

    /// <summary>Reads the table from an assembly's embedded resource.</summary>
    public static FighterAbilityCatalogue Load(Assembly assembly)
    {
        using var stream = assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidDataException($"The embedded resource {ResourceName} is missing.");
        return Parse(stream);
    }

    /// <summary>Reads the table from a JSON stream of the shape the EVE.js extraction writes.</summary>
    public static FighterAbilityCatalogue Parse(Stream json)
    {
        var file = JsonSerializer.Deserialize<CatalogueFile>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new InvalidDataException("The fighter ability table is empty.");
        return new FighterAbilityCatalogue(file.Abilities ?? [], (file.Types ?? []).Select(t => new FighterTypeAbilities(t.TypeId, t.Slots ?? [])));
    }

    /// <summary>The ids of the fighter types the table has.</summary>
    public IReadOnlyCollection<int> TypeIds => [.. _types.Keys];

    /// <summary>The abilities the table defines, by id.</summary>
    public IReadOnlyCollection<FighterAbility> Abilities => [.. _abilities.Values];

    /// <summary>An ability by id, or null for an id the table does not define.</summary>
    public FighterAbility? Ability(int abilityId) => _abilities.GetValueOrDefault(abilityId);

    /// <summary>The slots of a fighter type, in slot order (empty for a type the table does not have).</summary>
    public IReadOnlyList<FighterAbilitySlot> SlotsOf(int typeId) =>
        _types.TryGetValue(typeId, out var entry) ? [.. entry.Slots.OrderBy(s => s.SlotId)] : [];

    /// <summary>The slot of a fighter type with the id, or null when the type has no such slot.</summary>
    public FighterAbilitySlot? Slot(int typeId, int slotId) => SlotsOf(typeId).FirstOrDefault(s => s.SlotId == slotId);

    private sealed record CatalogueFile(List<FighterAbility>? Abilities, List<TypeEntry>? Types);

    private sealed record TypeEntry(int TypeId, List<FighterAbilitySlot>? Slots);
}
