using System.Globalization;
using System.Text.Json.Nodes;
using Eve.Marshal;
using Eve.Host.Persistence.Structures;
using Eve.Planets;
using Eve.Services;
using Eve.Services.Planets;
using Eve.Structures;
using Npgsql;

namespace Eve.Host;

public sealed partial class StructureDeploymentAuthority
{
    // SDE sde.types.key 81080 is Orbital Skyhook.
    private const int OrbitalSkyhookTypeId = 81080;

    /// <summary>Settings retained by the structure store for an existing Skyhook.</summary>
    public async Task<OrbitalSettings?> SkyhookOrbitalSettingsAsync(long structureId, CancellationToken cancellationToken)
    {
        var record = await _store.GetAsync(structureId, cancellationToken);
        if (record is not { TypeId: OrbitalSkyhookTypeId } || record.Snapshot.DestroyedMs > 0)
            return null;

        var doc = record.Doc;
        var rates = doc["taxRates"] as JsonObject;
        var pendingHour = record.Snapshot.PendingEffectiveMs > 0 && record.Snapshot.PendingHour is >= 0 and <= 23
            ? record.Snapshot.PendingHour
            : record.Snapshot.ReinforceHour;
        return new OrbitalSettings(
            pendingHour,
            new Customs.OwnerTaxRates(
                Rate(rates, "corporation"), Rate(rates, "alliance"), Rate(rates, "standingHigh"),
                Rate(rates, "standingGood"), Rate(rates, "standingNeutral"), Rate(rates, "standingBad"),
                Rate(rates, "standingHorrible")),
            Math.Clamp((int)Integer(doc["standingLevel"], 0), -10, 10),
            Boolean(doc["allowAlliance"]),
            Boolean(doc["allowStandings"]),
            Integer(doc["aclGroupID"], 0) is var groupId && groupId > 0 ? groupId : null);
    }

    /// <summary>Writes Skyhook settings with the structure's existing owner check and reinforcement timer.</summary>
    public async Task UpdateSkyhookOrbitalSettingsAsync(
        ServiceCall call, long structureId, OrbitalSettings settings, CancellationToken cancellationToken)
    {
        var rates = settings.TaxRates;
        if (settings.ReinforceHour is < 0 or > 23 || settings.StandingLevel is < -10 or > 10 ||
            settings.AccessGroupId is { } selectedGroupId && selectedGroupId <= 0 ||
            new[] { rates.Corporation, rates.Alliance, rates.Excellent, rates.Good, rates.Neutral, rates.Bad, rates.Terrible }
                .Any(rate => !double.IsFinite(rate) || rate is < 0 or > 1))
            throw MachoErrors.ThrowUserError("CustomsOfficeNotFound");

        await using var connection = await _source.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var record = await ManagedAsync(call, connection, transaction, structureId, cancellationToken);
        if (record.TypeId != OrbitalSkyhookTypeId)
            throw MachoErrors.ThrowUserError("CustomsOfficeNotFound");

        var transition = await _timers.ApplyAsync(connection, transaction, structureId,
            new ReinforceTimeRequested(record.Snapshot.ReinforceWeekday, settings.ReinforceHour), cancellationToken);
        if (transition?.Refusal is { } refusal)
            throw Refusal($"Failed to update reinforcement timing: {refusal}.");
        if (transition is null)
            throw MachoErrors.ThrowUserError("CustomsOfficeNotFound");

        var fields = new JsonObject
        {
            ["taxRates"] = new JsonObject
            {
                ["corporation"] = rates.Corporation,
                ["alliance"] = rates.Alliance,
                ["standingHigh"] = rates.Excellent,
                ["standingGood"] = rates.Good,
                ["standingNeutral"] = rates.Neutral,
                ["standingBad"] = rates.Bad,
                ["standingHorrible"] = rates.Terrible,
            },
            ["standingLevel"] = settings.StandingLevel,
            ["allowAlliance"] = settings.AllowAlliance,
            ["allowStandings"] = settings.AllowStandings,
            ["aclGroupID"] = settings.AccessGroupId is { } groupId ? JsonValue.Create(groupId) : null,
        };
        if (await _store.SetDocFieldsAsync(connection, transaction, structureId, fields, cancellationToken) != 1)
            throw MachoErrors.ThrowUserError("CustomsOfficeAccessDenied");

        if (transition is { Changed: true } && transition.Next.PendingEffectiveMs > 0)
            await _events.PublishInTransactionAsync(connection, transaction, ReinforcementChangedNotifications.Topic,
                new StructureReinforcementChanged(record.OwnerCorporationId, record.Snapshot.ReinforceWeekday, settings.ReinforceHour,
                    transition.Next.PendingEffectiveMs,
                    [new StructureReinforcementChangedStructure(structureId, record.Name, record.TypeId)]),
                cancellationToken: cancellationToken);

        await transaction.CommitAsync(cancellationToken);
        _scheduler.Wake();
    }

    private static double Rate(JsonObject? rates, string name)
    {
        var value = rates?[name];
        return double.TryParse(value?.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var rate) && double.IsFinite(rate)
            ? Math.Clamp(rate, 0, 1)
            : 0.05;
    }

    private static long Integer(JsonNode? node, long fallback)
    {
        var text = node?.ToString();
        if (long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
            return value;
        return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) &&
            double.IsFinite(number) && number > long.MinValue && number < long.MaxValue
                ? (long)Math.Truncate(number)
                : fallback;
    }

    private static bool Boolean(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<bool>(out var flag) && flag;
}
