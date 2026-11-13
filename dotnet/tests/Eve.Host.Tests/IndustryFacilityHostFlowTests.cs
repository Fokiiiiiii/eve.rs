using Eve.Host;
using Eve.Inventory;
using Eve.Macho;
using Eve.Marshal;
using Eve.Sde;
using Eve.Skills;
using Eve.Services;
using Eve.Services.Core;
using Npgsql;
using Xunit;

namespace Eve.Host.Tests;

public sealed class IndustryFacilityHostFlowTests
{
    private const long CorporationId = 900_001_001;
    private const long StructureId = 1_990_400_001;
    private const long StructureTypeId = 35_832;
    private const long SolarSystemId = 30_000_142;
    private const long RegionId = 10_000_002;
    private const int FactoryServiceId = 22;
    private const int IndustryContainerTypeId = 17_366;
    private const int BlueprintTypeId = 681;

    [PgFact]
    public async Task Facility_cost_gauge_uses_the_maximum_saved_index_per_activity_across_systems()
    {
        await using var fixture = await CreateAsync();
        var indexes = new Eve.Market.IndustryCostIndexStore(fixture.Rig.Source, fixture.Rig.MarketSchema);
        await indexes.ReplaceAsync(
        [
            new Eve.Market.IndustryCostIndexRow(SolarSystemId, IndustryRules.Manufacturing, 0.08),
            new Eve.Market.IndustryCostIndexRow(SolarSystemId + 1, IndustryRules.Manufacturing, 0.23),
            new Eve.Market.IndustryCostIndexRow(SolarSystemId, IndustryRules.Reaction, 0.19),
            new Eve.Market.IndustryCostIndexRow(SolarSystemId + 1, IndustryRules.Reaction, 0.41),
        ], fixture.Rig.NowUnixMs);

        var modifiers = Assert.IsType<PyDict>(await CallAsync(fixture, "GetMaxActivityModifiers"));
        Assert.Equal(new PyReal(0.23), modifiers.Entries.Single(entry =>
            entry.Key == new PyInt(IndustryRules.Manufacturing)).Value);
        Assert.Equal(new PyReal(0.41), modifiers.Entries.Single(entry =>
            entry.Key == new PyInt(IndustryRules.Reaction)).Value);
    }

    [PgFact]
    public async Task Facility_browser_lists_online_industry_structures_from_the_character_region()
    {
        await using var fixture = await CreateAsync();

        var facilities = Assert.IsType<PyList>(await CallAsync(fixture, "GetFacilities"));
        var facility = Assert.Single(facilities.Items,
            item => Field(item, "facilityID") == new PyLong(StructureId));

        Assert.Equal(new PyInt((int)StructureTypeId), Field(facility, "typeID"));
        Assert.Equal(new PyLong(CorporationId), Field(facility, "ownerID"));
        Assert.Equal(new PyInt((int)SolarSystemId), Field(facility, "solarSystemID"));
        var activities = Assert.IsType<PyDict>(Field(facility, "activities"));
        var manufacturing = Assert.Single(activities.Entries,
            entry => entry.Key == new PyInt(IndustryRules.Manufacturing));
        var manufacturingColumns = Assert.IsType<PyTuple>(manufacturing.Value).Items;
        var costModifiers = Assert.IsType<PyList>(manufacturingColumns[2]);
        var systemModifier = Assert.Single(costModifiers.Items,
            modifier => Assert.IsType<PyTuple>(modifier).Items[4] == new PyInt(4));
        Assert.Equal(new PyInt(4), Assert.IsType<PyTuple>(systemModifier).Items[4]);

        fixture.Session.Set("regionid", new PyLong(RegionId + 1));
        var otherRegion = Assert.IsType<PyList>(await CallAsync(fixture, "GetFacilities"));
        Assert.DoesNotContain(otherRegion.Items, item => Field(item, "facilityID") == new PyLong(StructureId));
    }

    [PgFact]
    public async Task Facility_locations_include_the_personal_hangar_at_an_upwell_structure()
    {
        await using var fixture = await CreateAsync();

        var locations = Assert.IsType<PyList>(await CallAsync(
            fixture, "GetFacilityLocations", new PyLong(StructureId), new PyLong(fixture.CharacterId)));
        var location = Assert.Single(locations.Items);

        var locationObject = Assert.IsType<PyObjectEx>(location);
        Assert.Equal(ObjectExKind.Ex1, locationObject.Kind);
        Assert.Equal(new PyStr("industry.Location"), locationObject.Header);
        Assert.DoesNotContain(locationObject.Dict,
            entry => JsSemantics.StringValue(entry.Key) == "inventoryLocationID");
        Assert.Equal(new PyLong(StructureId), Field(location, "itemID"));
        Assert.Equal(new PyInt((int)StructureTypeId), Field(location, "typeID"));
        Assert.Equal(new PyLong(fixture.CharacterId), Field(location, "ownerID"));
        Assert.Equal(new PyInt(4), Field(location, "flagID"));
        Assert.Equal(new PyInt((int)SolarSystemId), Field(location, "solarSystemID"));
        Assert.Equal(PyBool.True, Field(location, "canView"));
        Assert.Equal(PyBool.True, Field(location, "canTake"));
    }

    [PgFact]
    public async Task Corporation_station_facility_locations_separate_the_client_office_id_from_its_inventory_folder()
    {
        await using var rig = await CharacterHostTests.CharacterHostRig.CreateAsync();
        var characterId = await rig.CreateCharacterAsync();
        await rig.AddCorporationMemberAsync(characterId);
        await rig.AddCorporationOfficeAsync(CorporationId);
        var office = Assert.Single(await new Eve.Corporations.CorporationOfficeStore(
            rig.Source, rig.CorporationsSchema).ForCorporationAsync(CorporationId));
        await using (var connection = await rig.Source.OpenConnectionAsync())
        await using (var update = new NpgsqlCommand(
            $"UPDATE {rig.CorporationsSchema}.members SET roles = $2 WHERE character_id = $1", connection))
        {
            update.Parameters.Add(new NpgsqlParameter { Value = characterId });
            update.Parameters.Add(new NpgsqlParameter { Value = Eve.Corporations.CorporationRoles.Director });
            Assert.Equal(1, await update.ExecuteNonQueryAsync());
        }

        var capture = new Capture();
        var session = Session(capture, characterId, Eve.Corporations.CorporationRoles.Director);
        session.Set("regionid", new PyLong(RegionId));
        session.Set("solarsystemid2", new PyLong(SolarSystemId));
        session.Set("stationid", new PyLong(rig.Station));
        rig.Sessions.Enter(characterId, session);
        var fixture = new Fixture(rig, characterId, capture, session, ownsRig: false);

        var locations = Assert.IsType<PyList>(await CallAsync(fixture,
            "GetFacilityLocations", new PyLong(rig.Station), new PyLong(CorporationId)));
        var hangar = Assert.Single(locations.Items,
            location => Field(location, "flagID") == new PyInt(Eve.Services.Core.IndustryRules.CorporationHangarFirstFlag));

        Assert.Equal(new PyLong(office.OfficeId), Field(hangar, "itemID"));
        Assert.Equal(new PyInt(27), Field(hangar, "typeID"));
        Assert.DoesNotContain(Assert.IsType<PyObjectEx>(hangar).Dict,
            entry => JsSemantics.StringValue(entry.Key) == "inventoryLocationID");
        Assert.Equal(new PyLong(CorporationId), Field(hangar, "ownerID"));
        Assert.Equal(PyBool.True, Field(hangar, "canView"));
        Assert.Equal(PyBool.True, Field(hangar, "canTake"));
    }

