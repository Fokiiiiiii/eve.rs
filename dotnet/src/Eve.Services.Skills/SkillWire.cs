using Eve.Marshal;
using Eve.Skills;

namespace Eve.Services.Skills;

/// <summary>How skills look to the client.</summary>
public static class SkillWire
{
    public const string EntryClass = "characterskills.common.character_skill_entry.CharacterSkillEntry";

    /// <summary>The inventory flag skills are in.</summary>
    public const int SkillFlag = 7;

    private const long SkillItemCharacterStride = 100_000;

    /// <summary>
    /// The id the client sees for a character's copy of a skill: the client wants an item id for each skill, and a
    /// skill here is only a row, so the id is made from the two ids that name it (the reference numbers them the same way).
    /// </summary>
    public static long ItemId(long characterId, int typeId)
    {
        if (typeId < 0 || typeId >= SkillItemCharacterStride)
        {
            throw new ArgumentOutOfRangeException(nameof(typeId), typeId, "a skill type id must be below 100000 to make a skill item id");
        }
        return (characterId * SkillItemCharacterStride) + typeId;
    }

    /// <summary>
    /// A <c>CharacterSkillEntry</c>: <c>(typeID, trainedLevel, trainedPoints, rank, virtualLevel)</c> and, with <paramref name="metadata"/>, the item's fields
    /// (the events that only carry points leave them out).
    /// </summary>
    public static PyValue Entry(long characterId, SkillType type, CharacterSkill skill, bool inTraining = false, bool metadata = true) => MachoErrors.WrappedObject(
        EntryClass,
        [new PyInt(type.TypeId), new PyInt(skill.Level), new PyInt(skill.SkillPoints), new PyInt(type.Rank), PyNone.Instance],
        !metadata
            ? null
            :
            [
                Field("itemID", new PyLong(ItemId(characterId, type.TypeId))),
                Field("ownerID", new PyInt(characterId)),
                Field("locationID", new PyInt(characterId)),
                Field("flagID", new PyInt(SkillFlag)),
                Field("groupID", new PyInt(type.GroupId)),
                Field("categoryID", new PyInt(SkillCatalog.SkillCategoryId)),
                Field("groupName", new PyStr(type.GroupName)),
                Field("published", PyValue.Of(type.Published)),
                Field("inTraining", PyValue.Of(inTraining)),
            ]);

    /// <summary>The character's skills as the client keeps them: type id to entry, in type order.</summary>
    public static PyValue EntryDictionary(long characterId, SkillCatalog catalog, IEnumerable<CharacterSkill> skills, int? inTraining = null, bool metadata = true) =>
        new PyDict([.. skills
            .OrderBy(s => s.TypeId)
            .Where(s => catalog.Get(s.TypeId) is not null)
            .Select(s => new KeyValuePair<PyValue, PyValue>(new PyInt(s.TypeId), Entry(characterId, catalog.Get(s.TypeId)!, s, s.TypeId == inTraining, metadata)))]);

    private static KeyValuePair<PyValue, PyValue> Field(string name, PyValue value) => new(new PyStr(name), value);
}
