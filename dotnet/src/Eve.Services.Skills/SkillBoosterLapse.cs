using Eve.Characters;
using Eve.Marshal;
using Eve.Scheduler;
using Eve.Skills;
using Npgsql;

namespace Eve.Services.Skills;

/// <summary>
/// The end of a skill booster: a durable job per activated booster runs at its expiry. The booster's row and its item are removed
/// (the same pruning the reads do, <see cref="SkillHandlerService.PruneExpiredBoosterRowsAsync"/>), the running skill is rebased at the
/// rate it has from now on, and an online pilot is told (<c>OnServerBoostersChanged</c>, the reference's). A job that fires before the
/// expiry (a clock that is behind) schedules itself again at the expiry, so no booster lapses late or never.
/// </summary>
public sealed class SkillBoosterLapse(
    DurableScheduler scheduler,
    PgSkillStore skillStore,
    Eve.Inventory.PgItemStore items,
    NpgsqlDataSource source,
    CharacterTraining training,
    TrainingAnnouncer announcer,
    PgCharacterStore characters,
    Eve.Services.CharacterSessions sessions,
    Func<long> nowUnixMs)
{
    public const string JobKind = "skills.boosterLapse";

    public sealed record BoosterJob(long CharacterId, long ItemId);

    /// <summary>Registers the job kind with the scheduler; the Host calls it once at composition.</summary>
    public void Register() => scheduler.Register(JobKind, (job, token) => LapseAsync(job.Payload<BoosterJob>()!, token));

    /// <summary>Schedules the lapse of an activated booster in the caller's transaction, so the activation and its job commit together.</summary>
    public Task ScheduleAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, long characterId, long itemId, long expiryFileTime, CancellationToken cancellationToken) =>
        scheduler.ScheduleAsync(
            connection, transaction,
            new JobSpec(JobKind, DateTimeOffset.FromFileTime(expiryFileTime).ToUnixTimeMilliseconds())
            {
                Key = $"{characterId}:{itemId}",
                Payload = new BoosterJob(characterId, itemId),
            },
            cancellationToken);

    public async Task LapseAsync(BoosterJob job, CancellationToken cancellationToken)
    {
        var nowFileTime = DateTimeOffset.FromUnixTimeMilliseconds(nowUnixMs()).ToFileTime();
        bool lapsed;
        await using (var connection = await source.OpenConnectionAsync(cancellationToken))
        await using (var transaction = await connection.BeginTransactionAsync(cancellationToken))
        {
            lapsed = (await skillStore.ExpiredSkillBoostersAsync(connection, transaction, job.CharacterId, nowFileTime, cancellationToken)).Count > 0;
            if (lapsed)
                await SkillHandlerService.PruneExpiredBoosterRowsAsync(skillStore, items, connection, transaction, job.CharacterId, nowFileTime, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        if (!lapsed)
        {
            if (await skillStore.ActiveSkillBoosterAsync(job.CharacterId, job.ItemId, nowFileTime, cancellationToken) is { } active)
            {
                await using var connection = await source.OpenConnectionAsync(cancellationToken);
                await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
                await ScheduleAsync(connection, transaction, job.CharacterId, job.ItemId, active.ExpiryFileTime, cancellationToken);
                await transaction.CommitAsync(cancellationToken);
            }
            return;
        }

        var character = await characters.GetAsync(job.CharacterId, cancellationToken);
        var update = character is null ? null : await training.RebaseRunningAsync(job.CharacterId, character.AccountId, cancellationToken);
        sessions.Of(job.CharacterId)?.SendNotification("OnServerBoostersChanged", "clientID", new PyList([]));
        await announcer.AnnounceLearningChangedAsync(job.CharacterId, update);
    }

    /// <summary>
    /// items go in the caller's transaction, so the jump and the end commit together. Returns whether any booster ended; the caller then
    /// calls <see cref="AnnounceEndedAsync"/> after the commit.
    /// </summary>
    public async Task<bool> EndNormalBoostersAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, long characterId, long nowFileTime,
        Eve.Dogma.DogmaStatic dogma, CancellationToken cancellationToken)
    {
        await skillStore.LockActiveSkillBoostersAsync(connection, transaction, characterId, cancellationToken);
        var active = await skillStore.ActiveSkillBoostersAsync(connection, transaction, characterId, nowFileTime, cancellationToken);
        await dogma.EnsureTypesAsync(active.Select(booster => booster.BoosterTypeId).ToArray(), cancellationToken);
        var normal = active.Where(booster => !SkillHandlerService.IsCerebralAccelerator(dogma.Type(booster.BoosterTypeId))).ToList();
        foreach (var booster in normal)
        {
            var item = await items.LockAndGetAsync(connection, transaction, booster.ItemId, cancellationToken);
            if (item is not null && item.OwnerId == characterId && item.LocationId == characterId && item.TypeId == booster.BoosterTypeId)
                await items.DeleteAsync(connection, transaction, item.Id, cancellationToken);
            await skillStore.DeleteSkillBoosterAsync(connection, transaction, characterId, booster.ItemId, cancellationToken);
        }
        return normal.Count > 0;
    }

    /// <summary>After the commit of <see cref="EndNormalBoostersAsync"/>: the running skill is rebased at the rate it has now, and an online pilot is told.</summary>
    public async Task AnnounceEndedAsync(long characterId, CancellationToken cancellationToken)
    {
        var character = await characters.GetAsync(characterId, cancellationToken);
        var update = character is null ? null : await training.RebaseRunningAsync(characterId, character.AccountId, cancellationToken);
        sessions.Of(characterId)?.SendNotification("OnServerBoostersChanged", "clientID", new PyList([]));
        await announcer.AnnounceLearningChangedAsync(characterId, update);
    }
}