    [PgFact]
    public async Task Corporation_copying_accepts_a_visible_blueprint_location_without_take_access_when_no_materials_are_used()
    {
        await using var rig = await CharacterHostTests.CharacterHostRig.CreateAsync();
        var characterId = await rig.CreateCharacterAsync();
        await rig.AddCorporationMemberAsync(characterId);
        var stations = await Eve.Universe.StationCatalog.LoadAsync(new Eve.Sde.SdeReader(rig.Source));
        var scienceStation = stations.All
            .Where(station => stations.ServicesOf(station.Id).Contains(15))
            .OrderBy(station => station.Id).First();
        var officeFolderId = await rig.AddCorporationOfficeAsync(CorporationId, scienceStation.Id);
        await using (var connection = await rig.Source.OpenConnectionAsync())
        await using (var transaction = await connection.BeginTransactionAsync())
        {
            await rig.Characters.SetStationAsync(connection, transaction, characterId, scienceStation.Id);
            await rig.Characters.SetLocationAsync(connection, transaction, characterId, scienceStation.SolarSystemId,
                scienceStation.ConstellationId, scienceStation.RegionId);
            await transaction.CommitAsync();
        }
        var office = Assert.Single(await new Eve.Corporations.CorporationOfficeStore(
            rig.Source, rig.CorporationsSchema).ForCorporationAsync(CorporationId));
        const long roles = Eve.Corporations.CorporationRoles.FactoryManager | (1L << 51) | (1L << 27);
        await using (var connection = await rig.Source.OpenConnectionAsync())
        await using (var update = new NpgsqlCommand(
            $"UPDATE {rig.CorporationsSchema}.members SET roles = $2, roles_hq = 0 WHERE character_id = $1", connection))
        {
            update.Parameters.Add(new NpgsqlParameter { Value = characterId });
            update.Parameters.Add(new NpgsqlParameter { Value = roles });
            Assert.Equal(1, await update.ExecuteNonQueryAsync());
        }
        await rig.FundAsync(CorporationId, 1_000_000m);

        var inventory = new PgItemStore(rig.Source, rig.InventorySchema);
        long blueprintId;
        await using (var connection = await rig.Source.OpenConnectionAsync())
        await using (var transaction = await connection.BeginTransactionAsync())
        {
            blueprintId = await inventory.CreateAsync(connection, transaction,
                new NewItem(BlueprintTypeId, CorporationId, officeFolderId,
                    Eve.Inventory.ItemFlags.CorpSAG1, Singleton: true, BlueprintCopy: false,
                    MaterialEfficiency: 4, TimeEfficiency: 6));
            await transaction.CommitAsync();
        }

        var capture = new Capture();
        var session = Session(capture, characterId, roles);
        session.Set("regionid", new PyLong(scienceStation.RegionId));
        session.Set("solarsystemid2", new PyLong(scienceStation.SolarSystemId));
        session.Set("stationid", new PyLong(scienceStation.Id));
        session.Set("corpAccountKey", new PyInt(1000));
        rig.Sessions.Enter(characterId, session);
        var fixture = new Fixture(rig, characterId, capture, session, ownsRig: false);

        var facility = await CallAsync(fixture, "GetFacility", new PyLong(scienceStation.Id));
        Assert.Contains(Assert.IsType<PyDict>(Field(facility, "activities")).Entries,
            entry => entry.Key == new PyInt(IndustryRules.Copying));
        var locations = Assert.IsType<PyList>(await CallAsync(fixture,
            "GetFacilityLocations", new PyLong(scienceStation.Id), new PyLong(CorporationId)));
        var inputLocation = Assert.Single(locations.Items,
            location => Field(location, "flagID") == new PyInt(Eve.Inventory.ItemFlags.CorpSAG1));
        Assert.Equal(new PyLong(office.OfficeId), Field(inputLocation, "itemID"));
        Assert.DoesNotContain(Assert.IsType<PyObjectEx>(inputLocation).Dict,
            entry => JsSemantics.StringValue(entry.Key) == "inventoryLocationID");
        Assert.Equal(PyBool.True, Field(inputLocation, "canView"));
        Assert.Equal(PyBool.False, Field(inputLocation, "canTake"));

        var request = Py.KeyVal(
            ("activityID", new PyInt(IndustryRules.Copying)),
            ("blueprintID", new PyLong(blueprintId)),
            ("blueprintTypeID", new PyInt(BlueprintTypeId)),
            ("facilityID", new PyLong(scienceStation.Id)),
            ("solarSystemID", new PyLong(scienceStation.SolarSystemId)),
            ("characterID", new PyLong(characterId)),
            ("corporationID", new PyLong(CorporationId)),
            ("runs", new PyInt(1)),
            ("cost", new PyReal(0)),
            ("tax", new PyReal(0)),
            ("time", new PyReal(0)),
            ("materials", new PyDict([])),
            ("inputLocation", IndustryLocation(office.OfficeId, 27, CorporationId,
                Eve.Inventory.ItemFlags.CorpSAG1, scienceStation.SolarSystemId, canTake: false)),
            ("outputLocation", IndustryLocation(scienceStation.Id, scienceStation.TypeId, CorporationId,
                Eve.Services.Core.IndustryRules.CorporationDeliveriesFlag, scienceStation.SolarSystemId)),
            ("licensedRuns", new PyInt(10)),
            ("productTypeID", PyNone.Instance),
            ("account", Py.Tuple(new PyLong(CorporationId), new PyInt(1000))));

        var installResponse = await SendAsync(fixture, "industryManager", "InstallJob", [request]);
        var installPacket = MachoPacket.Decode(installResponse);
        var installError = installPacket?.Type == MachoMessage.ErrorResponse
            ? new Eve.Services.MachoError(MachoErrorType.WrappedException,
                Assert.IsType<PySubStream>(Assert.IsType<PyTuple>(
                    Assert.IsType<PyTuple>(installPacket.Payload).Items[2]).Items[0]).Decoded!).UserErrorMessage
            : "";
        Assert.True(installPacket?.Type == MachoMessage.CallRsp,
            $"InstallJob returned {installPacket?.TypeName} ({installError}).");
        var installedId = ResponseValue(installResponse) switch
        {
            PyInt integer => integer.Value,
            PyLong integer => integer.Value,
            _ => throw new Xunit.Sdk.XunitException("InstallJob must return a job ID."),
        };
        await rig.Events.PumpAsync();
        AssertIndustryJobNotification(capture, installedId, CorporationId, characterId, blueprintId, 1);
        var installedJob = await CallAsync(fixture, "industryManager", "GetJob", new PyLong(installedId));
        Assert.Equal(new PyInt(checked((int)office.OfficeId)), Field(installedJob, "blueprintLocationID"));
        Assert.True((await inventory.GetAsync(blueprintId))!.BlueprintLocked);
        Assert.Equal(1L, await ScalarLongAsync(rig.Source,
            $"SELECT count(*) FROM {rig.IndustrySchema}.jobs WHERE job_id = {installedId} AND status = 1"));

        await using (var makeReady = rig.Source.CreateCommand(
            $"UPDATE {rig.IndustrySchema}.jobs SET end_date_filetime = 0 WHERE job_id = $1"))
        {
            makeReady.Parameters.Add(new Npgsql.NpgsqlParameter { Value = installedId });
            Assert.Equal(1, await makeReady.ExecuteNonQueryAsync());
        }
        Assert.IsType<PyNone>(await CallAsync(fixture, "industryManager", "CompleteJob",
            new PyLong(installedId), new PyLong(scienceStation.SolarSystemId)));

        await rig.Events.PumpAsync();
        AssertIndustryJobNotification(capture, installedId, CorporationId, characterId, blueprintId, 100);
        Assert.False((await inventory.GetAsync(blueprintId))!.BlueprintLocked);
        Assert.Equal(office.OfficeFolderId, (await inventory.GetAsync(blueprintId))!.LocationId);
        Assert.Equal(Eve.Inventory.ItemFlags.CorpSAG1, (await inventory.GetAsync(blueprintId))!.Flag);
        var output = Assert.Single(await inventory.InContainerAsync(scienceStation.Id),
            item => item.TypeId == BlueprintTypeId && item.BlueprintCopy);
        Assert.Equal(CorporationId, output.OwnerId);
        Assert.Equal(scienceStation.Id, output.LocationId);
        Assert.Equal(Eve.Services.Core.IndustryRules.CorporationDeliveriesFlag, output.Flag);
        Assert.Equal(100L, await ScalarLongAsync(rig.Source,
            $"SELECT status FROM {rig.IndustrySchema}.jobs WHERE job_id = {installedId}"));
    }

