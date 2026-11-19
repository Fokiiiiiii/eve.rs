using Eve.Space;

namespace Eve.Services.Space;

public sealed partial class SpaceSessions
{
    /// <summary>A test seam: awaited once a ship password's matches are computed and before they are sent to the simulation (null in the game).</summary>
    public Func<CancellationToken, Task>? AfterShipPasswordMatch { get; set; }

    /// <summary>The per-system gates of the ship password matches: a pilot's match and its sends, and a rematch of the system, run one after the other.</summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<int, SemaphoreSlim> _passwordGates = new();

    private readonly object _harmonicSync = new();
    private Task _harmonicWork = Task.CompletedTask;

    /// <summary>
    /// Matches a ship password digest against every control tower of the system and returns each tower's answer. The simulation is told each answer and
    /// decides the ship's harmonic from them and from where the ship is in the field (protocol 105); the Host does not choose a harmonic itself.
    /// </summary>
    private async Task<List<(long TowerId, bool Matched)>> MatchShipPasswordAsync(int systemId, byte[] digest, CancellationToken cancellationToken)
    {
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
            }
        }
        return matches;
    }

    /// <summary>
    /// A control tower's saved password changed in this system. Every pilot there who holds a ship password is matched again from the digest kept
    /// when that password was set: the client sends the password only at its prompt, so the digest is all that is kept. The simulation is told the
    /// new answers, and its harmonic reports then follow the saved passwords.
    /// </summary>
    public async Task RematchShipPasswordsAsync(int systemId, CancellationToken cancellationToken)
    {
        var gate = _passwordGates.GetOrAdd(systemId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await RematchShipPasswordsUnderGateAsync(systemId, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>The rematch itself, run under the system's password gate.</summary>
    private async Task RematchShipPasswordsUnderGateAsync(int systemId, CancellationToken cancellationToken)
    {
        foreach (var (sessionId, credential) in _shipStarbaseCredentials)
        {
            if (!_pilots.TryGetValue(sessionId, out var pilot) || pilot.SolarSystemId != systemId ||
                pilot.CharacterId != credential.CharacterId || pilot.ShipId != credential.ShipId)
                continue;
            var matches = await MatchShipPasswordAsync(systemId, credential.PasswordDigest, cancellationToken);
            // a pilot who set another password, or left the ship, meanwhile keeps the state of that later set
            if (!_shipStarbaseCredentials.TryGetValue(sessionId, out var current) || !ReferenceEquals(current, credential))
                continue;
            foreach (var (towerId, matched) in matches)
                _sim.SetShipForceFieldPassword(new SimShipForceFieldPassword(sessionId, towerId, systemId, matched));
        }
    }

    /// <summary>
    /// The simulation's harmonic for a pilot's ship changed (protocol 105). The ship's slim harmonic is written from that answer alone, in the order the
    /// simulation tells them, so the slim field and the simulation's harmonic cannot disagree.
    /// </summary>
    private void HandleShipForceFieldHarmonic(SimShipForceFieldHarmonic change)
    {
        if (!_pilots.TryGetValue(change.SessionId, out var pilot) || pilot.ShipId != change.ShipId) return;
        var shipId = change.ShipId;
        var harmonic = change.Harmonic;
        Task work;
        lock (_harmonicSync)
        {
            work = _harmonicWork.ContinueWith(_ => WriteShipHarmonicAsync(shipId, harmonic), CancellationToken.None,
                TaskContinuationOptions.None, TaskScheduler.Default).Unwrap();
            _harmonicWork = work;
        }
        Background(() => work);
    }

    private async Task WriteShipHarmonicAsync(long shipId, long harmonic)
    {
        if (!await _sim.TrySetSlimFieldAsync(shipId, "harmonic", new SimSlimInt(harmonic), CancellationToken.None).ConfigureAwait(false))
            _log?.Invoke($"ship {shipId}: the harmonic {harmonic} could not be written from the simulation");
    }
}
