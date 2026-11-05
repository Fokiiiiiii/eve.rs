using Eve.Marshal;
using Eve.Macho;

namespace Eve.Services.Core;

public sealed partial class ContractProxyService
{
    [Method("SplitStack")]
    public async Task<PyValue> SplitStack(ServiceCall call)
    {
        var writer = writeModel ?? throw new InvalidOperationException("contract writer is not wired");
        var characterId = call.Session.CharacterId
            ?? throw new InvalidOperationException("contract stack splitting requires a character session");
        var stationId = ContractLong(call.Arg(0));
        var itemId = ContractLong(call.Arg(1));
        var quantity = ContractLong(call.Arg(2));
        var forCorporation = call.Arg(3) switch
        {
            PyBool value => value.Value,
            PyInt value => value.Value != 0,
            PyLong value => value.Value != 0,
            _ => throw MachoErrors.ThrowUserError("ConContractNotAvailable"),
        };
        var flag = ContractInt(call.Arg(4));
        var (corporationId, _) = await ReadCurrentOrganizationIdsAsync(call);
        if (stationId <= 0 || stationId != (call.Session.GetInt("stationid") ?? call.Session.GetInt("stationid2") ?? 0))
            throw MachoErrors.ThrowUserError("ConNotInStation");
        if (forCorporation && !await HasContractManagerRoleAsync(call, characterId, corporationId))
            throw MachoErrors.ThrowUserError("ConNotContractManager");

        try
        {
            return await writer.SplitStackAsync(itemId, quantity, characterId, corporationId, stationId,
                forCorporation, flag, call.Cancellation)
                ? PyBool.True
                : PyBool.False;
        }
        catch (ContractUserErrorException error)
        {
            throw MachoErrors.ThrowUserError(error.Message, error.Values.ToArray());
        }
    }
}