    [PgFact]
    public async Task Industry_install_and_delivery_use_authorized_industry_container_inventory_flags()
    {
        await using var rig = await CharacterHostTests.CharacterHostRig.CreateAsync();
        await rig.AddStructureAsync(StructureId, FactoryServiceId);
        var characterId = await rig.CreateCharacterAsync();
        await rig.AddCorporationMemberAsync(characterId);
        await SetFactoryManagerRoleAsync(rig, characterId);
        await AddCorporationAffiliationAsync(rig, characterId);
        await rig.FundAsync(characterId, 1_000_000m);

        var inventory = new PgItemStore(rig.Source, rig.InventorySchema);
        long containerId;
        long blueprintId;
        long materialId;
        await using (var connection = await rig.Source.OpenConnectionAsync())
        await using (var transaction = await connection.BeginTransactionAsync())
        {
            containerId = await inventory.CreateAsync(connection, transaction,
                new NewItem(IndustryContainerTypeId, characterId, StructureId, 4, Singleton: true));
            blueprintId = await inventory.CreateAsync(connection, transaction,
                new NewItem(BlueprintTypeId, characterId, containerId, 64, Singleton: true));
            materialId = await inventory.CreateAsync(connection, transaction,
                new NewItem(38, characterId, containerId, 64, Quantity: 86));
            await transaction.CommitAsync();
        }

        var capture = new Capture();
        var session = Session(capture, characterId, Eve.Corporations.CorporationRoles.FactoryManager);
        session.Set("solarsystemid2", new PyLong(SolarSystemId));
        session.Set("stationid", new PyLong(rig.Station));
        session.Set("corpAccountKey", new PyInt(1000));
        rig.Sessions.Enter(characterId, session);
        var fixture = new Fixture(rig, characterId, capture, session, ownsRig: false);

        var locations = Assert.IsType<PyList>(await CallAsync(fixture,
            "facilityManager", "GetFacilityLocations", new PyLong(StructureId), new PyLong(characterId)));
        var containerLocation = Assert.Single(locations.Items,
            location => Field(location, "itemID") == new PyLong(containerId));
        Assert.DoesNotContain(Assert.IsType<PyObjectEx>(containerLocation).Dict,
            entry => JsSemantics.StringValue(entry.Key) == "inventoryLocationID");
        Assert.Equal(new PyInt(4), Field(containerLocation, "flagID"));
        Assert.Equal(PyBool.True, Field(containerLocation, "canTake"));

        var request = Py.KeyVal(
            ("activityID", new PyInt(IndustryRules.Manufacturing)),
            ("blueprintID", new PyLong(blueprintId)),
            ("blueprintTypeID", new PyInt(BlueprintTypeId)),
            ("facilityID", new PyLong(StructureId)),
            ("solarSystemID", new PyLong(SolarSystemId)),
            ("characterID", new PyLong(characterId)),
            ("corporationID", new PyLong(CorporationId)),
            ("runs", new PyInt(1)),
            ("cost", new PyReal(0)),
            ("tax", new PyReal(0)),
            ("time", new PyReal(0)),
            ("materials", new PyDict([new(new PyInt(38), new PyInt(86))])),
            ("inputLocation", IndustryLocation(containerId, IndustryContainerTypeId, characterId,
                4, SolarSystemId)),
            ("outputLocation", IndustryLocation(containerId, IndustryContainerTypeId, characterId,
                4, SolarSystemId)),
            ("licensedRuns", new PyInt(0)),
            ("productTypeID", new PyInt(165)),
            ("account", Py.Tuple(new PyLong(characterId), new PyInt(1000))));

        var installResponse = await SendAsync(fixture, "industryManager", "InstallJob", [request]);
        Assert.Equal(MachoMessage.CallRsp, MachoPacket.Decode(installResponse)?.Type);
        var installedId = ResponseValue(installResponse) switch
        {
            PyInt integer => integer.Value,
            PyLong integer => integer.Value,
            _ => throw new Xunit.Sdk.XunitException("InstallJob must return a job ID."),
        };
        await rig.Events.PumpAsync();
        AssertIndustryJobNotification(capture, installedId, characterId, characterId, blueprintId, 1);
        Assert.Null(await inventory.GetAsync(materialId));
        Assert.True((await inventory.GetAsync(blueprintId))!.BlueprintLocked);
        Assert.Equal(1L, await ScalarLongAsync(rig.Source,
            $"SELECT count(*) FROM {rig.IndustrySchema}.jobs WHERE job_id = {installedId} AND status = 1"));

        await using (var makeReady = rig.Source.CreateCommand(
            $"UPDATE {rig.IndustrySchema}.jobs SET end_date_filetime = 0 WHERE job_id = $1"))
        {
            makeReady.Parameters.Add(new Npgsql.NpgsqlParameter { Value = installedId });
            Assert.Equal(1, await makeReady.ExecuteNonQueryAsync());
        }
        Assert.IsType<PyNone>(await CallAsync(fixture, "industryManager", "CompleteJob",
            new PyLong(installedId), new PyLong(SolarSystemId)));

        await rig.Events.PumpAsync();
        AssertIndustryJobNotification(capture, installedId, characterId, characterId, blueprintId, 100);
        Assert.False((await inventory.GetAsync(blueprintId))!.BlueprintLocked);
        var output = Assert.Single(await inventory.InContainerAsync(containerId), item => item.TypeId == 165);
        Assert.Equal(characterId, output.OwnerId);
        Assert.Equal(containerId, output.LocationId);
        Assert.Equal(64, output.Flag);
        Assert.Equal(100L, await ScalarLongAsync(rig.Source,
            $"SELECT status FROM {rig.IndustrySchema}.jobs WHERE job_id = {installedId}"));
    }

