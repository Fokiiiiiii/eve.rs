namespace Eve.Services.Core;
using Eve.Marshal;
using Eve.Macho;

/// <summary>

/// alliance, and universe stores. The core service owns EVE.js filtering and wire shape.
/// </summary>
public interface ILookupDirectory
{
    ValueTask<IReadOnlyList<LookupCharacter>> CharactersAsync(CancellationToken cancellationToken);
    ValueTask<IReadOnlyList<LookupCorporation>> CorporationsAsync(CancellationToken cancellationToken);
    ValueTask<IReadOnlyList<LookupAlliance>> AlliancesAsync(CancellationToken cancellationToken);
    ValueTask<IReadOnlyList<LookupFaction>> FactionsAsync(CancellationToken cancellationToken);
    ValueTask<IReadOnlyList<LookupLocation>> LocationsByGroupAsync(int groupId, CancellationToken cancellationToken) =>
        ValueTask.FromResult<IReadOnlyList<LookupLocation>>([]);
    ValueTask<IReadOnlyList<LookupType>> TypesAsync(CancellationToken cancellationToken) =>
        ValueTask.FromResult<IReadOnlyList<LookupType>>([]);
}
