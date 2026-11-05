using Eve.Events;

namespace Eve.Host.PublicGateway;

/// <summary>Publishes SKINR gateway notices from committed events.</summary>
internal static class ShipSkinrNoticeHandlers
{
    private const string Prefix = "eve_public.cosmetic.ship.skin.thirdparty.";

    public static void Register(EventBus events, NoticeHub notices)
    {
        events.Subscribe("ship-skinr-gateway-notices",
            [ShipSkinrSequenceJobs.CompletedTopic],
            (context, _) =>
            {
                var completed = context.Event.Payload<ShipSkinrSequenceJobs.SequenceCompleted>()
                    ?? throw new InvalidOperationException("a SKINR completion event has no payload");
                context.AfterCommit(_ =>
                {
                    var job = new ProtoWriter();
                    job.Message(1, Uuid(completed.JobId));
                    notices.Publish(Prefix + "sequencing.job.api.CompletedNotice", job.ToArray(),
                        character: completed.CharacterId);

                    var identifier = new ProtoWriter();
                    identifier.Message(1, Sequential(completed.CharacterId));
                    var skin = new ProtoWriter();
                    skin.String(1, completed.SkinHex);
                    identifier.Message(2, skin.ToArray());

                    var payload = new ProtoWriter();
                    payload.Message(1, identifier.ToArray());
                    payload.Message(2, LicenseAttributes(completed.Activated, completed.Unactivated,
                        completed.Escrowed, completed.FirstAcquiredMs));
                    payload.Message(3, LicenseAttributes(completed.Activated,
                        Math.Max(0, completed.Unactivated - completed.Quantity),
                        completed.Escrowed, completed.FirstAcquiredMs));
                    notices.Publish(Prefix + "license.api.UpdatedNotice", payload.ToArray(),
                        character: completed.CharacterId);
                    return Task.CompletedTask;
                });
                return Task.CompletedTask;
            },
            StartAt.Beginning);
    }

    private static byte[] LicenseAttributes(bool activated, long unactivated, long escrowed, long firstAcquiredMs)
    {
        var attributes = new ProtoWriter();
        attributes.Uint(1, activated ? 1UL : 0UL);
        attributes.Uint(2, (ulong)Math.Max(0, unactivated));
        attributes.Uint(3, (ulong)Math.Max(0, escrowed));
        attributes.Message(4, Timestamp(firstAcquiredMs));
        return attributes.ToArray();
    }

    private static byte[] Uuid(Guid value)
    {
        var uuid = new ProtoWriter();
        uuid.Bytes(1, value.ToByteArray(bigEndian: true));
        return uuid.ToArray();
    }

    private static byte[] Sequential(long value)
    {
        var identifier = new ProtoWriter();
        identifier.Uint(1, (ulong)value);
        return identifier.ToArray();
    }

    private static byte[] Timestamp(long unixMs)
    {
        var timestamp = new ProtoWriter();
        var seconds = Math.DivRem(unixMs, 1000, out var milliseconds);
        timestamp.Uint(1, (ulong)seconds);
        timestamp.Uint(2, (ulong)(milliseconds * 1_000_000));
        return timestamp.ToArray();
    }
}