    [PgFact]
    public async Task Registered_host_cancel_commits_status_unlocks_the_blueprint_keeps_materials_consumed_and_notifies()
    {
        await using var fixture = await CreateAsync();
        await fixture.Rig.FundAsync(fixture.CharacterId, 1_000_000m);
        var inventory = new PgItemStore(fixture.Rig.Source, fixture.Rig.InventorySchema);
        long blueprintId;
        long materialId;
        await using (var connection = await fixture.Rig.Source.OpenConnectionAsync())
        await using (var transaction = await connection.BeginTransactionAsync())
        {
            blueprintId = await inventory.CreateAsync(connection, transaction,
                new NewItem(BlueprintTypeId, fixture.CharacterId, StructureId, 4, Singleton: true));
            materialId = await inventory.CreateAsync(connection, transaction,
                new NewItem(38, fixture.CharacterId, StructureId, 4, Quantity: 86));
            await transaction.CommitAsync();
        }

        var installResponse = await SendAsync(fixture, "industryManager", "InstallJob", [Py.KeyVal(
            ("blueprintID", new PyLong(blueprintId)),
            ("blueprintTypeID", new PyInt(BlueprintTypeId)),
            ("activityID", new PyInt(IndustryRules.Manufacturing)),
            ("facilityID", new PyLong(StructureId)),
            ("solarSystemID", new PyLong(SolarSystemId)),
            ("characterID", new PyLong(fixture.CharacterId)),
            ("corporationID", new PyLong(CorporationId)),
            ("account", Py.Tuple(new PyLong(fixture.CharacterId), new PyInt(1000))),
            ("runs", new PyInt(1)),
            ("cost", new PyReal(0)),
            ("tax", new PyReal(0)),
            ("time", new PyReal(0)),
            ("materials", new PyDict([new(new PyInt(38), new PyInt(86))])),
            ("inputLocation", IndustryLocation(StructureId, (int)StructureTypeId,
                fixture.CharacterId, 4, SolarSystemId)),
            ("outputLocation", IndustryLocation(StructureId, (int)StructureTypeId,
                fixture.CharacterId, 4, SolarSystemId)),
            ("licensedRuns", new PyInt(0)),
            ("productTypeID", new PyInt(165))
        )]);
        Assert.Equal(MachoMessage.CallRsp, MachoPacket.Decode(installResponse)?.Type);
        var jobId = ResponseValue(installResponse) switch
        {
            PyInt integer => integer.Value,
            PyLong integer => integer.Value,
            _ => throw new Xunit.Sdk.XunitException("InstallJob must return a job ID."),
        };
        await fixture.Rig.Events.PumpAsync();
        AssertIndustryJobNotification(fixture.Capture, jobId, fixture.CharacterId,
            fixture.CharacterId, blueprintId, 1);
        Assert.Null(await inventory.GetAsync(materialId));
        Assert.True((await inventory.GetAsync(blueprintId))!.BlueprintLocked);

        var cancelResponse = await SendAsync(fixture, "industryManager", "CancelJob",
            [new PyLong(jobId), new PyLong(SolarSystemId)]);
        Assert.IsType<PyNone>(ResponseValue(cancelResponse));
        await fixture.Rig.Events.PumpAsync();

        AssertIndustryJobNotification(fixture.Capture, jobId, fixture.CharacterId,
            fixture.CharacterId, blueprintId, 102, new PyInt(0));
        Assert.False((await inventory.GetAsync(blueprintId))!.BlueprintLocked);
        Assert.Null(await inventory.GetAsync(materialId));
        Assert.Equal(102L, await ScalarLongAsync(fixture.Rig.Source,
            $"SELECT status FROM {fixture.Rig.IndustrySchema}.jobs WHERE job_id = {jobId}"));
        Assert.DoesNotContain(await inventory.InContainerAsync(StructureId), item => item.TypeId == 165);
    }

