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

public sealed class PersistenceSovFwWriteOperations(
    Func<string, JsonObject> readCacheTable,
    Func<string, string?, JsonNode?, Eve.GameStore.WriteResult> write,
    Func<string, CancellationToken, Task<int>> flushTableAsync,
    Func<string, CancellationToken, Task<JsonObject>> readPersistedTableAsync,
    Func<Eve.Services.ServiceCall, long, Eve.Services.Core.SovHubRecord, ValueTask<bool>> authorizeHubAsync,
    SemaphoreSlim writeGate,
    Func<IReadOnlyList<SovSkyhookAcquisitionCharge>, CancellationToken, ValueTask<bool>> chargeSkyhooksAsync,
    Func<SovSkyhookAcquisitionCharge, CancellationToken, ValueTask<bool>> hasWalletOperationAsync)
{
    private const decimal SkyhookBpoPriceProxyIsk = 5_500_000_000m;
    // ponytail: CCP has not published the sovereignty-capture conversion fee; the 5.5B BPO price is only a deterministic proxy when row pricing is absent.
    private const int SkyhookClaimFeeWalletReferenceTypeId = 184; // ESI wallet journal ref type skyhook_claim_fee.

    private static readonly HashSet<string> SolarSystemKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "solarSystemID", "systemID", "sourceSolarSystemID", "destinationSolarSystemID",
        "fromSolarSystemID", "toSolarSystemID", "importSolarSystemID", "exportSolarSystemID",
        "targetSolarSystemID",
    };

    public async ValueTask<bool> CanManageHubAsync(
        Eve.Services.ServiceCall call,
        long solarSystemId,
        Eve.Services.Core.SovHubRecord expected)
    {
        if (solarSystemId <= 0 || expected.HubId is not > 0)
            return false;

        var state = await readPersistedTableAsync("sovereignty", call.Cancellation);
        if (!TrySystem(state, solarSystemId, out var system) || !TryHub(system, out var current) ||
            current.HubId != expected.HubId ||
            current.CorporationId != expected.CorporationId ||
            current.AllianceId != expected.AllianceId ||
            current.ClaimTimeFileTime != expected.ClaimTimeFileTime)
            return false;

        return await authorizeHubAsync(call, solarSystemId, current);
    }

    public async ValueTask<Eve.Marshal.PyValue> SetFuelAccessGroupAsync(
        Eve.Services.ServiceCall call,
        long solarSystemId,
        long? groupId)
    {
        if (groupId is <= 0)
            return Eve.Marshal.PyBool.False;
        if (groupId is { } accessGroupId)
        {
            var groups = await readPersistedTableAsync("accessGroups", call.Cancellation);
            if (groups[accessGroupId.ToString(System.Globalization.CultureInfo.InvariantCulture)] is not JsonObject)
                return Eve.Marshal.PyBool.False;
        }

        var written = await MutateSystemAsync(
            call,
            solarSystemId,
            row =>
            {
                row["fuelAccessGroupID"] = groupId is { } value ? JsonValue.Create(value) : null;
                return true;
            },
            row => NullablePositive(row["fuelAccessGroupID"]) == groupId);
        return written ? Eve.Marshal.PyNone.Instance : Eve.Marshal.PyBool.False;
    }

    public async ValueTask<Eve.Marshal.PyValue> SaveUpgradeConfigurationAsync(
        Eve.Services.ServiceCall call,
        long hubId,
        IReadOnlyList<long> itemIds,
        IReadOnlyList<Eve.Services.Core.SovHubUpgradeState> states)
    {
        if (hubId <= 0 || itemIds.Count != states.Count ||
            itemIds.Any(id => id <= 0) || itemIds.Distinct().Count() != itemIds.Count ||
            states.Any(state => state.TypeId <= 0) ||
            states.Select(state => state.TypeId).Distinct().Count() != states.Count)
            return Eve.Marshal.PyBool.False;

        var snapshot = await readPersistedTableAsync("sovereignty", call.Cancellation);
        var systemId = FindHubSystemId(snapshot, hubId);
        if (systemId <= 0 || !TrySystem(snapshot, systemId, out var current) || !TryHub(current, out var hub))
            return Eve.Marshal.PyBool.False;

        var upgrades = new JsonArray();
        for (var index = 0; index < states.Count; index++)
            upgrades.Add(new JsonObject
            {
                ["itemID"] = itemIds[index],
                ["typeID"] = states[index].TypeId,
                ["online"] = states[index].Online,
                ["priority"] = index,
            });

        var written = await MutateSystemAsync(
            call,
            systemId,
            row =>
            {
                row["hubUpgradeConfiguration"] = upgrades.DeepClone();
                return true;
            },
            row => JsonNode.DeepEquals(row["hubUpgradeConfiguration"], upgrades),
            hub.HubId);
        return written ? Eve.Marshal.PyBool.True : Eve.Marshal.PyBool.False;
    }

    public async ValueTask<Eve.Marshal.PyValue> SelfDestructUpgradeAsync(
        Eve.Services.ServiceCall call,
        long hubId,
        long typeId)
    {
        if (hubId <= 0 || typeId <= 0)
            return Eve.Marshal.PyBool.False;

        var snapshot = await readPersistedTableAsync("sovereignty", call.Cancellation);
        var systemId = FindHubSystemId(snapshot, hubId);
        if (systemId <= 0 || !TrySystem(snapshot, systemId, out var current) || !TryHub(current, out var hub))
            return Eve.Marshal.PyBool.False;

        var removed = await MutateSystemAsync(
            call,
            systemId,
            row =>
            {
                if (row["hubUpgradeConfiguration"] is not JsonArray configured)
                    return false;
                var remaining = new JsonArray();
                var found = false;
                foreach (var node in configured)
                {
                    if (node is JsonObject upgrade && Positive(Integer(upgrade["typeID"])) == typeId)
                    {
                        found = true;
                        continue;
                    }
                    remaining.Add(node?.DeepClone());
                }
                if (!found)
                    return false;
                row["hubUpgradeConfiguration"] = remaining;
                return true;
            },
            row => row["hubUpgradeConfiguration"] is JsonArray values &&
                !values.OfType<JsonObject>().Any(upgrade => Positive(Integer(upgrade["typeID"])) == typeId),
            hub.HubId);
        return removed ? Eve.Marshal.PyBool.True : Eve.Marshal.PyBool.False;
    }

    public async ValueTask<Eve.Marshal.PyValue> SaveWorkforceConfigurationAsync(
        Eve.Services.ServiceCall call,
        long hubId,
        Eve.Marshal.PyValue configuration)
    {
        if (hubId <= 0 || configuration is Eve.Marshal.PyNone)
            return Eve.Marshal.PyBool.False;

        var snapshot = await readPersistedTableAsync("sovereignty", call.Cancellation);
        var systemId = FindHubSystemId(snapshot, hubId);
        if (systemId <= 0 || !TrySystem(snapshot, systemId, out var current) || !TryHub(current, out var hub) ||
            hub.AllianceId is not > 0)
            return Eve.Marshal.PyBool.False;

        var payload = ToJson(configuration);
        if (payload is null || !TargetsSameAlliance(payload, snapshot["systems"] as JsonObject, hub.AllianceId.Value))
            return Eve.Marshal.PyBool.False;

        var written = await MutateSystemAsync(
            call,
            systemId,
            row =>
            {
                row["workforceConfiguration"] = payload.DeepClone();
                return true;
            },
            row => JsonNode.DeepEquals(row["workforceConfiguration"], payload),
            hub.HubId);
        return written ? Eve.Marshal.PyBool.True : Eve.Marshal.PyBool.False;
    }

    public async ValueTask<IReadOnlyList<long>> DestroySkyhooksAsync(
        Eve.Services.ServiceCall call,
        long solarSystemId,
        Eve.Services.Core.SovHubRecord hub,
        IReadOnlyList<long> skyhookIds)
    {
        if (solarSystemId <= 0 || hub.HubId is not > 0 || hub.CorporationId is not > 0)
            return Array.Empty<long>();

        await writeGate.WaitAsync(call.Cancellation);
        try
        {
            if (!await CanManageHubAsync(call, solarSystemId, hub))
                return Array.Empty<long>();

            var root = readCacheTable("sovereignty");
            if (root["skyhooks"] is not JsonObject rows)
                return Array.Empty<long>();

            var changed = new HashSet<long>();
            foreach (var skyhookId in skyhookIds.Distinct())
            {
                if (skyhookId <= 0)
                    continue;
                var key = skyhookId.ToString(System.Globalization.CultureInfo.InvariantCulture);
                if (rows[key] is not JsonObject row ||
                    Positive(Integer(row["typeID"])) != 81080 ||
                    Positive(Integer(row["solarSystemID"])) != solarSystemId)
                    continue;
                if (Flag(row["destroyed"]))
                {
                    changed.Add(skyhookId);
                    continue;
                }

                var updated = (JsonObject)row.DeepClone();
                updated["destroyed"] = true;
                updated["productionActive"] = false;
                updated["resourceVersion"] = Guid.NewGuid().ToString("N");
                updated["destroyedAtUnixMs"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                var result = write("sovereignty", $"/skyhooks/{key}", updated);
                if (!result.Success)
                    throw new InvalidOperationException($"Could not persist Skyhook destruction for {skyhookId}: {result.ErrorMsg}");
                changed.Add(skyhookId);
            }

            if (changed.Count == 0)
                return Array.Empty<long>();

            await flushTableAsync("sovereignty", CancellationToken.None);
            var persisted = await readPersistedTableAsync("sovereignty", CancellationToken.None);
            var durableRows = persisted["skyhooks"] as JsonObject;
            return changed.Where(id =>
            {
                var key = id.ToString(System.Globalization.CultureInfo.InvariantCulture);
                return durableRows?[key] is JsonObject row && Flag(row["destroyed"]) &&
                    !Flag(row["productionActive"]);
            }).Order().ToArray();
        }
        finally
        {
            writeGate.Release();
        }
    }

    public async ValueTask<IReadOnlyList<long>> AcquireSkyhooksAsync(
        Eve.Services.ServiceCall call,
        long solarSystemId,
        Eve.Services.Core.SovHubRecord hub,
        IReadOnlyList<long> skyhookIds,
        long? accessListId)
    {
        if (solarSystemId <= 0 || hub.HubId is not > 0 || hub.CorporationId is not > 0 ||
            hub.AllianceId is not > 0 || accessListId is <= 0)
            return Array.Empty<long>();

        if (accessListId is { } groupId)
        {
            var groups = await readPersistedTableAsync("accessGroups", call.Cancellation);
            if (groups[groupId.ToString(System.Globalization.CultureInfo.InvariantCulture)] is not JsonObject)
                return Array.Empty<long>();
        }

        await writeGate.WaitAsync(call.Cancellation);
        try
        {
            if (!await CanManageHubAsync(call, solarSystemId, hub))
                return Array.Empty<long>();
            var claimTime = hub.ClaimTimeFileTime ?? 0;
            var nowFileTime = DateTime.UtcNow.ToFileTimeUtc();
            // CCP's Legion major-update notes extended the Skyhook management window to 24 hours.
            if (claimTime <= 0 || nowFileTime < claimTime ||
                nowFileTime - claimTime > TimeSpan.TicksPerDay)
                return Array.Empty<long>();

            var requested = skyhookIds.Where(id => id > 0).Distinct().Order().ToArray();
            if (requested.Length == 0)
                return Array.Empty<long>();

            var fingerprintSource = $"{hub.CorporationId}:{hub.AllianceId}:{hub.HubId}:{solarSystemId}:{accessListId?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "none"}:{string.Join(",", requested)}";
            var fingerprint = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(fingerprintSource))).ToLowerInvariant();
            var receiptKey = "sov-skyhook-acquire-" + fingerprint;
            var receiptPath = $"/skyhookAcquisitionSettlements/{receiptKey}";
            var durableRoot = await readPersistedTableAsync("sovereignty", call.Cancellation);
            var receipt = (durableRoot["skyhookAcquisitionSettlements"] as JsonObject)?[receiptKey] as JsonObject;

            if (receipt is null)
            {
                var cachedRoot = readCacheTable("sovereignty");
                var cachedReceipt = (cachedRoot["skyhookAcquisitionSettlements"] as JsonObject)?[receiptKey] as JsonObject;
                if (cachedReceipt is not null)
                {
                    var cachedWrite = write("sovereignty", receiptPath, cachedReceipt);
                    if (!cachedWrite.Success)
                        throw new InvalidOperationException($"Could not restage Skyhook acquisition receipt {receiptKey}: {cachedWrite.ErrorMsg}");
                    await flushTableAsync("sovereignty", CancellationToken.None);
                    durableRoot = await readPersistedTableAsync("sovereignty", CancellationToken.None);
                    receipt = (durableRoot["skyhookAcquisitionSettlements"] as JsonObject)?[receiptKey] as JsonObject;
                }
            }

            if (receipt is not null &&
                (!string.Equals(receipt["requestFingerprint"]?.ToString(), fingerprint, StringComparison.Ordinal) ||
                 Positive(Integer(receipt["corporationID"])) != hub.CorporationId ||
                 Positive(Integer(receipt["allianceID"])) != hub.AllianceId ||
                 Positive(Integer(receipt["solarSystemID"])) != solarSystemId ||
                 Positive(Integer(receipt["hubID"])) != hub.HubId ||
                 NullablePositive(receipt["accessListID"]) != accessListId))
                return Array.Empty<long>();

            if (receipt is null)
            {
                if (!TrySystem(durableRoot, solarSystemId, out var system) ||
                    Positive(Integer(system["infrastructureHubID"])) != hub.HubId ||
                    Positive(Integer(system["corporationID"])) != hub.CorporationId ||
                    Positive(Integer(system["allianceID"])) != hub.AllianceId ||
                    durableRoot["skyhooks"] is not JsonObject rows)
                    return Array.Empty<long>();

                var processedIds = new HashSet<long>();
                var expectedRows = new JsonObject();
                var desiredRows = new JsonObject();
                var charges = new List<SovSkyhookAcquisitionCharge>();
                foreach (var skyhookId in requested)
                {
                    var key = skyhookId.ToString(System.Globalization.CultureInfo.InvariantCulture);
                    if (rows[key] is not JsonObject row ||
                        Positive(Integer(row["typeID"])) != 81080 ||
                        Positive(Integer(row["solarSystemID"])) != solarSystemId ||
                        Positive(Integer(row["planetID"])) <= 0 ||
                        Flag(row["destroyed"]))
                        continue;

                    var owner = Positive(Integer(row["corporationID"] ?? row["ownerCorpID"]));
                    var planetId = Positive(Integer(row["planetID"]));
                    var anotherSkyhook = rows.Any(pair => pair.Key != key && pair.Value is JsonObject other &&
                        Positive(Integer(other["solarSystemID"])) == solarSystemId &&
                        Positive(Integer(other["planetID"])) == planetId &&
                        !Flag(other["destroyed"]));
                    if (anotherSkyhook)
                        continue;

                    var accessMatches = NullablePositive(row["accessListID"]) == accessListId;
                    if (owner == hub.CorporationId && Positive(Integer(row["allianceID"])) == hub.AllianceId && accessMatches)
                    {
                        processedIds.Add(skyhookId);
                        continue;
                    }

                    expectedRows[key] = row.DeepClone();
                    var updated = (JsonObject)row.DeepClone();
                    updated["corporationID"] = hub.CorporationId.Value;
                    updated["ownerCorpID"] = hub.CorporationId.Value;
                    updated["allianceID"] = hub.AllianceId.Value;
                    updated["accessListID"] = accessListId is { } access ? JsonValue.Create(access) : null;
                    if (owner != hub.CorporationId)
                    {
                        decimal price;
                        if (row["acquisitionPriceISK"] is null)
                            price = SkyhookBpoPriceProxyIsk;
                        else if (!decimal.TryParse(row["acquisitionPriceISK"]?.ToString(),
                                     System.Globalization.NumberStyles.Number,
                                     System.Globalization.CultureInfo.InvariantCulture, out price) ||
                                 price <= 0 || decimal.Round(price, 2) != price)
                            continue;

                        int refTypeId;
                        if (row["acquisitionWalletRefTypeID"] is null)
                            refTypeId = SkyhookClaimFeeWalletReferenceTypeId;
                        else if (!int.TryParse(row["acquisitionWalletRefTypeID"]?.ToString(),
                                     System.Globalization.NumberStyles.Integer,
                                     System.Globalization.CultureInfo.InvariantCulture, out refTypeId) ||
                                 refTypeId <= 0)
                            continue;

                        var resourceVersion = row["resourceVersion"]?.ToString() ?? "initial";
                        var operationIdentity = $"{hub.CorporationId}:{skyhookId}:{resourceVersion}";
                        var operationKey = "sov-skyhook-acquire:" +
                            Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                                System.Text.Encoding.UTF8.GetBytes(operationIdentity))).ToLowerInvariant();
                        charges.Add(new(hub.CorporationId.Value, price, refTypeId, operationKey));
                        updated["productionActive"] = false;
                        updated["acquisitionPaidISK"] = price;
                        updated["acquisitionWalletOperationKey"] = operationKey;
                    }

                    updated["resourceVersion"] = Guid.NewGuid().ToString("N");
                    updated["acquiredAtUnixMs"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                    desiredRows[key] = updated;
                    processedIds.Add(skyhookId);
                }

                if (processedIds.Count == 0)
                    return Array.Empty<long>();

                var chargeRows = new JsonArray();
                foreach (var charge in charges)
                    chargeRows.Add(new JsonObject
                    {
                        ["corporationID"] = charge.CorporationId,
                        ["amountISK"] = charge.AmountIsk,
                        ["referenceTypeID"] = charge.ReferenceTypeId,
                        ["operationKey"] = charge.OperationKey,
                    });
                var processedRows = new JsonArray();
                foreach (var id in processedIds.Order())
                    processedRows.Add(id);

                receipt = new JsonObject
                {
                    ["requestFingerprint"] = fingerprint,
                    ["state"] = "pending",
                    ["corporationID"] = hub.CorporationId.Value,
                    ["allianceID"] = hub.AllianceId.Value,
                    ["solarSystemID"] = solarSystemId,
                    ["hubID"] = hub.HubId.Value,
                    ["accessListID"] = accessListId is { } storedAccess ? JsonValue.Create(storedAccess) : null,
                    ["processedSkyhookIDs"] = processedRows,
                    ["expectedSkyhooks"] = expectedRows,
                    ["desiredSkyhooks"] = desiredRows,
                    ["charges"] = chargeRows,
                    ["createdAtUnixMs"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                };
                var intentWrite = write("sovereignty", receiptPath, receipt);
                if (!intentWrite.Success)
                    throw new InvalidOperationException($"Could not persist Skyhook acquisition receipt {receiptKey}: {intentWrite.ErrorMsg}");
                await flushTableAsync("sovereignty", CancellationToken.None);
                durableRoot = await readPersistedTableAsync("sovereignty", CancellationToken.None);
                receipt = (durableRoot["skyhookAcquisitionSettlements"] as JsonObject)?[receiptKey] as JsonObject;
                if (receipt is null || receipt["state"]?.ToString() != "pending" ||
                    !string.Equals(receipt["requestFingerprint"]?.ToString(), fingerprint, StringComparison.Ordinal))
                    throw new InvalidOperationException($"Skyhook acquisition receipt {receiptKey} was not durably recorded.");
            }

            var receiptState = receipt["state"]?.ToString();
            var receiptIds = (receipt["processedSkyhookIDs"] as JsonArray ?? [])
                .Select(node => Positive(Integer(node)))
                .Where(id => id > 0)
                .Distinct()
                .ToArray();
            if (receiptState == "completed")
                return await ReadCommittedAcquisitionsAsync(
                    receiptIds, solarSystemId, hub, accessListId, CancellationToken.None);
            if (receiptState != "pending")
                return Array.Empty<long>();

            var receiptCharges = new List<SovSkyhookAcquisitionCharge>();
            foreach (var node in receipt["charges"] as JsonArray ?? [])
            {
                if (node is not JsonObject charge ||
                    Positive(Integer(charge["corporationID"])) != hub.CorporationId ||
                    !decimal.TryParse(charge["amountISK"]?.ToString(),
                        System.Globalization.NumberStyles.Number,
                        System.Globalization.CultureInfo.InvariantCulture, out var amount) ||
                    amount <= 0 ||
                    !int.TryParse(charge["referenceTypeID"]?.ToString(),
                        System.Globalization.NumberStyles.Integer,
                        System.Globalization.CultureInfo.InvariantCulture, out var referenceTypeId) ||
                    referenceTypeId <= 0 ||
                    string.IsNullOrWhiteSpace(charge["operationKey"]?.ToString()))
                    throw new InvalidOperationException($"Skyhook acquisition receipt {receiptKey} contains invalid settlement data.");

                receiptCharges.Add(new(
                    hub.CorporationId.Value,
                    amount,
                    referenceTypeId,
                    charge["operationKey"]!.ToString()));
            }

            if (receiptCharges.Count > 0 && !await chargeSkyhooksAsync(receiptCharges, call.Cancellation))
                return Array.Empty<long>();

            var desired = receipt["desiredSkyhooks"] as JsonObject ?? new JsonObject();
            var cacheRows = (readCacheTable("sovereignty")["skyhooks"] as JsonObject) ??
                throw new InvalidOperationException("Skyhook table disappeared during acquisition replay.");
            foreach (var (key, node) in desired)
            {
                if (node is not JsonObject desiredRow || cacheRows[key] is not JsonObject currentRow)
                    throw new InvalidOperationException($"Skyhook acquisition receipt {receiptKey} has an invalid row payload.");
                var rowWrite = write("sovereignty", $"/skyhooks/{key}", MergeSkyhookAcquisitionRow(currentRow, desiredRow));
                if (!rowWrite.Success)
                    throw new InvalidOperationException($"Could not replay Skyhook acquisition row {key}: {rowWrite.ErrorMsg}");
            }

            await flushTableAsync("sovereignty", CancellationToken.None);
            durableRoot = await readPersistedTableAsync("sovereignty", CancellationToken.None);
            var durableRows = durableRoot["skyhooks"] as JsonObject;
            foreach (var (key, node) in desired)
                if (node is not JsonObject desiredRow ||
                    durableRows?[key] is not JsonObject savedRow ||
                    !MatchesSkyhookAcquisition(savedRow, desiredRow))
                    throw new InvalidOperationException($"Skyhook acquisition row {key} was not durably committed; receipt {receiptKey} remains pending.");

            var completedReceipt = (JsonObject)receipt.DeepClone();
            completedReceipt["state"] = "completed";
            completedReceipt["completedAtUnixMs"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var receiptWrite = write("sovereignty", receiptPath, completedReceipt);
            if (!receiptWrite.Success)
                throw new InvalidOperationException($"Could not complete Skyhook acquisition receipt {receiptKey}: {receiptWrite.ErrorMsg}");
            await flushTableAsync("sovereignty", CancellationToken.None);

            durableRoot = await readPersistedTableAsync("sovereignty", CancellationToken.None);
            var completed = (durableRoot["skyhookAcquisitionSettlements"] as JsonObject)?[receiptKey] as JsonObject;
            if (completed is null || completed["state"]?.ToString() != "completed" ||
                !string.Equals(completed["requestFingerprint"]?.ToString(), fingerprint, StringComparison.Ordinal))
                throw new InvalidOperationException($"Skyhook acquisition receipt {receiptKey} was not durably completed.");

            return await ReadCommittedAcquisitionsAsync(
                receiptIds, solarSystemId, hub, accessListId, CancellationToken.None);
        }
        finally
        {
            writeGate.Release();
        }
    }

