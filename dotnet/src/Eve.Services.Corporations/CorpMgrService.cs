using Eve.Characters;
using Eve.Corporations;
using Eve.Macho;
using Eve.Marshal;

namespace Eve.Services.Corporations;

[Service("corpmgr")]
public sealed partial class CorpMgrService(
    CorporationStore store,
    PgCharacterStore characters,
    Eve.Inventory.PgItemStore? items = null,
    Eve.Inventory.ItemTypeCatalog? itemTypes = null,
    Eve.Corporations.CorporationOfficeStore? offices = null,
    Func<long, (long SolarSystemId, int? TypeId)?>? resolveAssetLocation = null,
    Func<long, CancellationToken, ValueTask<CorpMgrAggressionSettings?>>? readAggressionSettings = null,
    Func<long, long, long, long, int, CancellationToken, ValueTask<IReadOnlyList<CorpMgrAuditEvent>>>? readAuditEvents = null,
    Func<long, CancellationToken, ValueTask<IReadOnlyList<Eve.Inventory.Item>>>? readAssetWraps = null)
{
    private async Task<Corporation?> Resolve(ServiceCall call) =>
        await store.GetAsync(call.Arg(0) is PyInt id && id.Value > 0 ? id.Value :
            call.Session.CharacterId is { } character ? (await characters.GetAsync(character,call.Cancellation))?.CorporationId ?? 0 : 0,call.Cancellation);
    [Method("GetPublicInfo")]
    public async ValueTask<PyValue> GetPublicInfo(ServiceCall call) => await Resolve(call) is { } c
        ? CorporationWire.PublicInfo(c,(await store.MembersAsync(c.Id,call.Cancellation)).Count) : PyNone.Instance;
    [Method("GetCorporations")]
    public async ValueTask<PyValue> GetCorporations(ServiceCall call) => await Resolve(call) is { } c
        ? CorporationWire.Row(c,(await store.MembersAsync(c.Id,call.Cancellation)).Count) : PyNone.Instance;
    [Method("GetCorporationIDForCharacter")]
    public async ValueTask<PyValue> GetCorporationIDForCharacter(ServiceCall call)
    {
        var id = call.Arg(0) switch { PyInt n => n.Value,PyLong n => n.Value,_ => 0 };
        if (id > 0 && await characters.GetAsync(id,call.Cancellation) is { CorporationId: > 0 } character)
            return new PyInt(character.CorporationId);
        return new PyInt(call.Session.GetInt("corporationID") ?? call.Session.GetInt("corpid") ?? 1000004);
    }
}
