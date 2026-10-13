using Eve.Macho;
using Eve.Marshal;
using Eve.Services;

namespace Eve.Services.Inventory;

/// <summary>
/// The planetary customs offices: the inventory broker opens an office as an inventory (the client's <c>invCache</c> binds it) and hands
/// its import and export to the planets (<c>ImportExportWithPlanet</c>, the client's importExportUI.py:549).
/// </summary>
public interface ICustomsOffices
{
    /// <summary>Whether the id is a customs office of the server (its item exists).</summary>
    ValueTask<bool> IsOfficeAsync(long officeId, CancellationToken cancellationToken);

    /// <summary>
    /// The import (office item id to quantity: the pilot's stacks in the office hangar) and the export (type to quantity: the launchpad's
    /// contents), with the rate the client saw; a rate that changed refuses with <c>TaxChanged</c> (importExportUI.py:551).
    /// </summary>
    ValueTask ImportExportAsync(ClientSession session, long officeId, long launchpadId, IReadOnlyDictionary<long, long> imports,
        IReadOnlyDictionary<int, long> exports, double taxRate, CancellationToken cancellationToken);
}
