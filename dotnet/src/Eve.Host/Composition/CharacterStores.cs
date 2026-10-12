using System.Net;
using System.Net.Sockets;
using Eve.Characters;
using Eve.Data;
using Eve.Host;
using Eve.Identity;
using Eve.Dogma;
using Eve.Fitting;
using Eve.GameStore;
using System.Text.Json;
using System.Text.Json.Nodes;
using Eve.Inventory;
using Eve.Ships;
using Eve.Marshal;
using Eve.Npc;
using Eve.Sde;
using Eve.Skills;
using Eve.Universe;
using Eve.Wallet;
using Npgsql;
using Sentry;
using Eve.Services;
using Eve.Services.CharacterSelect;
using Eve.Services.Core;
using Eve.Services.Dogma;
using Eve.Services.Inventory;
using Eve.Services.CharacterInfo;
using Eve.Services.Skills;
using Eve.Services.Slash;
using Eve.Services.Space;
using Eve.Space;
using Eve.Services.Station;
using Eve.Services.Wallet;
using static Eve.Host.HostConfiguration;

namespace Eve.Host;

internal sealed partial class CharacterServicesComposition
{
    private async Task OpenStorageAndCrimewatch()
    {
            // the mining ledger's queries over the space schema (the table is migration 0003 of the ships' migrations, applied just above)
            miningLedgerReads = Eve.Services.Core.MiningLedgerQueries.Register(
                new Eve.ReadSide.ReadSide(source, new Dictionary<string, string> { [Eve.Services.Core.MiningLedgerQueries.SpaceSchema] = spaceSchema }));
        assetSafetySchema = Env("EVE_ASSET_SAFETY_SCHEMA", PersistenceStructureAssetSafetyReadModel.DefaultSchema);
        await PersistenceStructureAssetSafetyReadModel.MigrateAsync(source, assetSafetySchema);
        crimewatchSchema = Env("EVE_CRIMEWATCH_SCHEMA", Eve.Crimewatch.PgCrimewatchStore.DefaultSchema);
        await Eve.Crimewatch.PgCrimewatchStore.MigrateAsync(source, crimewatchSchema);
        crimewatchStore = new Eve.Crimewatch.PgCrimewatchStore(source, crimewatchSchema);
        // agent offers and mission states (AG-2 / A5): the agents schema, migrated here
        agentMissionStore = new Eve.Agents.AgentMissionStore(source, Env("EVE_AGENTS_SCHEMA", Eve.Agents.AgentMissionStore.DefaultSchema));
        await Eve.Agents.AgentMissionStore.MigrateAsync(source, agentMissionStore.Schema);
        agentResearchProjects = new Eve.Agents.ResearchProjectStore(source, agentMissionStore.Schema);
        await Eve.Agents.ResearchProjectStore.MigrateAsync(source, agentMissionStore.Schema);
        agentLocates = new Eve.Agents.LocateStore(source, agentMissionStore.Schema);

        sde = new SdeReader(source);
    }

    private async Task LoadCatalogsAndStores()
    {
        startText = Env("EVE_START_STATION", "60015249");
        startStation = startText == "school" ? null : long.Parse(startText, System.Globalization.CultureInfo.InvariantCulture);
        catalog = await CreationCatalog.LoadAsync(sde, startStation);
        characters = new PgCharacterStore(source, characterSchema);
        items = new PgItemStore(source, inventorySchema);
        spaceItemStore = new PgSpaceItemStore(source, spaceSchema);
        itemTypes = new ItemTypeCatalog(sde);
        skills = new PgSkillStore(source, skillsSchema);
        skillCatalog = await SkillCatalog.LoadAsync(sde);
        wallets = new PgWalletStore(source, walletSchema);
        modules = new PgModuleStateStore(source, fittingSchema);
        savedFittings = new PgSavedFittingStore(source, fittingSchema);
        stations = await StationCatalog.LoadAsync(sde);
        solarSystems = await SolarSystemCatalog.LoadAsync(sde);
        dogma = await DogmaStatic.LoadAsync(sde);
    }
}
