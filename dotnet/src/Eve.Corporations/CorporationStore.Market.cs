using Npgsql;

namespace Eve.Corporations;

public sealed partial class CorporationStore
{
    /// <summary>
    /// The characters of a player corporation who hold the market's corporate rights at a station (Accountant or Trader: the client's
    /// session.corprole test for corporation orders, buyThisTypeWindow.py:324, marketOrdersWnd.py:40-41). Read under the caller's transaction.
    /// </summary>
    public async Task<IReadOnlyList<long>> MarketRightsHoldersAsync(NpgsqlConnection connection,NpgsqlTransaction? tx,long corporationId,long stationId,CancellationToken token = default)
    {
        Corporation? corporation;
        await using (var command = Command(connection,tx,$"SELECT {CorpColumns} FROM {_schema}.corporations WHERE id=$1",corporationId))
        await using (var r = await command.ExecuteReaderAsync(token))
            corporation = await r.ReadAsync(token) ? ReadCorporation(r) : null;
        if (corporation is null || corporation.IsNpc || corporation.Deleted) return [];
        var titles = await TitlesAsync(connection,tx,corporation.Id,token);
        var members = new List<Member>();
        await using (var command = Command(connection,tx,$"SELECT {MemberColumns} FROM {_schema}.members WHERE corporation_id=$1 ORDER BY character_id",corporationId))
        await using (var r = await command.ExecuteReaderAsync(token))
            while (await r.ReadAsync(token)) members.Add(ReadMember(r));
        return members
            .Where(member => (CorporationRoles.Session(corporation,member,stationId,titles).AtAll & (CorporationRoles.Accountant | CorporationRoles.Trader)) != 0)
            .Select(member => member.CharacterId)
            .ToArray();
    }
}