    [PgFact]
    public async Task Reused_call_id_after_reconnect_installs_a_distinct_job_while_same_session_retry_replays()
    {
        await using var fixture = await CreateAsync();
        await fixture.Rig.FundAsync(fixture.CharacterId, 1_000_000m);
        var massProduction = (await SkillCatalog.LoadAsync(new SdeReader(fixture.Rig.Source))).Get(3387);
        Assert.NotNull(massProduction);
        await using (var connection = await fixture.Rig.Source.OpenConnectionAsync())
        await using (var transaction = await connection.BeginTransactionAsync())
        {
            await new PgSkillStore(fixture.Rig.Source, fixture.Rig.SkillsSchema).GrantAsync(connection, transaction,
                fixture.CharacterId, [new CharacterSkill(massProduction.TypeId, 1, SkillPoints.ForTraining(massProduction.Rank, 1))]);
            await transaction.CommitAsync();
        }
        var inventory = new PgItemStore(fixture.Rig.Source, fixture.Rig.InventorySchema);
        long firstBlueprintId;
        long secondBlueprintId;
        long materialId;
        await using (var connection = await fixture.Rig.Source.OpenConnectionAsync())
        await using (var transaction = await connection.BeginTransactionAsync())
        {
            firstBlueprintId = await inventory.CreateAsync(connection, transaction,
                new NewItem(BlueprintTypeId, fixture.CharacterId, StructureId, 4, Singleton: true));
            secondBlueprintId = await inventory.CreateAsync(connection, transaction,
                new NewItem(BlueprintTypeId, fixture.CharacterId, StructureId, 4, Singleton: true));
            materialId = await inventory.CreateAsync(connection, transaction,
                new NewItem(38, fixture.CharacterId, StructureId, 4, Quantity: 172));
            await transaction.CommitAsync();
        }

        PyValue InstallRequest(long blueprintId) => Py.KeyVal(
            ("activityID", new PyInt(IndustryRules.Manufacturing)),
            ("blueprintID", new PyLong(blueprintId)),
            ("blueprintTypeID", new PyInt(BlueprintTypeId)),
            ("facilityID", new PyLong(StructureId)),
            ("solarSystemID", new PyLong(SolarSystemId)),
            ("characterID", new PyLong(fixture.CharacterId)),
            ("corporationID", new PyLong(CorporationId)),
            ("runs", new PyInt(1)),
            ("cost", new PyReal(0)),
            ("tax", new PyReal(0)),
            ("time", new PyReal(0)),
            ("materials", new PyDict([new(new PyInt(38), new PyInt(86))])),
            ("inputLocation", IndustryLocation(StructureId, (int)StructureTypeId,
                fixture.CharacterId, 4, SolarSystemId)),
            ("outputLocation", IndustryLocation(StructureId, (int)StructureTypeId,
                fixture.CharacterId, 4, SolarSystemId)),
            ("licensedRuns", new PyInt(0)),
            ("productTypeID", new PyInt(165)),
            ("account", Py.Tuple(new PyLong(fixture.CharacterId), new PyInt(1000))));

        string? dispatchProblem = null;
        Exception? dispatchException = null;
        var firstResponse = await SendAsync(fixture, "industryManager", "InstallJob", [InstallRequest(firstBlueprintId)],
            (message, error) =>
            {
                dispatchProblem = message;
                dispatchException = error;
            });
        var firstPacket = MachoPacket.Decode(firstResponse);
        var firstError = firstPacket?.Type == MachoMessage.ErrorResponse
            ? new Eve.Services.MachoError(MachoErrorType.WrappedException,
                Assert.IsType<PySubStream>(Assert.IsType<PyTuple>(Assert.IsType<PyTuple>(firstPacket.Payload).Items[2]).Items[0]).Decoded!).UserErrorMessage
            : "";
        Assert.True(firstPacket?.Type == MachoMessage.CallRsp,
            $"InstallJob returned {firstPacket?.TypeName} ({firstError}; {dispatchProblem}; {dispatchException}).");
        var firstJobId = Assert.IsType<PyInt>(ResponseValue(firstResponse)).Value;
        await fixture.Rig.Events.PumpAsync();
        AssertIndustryJobNotification(fixture.Capture, firstJobId, fixture.CharacterId,
            fixture.CharacterId, firstBlueprintId, 1);

        var sameSessionRetry = await SendAsync(fixture, "industryManager", "InstallJob", [InstallRequest(firstBlueprintId)]);
        Assert.Equal(MachoMessage.CallRsp, MachoPacket.Decode(sameSessionRetry)?.Type);
        Assert.Equal(new PyInt(firstJobId), ResponseValue(sameSessionRetry));
        Assert.Equal(86L, (await inventory.GetAsync(materialId))!.Quantity);

        var reconnected = await RebindAsync(fixture, fixture.Session.SessionId + 100_000);
        dispatchProblem = null;
        dispatchException = null;
        var secondResponse = await SendAsync(reconnected, "industryManager", "InstallJob", [InstallRequest(secondBlueprintId)],
            (message, error) =>
            {
                dispatchProblem = message;
                dispatchException = error;
            });
        var secondPacket = MachoPacket.Decode(secondResponse);
        var secondError = secondPacket?.Type == MachoMessage.ErrorResponse
            ? new Eve.Services.MachoError(MachoErrorType.WrappedException,
                Assert.IsType<PySubStream>(Assert.IsType<PyTuple>(Assert.IsType<PyTuple>(secondPacket.Payload).Items[2]).Items[0]).Decoded!).UserErrorMessage
            : "";
        Assert.True(secondPacket?.Type == MachoMessage.CallRsp,
            $"Reconnected InstallJob returned {secondPacket?.TypeName} ({secondError}; {dispatchProblem}; {dispatchException}).");
        var secondJobId = Assert.IsType<PyInt>(ResponseValue(secondResponse)).Value;
        Assert.NotEqual(firstJobId, secondJobId);
        await fixture.Rig.Events.PumpAsync();
        AssertIndustryJobNotification(reconnected.Capture, secondJobId, fixture.CharacterId,
            fixture.CharacterId, secondBlueprintId, 1);
        Assert.Null(await inventory.GetAsync(materialId));
        Assert.Equal(2L, await ScalarLongAsync(fixture.Rig.Source,
            $"SELECT count(*) FROM {fixture.Rig.IndustrySchema}.jobs WHERE status = 1"));
    }

