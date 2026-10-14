using Eve.Services.Core;

namespace Eve.Host;

internal sealed partial class CharacterServicesComposition
{
    private async ValueTask<(SovFwReadSnapshot Snapshot, IReadOnlyList<DevelopmentIndexRecord> DevelopmentIndices)>
        ReadStructureAnchorSovereigntyAsync(CancellationToken cancellationToken)
    {
        var snapshot = await sovereigntyReadModel.ReadSnapshotAsync(cancellationToken).ConfigureAwait(false);
        var developmentIndices = await sovereigntyReadModel.ReadAllDevelopmentIndicesAsync(cancellationToken).ConfigureAwait(false);
        return (snapshot, developmentIndices);
    }
}
