using Eve.Marshal;
using Eve.Services;
using Eve.Services.Core;

namespace Eve.Services.Core;

public sealed partial class ContractProxyService
{
    [Method("DeleteMultipleContracts")]
    public async Task<PyValue> DeleteMultipleContracts(ServiceCall call)
    {
        var writer = writeModel ?? throw new InvalidOperationException("contract writer is not wired");
        var characterId = call.Session.CharacterId
            ?? throw new InvalidOperationException("contract deletion requires a character session");
        var (corporationId, _) = await ReadCurrentOrganizationIdsAsync(call);
        if (call.Arg(0) is not PyList contractIds)
            throw MachoErrors.ThrowUserError("ConContractNotAvailable");

        var ids = contractIds.Items.Select(item => item switch
        {
            PyInt value => (long)value.Value,
            PyLong value => value.Value,
            _ => 0,
        }).Where(id => id > 0).ToArray();
        if (ids.Any(id => id > int.MaxValue))
            throw MachoErrors.ThrowUserError("ConContractNotAvailable");

        var hasCorpContractManager = await HasContractManagerRoleAsync(call, characterId, corporationId);
        var deleted = new List<PyValue>();
        var failed = new List<PyValue>();
        foreach (var contractId in ids)
        {
            try
            {
                if (await writer.DeleteContractAsync(contractId, characterId, corporationId,
                    hasCorpContractManager, call.Cancellation))
                    deleted.Add(new PyInt((int)contractId));
                else
                    failed.Add(new PyInt((int)contractId));
            }
            catch (ContractUserErrorException)
            {
                failed.Add(new PyInt((int)contractId));
            }
        }

        return new PyTuple([new PyList(deleted), new PyList(failed)]);
    }

