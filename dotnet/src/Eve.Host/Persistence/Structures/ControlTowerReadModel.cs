using Eve.Services;
using Eve.Services.Core;
using Eve.Ships;

namespace Eve.Host.Persistence;

/// <summary>Reads the POS rows the current character's corporation can see from the deployed-object store.</summary>
internal sealed class ControlTowerReadModel(PgDeployedStore deployed)
{
    private const int ControlTowerGroupId = 365;

    public async ValueTask<IReadOnlyList<ControlTowerRow>> ReadAsync(ClientSession session, CancellationToken cancellationToken)
    {
        var corporationId = session.GetInt("corpid") ?? session.GetInt("corporationid") ?? session.GetInt("corporationID") ?? 0;
        if (corporationId <= 0)
            return [];

        var towers = await deployed.ByKindAsync("control_tower", cancellationToken);
        return [.. towers
            .Where(tower => tower.CorporationId == corporationId && tower.GroupId == ControlTowerGroupId &&
                tower.TypeId > 0 && tower.DeployableId > 0 && tower.SolarSystemId > 0)
            .OrderBy(tower => tower.DeployableId)
            .Select(tower => new ControlTowerRow(tower.TypeId, tower.DeployableId, tower.SolarSystemId))];
    }
}
