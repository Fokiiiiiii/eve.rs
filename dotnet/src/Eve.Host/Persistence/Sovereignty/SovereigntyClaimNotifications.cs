using System.Text.Json.Nodes;
using Eve.Characters;
using Eve.Host.Persistence.Mail;

namespace Eve.Host.Persistence.Sovereignty;

/// <summary>

/// 44 CorpClaimAquired for the corporation), both with the payload corpID and allianceID. The recipients are our rule: the client names none, and EVE.js writes
/// no mail for a claim. The alliance's members get 43, the claiming corporation's members get 44. The key holds the hub, the claim time and the type, so a
/// retried handover writes nothing twice, and a later claim of the same hub is a new notice.
/// </summary>
internal sealed class SovereigntyClaimNotifications(
    Func<long, CancellationToken, Task<IReadOnlyList<long>>> allianceCorporations,
    PgCharacterStore characters,
    PersistenceTransactionMailbox mailbox)
{
    private const int AllianceClaimAcquiredTypeId = 43;
    private const int CorporationClaimAcquiredTypeId = 44;
    private const int SovereigntyGroupId = 6;

    public async Task WriteAsync(long hubId, long claimFileTime, long corporationId, long allianceId, CancellationToken cancellationToken)
    {
        var allianceMembers = new SortedSet<long>();
        foreach (var corporation in await allianceCorporations(allianceId, cancellationToken))
        {
            allianceMembers.UnionWith(await MembersAsync(corporation, cancellationToken));
        }
        await mailbox.WriteNotificationsAsync(
            allianceId, AllianceClaimAcquiredTypeId, SovereigntyGroupId, allianceMembers,
            $"sov-claim:{AllianceClaimAcquiredTypeId}:hub:{hubId}:claim:{claimFileTime}", Payload(corporationId, allianceId), cancellationToken);
        await mailbox.WriteNotificationsAsync(
            corporationId, CorporationClaimAcquiredTypeId, SovereigntyGroupId, await MembersAsync(corporationId, cancellationToken),
            $"sov-claim:{CorporationClaimAcquiredTypeId}:hub:{hubId}:claim:{claimFileTime}", Payload(corporationId, allianceId), cancellationToken);
    }

    private async Task<IReadOnlyList<long>> MembersAsync(long corporationId, CancellationToken cancellationToken) =>
        (await characters.ForCorporationAsync(corporationId, cancellationToken)).Select(character => character.Id).ToArray();

    private static JsonObject Payload(long corporationId, long allianceId) => new()
    {
        ["corpID"] = corporationId,
        ["allianceID"] = allianceId,
    };
}