    [Method("NumOutstandingContracts")]
    public async Task<PyValue> NumOutstandingContracts(ServiceCall call)
    {
        var store = readModel ?? throw new InvalidOperationException("contract read model is not wired");
        var characterId = call.Session.CharacterId
            ?? throw new InvalidOperationException("contract count reads require a character session");
        var (corporationId, _) = await ReadCurrentOrganizationIdsAsync(call);
        var nowFileTime = Eve.Services.Py.FileTimeFromUnixMs(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        var personal = await CountOutstandingContractsAsync(store, characterId, corporationId, characterId, nowFileTime, false, call);
        var corporation = corporationId > 0
            ? await CountOutstandingContractsAsync(store, characterId, corporationId, corporationId, nowFileTime, true, call)
            : 0;

        return Eve.Services.Py.KeyVal(
            ("nonCorpForMyChar", new PyInt(personal)),
            ("myCorpTotal", new PyInt(corporation)),
            ("nonCorpForMyCorp", new PyInt(corporation)),
            ("myCharTotal", new PyInt(personal)));
    }

    private static async ValueTask<int> CountOutstandingContractsAsync(
        IContractReadModel store,
        long characterId,
        long corporationId,
        long ownerId,
        long nowFileTime,
        bool corporationIssuer,
        ServiceCall call)
    {
        var count = 0;
        long? startContractId = null;
        while (true)
        {
            var bundle = await store.GetContractListForOwnerAsync(characterId, corporationId, ownerId,
                0, 0, 0, 100, startContractId, nowFileTime, call.Cancellation);
            foreach (var contract in bundle.Contracts)
            {
                if (corporationIssuer
                    ? contract.IsCorporationIssued && contract.IssuerCorporationId == ownerId
                    : !contract.IsCorporationIssued && contract.IssuerId == ownerId)
                    count++;
            }

            if (bundle.Contracts.Count < 100)
                return count;
            startContractId = bundle.Contracts[^1].ContractId;
        }
    }

    [Method("CollectMyPageInfo")]
    public async Task<PyValue> CollectMyPageInfo(ServiceCall call)
    {
        var store = readModel ?? throw new InvalidOperationException("contract read model is not wired");
        var characterId = call.Session.CharacterId
            ?? throw new InvalidOperationException("contract page reads require a character session");
        var (corporationId, allianceId) = await ReadCurrentOrganizationIdsAsync(call);
        var now = Eve.Services.Py.FileTimeFromUnixMs(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        var personalOpen = await ReadOwnerContractsAsync(store, characterId, corporationId, characterId, 0, now, call);
        var corporationOpen = corporationId > 0
            ? await ReadOwnerContractsAsync(store, characterId, corporationId, corporationId, 0, now, call)
            : [];
        var pageInfo = await store.GetLoginInfoAsync(characterId, corporationId, allianceId, now, call.Cancellation);
        var personalProgress = await store.GetMyCurrentContractListAsync(
            characterId, corporationId, true, false, now, call.Cancellation);
        var corporationProgress = corporationId > 0 && await HasContractManagerRoleAsync(call, characterId, corporationId)
            ? await store.GetMyCurrentContractListAsync(characterId, corporationId, true, true, now, call.Cancellation)
            : new ContractReadBundle([]);
        var personalBids = await store.GetMyBidsAsync(characterId, corporationId, false, now, call.Cancellation);
        var corporationBids = corporationId > 0 && await HasContractManagerRoleAsync(call, characterId, corporationId)
            ? await store.GetMyBidsAsync(characterId, corporationId, true, now, call.Cancellation)
            : new ContractReadBundle([]);

        var personalOutstandingCount = personalOpen.Count(row =>
            !row.IsCorporationIssued && row.IssuerId == characterId);
        var corporationOutstandingCount = corporationOpen.Count(row =>
            row.IsCorporationIssued && row.IssuerCorporationId == corporationId);
        var assigned = pageInfo.AssignedToMe
            .DistinctBy(row => row.ContractId)
            .Select(row => (PyValue)Eve.Services.Py.Tuple(
                new PyInt(row.IssuerId), new PyInt(row.IssuerCorporationId),
                new PyInt(row.AssigneeId), new PyInt(row.Type)))
            .ToArray();

        return Eve.Services.Py.KeyVal(
            ("numInProgressCorp", new PyInt(corporationProgress.Contracts.Count)),
            ("numOutstandingContractsNonCorp", new PyInt(personalOutstandingCount)),
            ("numOutstandingContractsForCorp", new PyInt(corporationOutstandingCount)),
            ("numOutstandingContracts", new PyInt(personalOutstandingCount + corporationOutstandingCount)),
            ("numBiddingOn", new PyInt(personalBids.Contracts.Count)),
            ("numRequiresAttentionCorp", new PyInt(pageInfo.NeedsAttention.Count(row => row.State == 1))),
            ("numInProgress", new PyInt(personalProgress.Contracts.Count)),
            ("numBiddingOnCorp", new PyInt(corporationBids.Contracts.Count)),
            ("outstandingContracts", Eve.Services.Py.List(assigned)),
            ("numRequiresAttention", new PyInt(pageInfo.NeedsAttention.Count(row => row.State == 0))),
            ("numContractsLeftCorp", new PyInt(0)));
    }

    private static async ValueTask<IReadOnlyList<ContractReadSnapshot>> ReadOwnerContractsAsync(
        IContractReadModel store,
        long characterId,
        long corporationId,
        long ownerId,
        int status,
        long nowFileTime,
        ServiceCall call)
    {
        var contracts = new List<ContractReadSnapshot>();
        long? startContractId = null;
        while (true)
        {
            var bundle = await store.GetContractListForOwnerAsync(characterId, corporationId, ownerId,
                status, 0, 0, 100, startContractId, nowFileTime, call.Cancellation);
            contracts.AddRange(bundle.Contracts);
            if (bundle.Contracts.Count < 100)
                return contracts;
            var nextStartContractId = bundle.Contracts[^1].ContractId;
            if (nextStartContractId == startContractId)
                return contracts;
            startContractId = nextStartContractId;
        }
    }

}
