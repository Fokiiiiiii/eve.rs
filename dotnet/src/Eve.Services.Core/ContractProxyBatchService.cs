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
}
