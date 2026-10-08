namespace Eve.Corporations;

public sealed partial class CorporationStore
{
    public Task<bool> SetFactionWarfareAllowedEnlistmentFactionsAsync(
        long actor,
        long corporation,
        IReadOnlyList<long> factionIds,
        CancellationToken token = default) =>
        WriteAsync(async (connection, transaction) =>
        {
            await RequireRoleAsync(connection, transaction, corporation, actor, CorporationRoles.Director, token);
            await using var update = Command(
                connection,
                transaction,
                $"UPDATE {_schema}.corporations SET settings=jsonb_set(COALESCE(settings,'{{}}'::jsonb), '{{fw}}', COALESCE(settings->'fw','{{}}'::jsonb) || jsonb_build_object('allowedEnlistmentFactions',$2::jsonb), true) WHERE id=$1",
                corporation,
                System.Text.Json.JsonSerializer.Serialize(factionIds));
            return await update.ExecuteNonQueryAsync(token) > 0;
        }, token);
}
