using System.Collections.Concurrent;
using Eve.Dogma;
using Eve.Inventory;
using Eve.Macho;
using Eve.Marshal;
using Eve.Services.Ships;
using Eve.Ships;
using Eve.Universe;

namespace Eve.Services.Inventory;

[Service("repackagingSvc")]
public sealed partial class RepackagingService(InvBrokerService inventory)
{
    [Method("RepackageItems")]
    public ValueTask<PyValue> RepackageItems(ServiceCall call) => inventory.RepackageItems(call);
}
