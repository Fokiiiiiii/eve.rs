using Eve.Macho;
using Eve.Marshal;
using Eve.Characters;
using Eve.Skills;
using Npgsql;

namespace Eve.Services.Skills;

/// <summary>An active booster returned to the client, including its expiry and selected side-effect IDs.</summary>
public sealed record SkillActiveBoosterSnapshot(long ItemId, int BoosterTypeId, long ExpiryTimeFileTime, IReadOnlyList<int> SideEffectIds);
