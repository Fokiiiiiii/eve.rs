using Eve.Macho;
using Eve.Marshal;

namespace Eve.Services.Wallet;

public sealed partial class AccountService
{
    public Func<long, CancellationToken, ValueTask<int?>>? ReadContactCost { get; set; }
    public Func<long, int?, CancellationToken, ValueTask>? WriteContactCost { get; set; }

    [Method("GetDefaultContactCost")]
    public async ValueTask<PyValue> GetDefaultContactCost(ServiceCall call)
    {
        var characterId = call.Session.CharacterId ?? throw new InvalidOperationException("Contact cost requires a character session.");
        var read = ReadContactCost ?? throw new InvalidOperationException("Contact cost storage is not configured.");
        var cost = await read(characterId, call.Cancellation);
        return cost is { } value ? new PyInt(value) : PyNone.Instance;
    }

    [Method("SetContactCost")]
    public async ValueTask<PyValue> SetContactCost(ServiceCall call)
    {
        var characterId = call.Session.CharacterId ?? throw new InvalidOperationException("Contact cost requires a character session.");
        var write = WriteContactCost ?? throw new InvalidOperationException("Contact cost storage is not configured.");
        var value = call.Kwarg("cost") ?? call.Arg(0);
        int? cost = value switch
        {
            PyNone => null,
            PyInt integer when integer.Value is >= 0 and <= 1_000_000 => (int)integer.Value,
            PyLong integer when integer.Value is >= 0 and <= 1_000_000 => (int)integer.Value,
            PyReal real when double.IsFinite(real.Value) && real.Value is >= 0 and <= 1_000_000 &&
                             Math.Truncate(real.Value) == real.Value => (int)real.Value,
            PyInt or PyLong or PyReal when Eve.Macho.JsSemantics.NumberValue(value) < 0 =>
                throw MachoErrors.ThrowUserError("ContactCostNegativeAmount"),
            _ => throw new ArgumentOutOfRangeException(nameof(value), "CSPA charge must be a whole amount between 0 and 1,000,000 ISK."),
        };
        await write(characterId, cost, call.Cancellation);
        return PyNone.Instance;
    }
}