public async Task ReplayPendingSkyhookAcquisitionsAsync(CancellationToken cancellationToken)
    {
        await writeGate.WaitAsync(cancellationToken);
        try
        {
            var initial = await readPersistedTableAsync("sovereignty", cancellationToken);
            var pendingKeys = (initial["skyhookAcquisitionSettlements"] as JsonObject)?
                .Where(pair => pair.Value is JsonObject receipt && receipt["state"]?.ToString() == "pending")
                .Select(pair => pair.Key)
                .ToArray() ?? Array.Empty<string>();

            foreach (var receiptKey in pendingKeys)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var root = await readPersistedTableAsync("sovereignty", cancellationToken);
                var receipt = (root["skyhookAcquisitionSettlements"] as JsonObject)?[receiptKey] as JsonObject;
                if (receipt is null || receipt["state"]?.ToString() != "pending")
                    continue;

                var corporationId = Positive(Integer(receipt["corporationID"]));
                var allianceId = Positive(Integer(receipt["allianceID"]));
                var systemId = Positive(Integer(receipt["solarSystemID"]));
                var hubId = Positive(Integer(receipt["hubID"]));
                var accessListId = NullablePositive(receipt["accessListID"]);
                var charges = new List<SovSkyhookAcquisitionCharge>();
                var invalidReceipt = corporationId <= 0 || allianceId <= 0 || systemId <= 0 || hubId <= 0;
                foreach (var node in receipt["charges"] as JsonArray ?? [])
                {
                    if (node is not JsonObject charge ||
                        Positive(Integer(charge["corporationID"])) != corporationId ||
                        !decimal.TryParse(charge["amountISK"]?.ToString(),
                            System.Globalization.NumberStyles.Number,
                            System.Globalization.CultureInfo.InvariantCulture, out var amount) ||
                        amount <= 0 ||
                        !int.TryParse(charge["referenceTypeID"]?.ToString(),
                            System.Globalization.NumberStyles.Integer,
                            System.Globalization.CultureInfo.InvariantCulture, out var referenceTypeId) ||
                        referenceTypeId <= 0 ||
                        string.IsNullOrWhiteSpace(charge["operationKey"]?.ToString()))
                    {
                        invalidReceipt = true;
                        break;
                    }
                    charges.Add(new(corporationId, amount, referenceTypeId, charge["operationKey"]!.ToString()));
                }

                var desiredRows = receipt["desiredSkyhooks"] as JsonObject;
                var expectedRows = receipt["expectedSkyhooks"] as JsonObject;
                if (desiredRows is null || expectedRows is null)
                    invalidReceipt = true;

                var systemIsCurrent = TrySystem(root, systemId, out var system) &&
                    Positive(Integer(system["infrastructureHubID"])) == hubId &&
                    Positive(Integer(system["corporationID"])) == corporationId &&
                    Positive(Integer(system["allianceID"])) == allianceId;
                var skyhooks = root["skyhooks"] as JsonObject;
                var rowsAreCurrent = systemIsCurrent && !invalidReceipt && skyhooks is not null;
                if (rowsAreCurrent)
                {
                    foreach (var (key, node) in desiredRows!)
                    {
                        if (node is not JsonObject desired || expectedRows![key] is not JsonObject expected ||
                            skyhooks![key] is not JsonObject current ||
                            Flag(current["destroyed"]) ||
                            Positive(Integer(current["typeID"])) != 81080 ||
                            Positive(Integer(current["solarSystemID"])) != systemId ||
                            Positive(Integer(current["planetID"])) != Positive(Integer(expected["planetID"])))
                        {
                            rowsAreCurrent = false;
                            break;
                        }

                        var currentOwner = Positive(Integer(current["corporationID"] ?? current["ownerCorpID"]));
                        var expectedOwner = Positive(Integer(expected["corporationID"] ?? expected["ownerCorpID"]));
                        if (currentOwner != expectedOwner && currentOwner != corporationId)
                        {
                            rowsAreCurrent = false;
                            break;
                        }

                        var planetId = Positive(Integer(current["planetID"]));
                        if (skyhooks.Any(pair => pair.Key != key && pair.Value is JsonObject other &&
                                Positive(Integer(other["solarSystemID"])) == systemId &&
                                Positive(Integer(other["planetID"])) == planetId &&
                                !Flag(other["destroyed"])))
                        {
                            rowsAreCurrent = false;
                            break;
                        }
                    }
                }

                if (!rowsAreCurrent)
                {
                    var refunds = new List<SovSkyhookAcquisitionCharge>();
                    foreach (var charge in charges)
                    {
                        if (!await hasWalletOperationAsync(charge, cancellationToken))
                            continue;
                        var refund = charge with
                        {
                            OperationKey = charge.OperationKey + ":refund",
                            IsRefund = true,
                        };
                        if (!await hasWalletOperationAsync(refund, cancellationToken))
                            refunds.Add(refund);
                    }
                    if (refunds.Count > 0 && !await chargeSkyhooksAsync(refunds, cancellationToken))
                        continue;

                    var refundedReceipt = (JsonObject)receipt.DeepClone();
                    refundedReceipt["state"] = "refunded";
                    refundedReceipt["resolvedAtUnixMs"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                    refundedReceipt["resolution"] = "sovereignty-or-skyhook-state-changed";
                    var refundReceiptWrite = write("sovereignty", $"/skyhookAcquisitionSettlements/{receiptKey}", refundedReceipt);
                    if (!refundReceiptWrite.Success)
                        throw new InvalidOperationException($"Could not resolve Skyhook acquisition receipt {receiptKey}: {refundReceiptWrite.ErrorMsg}");
                    await flushTableAsync("sovereignty", CancellationToken.None);
                    var refundedRoot = await readPersistedTableAsync("sovereignty", CancellationToken.None);
                    if ((refundedRoot["skyhookAcquisitionSettlements"] as JsonObject)?[receiptKey]?["state"]?.ToString() != "refunded")
                        throw new InvalidOperationException($"Skyhook acquisition receipt {receiptKey} refund was not durable.");
                    continue;
                }

                if (charges.Count > 0 && !await chargeSkyhooksAsync(charges, cancellationToken))
                    continue;

                var cacheRows = (readCacheTable("sovereignty")["skyhooks"] as JsonObject) ??
                    throw new InvalidOperationException("Skyhook table disappeared during acquisition recovery.");
                foreach (var (key, node) in desiredRows!)
                {
                    if (node is not JsonObject desired || cacheRows[key] is not JsonObject current)
                        throw new InvalidOperationException($"Skyhook acquisition receipt {receiptKey} has an invalid row payload.");
                    var rowWrite = write("sovereignty", $"/skyhooks/{key}", MergeSkyhookAcquisitionRow(current, desired));
                    if (!rowWrite.Success)
                        throw new InvalidOperationException($"Could not replay Skyhook acquisition row {key}: {rowWrite.ErrorMsg}");
                }

                await flushTableAsync("sovereignty", CancellationToken.None);
                var committed = await readPersistedTableAsync("sovereignty", CancellationToken.None);
                var committedRows = committed["skyhooks"] as JsonObject;
                foreach (var (key, node) in desiredRows!)
                    if (node is not JsonObject desired ||
                        committedRows?[key] is not JsonObject saved ||
                        !MatchesSkyhookAcquisition(saved, desired))
                        throw new InvalidOperationException($"Skyhook acquisition receipt {receiptKey} remains pending after row readback.");

                var completedReceipt = (JsonObject)receipt.DeepClone();
                completedReceipt["state"] = "completed";
                completedReceipt["completedAtUnixMs"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                var completedWrite = write("sovereignty", $"/skyhookAcquisitionSettlements/{receiptKey}", completedReceipt);
                if (!completedWrite.Success)
                    throw new InvalidOperationException($"Could not complete Skyhook acquisition receipt {receiptKey}: {completedWrite.ErrorMsg}");
                await flushTableAsync("sovereignty", CancellationToken.None);
                var completedRoot = await readPersistedTableAsync("sovereignty", CancellationToken.None);
                if ((completedRoot["skyhookAcquisitionSettlements"] as JsonObject)?[receiptKey]?["state"]?.ToString() != "completed")
                    throw new InvalidOperationException($"Skyhook acquisition receipt {receiptKey} completion was not durable.");
            }
        }
        finally
        {
            writeGate.Release();
        }
    }

private static JsonObject MergeSkyhookAcquisitionRow(JsonObject current, JsonObject desired)
    {
        var merged = (JsonObject)current.DeepClone();
        foreach (var field in new[]
        {
            "corporationID", "ownerCorpID", "allianceID", "accessListID", "productionActive",
            "resourceVersion", "acquiredAtUnixMs", "acquisitionPaidISK", "acquisitionWalletOperationKey",
        })
            if (desired.ContainsKey(field))
                merged[field] = desired[field]?.DeepClone();
        return merged;
    }

    private static bool MatchesSkyhookAcquisition(JsonObject saved, JsonObject desired)
    {
        foreach (var field in new[]
        {
            "corporationID", "ownerCorpID", "allianceID", "accessListID", "productionActive",
            "resourceVersion", "acquiredAtUnixMs", "acquisitionPaidISK", "acquisitionWalletOperationKey",
        })
            if (desired.ContainsKey(field) && !JsonNode.DeepEquals(saved[field], desired[field]))
                return false;
        return true;
    }

private async ValueTask<IReadOnlyList<long>> ReadCommittedAcquisitionsAsync(
        IReadOnlyList<long> ids,
        long solarSystemId,
        Eve.Services.Core.SovHubRecord hub,
        long? accessListId,
        CancellationToken cancellationToken)
    {
        var root = await readPersistedTableAsync("sovereignty", cancellationToken);
        var rows = root["skyhooks"] as JsonObject;
        if (rows is null)
            return Array.Empty<long>();

        return ids.Distinct().Where(id =>
        {
            var key = id.ToString(System.Globalization.CultureInfo.InvariantCulture);
            return rows[key] is JsonObject row &&
                !Flag(row["destroyed"]) &&
                Positive(Integer(row["corporationID"] ?? row["ownerCorpID"])) == hub.CorporationId &&
                Positive(Integer(row["allianceID"])) == hub.AllianceId &&
                Positive(Integer(row["solarSystemID"])) == solarSystemId &&
                Positive(Integer(row["planetID"])) > 0 &&
                NullablePositive(row["accessListID"]) == accessListId;
        }).Order().ToArray();
    }

    public async Task<bool> TransferAllianceSovereigntyOwnershipAsync(
        long allianceId, long fromCorporationId, long toCorporationId,
        CancellationToken cancellationToken = default)
    {
        if (allianceId <= 0 || fromCorporationId <= 0 || toCorporationId <= 0 ||
            fromCorporationId == toCorporationId)
            return false;
        await writeGate.WaitAsync(cancellationToken);
        try
        {
            var root = (JsonObject)readCacheTable("sovereignty").DeepClone();
            if (root["systems"] is not JsonObject systems)
                return true;

            var systemOwnerKeys = new HashSet<string>(StringComparer.Ordinal);
            var structureOwnerKeys = new HashSet<(string SystemKey, long StructureId)>();
            var hubOwnerIds = new HashSet<long>();
            var changed = false;
            var hubs = root["hubs"] as JsonObject;
            foreach (var (systemKey, node) in systems.ToArray())
            {
                if (node is not JsonObject system ||
                    Positive(Integer(system["allianceID"])) != allianceId)
                    continue;
                var systemId = Positive(Integer(system["solarSystemID"]));
                if (systemId <= 0 && !long.TryParse(systemKey,
                        System.Globalization.NumberStyles.Integer,
                        System.Globalization.CultureInfo.InvariantCulture, out systemId))
                    continue;
                var claimStructureId = Positive(Integer(system["claimStructureID"]));
                var hubId = Positive(Integer(system["infrastructureHubID"]));
                var coreIds = new HashSet<long> { claimStructureId, hubId };
                coreIds.RemoveWhere(id => id <= 0);
                if (coreIds.Count == 0)
                    continue;

                var systemCorporationId = Positive(Integer(system["corporationID"]));
                var systemOwnershipSelected = systemCorporationId == fromCorporationId;
                var coreStructures = (system["structures"] as JsonArray ?? [])
                    .OfType<JsonObject>()
                    .Where(structure => coreIds.Contains(
                        Positive(Integer(structure["itemID"] ?? structure["structureID"]))))
                    .ToArray();
                var stale = false;
                foreach (var structure in coreStructures)
                {
                    var structureAllianceId = Positive(Integer(structure["allianceID"]));
                    if (structureAllianceId > 0 && structureAllianceId != allianceId)
                    {
                        stale = true;
                        break;
                    }
                    var explicitOwners = new[]
                    {
                        Positive(Integer(structure["corporationID"] ?? structure["ownerCorpID"])),
                        Positive(Integer(structure["ownerID"]))
                    }.Where(ownerId => ownerId > 0).Distinct();
                    if (explicitOwners.Any(ownerId =>
                            ownerId != fromCorporationId && ownerId != toCorporationId))
                    {
                        stale = true;
                        break;
                    }
                }
                JsonObject? hub = null;
                if (hubId > 0 && hubs?[hubId.ToString(System.Globalization.CultureInfo.InvariantCulture)]
                        is JsonObject hubRow)
                {
                    hub = hubRow;
                    var hubAllianceId = Positive(Integer(hub["allianceID"]));
                    var hubCorporationId = Positive(Integer(hub["corporationID"]));
                    var hubOwnerId = Positive(Integer(hub["ownerID"]));
                    if (hubAllianceId > 0 && hubAllianceId != allianceId ||
                        hubCorporationId > 0 &&
                            hubCorporationId != fromCorporationId && hubCorporationId != toCorporationId ||
                        hubOwnerId > 0 && hubOwnerId != fromCorporationId && hubOwnerId != toCorporationId)
                        stale = true;
                }
                if (stale)
                    continue;

                var systemChanged = false;
                if (systemOwnershipSelected)
                {
                    system["corporationID"] = toCorporationId;
                    if (system["currentData"] is JsonArray currentData)
                        foreach (var row in currentData.OfType<JsonObject>())
                            if (Positive(Integer(row["corporationID"])) == fromCorporationId)
                                row["corporationID"] = toCorporationId;
                    systemOwnerKeys.Add(systemKey);
                    systemChanged = true;
                }

                foreach (var structure in coreStructures)
                {
                    var structureCorporationId = Positive(Integer(
                        structure["corporationID"] ?? structure["ownerCorpID"]));
                    var ownerId = Positive(Integer(structure["ownerID"]));
                    var effectiveOwnerId = structureCorporationId > 0
                        ? structureCorporationId
                        : ownerId > 0 ? ownerId : systemCorporationId;
                    if (effectiveOwnerId != fromCorporationId)
                        continue;
                    var structureId = Positive(Integer(
                        structure["itemID"] ?? structure["structureID"]));
                    structure["corporationID"] = toCorporationId;
                    structure["ownerID"] = toCorporationId;
                    structureOwnerKeys.Add((systemKey, structureId));
                    systemChanged = true;
                }

                if (hub is not null)
                {
                    var hubCorporationId = Positive(Integer(hub["corporationID"]));
                    var hubOwnerId = Positive(Integer(hub["ownerID"]));
                    var effectiveHubOwner = hubCorporationId > 0
                        ? hubCorporationId
                        : hubOwnerId > 0 ? hubOwnerId : systemCorporationId;
                    if (effectiveHubOwner == fromCorporationId)
                    {
                        hub["corporationID"] = toCorporationId;
                        if (hub.ContainsKey("ownerID"))
                            hub["ownerID"] = toCorporationId;
                        hubOwnerIds.Add(hubId);
                        systemChanged = true;
                    }
                }
                changed |= systemChanged;
            }

            if (!changed)
                return true;
            var writeResult = write("sovereignty", null, root);
            if (!writeResult.Success)
                throw new InvalidOperationException(
                    $"Could not persist sovereignty transfer for alliance {allianceId}: {writeResult.ErrorMsg}");
            await flushTableAsync("sovereignty", cancellationToken);
            var persisted = await readPersistedTableAsync("sovereignty", cancellationToken);
            var savedSystems = persisted["systems"] as JsonObject;
            foreach (var systemKey in systemOwnerKeys)
                if (savedSystems?[systemKey] is not JsonObject system ||
                    Positive(Integer(system["corporationID"])) != toCorporationId)
                    return false;
            foreach (var (systemKey, structureId) in structureOwnerKeys)
            {
                var structure = (savedSystems?[systemKey]?["structures"] as JsonArray ?? [])
                    .OfType<JsonObject>()
                    .FirstOrDefault(row => Positive(Integer(
                        row["itemID"] ?? row["structureID"])) == structureId);
                if (structure is null ||
                    Positive(Integer(structure["corporationID"])) != toCorporationId ||
                    structure.ContainsKey("ownerID") &&
                        Positive(Integer(structure["ownerID"])) != toCorporationId)
                    return false;
            }
            var savedHubs = persisted["hubs"] as JsonObject;
            foreach (var hubId in hubOwnerIds)
            {
                if (savedHubs?[hubId.ToString(System.Globalization.CultureInfo.InvariantCulture)]
                    is not JsonObject hub ||
                    Positive(Integer(hub["corporationID"])) != toCorporationId ||
                    hub.ContainsKey("ownerID") && Positive(Integer(hub["ownerID"])) != toCorporationId)
                    return false;
            }
            return true;
        }
        finally
        {
            writeGate.Release();
        }
    }

    public async Task<bool> DropAllianceSovereigntyClaimsAsync(
        long allianceId, long nowUnixMs, CancellationToken cancellationToken = default)
    {
        if (allianceId <= 0 || nowUnixMs < 0)
            return false;
        await writeGate.WaitAsync(cancellationToken);
        try
        {
            var root = (JsonObject)readCacheTable("sovereignty").DeepClone();
            if (root["systems"] is not JsonObject systems)
                return true;
            var droppedSystems = new HashSet<long>();
            var removedHubIds = new HashSet<long>();
            var changed = false;
            foreach (var (key, node) in systems.ToArray())
            {
                if (node is not JsonObject system || Positive(Integer(system["allianceID"])) != allianceId)
                    continue;
                if (!long.TryParse(key, System.Globalization.NumberStyles.Integer,
                        System.Globalization.CultureInfo.InvariantCulture, out var solarSystemId) ||
                    solarSystemId <= 0)
                    solarSystemId = Positive(Integer(system["solarSystemID"]));
                if (solarSystemId <= 0)
                    continue;

                var claimStructureId = Positive(Integer(system["claimStructureID"]));
                var hubId = Positive(Integer(system["infrastructureHubID"]));
                if (hubId > 0) removedHubIds.Add(hubId);
                var removedStructureIds = new HashSet<long>([claimStructureId, hubId]);
                removedStructureIds.RemoveWhere(id => id <= 0);
                if (system["structures"] is JsonArray structures)
                    system["structures"] = new JsonArray(structures
                        .Where(structure => structure is not JsonObject row ||
                            !removedStructureIds.Contains(Positive(Integer(row["itemID"] ?? row["structureID"]))))
                        .Select(structure => structure?.DeepClone()).ToArray());

                system["allianceID"] = null;
                system["corporationID"] = null;
                system["claimStructureID"] = null;
                system["infrastructureHubID"] = null;
                system["claimTime"] = "0";
                system["fuelAccessGroupID"] = null;
                system["currentData"] = new JsonArray();
                var devIndices = system["devIndices"] as JsonObject ?? new JsonObject();
                devIndices["claimedForDays"] = 0;
                system["devIndices"] = devIndices;
                var recentActivity = new JsonArray
                {
                    new JsonObject
                    {
                        ["solarSystemID"] = solarSystemId,
                        ["ownerID"] = null,
                        ["oldOwnerID"] = allianceId,
                        ["stationID"] = null,
                        ["changeTime"] = DateTimeOffset.FromUnixTimeMilliseconds(nowUnixMs)
                            .UtcDateTime.ToFileTimeUtc().ToString(System.Globalization.CultureInfo.InvariantCulture)
                    }
                };
                if (system["recentActivity"] is JsonArray history)
                    foreach (var entry in history) recentActivity.Add(entry?.DeepClone());
                system["recentActivity"] = recentActivity;
                droppedSystems.Add(solarSystemId);
                changed = true;
            }

            if (root["hubs"] is JsonObject hubs)
                foreach (var (key, node) in hubs.ToArray())
                {
                    var hubId = Positive(Integer((node as JsonObject)?["hubID"]));
                    var hubAllianceId = Positive(Integer((node as JsonObject)?["allianceID"]));
                    if (removedHubIds.Contains(hubId) || hubAllianceId == allianceId)
                    {
                        hubs.Remove(key);
                        changed = true;
                    }
                }

            var allianceKey = allianceId.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (root["alliances"] is JsonObject alliances &&
                alliances[allianceKey] is JsonObject alliance &&
                alliance["capitalInfo"] is JsonObject capital)
            {
                if (droppedSystems.Contains(Positive(Integer(capital["currentCapitalSystem"]))))
                    capital["currentCapitalSystem"] = null;
                if (droppedSystems.Contains(Positive(Integer(capital["newCapitalSystem"]))))
                {
                    capital["newCapitalSystem"] = null;
                    capital["newCapitalSystemValidAfter"] = "0";
                }
            }
            if (!changed) return true;

            var result = write("sovereignty", null, root);
            if (!result.Success)
                throw new InvalidOperationException($"Could not persist sovereignty closure for alliance {allianceId}: {result.ErrorMsg}");
            await flushTableAsync("sovereignty", cancellationToken);
            var persisted = await readPersistedTableAsync("sovereignty", cancellationToken);
            var claimsRemain = (persisted["systems"] as JsonObject)?.Any(entry =>
                entry.Value is JsonObject system && Positive(Integer(system["allianceID"])) == allianceId) == true;
            var hubsRemain = (persisted["hubs"] as JsonObject)?.Any(entry =>
                entry.Value is JsonObject hub && Positive(Integer(hub["allianceID"])) == allianceId) == true;
            return !claimsRemain && !hubsRemain;
        }
        finally
        {
            writeGate.Release();
        }
    }


    private async Task<bool> MutateSystemAsync(
        Eve.Services.ServiceCall call,
        long solarSystemId,
        Func<JsonObject, bool> mutate,
        Func<JsonObject, bool> verify,
        long? expectedHubId = null)
    {
        if (solarSystemId <= 0)
            return false;

        await writeGate.WaitAsync(call.Cancellation);
        try
        {
            var cache = readCacheTable("sovereignty");
            if (!TrySystem(cache, solarSystemId, out var current) || !TryHub(current, out var hub) ||
                (expectedHubId is { } expected && hub.HubId != expected) ||
                !await CanManageHubAsync(call, solarSystemId, hub))
                return false;

            var updated = (JsonObject)current.DeepClone();
            if (!mutate(updated))
                return false;

            var key = solarSystemId.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var result = write("sovereignty", $"/systems/{key}", updated);
            if (!result.Success)
                throw new InvalidOperationException($"Could not persist sovereignty system {solarSystemId}: {result.ErrorMsg}");

            await flushTableAsync("sovereignty", CancellationToken.None);
            var persisted = await readPersistedTableAsync("sovereignty", CancellationToken.None);
            return TrySystem(persisted, solarSystemId, out var saved) && verify(saved);
        }
        finally
        {
            writeGate.Release();
        }
    }

    private static long FindHubSystemId(JsonObject root, long hubId)
    {
        if (root["systems"] is not JsonObject systems)
            return 0;
        foreach (var (key, node) in systems)
        {
            if (node is not JsonObject system || Positive(Integer(system["infrastructureHubID"])) != hubId)
                continue;
            return long.TryParse(key, System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out var id) ? id : 0;
        }
        return 0;
    }

    private static bool TrySystem(JsonObject root, long systemId, out JsonObject system)
    {
        system = null!;
        return root["systems"] is JsonObject systems &&
            systems[systemId.ToString(System.Globalization.CultureInfo.InvariantCulture)] is JsonObject found &&
            (system = found) is not null;
    }

    private static bool TryHub(JsonObject system, out Eve.Services.Core.SovHubRecord hub)
    {
        var id = Positive(Integer(system["infrastructureHubID"]));
        if (id <= 0)
        {
            hub = null!;
            return false;
        }
        hub = new(
            id,
            NullablePositive(system["corporationID"]),
            NullablePositive(system["allianceID"]),
            NullablePositive(system["claimTime"]));
        return true;
    }

    private static bool TargetsSameAlliance(JsonNode payload, JsonObject? systems, long allianceId)
    {
        if (systems is null)
            return false;
        return Visit(payload);

        bool Visit(JsonNode? node)
        {
            if (node is JsonObject obj)
            {
                foreach (var (key, value) in obj)
                {
                    if (SolarSystemKeys.Contains(key))
                    {
                        if (!long.TryParse(value?.ToString(), System.Globalization.NumberStyles.Integer,
                                System.Globalization.CultureInfo.InvariantCulture, out var targetId) || targetId <= 0 ||
                            systems[targetId.ToString(System.Globalization.CultureInfo.InvariantCulture)] is not JsonObject target ||
                            Positive(Integer(target["allianceID"])) != allianceId)
                            return false;
                    }
                    else if (value is not null && !Visit(value))
                        return false;
                }
            }
            else if (node is JsonArray array)
            {
                foreach (var value in array)
                    if (value is not null && !Visit(value))
                        return false;
            }
            return true;
        }
    }

    private static JsonNode? ToJson(Eve.Marshal.PyValue value) => value switch
    {
        Eve.Marshal.PyNone => null,
        Eve.Marshal.PyBool boolean => JsonValue.Create(boolean.Value),
        Eve.Marshal.PyInt integer => JsonValue.Create(integer.Value),
        Eve.Marshal.PyLong integer => JsonValue.Create(integer.Value),
        Eve.Marshal.PyReal number when double.IsFinite(number.Value) => JsonValue.Create(number.Value),
        var text when Eve.Macho.JsSemantics.StringValue(text) is not null => JsonValue.Create(Eve.Macho.JsSemantics.StringValue(text)!),
        Eve.Marshal.PyList list => new JsonArray(list.Items.Select(ToJson).ToArray()),
        Eve.Marshal.PyTuple tuple => new JsonArray(tuple.Items.Select(ToJson).ToArray()),
        Eve.Marshal.PyDict dict => ToJsonObject(dict),
        _ => throw new InvalidOperationException($"Unsupported workforce configuration value {value.GetType().Name}."),
    };

    private static JsonObject ToJsonObject(Eve.Marshal.PyDict value)
    {
        var result = new JsonObject();
        foreach (var entry in value.Entries)
        {
            var key = entry.Key switch
            {
                var text when Eve.Macho.JsSemantics.StringValue(text) is not null => Eve.Macho.JsSemantics.StringValue(text)!,
                Eve.Marshal.PyInt integer => integer.Value.ToString(System.Globalization.CultureInfo.InvariantCulture),
                Eve.Marshal.PyLong integer => integer.Value.ToString(System.Globalization.CultureInfo.InvariantCulture),
                _ => throw new InvalidOperationException("Workforce configuration keys must be scalar."),
            };
            result[key] = ToJson(entry.Value);
        }
        return result;
    }

    private static long Integer(JsonNode? node) =>
        long.TryParse(node?.ToString(), System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture, out var value) ? value : 0;

    private static long Positive(long value) => value > 0 ? value : 0;

    private static long? NullablePositive(JsonNode? node)
    {
        var value = Integer(node);
        return value > 0 ? value : null;
    }

    private static bool Flag(JsonNode? node) =>
        bool.TryParse(node?.ToString(), out var value) ? value : Integer(node) != 0;
}
