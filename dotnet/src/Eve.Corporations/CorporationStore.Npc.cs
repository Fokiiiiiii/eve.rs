using System.Text.Json;
using Eve.Characters;
using Eve.Sde;

namespace Eve.Corporations;

public sealed partial class CorporationStore
{
    /// <summary>Static NPC corporations are loaded once at host startup; player corporations are never overwritten.</summary>
    public async Task SeedNpcAsync(SdeReader sde, CancellationToken token = default)
    {
        var rows = await sde.ReadAsync("npcCorporations", cancellationToken: token);
        await WriteAsync(async (connection, tx) =>
        {
            foreach (var row in rows)
            {
                var doc = row.Doc;
                long Number(string key) => doc.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt64() : 0;
                var name = SdeReader.English(doc.GetProperty("name"));
                var ticker = doc.TryGetProperty("tickerName", out var t) ? t.GetString() ?? "" : "";
                if (ticker.Length == 0) ticker = new string(name.ToUpperInvariant().Where(c => c is >= 'A' and <= 'Z' or >= '0' and <= '9').Take(5).ToArray());
                var settings = JsonSerializer.Deserialize<Dictionary<string,JsonElement>>(doc.GetRawText())!;
                settings["applicationsEnabled"] = JsonSerializer.SerializeToElement(0);
                await using (var owner = Command(connection, tx, $"""
                    INSERT INTO {_schema}.owner_names(owner_id,name_key,ticker_key,is_npc) VALUES($1,$2,$3,true)
                    ON CONFLICT(owner_id) DO UPDATE SET name_key=$2,ticker_key=$3 WHERE owner_names.is_npc
                    """,row.Key,Key(name),Key(ticker)))
                    await owner.ExecuteNonQueryAsync(token);
                await using var insert = Command(connection,tx,$"""
                    INSERT INTO {_schema}.corporations(id,name,ticker,ceo_id,creator_id,station_id,solar_system_id,tax_rate,lp_tax_rate,
                        description,url,member_limit,is_npc,created_ms,settings)
                    VALUES($1,$2,$3,$4,$5,$6,$7,$8,0,'','',-1,true,$9,$10::jsonb)
                    ON CONFLICT(id) DO UPDATE SET name=$2,ticker=$3,ceo_id=$4,creator_id=$5,station_id=$6,solar_system_id=$7,tax_rate=$8,settings=$10::jsonb
                    WHERE corporations.is_npc
                    """,row.Key,name,ticker,Number("ceoID"),Number("creatorID"),Number("stationID"),(int)Number("solarSystemID"),
                    doc.TryGetProperty("taxRate",out var tax) ? tax.GetDecimal() : 0m,_now(),JsonSerializer.Serialize(settings));
                await insert.ExecuteNonQueryAsync(token);
            }
            return true;
        },token);
    }

    private static Member NpcMember(Character c) => new(c.Id,c.CorporationId,c.CreatedAt.ToUnixTimeMilliseconds(),"",new RoleMasks(),null,0,false,1000);
    internal static long? Faction(Corporation c) =>
        c.Settings.TryGetProperty("factionID",out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt64() : null;
}
