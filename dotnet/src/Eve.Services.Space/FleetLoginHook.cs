using Eve.Macho;
using Eve.Marshal;
using Eve.Space;

namespace Eve.Services.Space;

public sealed class FleetLoginHook(Func<long, CancellationToken, Task<FleetMemberSnapshot?>> memberForCharacter)
    : Eve.Services.ICharacterEntryHook
{
    public async ValueTask<IReadOnlyList<(string Name, Eve.Marshal.PyValue Value)>> GetSessionAttributesAsync(
        Eve.Services.ClientSession session, long characterId, CancellationToken cancellationToken)
    {
        var member = await memberForCharacter(characterId, cancellationToken);
        return member is not { FleetId: > 0 }
            ? [
                ("fleetid", Eve.Marshal.PyNone.Instance),
                ("fleetrole", Eve.Marshal.PyNone.Instance),
                ("wingid", Eve.Marshal.PyNone.Instance),
                ("squadid", Eve.Marshal.PyNone.Instance),
            ]
            : [
                ("fleetid", new Eve.Marshal.PyLong(member.FleetId)),
                ("fleetrole", new Eve.Marshal.PyInt(member.Role)),
                ("wingid", member.WingId > 0 ? new Eve.Marshal.PyLong(member.WingId) : Eve.Marshal.PyNone.Instance),
                ("squadid", member.SquadId > 0 ? new Eve.Marshal.PyLong(member.SquadId) : Eve.Marshal.PyNone.Instance),
            ];
    }

    public ValueTask OnCharacterEnteredAsync(
        Eve.Services.ClientSession session, long characterId, CancellationToken cancellationToken) =>
        ValueTask.CompletedTask;
}
