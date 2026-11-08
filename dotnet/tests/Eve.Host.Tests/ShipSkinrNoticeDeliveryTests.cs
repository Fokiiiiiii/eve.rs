using Eve.Host.PublicGateway;
using Npgsql;

namespace Eve.Host.Tests;

[Collection("host-composition")]
public sealed class ShipSkinrNoticeDeliveryTests
{
    [PgFact]
    public async Task Completion_notices_survive_a_restart_before_the_stream_writes_the_queued_frames()
    {
        const long now = 1_800_000_000_000;
        const long characterId = 90_000_001;
        const string skinHex = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        await using var rig = await CharacterHostTests.CharacterHostRig.CreateAsync(nowUnixMs: now);
        var jobId = Guid.NewGuid();
        await using (var connection = await rig.Source.OpenConnectionAsync())
        await using (var transaction = await connection.BeginTransactionAsync())
        {
            await using var insert = new NpgsqlCommand($"""
                INSERT INTO {rig.SkinrSchema}.skinr_sequencing_jobs
                    (job_id, character_id, request_id, request_fingerprint, skin_hex, name, line_name,
                     ship_type_id, tier, licenses, plex_cost, due_ms, state, layout,
                     editor_configurations, created_ms)
                VALUES ($1, $2, $3, 'test', $4, 'Test skin', 'Test line', 587, 1, 2, 1, $5,
                        'pending', '\x'::bytea, '\x'::bytea, $5)
                """, connection, transaction);
            insert.Parameters.Add(new NpgsqlParameter { Value = jobId });
            insert.Parameters.Add(new NpgsqlParameter { Value = characterId });
            insert.Parameters.Add(new NpgsqlParameter { Value = Guid.NewGuid() });
            insert.Parameters.Add(new NpgsqlParameter { Value = skinHex });
            insert.Parameters.Add(new NpgsqlParameter { Value = now });
            await insert.ExecuteNonQueryAsync();
            await rig.Scheduler.ScheduleAsync(connection, transaction,
                new Eve.Scheduler.JobSpec(ShipSkinrSequenceJobs.Kind, now)
                {
                    Key = jobId.ToString("N"),
                    OnDuplicate = Eve.Scheduler.OnDuplicate.Keep,
                });
            await transaction.CommitAsync();
        }

        // The Host's registered sequence handler performs the state change and publishes
        // its completion event. The event subscriber stores both ordered gateway notices.
        await rig.Scheduler.RunDueAsync();
        await rig.Events.PumpAsync();
        await rig.Scheduler.RunDueAsync();

        await using (var verify = await rig.Source.OpenConnectionAsync())
        {
            await using var job = new NpgsqlCommand(
                $"SELECT state, completed_ms FROM {rig.SkinrSchema}.skinr_sequencing_jobs WHERE job_id = $1", verify);
            job.Parameters.Add(new NpgsqlParameter { Value = jobId });
            await using var row = await job.ExecuteReaderAsync();
            Assert.True(await row.ReadAsync());
            Assert.Equal("completed", row.GetString(0));
            Assert.Equal(now, row.GetInt64(1));
            await row.DisposeAsync();

            await using var license = new NpgsqlCommand(
                $"SELECT unactivated FROM {rig.SkinrSchema}.skinr_ship_licenses WHERE character_id = $1 AND skin_hex = $2", verify);
            license.Parameters.Add(new NpgsqlParameter { Value = characterId });
            license.Parameters.Add(new NpgsqlParameter { Value = skinHex });
            Assert.Equal(2L, await license.ExecuteScalarAsync());
        }

        // Let the real Host scheduler observe that no recipient stream exists. Its two
        // persistent notice jobs must remain pending for a later reconnect.
        await rig.Scheduler.RunDueAsync();
        await rig.RestartAsync();
        rig.AdvanceTo(now + 5_000);

        // The first recipient connects, but the process stops before its stream loop writes
        // either queued frame. A persisted delivery must still be retried after this restart.
        var interrupted = rig.Gateway.Notices.Subscribe(account: 900_000_001);
        interrupted.Character = characterId;
        await rig.Scheduler.RunDueAsync();
        Assert.True(interrupted.Frames.Reader.TryPeek(out _));
        rig.Gateway.Notices.Unsubscribe(interrupted);
        await rig.RestartAsync();
        rig.AdvanceTo(now + 10_000);

        var subscriber = rig.Gateway.Notices.Subscribe(account: 900_000_001);
        subscriber.Character = characterId;
        await rig.Scheduler.RunDueAsync();
        var completedFrame = await subscriber.Frames.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        var licenseFrame = await subscriber.Frames.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        rig.Gateway.Notices.Acknowledge(subscriber, completedFrame);
        rig.Gateway.Notices.Acknowledge(subscriber, licenseFrame);

        Assert.Equal("eve_public.cosmetic.ship.skin.thirdparty.sequencing.job.api.CompletedNotice", NoticeType(completedFrame));
        Assert.Equal("eve_public.cosmetic.ship.skin.thirdparty.license.api.UpdatedNotice", NoticeType(licenseFrame));
        Assert.Equal(characterId, checked((long)NoticeCharacter(completedFrame)));
        Assert.Equal(characterId, checked((long)NoticeCharacter(licenseFrame)));
        var completedPayload = NoticePayload(completedFrame);
        Assert.Equal(jobId.ToByteArray(bigEndian: true),
            Message(Message(completedPayload, 1), 1).ToArray());
        var licensePayload = NoticePayload(licenseFrame);
        Assert.Equal(characterId, checked((long)LicenseCharacter(licensePayload)));
        Assert.Equal(skinHex, LicenseSkin(licensePayload));
        Assert.Equal(2UL, Scalar(Message(licensePayload, 2), 2));
        Assert.Equal(0UL, ScalarOrDefault(Message(licensePayload, 3), 2));
        rig.AdvanceTo(now + 15_000);
        await rig.Scheduler.RunDueAsync();
        Assert.False(subscriber.Frames.Reader.TryRead(out _));
        rig.Gateway.Notices.Unsubscribe(subscriber);
    }

