using Eve.Planets;
using Npgsql;

namespace Eve.Services.Planets;

public sealed partial class PlanetMgrService
{
    /// <summary>Settings of an existing player-owned customs office, or null for a different or missing item.</summary>
    public async Task<OrbitalSettings?> OfficeSettingsAsync(long officeId, CancellationToken cancellationToken)
    {
        if (Customs.PlanetOf(officeId) is null)
            return null;

        await using var connection = await source.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var office = await items.LockAndGetAsync(connection, transaction, officeId, cancellationToken);
        if (office is not { TypeId: Customs.OfficeTypeId })
            return null;

        var policy = await CustomsPolicyAsync(connection, transaction, officeId, office.OwnerId, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new OrbitalSettings(policy.ReinforceHour, policy.Rates, policy.StandingLevel,
            policy.AllowAlliance, policy.AllowStandings, null);
    }
}
