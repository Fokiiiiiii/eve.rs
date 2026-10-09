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

sealed class PersistenceInsuranceReadModel(PersistenceStore store) : Eve.Services.Core.IInsuranceReadModel
{
    public async ValueTask<IReadOnlyDictionary<long, long>> GetInsurancePricesCentsAsync(
        IReadOnlyCollection<long> typeIds, CancellationToken cancellationToken)
    {
        var state = await PersistenceReadHelpers.ReadOptionalTableAsync(store, "shipInsurancePrices", cancellationToken);
        var rows = state["byTypeID"] as JsonObject;
        var prices = new Dictionary<long, long>();
        foreach (var typeId in typeIds.Distinct())
        {
            if (rows?[typeId.ToString(System.Globalization.CultureInfo.InvariantCulture)] is not JsonObject row)
                continue;
            var cents = Integer(row["fullInsurancePriceCents"]);
            if (cents > 0)
                prices[typeId] = cents;
        }
        return prices;
    }

    public async ValueTask<IReadOnlyList<Eve.Services.Core.InsuranceContractSnapshot>> GetContractsAsync(
        long ownerId, Eve.Services.Core.InsuranceOwnerKind ownerKind, CancellationToken cancellationToken)
    {
        var state = await PersistenceReadHelpers.ReadOptionalTableAsync(store, "insuranceContracts", cancellationToken);
        var rows = state["contractsByShipID"] as JsonObject;
        if (rows is null)
            return [];
        var contracts = new List<Eve.Services.Core.InsuranceContractSnapshot>();
        foreach (var (key, node) in rows)
        {
            if (node is not JsonObject row)
                continue;
            var contract = Contract(row, LongKey(key));
            if (contract is not null && contract.OwnerId == ownerId && contract.OwnerKind == ownerKind)
                contracts.Add(contract);
        }
        return contracts;
    }

    public async ValueTask<Eve.Services.Core.InsuranceContractSnapshot?> GetContractForShipAsync(
        long shipId, CancellationToken cancellationToken)
    {
        var state = await PersistenceReadHelpers.ReadOptionalTableAsync(store, "insuranceContracts", cancellationToken);
        return (state["contractsByShipID"] as JsonObject)?[shipId.ToString(System.Globalization.CultureInfo.InvariantCulture)] is JsonObject row
            ? Contract(row, shipId)
            : null;
    }

    private static Eve.Services.Core.InsuranceContractSnapshot? Contract(JsonObject row, long fallbackShipId)
    {
        var shipId = Integer(row["shipID"]);
        if (shipId <= 0) shipId = fallbackShipId;
        var ownerKind = Text(row["ownerKind"]).Equals("corporation", StringComparison.OrdinalIgnoreCase)
            ? Eve.Services.Core.InsuranceOwnerKind.Corporation
            : Eve.Services.Core.InsuranceOwnerKind.Character;
        var ownerId = Integer(row["ownerID"]);
        var typeId = Integer(row["typeID"]);
        if (shipId <= 0 || ownerId <= 0 || typeId <= 0)
            return null;
        return new Eve.Services.Core.InsuranceContractSnapshot(
            Integer(row["contractID"]), shipId, typeId, ownerId, ownerKind,
            Number(row["fraction"]), Integer(row["startDate"]), Integer(row["endDate"]),
            Text(row["status"]) is { Length: > 0 } status ? status : "active");
    }

    private static long Integer(JsonNode? value) =>
        long.TryParse(value?.ToString(), System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture, out var integer) ? integer : 0;

    private static long LongKey(string key) => Integer(JsonValue.Create(key));

    private static double Number(JsonNode? value) =>
        double.TryParse(value?.ToString(), System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var number) && double.IsFinite(number) ? number : 0;

    private static string Text(JsonNode? value) =>
        value is JsonValue scalar && scalar.TryGetValue<string>(out var text) ? text : "";
}