    [PgFact]
    public async Task Facility_tax_update_is_committed_notified_and_read_by_the_next_facility_request()
    {
        await using var fixture = await CreateAsync();
        var observerCapture = new Capture();
        var observerId = fixture.CharacterId + 1;
        var observer = Session(observerCapture, observerId, role: 0);
        fixture.Rig.Sessions.Enter(observerId, observer);
        Assert.Equal(PyBool.True, await CallAsync(fixture, "structureSettings", "CharacterHasService",
            new PyLong(StructureId), new PyInt(FactoryServiceId)));

        var changed = await CallAsync(fixture, "SetFacilityTaxes",
            new PyLong(StructureId), new PyLong(CorporationId), TaxValues(0.025));
        Assert.IsType<PyNone>(changed);

        AssertFacilityUpdatedNotification(observerCapture, StructureId);
        AssertFacilityUpdatedNotification(fixture.Capture, StructureId);
        Assert.Equal(MachoMessage.Notification, MachoPacket.Decode(fixture.Capture.Packets[0])?.Type);
        Assert.NotEqual(MachoMessage.Notification, MachoPacket.Decode(fixture.Capture.Packets[1])?.Type);

        var facility = await CallAsync(fixture, "GetFacility", new PyLong(StructureId));
        Assert.Equal(PyNone.Instance, Field(facility, "tax"));
        Assert.Equal(new PyReal(0.025), Assert.Single(Assert.IsType<PyDict>(Field(facility, "serviceAccess")).Entries,
            entry => entry.Key == new PyInt(FactoryServiceId)).Value);
        var taxes = await CallAsync(fixture, "GetFacilityTaxes", new PyLong(StructureId), new PyLong(CorporationId));
        Assert.Equal(new PyReal(0.025), Field(taxes, "taxCorporation"));

        await fixture.Rig.FundAsync(fixture.CharacterId, 1_000_000m);
        var inventory = new PgItemStore(fixture.Rig.Source, fixture.Rig.InventorySchema);
        long blueprintId;
        await using (var connection = await fixture.Rig.Source.OpenConnectionAsync())
        await using (var transaction = await connection.BeginTransactionAsync())
        {
            blueprintId = await inventory.CreateAsync(connection, transaction,
                new NewItem(BlueprintTypeId, fixture.CharacterId, StructureId, 4, Singleton: true));
            await inventory.CreateAsync(connection, transaction,
                new NewItem(38, fixture.CharacterId, StructureId, 4, Quantity: 86));
            await transaction.CommitAsync();
        }
        var installResponse = await SendAsync(fixture, "industryManager", "InstallJob", [Py.KeyVal(
            ("activityID", new PyInt(IndustryRules.Manufacturing)),
            ("blueprintID", new PyLong(blueprintId)),
            ("blueprintTypeID", new PyInt(BlueprintTypeId)),
            ("facilityID", new PyLong(StructureId)),
            ("solarSystemID", new PyLong(SolarSystemId)),
            ("characterID", new PyLong(fixture.CharacterId)),
            ("corporationID", new PyLong(CorporationId)),
            ("runs", new PyInt(1)),
            ("cost", new PyReal(0)),
            ("tax", new PyReal(0)),
            ("time", new PyReal(0)),
            ("materials", new PyDict([new(new PyInt(38), new PyInt(86))])),
            ("inputLocation", IndustryLocation(StructureId, (int)StructureTypeId, fixture.CharacterId,
                4, SolarSystemId)),
            ("outputLocation", IndustryLocation(StructureId, (int)StructureTypeId, fixture.CharacterId,
                4, SolarSystemId)),
            ("licensedRuns", new PyInt(0)),
            ("productTypeID", new PyInt(165)),
            ("account", Py.Tuple(new PyLong(fixture.CharacterId), new PyInt(1000)))
        )]);
        Assert.Equal(MachoMessage.CallRsp, MachoPacket.Decode(installResponse)?.Type);
        var jobId = ResponseValue(installResponse) switch
        {
            PyInt integer => integer.Value,
            PyLong integer => integer.Value,
            _ => throw new Xunit.Sdk.XunitException("InstallJob must return a job ID."),
        };
        var job = await CallAsync(fixture, "industryManager", "GetJob", new PyLong(jobId));
        Assert.Equal("0.025", await ScalarStringAsync(fixture.Rig.Source,
            $"SELECT activity_snapshot->>'facilityTaxRate' FROM {fixture.Rig.IndustrySchema}.jobs WHERE job_id = {jobId}"));

        await fixture.Rig.RestartAsync();
        var restarted = await RebindAsync(fixture);
        facility = await CallAsync(restarted, "GetFacility", new PyLong(StructureId));
        taxes = await CallAsync(restarted, "GetFacilityTaxes", new PyLong(StructureId), new PyLong(CorporationId));
        Assert.Equal(PyNone.Instance, Field(facility, "tax"));
        Assert.Equal(new PyReal(0.025), Assert.Single(Assert.IsType<PyDict>(Field(facility, "serviceAccess")).Entries,
            entry => entry.Key == new PyInt(FactoryServiceId)).Value);
        Assert.Equal(new PyReal(0.025), Field(taxes, "taxCorporation"));
    }

    [PgFact]
    public async Task Facility_tax_update_rejects_a_member_without_factory_manager_and_has_no_side_effects()
    {
        await using var fixture = await CreateAsync(factoryManager: false);

        var response = await SendAsync(fixture, "SetFacilityTaxes",
            new PyLong(StructureId), new PyLong(CorporationId), TaxValues(0.025));
        Assert.Equal(MachoMessage.ErrorResponse, MachoPacket.Decode(response)?.Type);
        Assert.DoesNotContain(fixture.Capture.Packets,
            packet => MachoPacket.Decode(packet)?.Type == MachoMessage.Notification);

        var taxes = await CallAsync(fixture, "GetFacilityTaxes", new PyLong(StructureId), new PyLong(CorporationId));
        Assert.Equal(new PyInt(0), Field(taxes, "taxCorporation"));
    }

    [PgFact]
    public Task Industry_install_rejects_materials_outside_the_facility_locations_returned_to_the_client() =>
        AssertLocationRefusedAsync("input");

    [PgFact]
    public Task Industry_install_rejects_blueprints_outside_the_facility_locations_returned_to_the_client() =>
        AssertLocationRefusedAsync("blueprint");

    [PgFact]
    public Task Industry_install_rejects_outputs_outside_the_facility_locations_returned_to_the_client() =>
        AssertLocationRefusedAsync("output");

