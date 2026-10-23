using System.Text.Json;
using Eve.Planets;
using Npgsql;

namespace Eve.Services.Planets;

public sealed partial class PlanetMgrService
{
    private sealed record CustomsPolicy(long OwnerCorporationId, bool AllowAlliance, bool AllowStandings, int StandingLevel, Customs.OwnerTaxRates Rates);

    private static readonly JsonSerializerOptions CustomsJson = new(JsonSerializerDefaults.Web);

    private async Task<long?> OfficeOwnerAsync(
        NpgsqlConnection connection, NpgsqlTransaction? transaction, long officeId, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            $"SELECT owner_id FROM {inventorySchema}.items WHERE id = $1 AND type_id = $2", connection, transaction)
        {
            Parameters = { new() { Value = officeId }, new() { Value = Customs.OfficeTypeId } },
        };
        var value = await command.ExecuteScalarAsync(cancellationToken);
        return value is long owner ? owner : null;
    }

    private async Task<CustomsPolicy> CustomsPolicyAsync(
        NpgsqlConnection connection, NpgsqlTransaction? transaction, long officeId, long ownerCorporationId, CancellationToken cancellationToken)
    {
        await using var insert = new NpgsqlCommand($"""
            INSERT INTO {store.Schema}.customs_offices (office_id, owner_corporation_id)
            VALUES ($1, $2) ON CONFLICT (office_id) DO NOTHING
            """, connection, transaction)
        {
            Parameters = { new() { Value = officeId }, new() { Value = ownerCorporationId } },
        };
        await insert.ExecuteNonQueryAsync(cancellationToken);

        await using var read = new NpgsqlCommand($"""
            SELECT owner_corporation_id, allow_alliance, allow_standings, standing_level, tax_rates::text
            FROM {store.Schema}.customs_offices WHERE office_id = $1
            """, connection, transaction)
        {
            Parameters = { new() { Value = officeId } },
        };
        await using var reader = await read.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            throw new InvalidOperationException($"customs office policy {officeId} was not stored");
        var rates = ReadOwnerRates(reader.GetString(4));
        return new CustomsPolicy(reader.GetInt64(0), reader.GetBoolean(1), reader.GetBoolean(2), reader.GetInt32(3), rates);
    }

    private static Customs.OwnerTaxRates ReadOwnerRates(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        double Rate(string clientName, string currentName) =>
            root.TryGetProperty(clientName, out var value) && value.TryGetDouble(out var rate) && double.IsFinite(rate)
                ? Math.Clamp(rate, 0, 1)
                : root.TryGetProperty(currentName, out value) && value.TryGetDouble(out rate) && double.IsFinite(rate)
                    ? Math.Clamp(rate, 0, 1)
                    : 0.05;
        return new Customs.OwnerTaxRates(
            Rate("corporation", "corporation"), Rate("alliance", "alliance"), Rate("standingHigh", "excellent"),
            Rate("standingGood", "good"), Rate("standingNeutral", "neutral"), Rate("standingBad", "bad"),
            Rate("standingHorrible", "terrible"));
    }

    private static string WriteOwnerRates(Customs.OwnerTaxRates rates) => JsonSerializer.Serialize(new Dictionary<string, double>
    {
        ["corporation"] = rates.Corporation,
        ["alliance"] = rates.Alliance,
        ["standingHigh"] = rates.Excellent,
        ["standingGood"] = rates.Good,
        ["standingNeutral"] = rates.Neutral,
        ["standingBad"] = rates.Bad,
        ["standingHorrible"] = rates.Terrible,
    });

    public async Task UpdateOfficeSettingsAsync(
        ClientSession session, long officeId, Customs.OwnerTaxRates rates, int standingLevel,
        bool allowAlliance, bool allowStandings, CancellationToken cancellationToken)
    {
        if (Customs.PlanetOf(officeId) is not { } planetId || standingLevel is < -10 or > 10)
            throw MachoErrors.ThrowUserError("CustomsOfficeNotFound");
        var corporationId = session.GetInt("corpid") ?? 0;
        if (corporationId <= 0 || canManageCustomsOffice is null ||
            !await canManageCustomsOffice(session.CharacterId ?? 0, corporationId, cancellationToken))
            throw MachoErrors.ThrowUserError("CustomsOfficeAccessDenied");
        if (new[] { rates.Corporation, rates.Alliance, rates.Excellent, rates.Good, rates.Neutral, rates.Bad, rates.Terrible }
            .Any(rate => !double.IsFinite(rate) || rate is < 0 or > 1))
            throw MachoErrors.ThrowUserError("CustomsOfficeAccessDenied");

        var body = await BodyAsync(planetId, cancellationToken);
        await using var connection = await source.OpenConnectionAsync(cancellationToken);
        await EnsureOfficeAsync(connection, null, planetId, body.SolarSystemId, cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var office = await items.LockAndGetAsync(connection, transaction, officeId, cancellationToken);
        if (office is not { TypeId: Customs.OfficeTypeId } || office.OwnerId != corporationId)
            throw MachoErrors.ThrowUserError("CustomsOfficeAccessDenied");
        await CustomsPolicyAsync(connection, transaction, officeId, office.OwnerId, cancellationToken);
        await using var update = new NpgsqlCommand($"""
            UPDATE {store.Schema}.customs_offices
            SET allow_alliance = $3, allow_standings = $4, standing_level = $5, tax_rates = $6::jsonb, updated_ms = $7
            WHERE office_id = $1 AND owner_corporation_id = $2
            """, connection, transaction)
        {
            Parameters =
            {
                new() { Value = officeId }, new() { Value = corporationId }, new() { Value = allowAlliance },
                new() { Value = allowStandings }, new() { Value = standingLevel }, new() { Value = WriteOwnerRates(rates) },
                new() { Value = nowMs() },
            },
        };
        if (await update.ExecuteNonQueryAsync(cancellationToken) != 1)
            throw MachoErrors.ThrowUserError("CustomsOfficeAccessDenied");
        await transaction.CommitAsync(cancellationToken);
    }

    private async Task<double?> OwnerTaxRateAsync(
        NpgsqlConnection connection, NpgsqlTransaction? transaction, CustomsPolicy policy, long characterId, CancellationToken cancellationToken)
    {
        if (policy.OwnerCorporationId == 1000125)
            return 0;

        long visitorCorporationId;
        long visitorAllianceId;
        await using (var visitor = new NpgsqlCommand(
            $"SELECT COALESCE(corporation_id, 0), COALESCE(alliance_id, 0) FROM {Environment.GetEnvironmentVariable("EVE_CHARACTERS_SCHEMA") ?? "characters"}.characters WHERE id = $1",
            connection, transaction)
        {
            Parameters = { new() { Value = characterId } },
        })
        await using (var reader = await visitor.ExecuteReaderAsync(cancellationToken))
        {
            if (!await reader.ReadAsync(cancellationToken))
                return null;
            visitorCorporationId = reader.GetInt64(0);
            visitorAllianceId = reader.GetInt64(1);
        }

        if (visitorCorporationId == policy.OwnerCorporationId)
            return policy.Rates.Corporation;

        long ownerAllianceId = 0;
        await using (var owner = new NpgsqlCommand(
            $"SELECT COALESCE(alliance_id, 0) FROM {Environment.GetEnvironmentVariable("EVE_CORPORATIONS_SCHEMA") ?? "corporations"}.corporations WHERE id = $1",
            connection, transaction)
        {
            Parameters = { new() { Value = policy.OwnerCorporationId } },
        })
        {
            ownerAllianceId = Convert.ToInt64(await owner.ExecuteScalarAsync(cancellationToken) ?? 0L);
        }
        if (policy.AllowAlliance && ownerAllianceId > 0 && ownerAllianceId == visitorAllianceId)
            return policy.Rates.Alliance;
        if (!policy.AllowStandings)
            return null;

        var standing = 0;
        await using (var contacts = new NpgsqlCommand("""
            SELECT relationship_id FROM {Environment.GetEnvironmentVariable("EVE_CORPORATIONS_SCHEMA") ?? "corporations"}.corporation_contacts
            WHERE corporation_id = $1 AND contact_id = ANY($2::bigint[])
            ORDER BY CASE contact_id WHEN $3 THEN 1 WHEN $4 THEN 2 ELSE 3 END LIMIT 1
            """, connection, transaction)
        {
            Parameters =
            {
                new() { Value = policy.OwnerCorporationId },
                new() { Value = new[] { characterId, visitorCorporationId, visitorAllianceId }.Where(id => id > 0).ToArray() },
                new() { Value = characterId }, new() { Value = visitorCorporationId },
            },
        })
        {
            standing = Convert.ToInt32(await contacts.ExecuteScalarAsync(cancellationToken) ?? 0);
        }
        if (standing < policy.StandingLevel)
            return null;
        return policy.Rates.TierRate(standing);
    }

    private async Task CreditOfficeOwnerAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, long ownerCorporationId, long payerCharacterId,
        double fee, long now, CancellationToken cancellationToken)
    {
        var amount = Math.Round((decimal)fee, 2);
        if (ownerCorporationId <= 0 || ownerCorporationId == 1000125 || amount <= 0)
            return;
        await wallets.EnsureAsync(connection, transaction, ownerCorporationId, cancellationToken);
        await wallets.AdjustAsync(connection, transaction, ownerCorporationId, amount, payerCharacterId,
            RefPlanetaryExportTax, "planetary customs office tax", now,
            $"poco-tax:{ownerCorporationId}:{payerCharacterId}:{now}:{Guid.NewGuid():N}", cancellationToken);
    }
}
