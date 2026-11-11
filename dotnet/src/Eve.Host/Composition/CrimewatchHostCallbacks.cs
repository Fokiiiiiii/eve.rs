namespace Eve.Host;

internal sealed partial class CharacterServicesComposition
{
    private void WireKillRightCallbacks()
    {
        if (spaceSessions is null || crimewatchStore is null || characterSessions is null)
            return;

        spaceSessions.ShipLost += loss => ConsumeKillRightAfterLoss(
            loss.CharacterId, loss.ShipId, loss.KillerCharacterId);
        spaceSessions.PodLost += loss => ConsumeKillRightAfterLoss(
            loss.CharacterId, loss.CapsuleId, loss.KillerCharacterId);
    }

    private void ConsumeKillRightAfterLoss(long targetCharacterId, long lostItemId, long finalAttackerCharacterId)
    {
        if (targetCharacterId <= 0 || lostItemId <= 0)
            return;

        var activation = crimewatchStore.ConsumeKillRightAfterKillAsync(
            targetCharacterId, finalAttackerCharacterId, lostItemId, now(), CancellationToken.None)
            .GetAwaiter().GetResult();
        if (activation is null)
            return;

        foreach (var session in characterSessions.All())
        {
            if (session.CharacterId is { } characterId &&
                (characterId == activation.OwnerCharacterId ||
                 characterId == activation.TargetCharacterId ||
                 characterId == activation.ActivatorCharacterId ||
                 characterId == finalAttackerCharacterId))
                session.SendNotification("OnKillRightUsed", "clientID",
                    Eve.Services.InventoryWire.Id(activation.KillRightId),
                    Eve.Services.InventoryWire.Id(activation.TargetCharacterId));
        }
    }
}
