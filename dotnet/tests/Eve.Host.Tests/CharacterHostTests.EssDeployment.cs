using Eve.Characters;
using Eve.Host;
using Eve.Inventory;
using Eve.Marshal;
using Eve.Macho;
using Eve.Services;
using Eve.Ships;
using Eve.Space;
using Eve.Universe;
using Xunit;

namespace Eve.Host.Tests;

public sealed partial class CharacterHostTests
{
    [RealAbyssalSimPgFact]
    public async Task Host_places_the_claimable_nullsec_ess_when_a_pilot_enters_its_system()
    {
        const int earlierUnenteredSystem = 30_000_208; // LZ-6SU, the first eligible system by ID in the pinned SDE
        const int enteredSystem = 30_000_903; // E02-IK, eligible nullsec with NPC station 60014062
        const long essItemIdBase = 8_440_000_000_000_000;
        const long stationId = 60_014_062;

        var start = new System.Diagnostics.ProcessStartInfo(Environment.GetEnvironmentVariable("EVE_ABYSSAL_SIM_BINARY")!)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
        };
        start.Environment["EVE_SIM_LISTEN"] = "127.0.0.1:0";
        start.Environment["EVE_SDE_DIR"] = Environment.GetEnvironmentVariable("EVE_ABYSSAL_SIM_SDE")!;
        using var process = System.Diagnostics.Process.Start(start) ?? throw new InvalidOperationException("Simulation failed to start.");
        try
        {
            string? endpoint = null;
            using var ready = new CancellationTokenSource(TimeSpan.FromSeconds(45));
            while (await process.StandardError.ReadLineAsync(ready.Token) is { } line)
            {
                if (line.StartsWith("eve-sim: listening on ", StringComparison.Ordinal))
                {
                    endpoint = line["eve-sim: listening on ".Length..];
                    break;
                }
            }
            Assert.NotNull(endpoint);
            _ = process.StandardError.ReadToEndAsync();
            _ = process.StandardOutput.ReadToEndAsync();

            await using var rig = await CharacterHostRig.CreateAsync(simSocket: $"tcp:{endpoint}");
            await rig.WaitForSimulationConnectionAsync();

            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var beforeEntry = await rig.Simulation.GetSceneBodyAsync(
                new SimGetSceneBody(SimClient.NextRequestId(), SimSceneKey.SolarSystem(earlierUnenteredSystem), essItemIdBase + earlierUnenteredSystem),
                timeout.Token);
            Assert.Equal("SCENE_NOT_LOADED", beforeEntry.Error);

            var station = (await StationCatalog.LoadAsync(new Eve.Sde.SdeReader(rig.Source))).All.Single(item => item.Id == stationId);
            var character = await rig.CreateCharacterAsync();
            await using (var connection = await rig.Source.OpenConnectionAsync())
            await using (var transaction = await connection.BeginTransactionAsync())
            {
                await rig.Characters.SetStationAsync(connection, transaction, character, station.Id);
                await rig.Characters.SetLocationAsync(connection, transaction, character,
                    station.SolarSystemId, station.ConstellationId, station.RegionId);
                await transaction.CommitAsync();
            }
            var ship = await GiveItemAsync(rig, character, 587, station.Id, ItemFlags.Hangar, singleton: true);
            await SetShipAsync(rig, character, ship);
            var session = new ClientSession { CharacterId = character, UserId = 1, Sink = new RecordingPacketSink() };
            session.Set("stationid", new PyLong(station.Id));
            rig.Sessions.Enter(character, session);
            var essManager = Assert.IsAssignableFrom<IService>(rig.Registry.Lookup("essMgr"));
            var bankData = Assert.IsType<PyDict>(await CallAsync(
                essManager, "GetDataForClientSolarSystem", session, new PyInt(enteredSystem)));
            Assert.Equal(new PyInt(essItemIdBase + enteredSystem), Field(bankData, "essID"));
            var shipService = Assert.IsAssignableFrom<IService>(rig.Registry.Lookup("ship"));
            await BoundAtAsync(shipService, session, station.Id, "Undock", new PyLong(ship), PyBool.True);
            Assert.Equal(enteredSystem, session.GetInt("solarsystemid"));

            SimGetSceneBodyReply? placed = null;
            using var placementTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            while (!placementTimeout.IsCancellationRequested)
            {
                try
                {
                    placed = await rig.Simulation.GetSceneBodyAsync(
                        new SimGetSceneBody(SimClient.NextRequestId(), SimSceneKey.SolarSystem(enteredSystem), essItemIdBase + enteredSystem),
                        placementTimeout.Token);
                }
                catch (OperationCanceledException) when (placementTimeout.IsCancellationRequested)
                {
                    break;
                }
                if (placed.Error is null)
                    break;
                await Task.Delay(50);
            }
            Assert.NotNull(placed);
            Assert.Null(placed.Error);
            Assert.Equal(Eve.Services.Space.EssRules.TypeEncounterSurveillanceSystem, placed.TypeId);
            Assert.True(rig.Simulation.IsConnected);
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
            }
        }
    }
}
