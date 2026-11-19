using Eve.Space;

namespace Eve.Services.Space;

public sealed partial class SpaceSessions
{
    /// <summary>
    /// Matches a ship password digest against every control tower of the system. The harmonic is the first online tower it matches;
    /// every tower's answer is returned, so the simulation is told each one.
    /// </summary>
    private async Task<(long Harmonic, List<(long TowerId, bool Matched)> Matches)> MatchShipPasswordAsync(int systemId, byte[] digest, CancellationToken cancellationToken)
    {
        var harmonic = -1L;
        var matches = new List<(long TowerId, bool Matched)>();
        if (DeployedRecords is { } deployed)
        {
            foreach (var tower in await deployed.InSystemAsync(systemId, cancellationToken))
            {
                if (tower.Kind != "control_tower" || tower.GroupId != 365 || tower.ExpiresAtMs <= _nowUnixMs())
                    continue;
                var matched = await StarbasePasswordHash.MatchesDigestAsync(
                    await deployed.TowerConfigurationAsync(tower.DeployableId, cancellationToken), digest, cancellationToken);
                matches.Add((tower.DeployableId, matched));
                if (matched && harmonic < 0 && tower.ActivateAtMs <= _nowUnixMs() &&
                    StringComparer.Ordinal.Equals(await deployed.TowerStateAsync(tower.DeployableId, cancellationToken), "online"))
                    harmonic = tower.DeployableId;
            }
        }
        return (harmonic, matches);
    }

    /// <summary>
    /// A control tower's saved password changed in this system. Every pilot there who holds a ship password is matched again from the digest kept
    /// when that password was set: the client sends the password only at its prompt, so the digest is all that is kept. The harmonic and the
    /// simulation's answers then follow the saved passwords.
    /// </summary>
    public async Task RematchShipPasswordsAsync(int systemId, CancellationToken cancellationToken)
    {
        foreach (var (sessionId, credential) in _shipStarbaseCredentials)
        {
            if (!_pilots.TryGetValue(sessionId, out var pilot) || pilot.SolarSystemId != systemId ||
                pilot.CharacterId != credential.CharacterId || pilot.ShipId != credential.ShipId)
                continue;
            var (harmonic, matches) = await MatchShipPasswordAsync(systemId, credential.PasswordDigest, cancellationToken);
            // a pilot who set another password, or left the ship, meanwhile keeps the state of that later set
            if (!_shipStarbaseCredentials.TryGetValue(sessionId, out var current) || !ReferenceEquals(current, credential))
                continue;
            if (!await _sim.TrySetSlimFieldAsync(credential.ShipId, "harmonic", new SimSlimInt(harmonic), cancellationToken))
                _log?.Invoke($"ship {credential.ShipId}: the harmonic could not be re-matched after a starbase password change");
            foreach (var (towerId, matched) in matches)
                _sim.SetShipForceFieldPassword(new SimShipForceFieldPassword(sessionId, towerId, systemId, matched));
        }
    }
}