    private static async Task AssertLocationRefusedAsync(string invalidLocation)
    {
        await using var rig = await CharacterHostTests.CharacterHostRig.CreateAsync();
        await rig.AddStructureAsync(StructureId, FactoryServiceId);
        var characterId = await rig.CreateCharacterAsync();
        await rig.AddCorporationMemberAsync(characterId);
        await SetFactoryManagerRoleAsync(rig, characterId);
        await AddCorporationAffiliationAsync(rig, characterId);
        await rig.FundAsync(characterId, 1_000_000m);

        var inventory = new PgItemStore(rig.Source, rig.InventorySchema);
        long blueprintId;
        long materialId;
        var blueprintLocationId = invalidLocation == "blueprint" ? StructureId + 1 : StructureId;
        var inputLocationId = invalidLocation == "input" ? StructureId + 1 : StructureId;
        var outputLocationId = invalidLocation == "output" ? StructureId + 1 : StructureId;
        await using (var connection = await rig.Source.OpenConnectionAsync())
        await using (var transaction = await connection.BeginTransactionAsync())
        {
            blueprintId = await inventory.CreateAsync(connection, transaction,
                new NewItem(681, characterId, blueprintLocationId, 4, Singleton: true));
            materialId = await inventory.CreateAsync(connection, transaction, new NewItem(
                38, characterId, inputLocationId, 4, Quantity: 86));
            await transaction.CommitAsync();
        }

        var capture = new Capture();
        var session = Session(capture, characterId, Eve.Corporations.CorporationRoles.FactoryManager);
        session.Set("charid", new PyLong(characterId));
        session.Set("corpid", new PyLong(CorporationId));
        session.Set("solarsystemid2", new PyLong(SolarSystemId));
        session.Set("stationid", new PyLong(rig.Station));
        session.Set("corpAccountKey", new PyInt(1000));
        rig.Sessions.Enter(characterId, session);

        var facilityManager = Assert.IsAssignableFrom<IService>(rig.Registry.Lookup("facilityManager"));
        var locations = Assert.IsType<PyList>(await facilityManager.CallAsync(new ServiceCall
        {
            Method = "GetFacilityLocations",
            Args = [new PyLong(StructureId), new PyLong(characterId)],
            Session = session,
        }));
        Assert.Collection(locations.Items, location =>
        {
            Assert.Equal(new PyLong(StructureId), Field(location, "itemID"));
            Assert.Equal(new PyInt(4), Field(location, "flagID"));
            Assert.DoesNotContain(Assert.IsType<PyObjectEx>(location).Dict,
                entry => JsSemantics.StringValue(entry.Key) == "inventoryLocationID");
            Assert.Equal(PyBool.True, Field(location, "canTake"));
        });

        var request = Py.KeyVal(
            ("activityID", new PyInt(IndustryRules.Manufacturing)),
            ("blueprintID", new PyLong(blueprintId)),
            ("blueprintTypeID", new PyInt(681)),
            ("facilityID", new PyLong(StructureId)),
            ("solarSystemID", new PyLong(SolarSystemId)),
            ("characterID", new PyLong(characterId)),
            ("corporationID", new PyLong(CorporationId)),
            ("runs", new PyInt(1)),
            ("cost", new PyReal(0)),
            ("tax", new PyReal(0)),
            ("time", new PyReal(0)),
            ("materials", new PyDict([new(new PyInt(38), new PyInt(86))])),
            ("inputLocation", IndustryLocation(inputLocationId, (int)StructureTypeId, characterId,
                4, SolarSystemId)),
            ("outputLocation", IndustryLocation(outputLocationId, (int)StructureTypeId, characterId,
                4, SolarSystemId)),
            ("licensedRuns", new PyInt(0)),
            ("productTypeID", new PyInt(165)),
            ("account", Py.Tuple(new PyLong(characterId), new PyInt(1000))));
        var dispatcher = new PacketDispatcher(rig.Registry, () => rig.NowUnixMs);
        Assert.True(await dispatcher.DispatchAsync(Request(
            "industryManager", "InstallJob", [request], session.SessionId, session.ClientId,
            session.UserId.GetValueOrDefault()), session));

        var response = Assert.Single(capture.Packets,
            packet => MachoPacket.Decode(packet)?.Type != MachoMessage.Notification);
        Assert.Equal(MachoMessage.ErrorResponse, MachoPacket.Decode(response)?.Type);
        Assert.Equal(86, (await inventory.GetAsync(materialId))?.Quantity);
        Assert.False((await inventory.GetAsync(blueprintId))!.BlueprintLocked);
        Assert.Equal(0L, await ScalarLongAsync(rig.Source,
            $"SELECT count(*) FROM {rig.IndustrySchema}.jobs"));
    }

    private static async Task<Fixture> CreateAsync(bool factoryManager = true)
    {
        var rig = await CharacterHostTests.CharacterHostRig.CreateAsync();
        try
        {
            await rig.AddStructureAsync(StructureId, FactoryServiceId);
            var characterId = await rig.CreateCharacterAsync();
            await rig.AddCorporationMemberAsync(characterId);
            if (factoryManager)
                await SetFactoryManagerRoleAsync(rig, characterId);
            await AddCorporationAffiliationAsync(rig, characterId);
            var capture = new Capture();
            var session = Session(capture, characterId, role: factoryManager ? 1024 : 0);
            session.Set("regionid", new PyLong(RegionId));
            session.Set("solarsystemid2", new PyLong(SolarSystemId));
            rig.Sessions.Enter(characterId, session);
            return new Fixture(rig, characterId, capture, session);
        }
        catch
        {
            await rig.DisposeAsync();
            throw;
        }
    }

    private static async Task SetFactoryManagerRoleAsync(CharacterHostTests.CharacterHostRig rig, long characterId)
    {
        await using var connection = await rig.Source.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"UPDATE {rig.CorporationsSchema}.members SET roles = $2 WHERE character_id = $1";
        command.Parameters.Add(new Npgsql.NpgsqlParameter { Value = characterId });
        command.Parameters.Add(new Npgsql.NpgsqlParameter { Value = Eve.Corporations.CorporationRoles.FactoryManager });
        Assert.Equal(1, await command.ExecuteNonQueryAsync());
    }

