using System.Net;
using System.Net.Sockets;
using Eve.Characters;
using Eve.Data;
using Eve.Host;
using Eve.Identity;
using Eve.Dogma;
using Eve.Fitting;
using Eve.GameStore;
using System.Text.Json;
using System.Text.Json.Nodes;
using Eve.Inventory;
using Eve.Ships;
using Eve.Marshal;
using Eve.Npc;
using Eve.Sde;
using Eve.Skills;
using Eve.Universe;
using Eve.Wallet;
using Npgsql;
using Sentry;
using Eve.Services;
using Eve.Services.CharacterSelect;
using Eve.Services.Core;
using Eve.Services.Dogma;
using Eve.Services.Inventory;
using Eve.Services.CharacterInfo;
using Eve.Services.Skills;
using Eve.Services.Slash;
using Eve.Services.Space;
using Eve.Space;
using Eve.Services.Station;
using Eve.Services.Wallet;
using static Eve.Host.HostConfiguration;

namespace Eve.Host;

internal sealed partial class CharacterServicesComposition
{
    async Task<IReadOnlyList<Eve.Services.Core.SkyhookResourceSnapshot>> ReadPersistedSkyhooksAsync(CancellationToken cancellationToken)
    {
        var state = await mailStore.LoadTableAsync("sovereignty", createIfMissing: false, cancellationToken);
        var rows = state["skyhooks"] as JsonObject;
        if (rows is null)
            return Array.Empty<Eve.Services.Core.SkyhookResourceSnapshot>();

        static long Number(JsonNode? value, long fallback = 0) =>
            long.TryParse(value?.ToString(), System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out var number) ? number : fallback;
        static bool Flag(JsonNode? value) =>
            bool.TryParse(value?.ToString(), out var flag) ? flag : Number(value) != 0;

        var result = new List<Eve.Services.Core.SkyhookResourceSnapshot>();
        foreach (var (key, node) in rows)
        {
            if (node is not JsonObject row)
                continue;
            var skyhookId = Number(row["skyhookID"], Number(row["itemID"],
                long.TryParse(key, System.Globalization.NumberStyles.Integer,
                    System.Globalization.CultureInfo.InvariantCulture, out var keyedId) ? keyedId : 0));
            var typeId = Number(row["typeID"]);
            var systemId = Number(row["solarSystemID"]);
            var planetId = Number(row["planetID"]);
            var corporationId = Number(row["corporationID"], Number(row["ownerCorpID"]));
            if (skyhookId <= 0 || typeId != 81080 || systemId <= 0 || planetId <= 0 || corporationId <= 0 ||
                Flag(row["destroyed"]))
                continue;

            var reagents = (row["reagents"] as JsonArray ?? [])
                .OfType<JsonObject>()
                .Select(reagent => new Eve.Services.Core.SkyhookReagentResource(
                    Number(reagent["typeID"]),
                    Math.Max(0, Number(reagent["secureStock"])),
                    Math.Max(0, Number(reagent["insecureStock"]))))
                .Where(reagent => reagent.TypeId > 0)
                .ToArray();
            Eve.Services.Core.SkyhookTheftVulnerability? vulnerability = null;
            if (row["theftVulnerability"] is JsonObject theft)
            {
                vulnerability = new Eve.Services.Core.SkyhookTheftVulnerability(
                    Flag(theft["vulnerable"]),
                    Number(theft["startUnixSeconds"]),
                    Number(theft["endUnixSeconds"]));
            }
            int? skyhookState = int.TryParse(row["state"]?.ToString(),
                System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var parsedState)
                ? parsedState : null;
            var allianceId = Number(row["allianceID"]);

            result.Add(new Eve.Services.Core.SkyhookResourceSnapshot(
                skyhookId,
                typeId,
                systemId,
                planetId,
                corporationId,
                allianceId > 0 ? allianceId : null,
                Flag(row["productionActive"]),
                row["workforce"] is null ? null : Number(row["workforce"]),
                row["resourceVersion"]?.ToString() ?? "",
                reagents,
                vulnerability,
                skyhookState));
        }
        return result;
    }

    async ValueTask<Eve.Services.Core.SkyhookResourceSnapshot?> ReadSkyhookAsync(long skyhookId, CancellationToken cancellationToken) =>
        (await ReadPersistedSkyhooksAsync(cancellationToken)).FirstOrDefault(skyhook => skyhook.SkyhookId == skyhookId);

    async ValueTask<IReadOnlyList<Eve.Services.Core.SkyhookResourceSnapshot>> ReadCorporationSkyhooksAsync(
        long corporationId, CancellationToken cancellationToken) =>
        (await ReadPersistedSkyhooksAsync(cancellationToken))
            .Where(skyhook => skyhook.CorporationId == corporationId)
            .ToArray();

    async ValueTask<bool> AuthorizeSkyhookAsync(
        Eve.Services.ServiceCall call,
        Eve.Services.Core.SkyhookResourceSnapshot skyhook,
        Eve.Services.Core.SkyhookResourcePermission permission,
        CancellationToken cancellationToken)
    {
        if (call.Session.CharacterId is not { } characterId || characterId <= 0 ||
            (call.Session.GetInt("corpid") ?? call.Session.GetInt("corporationID")) != skyhook.CorporationId)
            return false;

        var affiliation = await AffiliationOfAsync(characterId, cancellationToken);
        if (affiliation.Corporation != skyhook.CorporationId ||
            await corporations.MemberAsync(characterId, cancellationToken) is not { } member ||
            member.CorporationId != skyhook.CorporationId)
            return false;

        if (permission == Eve.Services.Core.SkyhookResourcePermission.Read)
            return true;
        if (permission is not (Eve.Services.Core.SkyhookResourcePermission.Activate or Eve.Services.Core.SkyhookResourcePermission.Deactivate))
            return false;

        var corporation = await corporations.GetAsync(skyhook.CorporationId, cancellationToken);
        if (corporation is null || corporation.Deleted)
            return false;
        return corporation.CeoId == characterId && !corporation.CeoRolesSuspended ||
            (member.Masks.Roles & Eve.Corporations.CorporationRoles.Director) != 0;
    }

