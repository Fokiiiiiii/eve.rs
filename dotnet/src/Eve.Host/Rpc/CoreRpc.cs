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
    /// <summary>Character select, lookup, security, faction warfare, fighters, ess and resource services, ship kill counter.</summary>
    /// <summary>The fighter provider: a pilot that enters space takes up the squadrons its character has out there (a restart leaves them in space).</summary>
    private Eve.Services.Space.SpaceFighters FighterProvider()
    {
        var fighters = new Eve.Services.Space.SpaceFighters(sim, ships, dogma, session => spaceSessions?.SolarSystemOf(session), message => Console.Error.WriteLine($"eve-host: fighters: {message}"),
            systemId => spaceSessions?.SecurityOfSystem(systemId));
        if (spaceSessions is not null)
        {
            spaceSessions.PilotEntered = fighters.RebuildAsync;
        }
        return fighters;
    }

    /// <summary>The clone grades the multi-login check reads: the same stored subscription state as every other answer.</summary>
    private SubscriptionMgrService? _cloneGradeSource;

    private SubscriptionMgrService CloneGradeSource => _cloneGradeSource ??= new SubscriptionMgrService(omega);

    /// <summary>
    /// Who is in each solar system now. The cap is <c>EVE_SYSTEM_POPULATION_CAP</c> characters per system (a positive
    /// whole number), or <see cref="SystemPopulation.DefaultCapacity"/>.
    /// </summary>
    private SystemPopulation? _systemPopulation;

    private SystemPopulation SystemPopulationModel => _systemPopulation ??= new SystemPopulation(
        characterSessions, solarSystems,
        int.TryParse(Environment.GetEnvironmentVariable("EVE_SYSTEM_POPULATION_CAP"), out var cap) && cap > 0
            ? cap
            : SystemPopulation.DefaultCapacity);

    private IReadOnlyList<IService> CoreRpc() =>
    [
            new CharUnboundMgrService(
                accounts, characters, items, training, wallets, creation, catalog, online, characterEntryHooks, Random.Shared,
                newCharacterExperienceMode == "tutorial",
                sendWelcomeMailAsync: SendNewCharacterWelcomeMailAsync,
                unreadMailCount: ReadUnreadMailCount,
                securityStatusOf: securityStatusOf,
                population: SystemPopulationModel),
            new MultiLoginBlockerService(session => CloneGradeSource.CloneGradeOf(session.Get("userid"))),
            new PopulationCapService(characters, SystemPopulationModel),
            new Eve.Services.CharacterInfo.ConfigService(characters, corporations, stations, solarSystems,
                ct => market.LatestAverageSalesAsync(ct), items, dogma),
            Eve.Services.Core.CoreServices.CreateLookupService(
                new Eve.Host.PersistenceLookupDirectory(characters, corporations, sde, stations)),
            new Eve.Services.Core.SecurityMgrService(),
            new Eve.Services.Core.RWManagerService(),
            new Eve.Services.Core.FwCharacterEnlistmentMgrService(new Eve.Services.Core.FwCharacterEnlistmentRpcCallbacks(
                ReadCharacterStateAsync: async (characterId, cancellationToken) =>
                {
                    var character = await characters.GetAsync(characterId, cancellationToken);
                    return character is null
                        ? null
                        : new Eve.Services.Core.FwCharacterWarfareState(character.DirectFactionId, character.DirectEnlistmentCooldownFileTime);
                },
                ReadCorporationFactionIdAsync: async (corporationId, cancellationToken) =>
                    Eve.Services.Corporations.CorporationWire.FactionId(await corporations.GetAsync(corporationId, cancellationToken)),
                ReadAllowedFactionsAsync: async (corporationId, cancellationToken) =>
                    Eve.Services.Corporations.CorporationWire.AllowedEnlistmentFactions(await corporations.GetAsync(corporationId, cancellationToken)),
                SetAllowedFactionsAsync: async (characterId, corporationId, factions, cancellationToken) =>
                    await corporations.SetFactionWarfareAllowedEnlistmentFactionsAsync(characterId, corporationId, factions, cancellationToken),
                SaveCharacterStateAsync: async (characterId, factionId, cooldown, cancellationToken) =>
                    await characters.SetFactionWarfareStateAsync(characterId, factionId, cooldown, cancellationToken))),
            new Eve.Services.Core.FwVictoryPointMgrService(),
            new Eve.Services.Core.InsurgencySolarsystemService(new Eve.Services.Core.InsurgencySolarsystemRpcCallbacks(ReadInsurgencyCampaignsAsync)),
            new Eve.Services.Core.AlertService(),
            new Eve.Services.Core.FighterMgrService { Provider = FighterProvider() },
            new Eve.Services.Space.EssMgrService(EssCallbacks()),
            new Eve.Services.Space.DynamicResourceCacheMgrService(readSystem: ReadEssSystemAsync, listSystems: ListEssSystemsAsync, quadrantOf: EssQuadrantOf),
            new Eve.Services.Core.ShipKillCounterService(ct => ReadGameStoreTableAsync("shipKillCounters", ct)),
    ];
}
