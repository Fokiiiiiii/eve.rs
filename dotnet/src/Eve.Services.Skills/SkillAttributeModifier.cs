using Eve.Macho;
using Eve.Marshal;
using Eve.Characters;
using Eve.Skills;
using Npgsql;

namespace Eve.Services.Skills;

/// <summary>A client-visible Dogma modifier from an equipped implant or active booster.</summary>
public sealed record SkillAttributeModifier(int AttributeId, long ItemId, int TypeId, int Operation, double Value);
