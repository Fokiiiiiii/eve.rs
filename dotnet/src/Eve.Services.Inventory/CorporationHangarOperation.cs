using System.Collections.Concurrent;
using Eve.Dogma;
using Eve.Inventory;
using Eve.Macho;
using Eve.Marshal;
using Eve.Services.Ships;
using Eve.Ships;
using Eve.Universe;

namespace Eve.Services.Inventory;

/// <summary>
/// The inventory broker: it hands out the inventories the client opens (the hangar of its station, the station, the ship in
/// the hangar) as bound objects, and each of them lists what is in it. Only reading is here: moving, stacking and fitting
/// items come later.
/// </summary>
public enum CorporationHangarOperation
{
    Query,
    Take,
    Deposit,
}

public delegate ValueTask<bool> CorporationHangarAccess(
    long characterId, long corporationId, long stationId, int divisionFlag,
    CorporationHangarOperation operation, CancellationToken cancellationToken);