    private static string NoticeType(byte[] frame)
    {
        var message = GatewayEnvelope.TakeFrames(frame.ToList()).Single();
        var any = Message(message, 5);
        return System.Text.Encoding.UTF8.GetString(Message(any, 1).Span)[GatewayEnvelope.TypeUrlPrefix.Length..];
    }

    private static ReadOnlyMemory<byte> NoticePayload(byte[] frame)
    {
        var message = GatewayEnvelope.TakeFrames(frame.ToList()).Single();
        return Message(Message(message, 5), 2);
    }

    private static ulong NoticeCharacter(byte[] frame) =>
        ProtoReader.Fields(Message(GatewayEnvelope.TakeFrames(frame.ToList()).Single(), 6))
            .Single(field => field.Number == 4 && field.WireType == 0).Scalar;

    private static ulong LicenseCharacter(ReadOnlyMemory<byte> payload)
    {
        var identifier = Message(payload, 1);
        var character = Message(identifier, 1);
        return ProtoReader.Fields(character).Single(field => field.Number == 1 && field.WireType == 0).Scalar;
    }

    private static string LicenseSkin(ReadOnlyMemory<byte> payload)
    {
        var identifier = Message(payload, 1);
        var skin = Message(identifier, 2);
        return System.Text.Encoding.UTF8.GetString(Message(skin, 1).Span);
    }

    private static ulong Scalar(ReadOnlyMemory<byte> payload, int number) =>
        ProtoReader.Fields(payload).Single(field => field.Number == number && field.WireType == 0).Scalar;

    private static ulong ScalarOrDefault(ReadOnlyMemory<byte> payload, int number) =>
        ProtoReader.Fields(payload).Where(field => field.Number == number && field.WireType == 0)
            .Select(field => field.Scalar).DefaultIfEmpty().Single();

    private static ReadOnlyMemory<byte> Message(ReadOnlyMemory<byte> payload, int number) =>
        ProtoReader.Fields(payload).Single(field => field.Number == number && field.WireType == 2).Bytes;
}