    async ValueTask<bool> CanManageSovHubAsync(
        Eve.Services.ServiceCall call,
        long solarSystemId,
        Eve.Services.Core.SovHubRecord hub)
    {
        if (solarSystemId <= 0 || hub.HubId is not > 0 || hub.CorporationId is not { } corporationId ||
            call.Session.CharacterId is not { } characterId || characterId <= 0 ||
            (call.Session.GetInt("corpid") ?? call.Session.GetInt("corporationID")) != corporationId)
            return false;

        var affiliation = await AffiliationOfAsync(characterId, call.Cancellation);
        if (affiliation.Corporation != corporationId ||
            await corporations.MemberAsync(characterId, call.Cancellation) is not { } member ||
            member.CorporationId != corporationId)
            return false;

        var corporation = await corporations.GetAsync(corporationId, call.Cancellation);
        return corporation is { Deleted: false } &&
            (corporation.CeoId == characterId && !corporation.CeoRolesSuspended ||
                (member.Masks.Roles & Eve.Corporations.CorporationRoles.Director) != 0);
    }

    async ValueTask<bool> ChargeSovSkyhookAcquisitionAsync(
        IReadOnlyList<SovSkyhookAcquisitionCharge> charges,
        CancellationToken cancellationToken)
    {
        if (charges.Count == 0)
            return true;

        await using var connection = await source.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        foreach (var charge in charges)
        {
            if (charge.CorporationId <= 0 || charge.AmountIsk <= 0 ||
                decimal.Round(charge.AmountIsk, 2) != charge.AmountIsk ||
                charge.ReferenceTypeId <= 0 || string.IsNullOrWhiteSpace(charge.OperationKey))
                return false;

            await economyWallets.AdjustAsync(
                connection,
                transaction,
                charge.CorporationId,
                charge.IsRefund ? charge.AmountIsk : -charge.AmountIsk,
                0,
                charge.ReferenceTypeId,
                charge.IsRefund ? "Skyhook acquisition refund" : "Skyhook acquisition",
                now(),
                charge.OperationKey,
                cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    async ValueTask<bool> HasSovSkyhookWalletOperationAsync(
        SovSkyhookAcquisitionCharge charge,
        CancellationToken cancellationToken)
    {
        await using var connection = await source.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            $"SELECT EXISTS (SELECT 1 FROM {walletSchema}.journal WHERE owner_id = $1 AND account_key = 1000 AND operation_key = $2)",
            connection);
        command.Parameters.AddWithValue(charge.CorporationId);
        command.Parameters.AddWithValue(charge.OperationKey);
        return await command.ExecuteScalarAsync(cancellationToken) is true;
    }

    private Task SovereigntyWriteOperations()
    {
        sovereigntyWriteGate = new SemaphoreSlim(1, 1);

        sovWriteOperations = new PersistenceSovFwWriteOperations(
            table => mailCache.Read(table).Data as JsonObject ?? new JsonObject(),
            (table, path, data) => mailCache.Write(table, path, data),
            (table, token) => mailCache.FlushTableAsync(table, token),
            (table, token) => mailStore.LoadTableAsync(table, createIfMissing: false, token),
            CanManageSovHubAsync,
            sovereigntyWriteGate,
            ChargeSovSkyhookAcquisitionAsync,
            HasSovSkyhookWalletOperationAsync);
        if (allianceMaintenanceBillWorker is { } maintenance)
            maintenance.DropSovereigntyClaimsAsync = sovWriteOperations.DropAllianceSovereigntyClaimsAsync;
        if (allianceSovereigntyTransferWorker is { } transfer)
            transfer.TransferSovereigntyAsync = sovWriteOperations.TransferAllianceSovereigntyOwnershipAsync;
        return Task.CompletedTask;
    }

    private async Task SkyhookJobs()
    {
        await sovWriteOperations.ReplayPendingSkyhookAcquisitionsAsync(CancellationToken.None);
        foundations.Scheduler.Register(
            "sovereignty.skyhook-acquisition.reconcile",
            async (_, token) => await sovWriteOperations.ReplayPendingSkyhookAcquisitionsAsync(token));
        await foundations.Scheduler.EnsureRecurringAsync(
            "sovereignty.skyhook-acquisition.reconcile", "pending", TimeSpan.FromMinutes(1));
        skyhookActivationWriter = new PersistenceSkyhookActivationWriter(
            table => mailCache.Read(table).Data as JsonObject ?? new JsonObject(),
            (table, path, data) => mailCache.Write(table, path, data),
            (table, token) => mailCache.FlushTableAsync(table, token),
            ReadSkyhookAsync,
            AuthorizeSkyhookAsync,
            sovereigntyWriteGate);
        skyhookResourceService = new Eve.Services.Core.SkyhookResourceService(
            ReadSkyhookAsync,
            ReadCorporationSkyhooksAsync,
            AuthorizeSkyhookAsync,
            skyhookActivationWriter.PersistAsync);
        skyhookResourceMgrService = new Eve.Services.Core.SkyhookResourceMgrService(skyhookResourceService);
    }
}
