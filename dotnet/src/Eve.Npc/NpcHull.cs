using Eve.Dogma;

namespace Eve.Npc;

/// <summary>
/// Is a hull something a weapon can work on? An NPC's hit points come from its ship type's dogma and from nowhere else; a type with none (38 of the
/// 7334 entity types have no dogma record at all, and three more declare no hit points) makes an NPC that cannot be shot or killed, so a spawn never
/// makes one. A port, as it is, of <c>npcHullDamageability.js</c> of EVE.js (ADR 0009).
/// </summary>
public static class NpcHull
{
    private const int ShieldCapacity = 263;
    private const int ArmorHitPoints = 265;
    private const int StructureHitPoints = 9;

    public static bool HasHitPoints(TypeDef? type) =>
        type is not null
        && (type.Attributes.GetValueOrDefault(ShieldCapacity) > 0 || type.Attributes.GetValueOrDefault(ArmorHitPoints) > 0 || type.Attributes.GetValueOrDefault(StructureHitPoints) > 0);
}
