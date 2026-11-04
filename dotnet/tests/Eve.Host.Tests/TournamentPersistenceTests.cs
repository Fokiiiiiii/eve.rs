using Eve.Host.Persistence;
using Eve.Macho;
using Eve.Marshal;
using Eve.Sde;
using Eve.Services;
using Eve.Services.Core;
using Npgsql;

namespace Eve.Host.Tests;

public sealed class TournamentPersistenceTests
{
    [PgFact]
    public async Task Configuration_and_series_survive_restart_and_import_does_not_overwrite_existing_state()
    {
        var url = Environment.GetEnvironmentVariable("EVE_PG_URL")!;
        await using var source = NpgsqlDataSource.Create(Eve.Data.PgConnection.ForTest(url));
        await PersistenceTournamentStore.MigrateAsync(source);
        var id = Random.Shared.NextInt64(1_000_000_000, 9_000_000_000);
        try
        {
            var sde = new SdeReader(source);
            var store = new PersistenceTournamentStore(source, sde);
            var config = new TournamentConfiguration(id, "Practice", 100, 600, 300, null, [587]);
            var seriesSeed = new[] { new TournamentSeries(0, "Red", "Blue", 0, 0, 3) };
            await Task.WhenAll(store.ImportIfMissingAsync(config, seriesSeed), store.ImportIfMissingAsync(config, seriesSeed));
            await store.ImportIfMissingAsync(config with { Name = "Must not overwrite" }, []);
            var restarted = new PersistenceTournamentStore(source, sde);
            var session = new ClientSession();
            var details = Assert.IsType<PyDict>(await restarted.QueryAsync(new(TourneyQuery.QueryTournamentDetails, id, session), default));
            Assert.Equal(new PyInt(100), Field(details, "maximumPointsMatch"));
            var ships = Assert.IsType<PyList>(Field(details, "allowedShipList"));
            var ship = Assert.IsType<PyTuple>(Assert.Single(ships.Items));
            Assert.Equal(new PyStr("Rifter"), ship.Items[0]);
            Assert.Equal(new PyInt(587), ship.Items[1]);
            var matches = Assert.IsType<PyDict>(await restarted.QueryAsync(new(TourneyQuery.GetPotentialMatches, id, session), default));
            var match = Assert.IsType<PyDict>(Assert.Single(matches.Entries).Value);
            Assert.Equal(new PyStr("Red"), Field(match, "redTeam"));
            Assert.Equal(new PyStr("Blue"), Field(match, "blueTeam"));
            var series = Assert.IsType<PyDict>(await restarted.QueryAsync(new(TourneyQuery.GetAllCurrentSeriesMatchDetails, id, session), default));
            var detail = Assert.IsType<PyTuple>(Assert.Single(series.Entries).Value);
            Assert.Equal(new PyInt(0), detail.Items[0]);
            Assert.Equal(new PyInt(3), detail.Items[1]);
            var setup = Assert.IsType<PyTuple>(await restarted.QueryAsync(new(TourneyQuery.GetTourneySetup, null, session), default));
            Assert.Contains(Assert.IsType<PyDict>(setup.Items[0]).Entries, e => e.Key.Equals(id <= int.MaxValue ? new PyInt(id) : new PyLong(id)) && e.Value.Equals(new PyStr("Practice")));
            await Assert.ThrowsAsync<ArgumentException>(() => store.ImportIfMissingAsync(config with { Id = id + 1, AllowedShipTypes = [34] }, []));
        }
        finally
        {
            await using var cmd = source.CreateCommand("DELETE FROM tournament.tournaments WHERE id=$1");
            cmd.Parameters.AddWithValue(id);
            await cmd.ExecuteNonQueryAsync();
        }
    }

    private static PyValue Field(PyDict dict, string key) => dict.Entries.Single(x => x.Key.Equals(new PyStr(key))).Value;
}