    private static async Task AddCorporationAffiliationAsync(CharacterHostTests.CharacterHostRig rig, long characterId)
    {
        await using var connection = await rig.Source.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            INSERT INTO {rig.RelationsSchema}.relations (kind, subject, object, value, since_ms, updated_ms)
            VALUES ('affiliation.corporation', $1, $2, 0, 0, 0)
            ON CONFLICT (kind, subject, object) DO NOTHING
            """;
        command.Parameters.Add(new Npgsql.NpgsqlParameter { Value = characterId });
        command.Parameters.Add(new Npgsql.NpgsqlParameter { Value = CorporationId });
        await command.ExecuteNonQueryAsync();
    }

    private static Task<Fixture> RebindAsync(Fixture old, long? sessionId = null)
    {
        var capture = new Capture();
        var session = Session(capture, old.CharacterId, role: 1024, sessionId: sessionId);
        session.Set("regionid", new PyLong(RegionId));
        session.Set("solarsystemid2", new PyLong(SolarSystemId));
        old.Rig.Sessions.Enter(old.CharacterId, session);
        return Task.FromResult(new Fixture(old.Rig, old.CharacterId, capture, session, ownsRig: false));
    }

    private static ClientSession Session(Capture capture, long characterId, long role, long? sessionId = null)
    {
        var session = new ClientSession(capture)
        {
            UserId = 7,
            SessionId = sessionId ?? characterId,
            ClientId = characterId,
            CharacterId = characterId,
        };
        session.Set("charid", new PyLong(characterId));
        session.Set("corpid", new PyLong(CorporationId));
        session.Set("corprole", new PyLong(role));
        return session;
    }

    private static Task<PyValue> CallAsync(Fixture fixture, string method, params PyValue[] args) =>
        CallAsync(fixture, "facilityManager", method, args);

    private static async Task<PyValue> CallAsync(Fixture fixture, string service, string method, params PyValue[] args)
    {
        var response = await SendAsync(fixture, service, method, args);
        return ResponseValue(response);
    }

    private static PyValue ResponseValue(PyValue response)
    {
        var responseObject = Assert.IsType<PyObject>(response);
        var packetArguments = Assert.IsType<PyTuple>(responseObject.Args);
        var payload = Assert.IsType<PyTuple>(packetArguments.Items[4]);
        return payload.Items[0] is PySubStream { Decoded: { } decoded } ? decoded : payload.Items[0];
    }

    private static async Task<PyValue> SendAsync(Fixture fixture, string method, params PyValue[] args) =>
        await SendAsync(fixture, "facilityManager", method, args);

    private static async Task<PyValue> SendAsync(Fixture fixture, string service, string method, PyValue[] args,
        Action<string, Exception?>? onProblem = null)
    {
        fixture.Capture.Packets.Clear();
        var dispatcher = new PacketDispatcher(fixture.Rig.Registry) { OnProblem = onProblem };
        Assert.True(await dispatcher.DispatchAsync(Request(
            service, method, args, fixture.Session.SessionId, fixture.Session.ClientId,
            fixture.Session.UserId.GetValueOrDefault()), fixture.Session));
        return Assert.Single(fixture.Capture.Packets,
            packet => MachoPacket.Decode(packet)?.Type != MachoMessage.Notification);
    }

    private static PyObject Request(string service, string method, PyValue[] args, long sessionId, long clientId, long userId)
    {
        var source = new PyTuple([
            new PyInt(MachoAddress.TypeClient), new PyLong(sessionId), new PyLong(clientId), PyNone.Instance,
        ]);
        var body = new PyTuple([new PyInt(1), new PyStr(method), new PyTuple(args), PyNone.Instance]);
        var payload = new PyTuple([new PyTuple([new PyInt(0), new PySubStream(body)])]);
        var head = new PyValue[]
        {
            new PyInt(6), source, MachoAddress.Any(service).Encode(), new PyLong(userId), payload, PyNone.Instance,
        };
        return new PyObject(
            new PyStr("carbon.common.script.net.machoNetPacket.CallReq"),
            new PyTuple([.. head, .. Enumerable.Repeat<PyValue>(PyNone.Instance, 14 - head.Length)]));
    }

    private static PyValue TaxValues(double corporationRate) => new PyDict([
        new(new PyStr("taxCorporation"), new PyReal(corporationRate)),
        new(new PyStr("taxAlliance"), PyNone.Instance),
        new(new PyStr("taxStandingsHorrible"), PyNone.Instance),
        new(new PyStr("taxStandingsBad"), PyNone.Instance),
        new(new PyStr("taxStandingsNeutral"), PyNone.Instance),
        new(new PyStr("taxStandingsGood"), PyNone.Instance),
        new(new PyStr("taxStandingsHigh"), PyNone.Instance),
    ]);

    private static PyObjectEx IndustryLocation(
        long itemId,
        int typeId,
        long ownerId,
        int flagId,
        long solarSystemId,
        bool canView = true,
        bool canTake = true) => new(
        ObjectExKind.Ex1,
        new PyStr("industry.Location"),
        [],
        [
            new(new PyStr("itemID"), new PyLong(itemId)),
            new(new PyStr("typeID"), new PyInt(typeId)),
            new(new PyStr("ownerID"), new PyLong(ownerId)),
            new(new PyStr("flagID"), new PyInt(flagId)),
            new(new PyStr("solarSystemID"), new PyInt(checked((int)solarSystemId))),
            new(new PyStr("canView"), PyValue.Of(canView)),
            new(new PyStr("canTake"), PyValue.Of(canTake)),
        ]);

    private static async Task<long> ScalarLongAsync(NpgsqlDataSource source, string sql)
    {
        await using var command = source.CreateCommand(sql);
        return Convert.ToInt64(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static async Task<string> ScalarStringAsync(NpgsqlDataSource source, string sql)
    {
        await using var command = source.CreateCommand(sql);
        var value = await command.ExecuteScalarAsync();
        return Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? "";
    }

    private static PyValue Field(PyValue value, string name)
    {
        IEnumerable<KeyValuePair<PyValue, PyValue>> entries = value switch
        {
            PyObject { Args: PyDict args } => args.Entries,
            PyObjectEx objectEx => objectEx.Dict,
            _ => Assert.IsType<PyDict>(Assert.IsType<PyObject>(value).Args).Entries,
        };
        return entries.Single(pair => JsSemantics.StringValue(pair.Key) == name).Value;
    }

    private static void AssertFacilityUpdatedNotification(Capture capture, long facilityId)
    {
        var notificationPacket = Assert.Single(capture.Packets,
            packet => MachoPacket.Decode(packet)?.Type == MachoMessage.Notification);
        var packet = MachoPacket.Decode(notificationPacket)!;
        Assert.Equal(MachoMessage.Notification, packet.Type);
        Assert.Equal("OnFacilitiesUpdated", packet.Dest.BroadcastId);
        Assert.Equal("clientID", packet.Dest.IdType);
        var call = Assert.IsType<PyTuple>(Assert.IsType<PyTuple>(packet.Payload).Items.Single());
        var inner = Assert.IsType<PyTuple>(MarshalCodec.Decode(Assert.IsType<PyBuffer>(call.Items[1]).Value));
        var notification = Assert.IsType<PyTuple>(inner.Items[1]);
        Assert.Equal(new PyInt(1), notification.Items[0]);
        Assert.Equal(new PyTuple([new PyList([new PyLong(facilityId)])]), notification.Items[1]);
    }

    private static void AssertIndustryJobNotification(
        Capture capture, long jobId, long ownerId, long installerId, long blueprintId, int status,
        PyValue? successfulRuns = null)
    {
        var notification = Assert.Single(capture.Packets,
            value => MachoPacket.Decode(value) is { Type: MachoMessage.Notification, Dest.BroadcastId: "OnIndustryJob" });
        var packet = MachoPacket.Decode(notification)!;
        Assert.Equal(MachoMessage.Notification, packet.Type);
        Assert.Equal("OnIndustryJob", packet.Dest.BroadcastId);
        Assert.Equal("charid", packet.Dest.IdType);
        var call = Assert.IsType<PyTuple>(Assert.IsType<PyTuple>(packet.Payload).Items.Single());
        var inner = Assert.IsType<PyTuple>(MarshalCodec.Decode(Assert.IsType<PyBuffer>(call.Items[1]).Value));
        var eventCall = Assert.IsType<PyTuple>(inner.Items[1]);
        Assert.Equal(new PyInt(1), eventCall.Items[0]);
        var eventArguments = Assert.IsType<PyTuple>(eventCall.Items[1]);
        Assert.Collection(eventArguments.Items,
            value => Assert.Equal(new PyInt(jobId), value),
            value => Assert.Equal(new PyInt(ownerId), value),
            value => Assert.Equal(new PyInt(blueprintId), value),
            value => Assert.Equal(new PyInt(installerId), value),
            value => Assert.Equal(new PyInt(status), value),
            value => Assert.Equal(successfulRuns ?? new PyInt(status == 1 ? 0 : 1), value));
    }

    private sealed class Capture : IPacketSink
    {
        public List<PyValue> Packets { get; } = [];
        public void Send(PyValue packet) => Packets.Add(packet);
    }

    private sealed class Fixture(
        CharacterHostTests.CharacterHostRig rig,
        long characterId,
        Capture capture,
        ClientSession session,
        bool ownsRig = true) : IAsyncDisposable
    {
        public CharacterHostTests.CharacterHostRig Rig { get; } = rig;
        public long CharacterId { get; } = characterId;
        public Capture Capture { get; } = capture;
        public ClientSession Session { get; } = session;

        public ValueTask DisposeAsync() => ownsRig ? Rig.DisposeAsync() : ValueTask.CompletedTask;
    }
}
