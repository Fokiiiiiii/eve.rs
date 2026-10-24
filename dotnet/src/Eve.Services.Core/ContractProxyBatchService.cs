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
}
