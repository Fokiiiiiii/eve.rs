# EveJS v0.12.9 vs 261108.1

| Area | Added | Removed | Content changed | Line endings only |
|---|---:|---:|---:|---:|
| Space, combat and sites | 153 | 55 | 237 | 412 |
| Industry and market | 3 | 0 | 20 | 20 |
| Characters, agents and events | 89 | 4 | 35 | 23 |
| Wormholes and exploration | 8 | 0 | 10 | 18 |
| GM, chat and nodegraph | 22 | 0 | 14 | 31 |
| Content, config, static tables | 52 | 5 | 32 | 55 |
| Other server services | 128 | 7 | 266 | 290 |
| Runtime, network and packaging | 19 | 53 | 52 | 64 |
| Mods and tools | 6 | 48 | 14 | 54 |
| Tests and fixtures | 0 | 35 | 1 | 1 |
| Documentation | 2 | 4 | 16 | 1 |

The first four columns are a disjoint path classification of the file inventory. After normalizing CRLF and CR to LF, the two trees contain 2,209 old tracked files and 2,480 new distribution files: 1,998 common paths, 482 additions, 211 removals, 697 content changes, 969 line-ending-only changes, and 332 byte-identical common files. No rename was detected by exact normalized-content matching or by same-basename similarity of at least 55%; paths with substantial content and name changes remain reported as add/remove pairs. The complete path lists are in the file-level manifest at the end.

<details>
<summary>Directory-level path counts</summary>

Each file is counted once under its immediate parent directory. Functional groups follow the summary table classification.

| Functional area | Directory | Added | Removed | Content changed | Line endings only |
|---|---|---:|---:|---:|---:|
| Characters, agents and events | `server/src/services/agent` | 9 | 3 | 10 | 6 |
| Characters, agents and events | `server/src/services/campaign` | 7 | 0 | 2 | 1 |
| Characters, agents and events | `server/src/services/career` | 20 | 0 | 0 | 0 |
| Characters, agents and events | `server/src/services/character` | 9 | 1 | 20 | 13 |
| Characters, agents and events | `server/src/services/npe` | 10 | 0 | 3 | 3 |
| Characters, agents and events | `server/src/services/seasons` | 34 | 0 | 0 | 0 |
| Content, config, static tables | `config` | 0 | 0 | 5 | 1 |
| Content, config, static tables | `content-packs` | 15 | 1 | 0 | 0 |
| Content, config, static tables | `server/src/config` | 22 | 0 | 3 | 1 |
| Content, config, static tables | `server/src/config/schema` | 0 | 0 | 5 | 0 |
| Content, config, static tables | `server/src/contentPacks` | 4 | 0 | 2 | 0 |
| Content, config, static tables | `server/src/gameStore` | 0 | 0 | 5 | 10 |
| Content, config, static tables | `server/src/gameStore/data` | 1 | 0 | 0 | 0 |
| Content, config, static tables | `server/src/gameStore/data/authoredSpaceProps` | 0 | 0 | 0 | 16 |
| Content, config, static tables | `server/src/gameStore/data/fighterAbilities` | 0 | 0 | 0 | 1 |
| Content, config, static tables | `tools/DatabaseCreator` | 0 | 3 | 1 | 6 |
| Content, config, static tables | `tools/DatabaseCreator/staticTables/agentAuthority` | 0 | 0 | 1 | 0 |
| Content, config, static tables | `tools/DatabaseCreator/staticTables/asteroidTypesBySolarSystemID` | 0 | 0 | 0 | 1 |
| Content, config, static tables | `tools/DatabaseCreator/staticTables/capitalNpcAuthority` | 0 | 0 | 0 | 1 |
| Content, config, static tables | `tools/DatabaseCreator/staticTables/characterCreationSchools` | 1 | 0 | 0 | 0 |
| Content, config, static tables | `tools/DatabaseCreator/staticTables/clientEntityStandings` | 0 | 0 | 0 | 1 |
| Content, config, static tables | `tools/DatabaseCreator/staticTables/clientMicroJumpComponents` | 1 | 0 | 0 | 0 |
| Content, config, static tables | `tools/DatabaseCreator/staticTables/clientPvpFilaments` | 1 | 0 | 0 | 0 |
| Content, config, static tables | `tools/DatabaseCreator/staticTables/clientShipLinkComponents` | 1 | 0 | 0 | 0 |
| Content, config, static tables | `tools/DatabaseCreator/staticTables/clientTowComponents` | 1 | 0 | 0 | 0 |
| Content, config, static tables | `tools/DatabaseCreator/staticTables/dungeonAuthority` | 0 | 0 | 1 | 0 |
| Content, config, static tables | `tools/DatabaseCreator/staticTables/evermarksCatalog` | 0 | 0 | 1 | 0 |
| Content, config, static tables | `tools/DatabaseCreator/staticTables/expertSystems` | 0 | 0 | 0 | 1 |
| Content, config, static tables | `tools/DatabaseCreator/staticTables/explorationAuthority` | 0 | 0 | 1 | 0 |
| Content, config, static tables | `tools/DatabaseCreator/staticTables/explorationWormholeStatic` | 0 | 0 | 1 | 0 |
| Content, config, static tables | `tools/DatabaseCreator/staticTables/fighterAbilities` | 0 | 0 | 0 | 2 |
| Content, config, static tables | `tools/DatabaseCreator/staticTables/incursionSpawnHistory` | 0 | 1 | 0 | 0 |
| Content, config, static tables | `tools/DatabaseCreator/staticTables/missionAuthority` | 0 | 0 | 1 | 0 |
| Content, config, static tables | `tools/DatabaseCreator/staticTables/newEdenStore` | 0 | 0 | 0 | 1 |
| Content, config, static tables | `tools/DatabaseCreator/staticTables/npcBehaviorProfiles` | 0 | 0 | 1 | 0 |
| Content, config, static tables | `tools/DatabaseCreator/staticTables/npcHostileUtilities` | 0 | 0 | 0 | 1 |
| Content, config, static tables | `tools/DatabaseCreator/staticTables/npcLoadouts` | 0 | 0 | 0 | 1 |
| Content, config, static tables | `tools/DatabaseCreator/staticTables/npcLootTables` | 0 | 0 | 1 | 0 |
| Content, config, static tables | `tools/DatabaseCreator/staticTables/npcProfiles` | 0 | 0 | 1 | 0 |
| Content, config, static tables | `tools/DatabaseCreator/staticTables/npcSpawnGroups` | 0 | 0 | 1 | 0 |
| Content, config, static tables | `tools/DatabaseCreator/staticTables/npcStandingsAuthority` | 0 | 0 | 1 | 0 |
| Content, config, static tables | `tools/DatabaseCreator/staticTables/npcStartupRules` | 0 | 0 | 0 | 1 |
| Content, config, static tables | `tools/DatabaseCreator/staticTables/reprocessingClientRandomizedMaterials` | 0 | 0 | 0 | 1 |
| Content, config, static tables | `tools/DatabaseCreator/staticTables/researchFieldAuthority` | 0 | 0 | 0 | 1 |
| Content, config, static tables | `tools/DatabaseCreator/staticTables/shipInsurancePrices` | 0 | 0 | 0 | 1 |
| Content, config, static tables | `tools/DatabaseCreator/staticTables/shipSkinComponentAuthority` | 1 | 0 | 0 | 0 |
| Content, config, static tables | `tools/DatabaseCreator/staticTables/shipSkinComponentListingAuthority` | 1 | 0 | 0 | 0 |
| Content, config, static tables | `tools/DatabaseCreator/staticTables/shipSkinMarketAuthority` | 1 | 0 | 0 | 0 |
| Content, config, static tables | `tools/DatabaseCreator/staticTables/shipSkinSequencingAuthority` | 1 | 0 | 0 | 0 |
| Content, config, static tables | `tools/DatabaseCreator/staticTables/shipSkinStoreAuthority` | 1 | 0 | 0 | 0 |
| Content, config, static tables | `tools/DatabaseCreator/staticTables/skillTrainingAlphaCaps` | 0 | 0 | 0 | 1 |
| Content, config, static tables | `tools/DatabaseCreator/staticTables/stargateVisualOverrides` | 0 | 0 | 0 | 1 |
| Content, config, static tables | `tools/DatabaseCreator/staticTables/stationDockingPlacements` | 0 | 0 | 0 | 1 |
| Content, config, static tables | `tools/DatabaseCreator/staticTables/stationGraphicLocators` | 0 | 0 | 0 | 1 |
| Content, config, static tables | `tools/DatabaseCreator/staticTables/stationStandingsRestrictions` | 0 | 0 | 0 | 1 |
| Content, config, static tables | `tools/DatabaseCreator/staticTables/structureGraphicLocators` | 0 | 0 | 0 | 1 |
| Content, config, static tables | `tools/DatabaseCreator/staticTables/trigDrifterSpawnAuthority` | 0 | 0 | 0 | 1 |
| Documentation | `.` | 0 | 0 | 1 | 0 |
| Documentation | `doc` | 1 | 1 | 11 | 0 |
| Documentation | `mods/eft-fit-spawner` | 0 | 1 | 0 | 0 |
| Documentation | `mods/lonely-fleet-command` | 0 | 1 | 0 | 0 |
| Documentation | `tools/ContentPacks` | 0 | 0 | 0 | 1 |
| Documentation | `tools/ErrorReport` | 0 | 0 | 1 | 0 |
| Documentation | `tools/IncursionData` | 0 | 1 | 0 | 0 |
| Documentation | `tools/market-seed` | 0 | 0 | 1 | 0 |
| Documentation | `tools/market-seederv2` | 0 | 0 | 1 | 0 |
| Documentation | `tools/market-seederv3` | 0 | 0 | 1 | 0 |
| Documentation | `tools/market-seederv4` | 1 | 0 | 0 | 0 |
| GM, chat and nodegraph | `server/src/services/admin` | 0 | 0 | 1 | 0 |
| GM, chat and nodegraph | `server/src/services/chat` | 0 | 0 | 5 | 10 |
| GM, chat and nodegraph | `server/src/services/chat/capitalNpc` | 0 | 0 | 0 | 5 |
| GM, chat and nodegraph | `server/src/services/chat/commands` | 16 | 0 | 7 | 13 |
| GM, chat and nodegraph | `server/src/services/chat/trigDrifter` | 0 | 0 | 0 | 1 |
| GM, chat and nodegraph | `server/src/services/chat/wormhole` | 0 | 0 | 0 | 2 |
| GM, chat and nodegraph | `server/src/services/nodegraph` | 6 | 0 | 1 | 0 |
| Industry and market | `externalservices/market-server` | 0 | 0 | 1 | 0 |
| Industry and market | `externalservices/market-server/config` | 0 | 0 | 0 | 3 |
| Industry and market | `externalservices/market-server/crates/market-common/src` | 0 | 0 | 1 | 0 |
| Industry and market | `externalservices/market-server/src` | 0 | 0 | 2 | 1 |
| Industry and market | `externalservices/market-server/src/state` | 0 | 0 | 2 | 0 |
| Industry and market | `server/src/services/industry` | 3 | 0 | 10 | 12 |
| Industry and market | `server/src/services/market` | 0 | 0 | 4 | 4 |
| Mods and tools | `mods/eft-fit-spawner` | 0 | 3 | 0 | 0 |
| Mods and tools | `mods/eft-fit-spawner/payload/server` | 0 | 5 | 0 | 0 |
| Mods and tools | `mods/lonely-fleet-command` | 0 | 2 | 0 | 0 |
| Mods and tools | `mods/lonely-fleet-command/payload/server` | 0 | 35 | 0 | 0 |
| Mods and tools | `scripts/Diagnostics` | 1 | 0 | 0 | 0 |
| Mods and tools | `tools` | 0 | 1 | 0 | 1 |
| Mods and tools | `tools/ClientCodeGrabber` | 1 | 0 | 1 | 1 |
| Mods and tools | `tools/ClientSETUP` | 0 | 0 | 1 | 6 |
| Mods and tools | `tools/ClientSETUP/legacy-python` | 0 | 0 | 0 | 1 |
| Mods and tools | `tools/ClientSETUP/scripts` | 0 | 0 | 5 | 6 |
| Mods and tools | `tools/ConfigEditor` | 0 | 0 | 0 | 6 |
| Mods and tools | `tools/ErrorReport` | 1 | 0 | 1 | 4 |
| Mods and tools | `tools/IncursionData` | 0 | 2 | 0 | 0 |
| Mods and tools | `tools/Maintenance` | 1 | 0 | 0 | 2 |
| Mods and tools | `tools/NewEdenStoreEditor` | 0 | 0 | 0 | 5 |
| Mods and tools | `tools/NewEdenStoreEditor/public` | 0 | 0 | 0 | 3 |
| Mods and tools | `tools/Setup` | 0 | 0 | 1 | 0 |
| Mods and tools | `tools/Updater` | 0 | 0 | 0 | 2 |
| Mods and tools | `tools/market-seed` | 0 | 0 | 2 | 1 |
| Mods and tools | `tools/market-seed/config` | 0 | 0 | 0 | 3 |
| Mods and tools | `tools/market-seed/src` | 0 | 0 | 1 | 1 |
| Mods and tools | `tools/market-seederv2` | 0 | 0 | 1 | 0 |
| Mods and tools | `tools/market-seederv2/config` | 0 | 0 | 0 | 1 |
| Mods and tools | `tools/market-seederv2/src` | 0 | 0 | 1 | 0 |
| Mods and tools | `tools/market-seederv3` | 0 | 0 | 0 | 1 |
| Mods and tools | `tools/market-seederv3/config` | 0 | 0 | 0 | 2 |
| Mods and tools | `tools/market-seederv3/src` | 0 | 0 | 0 | 7 |
| Mods and tools | `tools/market-seederv4` | 1 | 0 | 0 | 0 |
| Mods and tools | `tools/market-seederv4/config` | 1 | 0 | 0 | 0 |
| Mods and tools | `tools/moon-data` | 0 | 0 | 0 | 1 |
| Other server services | `server/src/services` | 0 | 0 | 2 | 0 |
| Other server services | `server/src/services/_other` | 0 | 0 | 3 | 2 |
| Other server services | `server/src/services/_shared` | 3 | 0 | 9 | 17 |
| Other server services | `server/src/services/account` | 2 | 0 | 10 | 6 |
| Other server services | `server/src/services/achievement` | 0 | 0 | 2 | 1 |
| Other server services | `server/src/services/agency` | 0 | 0 | 1 | 0 |
| Other server services | `server/src/services/alert` | 0 | 0 | 0 | 1 |
| Other server services | `server/src/services/bookmark` | 0 | 1 | 4 | 4 |
| Other server services | `server/src/services/bounty` | 0 | 0 | 3 | 4 |
| Other server services | `server/src/services/browser` | 0 | 0 | 0 | 1 |
| Other server services | `server/src/services/cache` | 0 | 0 | 1 | 2 |
| Other server services | `server/src/services/calendar` | 0 | 0 | 1 | 9 |
| Other server services | `server/src/services/config` | 0 | 0 | 1 | 0 |
| Other server services | `server/src/services/contracts` | 4 | 0 | 3 | 1 |
| Other server services | `server/src/services/corporation` | 10 | 0 | 37 | 14 |
| Other server services | `server/src/services/dailyGoals` | 0 | 0 | 3 | 0 |
| Other server services | `server/src/services/dogma` | 3 | 0 | 3 | 2 |
| Other server services | `server/src/services/dogma/brain` | 0 | 0 | 1 | 0 |
| Other server services | `server/src/services/dogma/brain/providers` | 0 | 0 | 1 | 2 |
| Other server services | `server/src/services/dogma/dogmaService` | 0 | 0 | 5 | 6 |
| Other server services | `server/src/services/dogma/implants` | 0 | 0 | 1 | 1 |
| Other server services | `server/src/services/drone` | 0 | 0 | 2 | 1 |
| Other server services | `server/src/services/dynamic` | 10 | 0 | 3 | 1 |
| Other server services | `server/src/services/eveOffline` | 0 | 0 | 1 | 0 |
| Other server services | `server/src/services/evermarks` | 0 | 0 | 1 | 8 |
| Other server services | `server/src/services/fighter` | 1 | 0 | 4 | 1 |
| Other server services | `server/src/services/fighter/npc` | 0 | 0 | 1 | 2 |
| Other server services | `server/src/services/fitting` | 0 | 0 | 6 | 2 |
| Other server services | `server/src/services/fleetShare` | 1 | 0 | 0 | 0 |
| Other server services | `server/src/services/fleets` | 2 | 0 | 4 | 6 |
| Other server services | `server/src/services/freelance` | 5 | 0 | 0 | 0 |
| Other server services | `server/src/services/insurance` | 0 | 0 | 1 | 3 |
| Other server services | `server/src/services/inventory` | 12 | 0 | 18 | 11 |
| Other server services | `server/src/services/inventory/invBroker` | 4 | 0 | 2 | 6 |
| Other server services | `server/src/services/killmail` | 0 | 0 | 4 | 2 |
| Other server services | `server/src/services/logging` | 0 | 0 | 0 | 2 |
| Other server services | `server/src/services/login` | 0 | 0 | 0 | 3 |
| Other server services | `server/src/services/loyalty` | 0 | 0 | 0 | 2 |
| Other server services | `server/src/services/machoNet` | 0 | 0 | 1 | 4 |
| Other server services | `server/src/services/mail` | 0 | 0 | 4 | 0 |
| Other server services | `server/src/services/map` | 0 | 0 | 8 | 1 |
| Other server services | `server/src/services/mining` | 5 | 0 | 7 | 16 |
| Other server services | `server/src/services/moduleGrouping` | 0 | 0 | 2 | 2 |
| Other server services | `server/src/services/newEdenStore` | 3 | 0 | 2 | 6 |
| Other server services | `server/src/services/notifications` | 1 | 0 | 3 | 0 |
| Other server services | `server/src/services/online` | 0 | 0 | 1 | 4 |
| Other server services | `server/src/services/overview` | 0 | 0 | 0 | 2 |
| Other server services | `server/src/services/ping` | 0 | 0 | 0 | 1 |
| Other server services | `server/src/services/planet` | 5 | 0 | 6 | 2 |
| Other server services | `server/src/services/populationCap` | 0 | 0 | 0 | 1 |
| Other server services | `server/src/services/raffles` | 0 | 0 | 2 | 16 |
| Other server services | `server/src/services/reprocessing` | 2 | 0 | 2 | 7 |
| Other server services | `server/src/services/rewards` | 3 | 0 | 1 | 0 |
| Other server services | `server/src/services/scheduler` | 0 | 0 | 3 | 1 |
| Other server services | `server/src/services/security` | 8 | 0 | 6 | 2 |
| Other server services | `server/src/services/settings` | 0 | 0 | 0 | 1 |
| Other server services | `server/src/services/ship` | 21 | 2 | 24 | 14 |
| Other server services | `server/src/services/skills` | 1 | 0 | 3 | 4 |
| Other server services | `server/src/services/skills/boosters` | 0 | 0 | 1 | 1 |
| Other server services | `server/src/services/skills/certificates` | 0 | 0 | 1 | 1 |
| Other server services | `server/src/services/skills/expertSystems` | 0 | 0 | 3 | 3 |
| Other server services | `server/src/services/skills/plans` | 0 | 0 | 1 | 1 |
| Other server services | `server/src/services/skills/skillbooks` | 0 | 0 | 0 | 1 |
| Other server services | `server/src/services/skills/trading` | 0 | 0 | 1 | 3 |
| Other server services | `server/src/services/skills/training` | 0 | 0 | 3 | 3 |
| Other server services | `server/src/services/spaceComponents` | 8 | 0 | 0 | 0 |
| Other server services | `server/src/services/starbase` | 1 | 0 | 6 | 18 |
| Other server services | `server/src/services/station` | 0 | 0 | 4 | 6 |
| Other server services | `server/src/services/structure` | 10 | 4 | 29 | 32 |
| Other server services | `server/src/services/subscription` | 2 | 0 | 1 | 0 |
| Other server services | `server/src/services/superweapons` | 0 | 0 | 0 | 2 |
| Other server services | `server/src/services/superweapons/npc` | 0 | 0 | 0 | 1 |
| Other server services | `server/src/services/support` | 0 | 0 | 1 | 9 |
| Other server services | `server/src/services/tournament` | 0 | 0 | 0 | 1 |
| Other server services | `server/src/services/trade` | 0 | 0 | 1 | 1 |
| Other server services | `server/src/services/user` | 1 | 0 | 1 | 0 |
| Runtime, network and packaging | `.` | 1 | 1 | 7 | 8 |
| Runtime, network and packaging | `docker` | 0 | 0 | 1 | 3 |
| Runtime, network and packaging | `server` | 0 | 0 | 4 | 1 |
| Runtime, network and packaging | `server/contracts` | 0 | 0 | 1 | 0 |
| Runtime, network and packaging | `server/src/RemoteRepShow` | 0 | 0 | 1 | 1 |
| Runtime, network and packaging | `server/src/_secondary/chat` | 0 | 0 | 2 | 2 |
| Runtime, network and packaging | `server/src/_secondary/express` | 0 | 0 | 3 | 6 |
| Runtime, network and packaging | `server/src/_secondary/express/gatewayServices` | 13 | 0 | 14 | 10 |
| Runtime, network and packaging | `server/src/_secondary/fitting` | 0 | 0 | 2 | 3 |
| Runtime, network and packaging | `server/src/_secondary/image` | 0 | 0 | 1 | 0 |
| Runtime, network and packaging | `server/src/_secondary/image/generated/Alliance` | 0 | 0 | 0 | 1 |
| Runtime, network and packaging | `server/src/_secondary/image/generated/agents` | 0 | 0 | 0 | 1 |
| Runtime, network and packaging | `server/src/_secondary/launcher` | 0 | 0 | 0 | 1 |
| Runtime, network and packaging | `server/src/_secondary/lonelyFleetCommand` | 0 | 51 | 0 | 0 |
| Runtime, network and packaging | `server/src/_secondary/redshift` | 0 | 0 | 0 | 1 |
| Runtime, network and packaging | `server/src/common` | 1 | 0 | 2 | 8 |
| Runtime, network and packaging | `server/src/edge` | 0 | 0 | 0 | 2 |
| Runtime, network and packaging | `server/src/edge/chat` | 0 | 0 | 0 | 2 |
| Runtime, network and packaging | `server/src/edge/gateway` | 0 | 0 | 0 | 3 |
| Runtime, network and packaging | `server/src/network` | 2 | 0 | 5 | 3 |
| Runtime, network and packaging | `server/src/network/tcp` | 1 | 1 | 5 | 1 |
| Runtime, network and packaging | `server/src/network/tcp/utils` | 0 | 0 | 1 | 1 |
| Runtime, network and packaging | `server/src/network/tcp/utils/decode` | 0 | 0 | 1 | 0 |
| Runtime, network and packaging | `server/src/network/tcp/utils/encode` | 0 | 0 | 0 | 1 |
| Runtime, network and packaging | `server/src/utils` | 1 | 0 | 1 | 5 |
| Runtime, network and packaging | `server/src/utils/logger` | 0 | 0 | 1 | 0 |
| Space, combat and sites | `server/src/abyssal` | 0 | 0 | 0 | 1 |
| Space, combat and sites | `server/src/abyssal/catalog` | 0 | 0 | 0 | 23 |
| Space, combat and sites | `server/src/abyssal/catalog/abyssalLootTables.generated.data` | 0 | 0 | 0 | 7 |
| Space, combat and sites | `server/src/abyssal/catalog/content` | 0 | 0 | 0 | 22 |
| Space, combat and sites | `server/src/abyssal/catalog/fixtures` | 0 | 0 | 0 | 1 |
| Space, combat and sites | `server/src/abyssal/domain` | 1 | 0 | 0 | 14 |
| Space, combat and sites | `server/src/abyssal/policy` | 0 | 0 | 2 | 15 |
| Space, combat and sites | `server/src/abyssal/ports` | 0 | 0 | 1 | 0 |
| Space, combat and sites | `server/src/abyssal/runtime` | 2 | 0 | 25 | 140 |
| Space, combat and sites | `server/src/abyssal/runtime/content` | 0 | 0 | 0 | 5 |
| Space, combat and sites | `server/src/abyssal/runtime/controllers` | 0 | 0 | 3 | 2 |
| Space, combat and sites | `server/src/abyssal/state` | 0 | 0 | 0 | 10 |
| Space, combat and sites | `server/src/abyssal/transport` | 0 | 0 | 0 | 2 |
| Space, combat and sites | `server/src/services/activity` | 8 | 0 | 2 | 8 |
| Space, combat and sites | `server/src/services/dungeon` | 27 | 0 | 14 | 4 |
| Space, combat and sites | `server/src/services/faction` | 5 | 0 | 2 | 2 |
| Space, combat and sites | `server/src/services/homefront` | 15 | 0 | 0 | 0 |
| Space, combat and sites | `server/src/services/incursion` | 43 | 3 | 2 | 0 |
| Space, combat and sites | `server/src/services/shipcaster` | 0 | 0 | 0 | 5 |
| Space, combat and sites | `server/src/services/sovereignty` | 5 | 0 | 20 | 16 |
| Space, combat and sites | `server/src/space` | 3 | 0 | 8 | 10 |
| Space, combat and sites | `server/src/space/FireShow` | 2 | 0 | 0 | 0 |
| Space, combat and sites | `server/src/space/asteroids` | 4 | 0 | 1 | 2 |
| Space, combat and sites | `server/src/space/combat` | 4 | 0 | 3 | 3 |
| Space, combat and sites | `server/src/space/combat/missiles` | 0 | 0 | 0 | 2 |
| Space, combat and sites | `server/src/space/destiny` | 0 | 0 | 2 | 0 |
| Space, combat and sites | `server/src/space/destiny/authority` | 0 | 4 | 3 | 0 |
| Space, combat and sites | `server/src/space/destiny/batching` | 1 | 1 | 2 | 1 |
| Space, combat and sites | `server/src/space/destiny/bootstrap` | 0 | 0 | 0 | 2 |
| Space, combat and sites | `server/src/space/destiny/commands` | 2 | 2 | 10 | 7 |
| Space, combat and sites | `server/src/space/destiny/delivery` | 1 | 13 | 2 | 2 |
| Space, combat and sites | `server/src/space/destiny/dispatch` | 1 | 7 | 1 | 0 |
| Space, combat and sites | `server/src/space/destiny/identity` | 0 | 0 | 1 | 1 |
| Space, combat and sites | `server/src/space/destiny/native` | 0 | 0 | 0 | 1 |
| Space, combat and sites | `server/src/space/destiny/presentation` | 0 | 2 | 7 | 1 |
| Space, combat and sites | `server/src/space/destiny/projection` | 0 | 1 | 2 | 0 |
| Space, combat and sites | `server/src/space/destiny/protocol` | 1 | 0 | 1 | 3 |
| Space, combat and sites | `server/src/space/destiny/simulation` | 3 | 10 | 4 | 4 |
| Space, combat and sites | `server/src/space/destiny/stream` | 0 | 0 | 3 | 4 |
| Space, combat and sites | `server/src/space/destiny/visibility` | 1 | 10 | 1 | 0 |
| Space, combat and sites | `server/src/space/empireGatePresence` | 1 | 0 | 0 | 4 |
| Space, combat and sites | `server/src/space/modules` | 2 | 0 | 11 | 13 |
| Space, combat and sites | `server/src/space/modules/superweapons` | 0 | 0 | 1 | 1 |
| Space, combat and sites | `server/src/space/npc` | 6 | 0 | 18 | 20 |
| Space, combat and sites | `server/src/space/npc/abyssal` | 1 | 0 | 0 | 1 |
| Space, combat and sites | `server/src/space/npc/capitals` | 0 | 0 | 2 | 8 |
| Space, combat and sites | `server/src/space/npc/empireSecurity` | 1 | 0 | 1 | 0 |
| Space, combat and sites | `server/src/space/npc/trigDrifter` | 0 | 0 | 0 | 2 |
| Space, combat and sites | `server/src/space/runtime` | 8 | 2 | 55 | 32 |
| Space, combat and sites | `server/src/space/runtime/scene` | 5 | 0 | 16 | 2 |
| Space, combat and sites | `server/src/space/runtime/spaceRuntime` | 0 | 0 | 11 | 9 |
| Tests and fixtures | `mods/eft-fit-spawner/tests` | 0 | 3 | 0 | 0 |
| Tests and fixtures | `server/src/services/_shared` | 0 | 0 | 0 | 1 |
| Tests and fixtures | `server/src/services/corporation` | 0 | 0 | 1 | 0 |
| Tests and fixtures | `server/tests` | 0 | 26 | 0 | 0 |
| Tests and fixtures | `server/tests/fixtures/agentAuthority` | 0 | 2 | 0 | 0 |
| Tests and fixtures | `server/tests/fixtures/incursion-history/2026/2026-09-26` | 0 | 3 | 0 | 0 |
| Tests and fixtures | `server/tests/helpers` | 0 | 1 | 0 | 0 |
| Wormholes and exploration | `server/src/services/exploration` | 3 | 0 | 2 | 1 |
| Wormholes and exploration | `server/src/services/exploration/gas` | 1 | 0 | 0 | 0 |
| Wormholes and exploration | `server/src/services/exploration/probes` | 0 | 0 | 1 | 4 |
| Wormholes and exploration | `server/src/services/exploration/signatures` | 1 | 0 | 1 | 2 |
| Wormholes and exploration | `server/src/services/exploration/signatures/providers` | 0 | 0 | 1 | 6 |
| Wormholes and exploration | `server/src/services/exploration/wormholes` | 2 | 0 | 4 | 4 |
| Wormholes and exploration | `server/src/services/tale` | 1 | 0 | 1 | 1 |

</details>


## Source identity and comparison limits

- Old: `Fokiiiiiii/evejs-v0.12.9`, shallow clone commit `7cc6f323c7c5c92c2738476a62768318799d8077` (package version `0.12.9`; commit subject `fix: sync Abyssal module attributes to client`). In the source citations below, paths are relative to that checkout.
- New: `/home/user/evejs-261108.1`, package version `261108.1`. This directory has no `.git` metadata, so its package version is the only available identity; no new-version commit hash was available to record.
- Client: the supplied `/home/user/eve-logic` V24.01 source. Client source confirms RPC call shapes and some UI transitions; it does not, by itself, establish server-side formulas, spawn tables, event schedules or encounter payout rules.
- eve.rs status is from the current worktree source and project records. `docs/project/TODO.md`, ADRs and compatibility deviations are cited where they qualify implementation status.
- Inventory method: v0.12.9 paths are the shallow checkout’s tracked files (`git ls-files`); 261108.1 paths are all regular files in its supplied tree except `.git` metadata and `node_modules` (ignored by its `.gitignore:1`). This accounts for the respective 2,209 and 2,480 path totals. The functional notes cover substantial client-visible or gameplay differences, not every internal edit among the 697 changed files. Many additions are architecture, tests, administration or authored data. A textual change is not treated as a verified behavior change without source evidence.
- No EveJS server scenario was executed for this comparison; the two source trees were inspected statically. No graphical-client walk was performed.

## Functional findings

| Area / difference | v0.12.9 → 261108.1 | eve.rs disposition | V24.01 evidence and conclusion |
|---|---|---|---|
| Industry blueprint pricing | Replaces base-price fallback and ambiguous empty-material handling with the recursive client rule and adjusted-average fallback. | Matches 261108.1/client rule in the Host pricing path. | Confirmed by `industryCommon.py:521-543`; details below. |
| Market BuyMulti | Returns immediate expired-order rows and reason `Expiry`, which the old path omitted; notification address marker also changes. | Client-visible reply and expiry notification match new behavior; notification address remains `charid` as old; atomic transaction policy differs from both. | `buyMultiFromBase.py:296-311,558-590` confirms rows, reason and remaining-quantity restoration. Client does not establish the notification address marker or transaction atomicity. |
| Incursions | Moves old in-file scheduler/planner into lifecycle/store/scheduler authorities and adds authored encounter mechanics, contribution qualification and durable reward settlement. | Lifecycle, site projection and map data are implemented; final encounter contribution payouts and delayed LP settlement remain open. | V24 consumes tale data and delayed-reward APIs. It confirms the interface, not the new reward formula. Client-confirmed payout-flow gap is listed below. |
| Homefront | Adds a dedicated operation runtime, admission, encounter/objective state, rewards, persistence and notification modules, plus a 24-layout pack. | Site discovery by archetype 70 is implemented; reviewed Host code does not provide the operation runtime/job state. | V24 Agency/Job Board sources confirm Homefront discovery and a per-instance job lookup. The pack’s later client build and authored encounter estimates are not accepted as V24 data. |
| AIR NPE and node graphs | Adds tutorial graph resume/orchestration, checkpoint persistence and registered mining, encounter, combat, economy and guidance effects around the existing `air_npe` service. | `air_npe` state/skip RPCs exist; `node_graph` is a pass-through adapter with no callback wired in the inspected Host composition, so graph messages do not execute. | V24 calls `air_npe`, reads active graph context and sends client graph/blackboard messages. The missing runtime is a confirmed gap; a job is listed below. AIR pack content itself remains unverified. |
| Skyhook raid links | Adds theft-window, raid-link, damage, settlement and durable retry logic alongside the prior reagent extraction service. | `linkWithShip` currently routes only the CRAB/Analysis Beacon handler; no Skyhook raid provider is wired. | V24 confirms the component method names `InitiateLink` and `InitiateLinkQuick`; it does not confirm 261108’s theft window or payout values. Missing Skyhook link handling is a gap; raid policy still needs target-build validation. |
| SKINR | Adds design drafts, component/store authorities, licenses, sequencing jobs, third-party use, validation and persistent notices; the old service tree had no equivalent runtime. | Matches 261108.1 for quantity-independent batch duration and persistent sequencing. | V24 ship-SKINR client uses the public gateway sequencing requests. See the data-source qualification below. |
| Character resculpt | Adds a real certificate activation path that consumes the item and stores resculpt state; old `userSvc.ActivateCharacterReSculpt` only returned `True`. | Matches V24 item-state mapping, not 261108.1’s state `1` for both certificate types. | V24 defines editor states 1 and 3; eve.rs sets 3 for type 34132 and 1 for 89956. The new EveJS state choice is rejected for type 34132. |
| Agent/security mission authority | Replaces the older mission-pool resolver with a larger normalized mission authority, candidate indexes, offer policy/projection and per-level packs. | No live mission-offer path: source-backed agent-to-mission membership is still open. | V24 confirms `agentMgr` and mission UI routes, but the source inspected does not confirm 261108’s candidate membership or new pack records. Do not port those pool contents as established behavior. |
| Wormhole data and exploration | Adds wandering K-space profiles and updates wormhole-site pack `1.2.0` → `1.2.1`; the core jump RPC remains present. | J-space lifecycle, static connections and `WormholeJump` are implemented; wandering K-space connections are explicitly cut. | V24 calls `wormholeMgr.WormholeJump`; it does not establish wandering-spawn rules. The new pack’s dungeon records use client build 3475087, so they are not target V24 evidence. |
| Ansiblex structure travel | Keeps the `CmdJumpThroughStructureStargate` flow but removes the old explicit active-jump and pending-warp-landing guards; the rest of the handler is substantially unchanged. | RPC is registered for read calls, but actual travel has no Host callback and returns “Structure travel is not wired on this server.” | V24 menu and autopilot invoke this RPC. The client confirms the route, not whether those two old guards should remain. Confirmed eve.rs travel gap; see suggested job below. |
| Ship self-destruct | Adds ship `SelfDestruct`/`AbortSelfDestruct` handlers and a persisted two-minute countdown; v0.12.9 has no ship self-destruct RPC/runtime in the audited service tree. | Has bound start/abort methods and a durable 120-second countdown. | V24 ship menu confirms both calls after player confirmation. The client callsite confirms the contract, not the exact timer duration. eve.rs matches 261108’s implementation shape. |
| Gate customs cases | Adds durable arrival-case tracking after the base contraband inspection, including delayed scans, optional answer/surrender and enforcement; old code directly inspected contraband during transition. | Implements SDE-based inspection and penalties, but not 261108’s persisted case/prompt flow. | V24 confirms the undock warning and standard confiscation notification surfaces. The client source does not confirm the new gate-case selection, timing or answer policy; this difference is not an accepted eve.rs gap. |
| Space/combat simulation | Adds dedicated bomb, defender-missile and breacher-pod runtime files and splits Destiny tick/history/movement responsibilities. Some defender-missile behavior already existed in v0.12.9. | eve.rs has bomb, defender and breacher effect paths, but this source inventory does not establish parity for each damage/timing rule. | No distinct client RPC contract for these server effects was found in the inspected callsites; exact effect parity remains unchecked. Treat the file move/addition as implementation evidence, not client confirmation. |
| GM commands and NodeGraph | Adds 16 chat-command modules and the server NodeGraph compiler/runtime/event/effect stack; old had no NodeGraph service directory. | Screen-issued slash commands are individually served per `GM_QA_DESIGN.md`; NodeGraph RPC routes exist but lack the runtime callback. | V24 menu calls `slash.SlashCmd`; NodeGraph client calls the graph and QA RPCs. The NodeGraph execution gap is listed above. No claim is made that every new EveJS GM-only command is part of the unmodified client flow. |
| Career, seasons, campaigns and other packs | Adds career/event/reward services and authored Gallente, FW, AIR, Homefront, incursion, security-mission and other packs. | eve.rs implements some base client services and separate gameplay foundations; the event-specific content is not treated as parity without target-build authority. | V24 has career-agent and event service callsites, but no inspected evidence here confirms every later event, schedule or pack record. These data additions are catalogued, not accepted as V24 behavior. |

### Industry pricing: confirmed behavior change

In v0.12.9, `server/src/services/industry/industryPricing.js:42-49` calls the type metadata base price the adjusted average. Its recursive price resolver treats missing activity data like an empty material list and falls back to the type price whenever the calculated total is not positive (`:128-135,156-175`). In 261108.1, `industryPricing.js:50-57,137-212` reads the adjusted-average table, distinguishes “no blueprint/activity” (`null`) from “activity with no materials” (`[]`), and only uses adjusted average for the former.

V24 `eve/common/script/util/industryCommon.py:521-543` maps science activities to manufacturing, recursively sums the activity’s materials, and falls back to adjusted average only when the blueprint/activity lookup raises `KeyError` or `UserError`; no value resolves to zero. eve.rs `dotnet/src/Eve.Host/Persistence/Industry/PersistenceIndustryJobOperations.cs:241-287` follows that rule using SDE blueprints and adjusted prices, including the science-activity mapping in `dotnet/src/Eve.Services.Core/IndustryRules.cs:73-89`. **Disposition: eve.rs matches 261108.1 and the V24 client. No gap.**

### Market BuyMulti: client contract correction, internal atomicity differs

The old EveJS path uses reason `Expired` (`server/src/services/market/marketProxyService.js:232`), notifies with `charid` (`:996-1005`), processes each requested item line, and returns an empty list (`:6915-6958`). The new path uses `Expiry` (`:239-241`), `clientID` (`:1005-1014`), builds one transient already-expired order per purchase line and returns those rows (`:1065-1096,7057-7119`).

The client records returned `orderID`s from `BuyMulti` and waits for them (`eve/client/script/ui/shared/market/buyMultiFromBase.py:296-311`). Its `OnOwnOrdersChanged` handler accepts only `Expiry` and restores `volRemaining` for incomplete orders (`:558-590`); `marketbase.py:537-545` also checks the expiry reason. This confirms the new returned rows and reason. It does not confirm the `clientID`/`charid` audience marker; the client code inspected only confirms the notification name and payload handling.

In eve.rs, `dotnet/src/Eve.Services.Market/MarketProxyService.cs:600-627` returns transient immediate orders and publishes `Expiry`; `MarketTrading.cs:30-31` describes those rows. `MarketWire.cs:8-21` uses the 64-bit `stationID` descriptor, as the new EveJS does (`:182-184`; old used type 3 at `:177`). `MarketNotifier.cs:84-98` still addresses `OnOwnOrdersChanged` to `charid`, matching old EveJS. eve.rs submits all lines to `PlaceBuyOrdersAsync` in one transactional call; the new EveJS calls `executeBuyRequest` once per line (`:7078-7113`). The client does not prescribe atomicity. **Disposition: eve.rs matches new EveJS for the wire rows and `Expiry`, old EveJS for the unconfirmed recipient marker, and neither version’s per-line transaction policy.**

### Incursions: lifecycle refactor and settlement gap

In v0.12.9, `server/src/services/incursion/incursionRuntime.js:107-130,617-683,1165-1296` owns the large runtime, including phase scheduling, recovery, startup and placement. In 261108.1 the same filename is only a presentation projection (`incursionRuntime.js:11-30`); lifecycle creation/transitions are in `incursionLifecycle.js:74-118`, and lifecycle persistence/scheduling is separated into `incursionLifecycleStore.js` and `incursionLifecycleScheduler.js`. The new version adds site/encounter policies and participation/reward authorities, including fleet/contribution qualification (`incursionParticipation.js:10-51`) and durable reward sealing/release/query paths (`incursionRewards.js:63-120,179-220,381-401`).

V24 `eve/client/script/ui/shared/incursionSvc.py:68-82` consumes `OnTaleData`; `:254-260` asks `rewardMgr.GetDelayedRewardsByGroupIDs`; `eve/client/script/ui/shared/agencyNew/contentPieces/incursionContentPiece.py:50` reads delayed rewards for the displayed reward group. eve.rs publishes tale data and implements the lifecycle/site/map reads, but `docs/project/TODO.md:1483` says the pending delayed LP is zero until the final-boss pipeline exists, and line 62 leaves contribution payouts and delayed LP settlement open. **Disposition: partial match to both versions. The V24 delayed-reward interface confirms a gap:** implement final-site completion, participation-qualified reward sealing, durable LP/ISK settlement and reward-log projection through the existing incursion owners; cover duplicate completion and restart recovery. Exact 261108 participation and reward values still require V24/gameplay authority before adoption.

### Homefront: discovery is client-confirmed; 261108 content is not

No `services/homefront` runtime exists in the old source. 261108.1 adds `homefrontRuntime.js`, admission, engine, rewards, persistence, cargo, notifications and hooks. The runtime advances objective state, seals rewards and publishes client state (`homefrontRuntime.js:670-705`); the engine records active/completed/failed objectives and reward eligibility (`homefrontEngine.js:82-115,359-402`). The new pack describes 24 layouts, but its manifest is built against server SDE 3396210 and client build 3503375, and explicitly says the server spawn tables are unavailable and encounter estimates are authored (`content-packs/evejs-homefront-1.0.5.json:7-33`).

V24 `contentProviderHomefrontSites.py:12-18` exposes the site provider and archetype 70. The Job Board provider names the `homefront_operations` tracker (`jobboard/client/features/dungeons/homefront_operations/provider.py:13-31`), and the Agency detail container looks up a job for each dungeon instance (`homefrontSiteInfoContainer.py:61-65`). eve.rs `DungeonRpcServices.cs:152-180` serves `GetHomefrontSiteInstances` grouped by archetype; the reviewed Host code does not expose the new operation runtime/job state. **Disposition: eve.rs matches the discovery slice, not the new full runtime.** The discovery contract is already implemented, so no new-discovery job is open. A Homefront progression/job-state job is appropriate only after tracing the exact V24 Job Board methods and using target-build site authority; do not treat the 3503375 pack’s authored encounter estimates as verified.

### AIR NPE and NodeGraph: client RPCs are present, execution is not wired

The old `services/npe/airNpeService.js:36-50` stores the skip transition and emits only `OnAirNpeStateChanged`. The new service adds a post-call resume into `airNpeRuntime` (`:53-59`); that runtime saves tutorial checkpoints and registers mining, encounter, combat, economy and guidance effects (`airNpeRuntime.js:3-18,20-54`). Its pack calls native-client verification pending and donor graph build 3487903 (`content-packs/evejs-air-tutorial-1.0.11.json:6-30`), so the pack’s authored scene data is not independently accepted as V24-verified.

V24 `storylines/client/airnpe.py:14-34,46-75` queries the `air_npe` service, tracks state notifications and checks the active client graph. `nodegraph/client/graph.py:13-26` sends client messages and blackboard reads to `node_graph`; `nodegraph/client/log_graph.py:45-77,114-127` uses QA graph controls. eve.rs `AirNpeService.cs:17-61` registers enabled/state/skip, persists skip and sends `OnTutorialSkipped` before `OnAirNpeStateChanged`; `docs/reference/compat/DEVIATIONS.md:90-92` records that ordering. But `NodeGraphService` has an optional callback and returns its default when none is supplied (`UnportedFeatureRpcServices.cs:197-209,239-247`); the inspected source has no Host `nodeGraphRpcCallback` wiring (`CoreServices.cs:22,73`).

**Disposition: eve.rs matches the V24 `air_npe` service/skip transition, but neither EveJS graph execution runtime. Confirmed gap:** wire a persistent NodeGraph runtime into Host composition for the client graph message and blackboard RPCs, then connect AIR tutorial checkpoint/resume and graph events; add registered-Host tests for request shape, persisted state, notifications and restart. Keep QA-only graph control methods staff-gated.

### Skyhook theft link: new EveJS flow, missing Host provider

The old `services/sovereignty/skyhookRuntime.js:101-170` handles authorization and reagent extraction. 261108.1 adds `skyhookRaidRules.js` and `skyhookRaidRuntime.js`; the latter starts a persisted link, validates completion and prepares a retryable reagent-container settlement (`:296-336,338-372,547-569`).

V24 `spacecomponents/client/components/linkWithShip.py:24-25,237-245` sends `CallComponentFromClient(itemID, 'linkWithShip', 'InitiateLink'|'InitiateLinkQuick')`; its component/slim state is read at `:118-122`. eve.rs `BeyonceService.Components.cs:9-28` only forwards that component call to the configured Analysis Beacon/CRAB provider. `TODO.md:69` lists full Skyhook raids as open. **Disposition: eve.rs handles the CRAB case, but neither EveJS raid flow for Skyhooks. Confirmed link-call gap:** add a Skyhook link provider through the existing `BeyonceService` component dispatch, enforce target-client eligibility/state updates, and settle raid cargo durably; test interruption/restart. Verify theft window, duration and reward rules against V24/SDE before adopting 261108 constants.

### SKINR: client surface and batch duration align

The old tree has no equivalent `shipSkin*` server runtime. 261108.1 adds separate draft/design, component, store, market, license and sequencing owners; `shipSkinSequencingRuntime.js:21-36,141-175` stages binders/licenses, schedules jobs, charges PLEX and grants the completed license. `shipSkinSequencingAuthority.js:35-44` prices by run count but computes one job duration independent of output quantity. V24 `cosmetics/client/messengers/cosmetics/ship/shipSkinSequencingRequestMessenger.py:22-40` builds sequencing requests from component licenses, and `shipSkinSequencingSvc.py:62-65,198-208` sends requests and tracks pending jobs. eve.rs has V24 public-gateway handlers in `ShipSkinrGateway.cs` and `ShipSkinrSequencingGateway.cs`, persisted state/store and scheduler integration.

**Disposition: eve.rs matches 261108.1 for sequencing quantity and duration.** The V24 client confirms the request surface and run count, while the one-duration rule is stated directly by the new EveJS authority; no contrary client calculation was found. Host catalog data reads skinr tables from SDE build 3405148 because pinned V24 SDE 3396210 lacks those tables (`docs/reference/compat/DEVIATIONS.md:146-148`); that supplemental data source remains an explicit compatibility limitation. The older 261018 deviation about multiplying duration does not apply to 261108.1.

### Resculpt: old no-op replaced; new state mapping rejected

Old `services/user/userService.js:1097-1102` logs `ActivateCharacterReSculpt` and returns `True` without consuming an item or changing state. The new `services/character/characterResculpt.js:92-97,99-129` consumes one certificate and stores paper-doll state 1 for either certificate type.

V24 `paperdoll/common/state.py:8-13` defines `resculpting=1` and `full_recustomizing=3`; `eve/client/script/ui/login/characterCreationContainer_new.py:275-284,301-328` selects the editor mode from that state. eve.rs `dotnet/src/Eve.Host/Rpc/RewardRpc.cs:49-76` consumes the certificate and stores state 3 for type 34132 and state 1 for 89956, matching its certificate/SDE distinction. The client state mapping and selected-character call are recorded with exact client paths in `docs/reference/compat/DEVIATIONS.md:6267`.

**Disposition: eve.rs matches V24 and neither old nor new EveJS. The new EveJS state 1 for type 34132 is rejected; no eve.rs gap.**

### Agent missions, event additions and data authority

The old source has `services/agent/missionPoolResolver.js:216-292` and `missionLevelResolver.js`. The new source replaces those with the normalized `services/agent/missionAuthority.js:186-205,444-475` index and `server/src/config/securityMissionOfferProjection.js:7-54`, plus evidence, baseline and policy files and security-level content packs. V24 `eve/client/script/ui/station/agents/agents.py:89-98,216-224,356-363,799-802` confirms calls for agent lists, completed career agents, disabled missions and agent system location. It does not establish mission candidate membership for each agent.

eve.rs `docs/project/TODO.md:1175` has no live offer creation path; `:1270-1276` records the mission-data build mismatch and the missing source-backed agent-to-mission pool links. **Disposition: no accepted old/new pool match; candidate membership remains unverified.** Suggested follow-up: obtain the exact V24 mission authority data or client traces, compare to 261108 candidates, then implement the offer path and success/refusal regressions. Do not adopt the new packs as authoritative merely because their resolver is more data-driven.

The new `services/career`, `services/seasons` and `services/campaign` trees add career progress/rewards, event scheduling, Campaign Trail and Gallente login rewards. These are real additions in the new tree (`services/seasons/eventLifecycle.js`, `services/campaign/gallenteElectionLoginCampaign.js`, `services/career/careerProgress.js`), but this comparison did not establish their event schedule or every reward against target build 3396210. eve.rs’s season and campaign coverage is per-call and recorded in `TODO.md`; no blanket parity is claimed. Treat later event packs as unconfirmed unless their exact V24 build/data source is demonstrated.

### Wormhole and exploration data

Both EveJS versions expose a `WormholeJump` service; new `wormholeMgrService.js:192-292` performs transition/handoff, while the new source adds `kspaceWanderingProfiles.json` and an authority path that constructs wandering K-space profiles (`wormholeAuthority.js:13,50-82`). The V24 callsite is `eve/client/script/ui/services/menuSvcExtras/movementFunctions.py:593-599`; the Host’s supported jump flow is recorded in `docs/project/TODO.md:65` and `DEVIATIONS.md:3849-3873`.

The 261108.1 wormhole pack is `1.2.1`, with 48 templates and client dungeon build 3475087 (`content-packs/evejs-wormhole-sites-1.2.1.json:4-32`); the old pack is `1.2.0`. eve.rs deliberately cuts wandering K-space connections (`TODO.md:65,1714`). **Disposition: core jumps match both references and the V24 callsite; new wandering-profile rules are not client-confirmed and remain cut. The newer dungeon pack is rejected as evidence for V24.01 until recompiled/verified against the matching client data.**

### Ansiblex structure travel: both EveJS routes exist, Host movement is not wired

Both EveJS versions implement `structureJumpBridgeMgr.CmdJumpThroughStructureStargate`. The v0.12.9 handler starts at `server/src/services/structure/structureJumpBridgeMgrService.js:1792`; 261108.1 starts at `:1713` and adds a warning when settlement fails (`:1876`). The substantive guard difference is that the old handler rejects an active jump activation (`:1798`) and a pending warp landing (`:1827`), while the new handler omits both checks. No V24 client evidence found here decides whether those guards represent intended behavior, so this report does not recommend copying their removal.

V24 invokes the RPC through the menu (`eve/client/script/ui/services/menusvc.py:2095-2108`) and the autopilot (`eve/client/script/parklife/autopilot.py:348-352`). eve.rs registers `StructureTravelRpcService` with directory read callbacks only (`dotnet/src/Eve.Host/Rpc/StructureRpc.cs:68-71`); `StructureTravelRpcService.cs:79-88` raises “Structure travel is not wired on this server” when no travel callback is supplied. **Disposition: neither EveJS behavior is implemented by eve.rs; the V24 callsites confirm this is a client-visible gap.** Suggested job: wire `CmdJumpThroughStructureStargate` through the existing Host structure-travel and session-movement owners, implement authoritative source/link/access/fuel/fee checks and transition settlement, and cover successful travel plus refusal/retry and autopilot invocation. Resolve the two guard semantics from target-client traces before changing them.

### Ship self-destruct: new bound service and durable countdown

The v0.12.9 service tree has no ship `SelfDestruct` or `AbortSelfDestruct` handler/runtime (the old tree’s `selfDestruct` references are limited to mobile-analysis-beacon cleanup). 261108.1 binds the two methods in `server/src/services/ship/shipService.js:2355-2365`; `selfDestructRuntime.js:15,92-127,133-151,222-256` stores the countdown, publishes the slim timer, handles abort and schedules completion durably. Its timer constant is 120 seconds (`:15`).

V24 exposes these choices on the in-space menu (`eve/client/script/ui/services/menusvc.py:1654-1657`) and calls `ship.SelfDestruct` / `ship.AbortSelfDestruct` after confirmation (`eve/client/script/ui/services/menuSvcExtras/menuFunctions.py:550-575`; abort: `:624-632`). eve.rs has both bound methods (`dotnet/src/Eve.Services.Space/ShipService.cs:300-311`) and schedules a durable 120-second job while updating `selfDestructTime` and notifications (`dotnet/src/Eve.Services.Space/SpaceSessions.Death.cs:24-26,55-101,104-121`). **Disposition: eve.rs matches 261108.1 for the client contract and durable countdown, filling a feature absent in old EveJS.** V24 confirms the method calls and flow, while the exact duration is evidenced by the two server implementations rather than the cited client callsites.

### Customs cases: new interaction is not established by V24 source

In v0.12.9, `server/src/space/transitions.js:2543-2548,4230-4235` calls the shared contraband inspection during undock and gate arrival. 261108.1 retains that inspection and adds a separate `customsInspectionRuntime.registerArrival` after successful gate arrival (`server/src/space/transitions.js:1998-2018`). It persists a delayed scan case with captured cargo and authority (`customsInspectionRuntime.js:105-147`), then may ask a yes/no question and record surrender/enforcement (`:150-159,461-483`).

V24’s station undock flow displays `ShipContrabandWarningUndock` and retries with `ignoreContraband=True` after acceptance (`eve/client/script/ui/station/base.py:488-511`). It also has a standard customs-confiscation notification formatter (`eve/common/script/util/notificationUtil.py:269-290,1174-1175`). The inspected client sources do not establish 261108’s delayed gate scan, detection chances, case prompt or surrender rules. eve.rs implements the SDE-backed contraband inspection and penalty flow (`docs/project/TODO.md:2761`) but not this new case state machine. **Disposition: eve.rs matches the shared/base inspection slice, while neither the case interaction nor its new EveJS policy is confirmed by the target client; do not report this as a client-confirmed gap or port its policy without target-build evidence.**

### Space/combat, GM and server-architecture changes

Space changes include `modules/bombRuntime.js`, `modules/defenderMissileRuntime.js`, `combat/breacherPods.js`, `combat/breacherPodState.js`, `runtime/breacherPodDamage.js`, and the Destiny tick/history/movement split. The old version already contains defender-missile support (`space/runtime/missileEntity.js:77,487-488`, `space/combat/weaponDogma.js:1142-1175`), so the new filenames do not mean every effect was introduced in this release. eve.rs has distinct bomb/defender/breacher effect handlers under `dotnet/src/Eve.Services.Space/Effects/`; there was no target-client RPC delta found for these generic dogma effects. Exact damage, lifetime and simulation parity are not verified by this comparison.

New slash command files include `changeOwner`, `copyShip`, `dogma`, `fireShow`, `nativeDestroy`, `nativeSpawns`, `onlineModules`, `orbitalDiscoveryCommand`, `reportDesync`, `shipSetup`, `skinrComponents`, `skinrDrafts`, `transientGates`, `unspawn` and `voidReplay`, plus one companion-retirement command. The client issues GM menu actions through `slash.SlashCmd` (`eve/client/script/ui/services/menusvc.py:781-804,834,885,1061`); eve.rs’s individually served set is listed in `docs/project/GM_QA_DESIGN.md:49-71`. That file-level growth is not evidence that every command is invoked by the stock client.

The new NodeGraph files (`nodeGraphAuthority.js`, `nodeGraphCompiler.js`, `nodeGraphEffects.js`, `nodeGraphEvents.js`, `nodeGraphRuntime.js`) are functional infrastructure, not just QA routes. V24 client calls documented above establish the message and blackboard RPC contract. The current eve.rs default callback makes the service reply without changing graph state, so graph execution is a real parity gap, not a route-registration success.

## Content packs and config summary

The pack directory changes from 10 to 24 files: 15 additions, one removal (`evejs-wormhole-sites-1.2.0.json`) and no same-path content changes. Additions are AIR tutorial, career agents, fractured legacy, FW open, Gallente depots/fabricators, Homefront, incursions, security mission levels 1–5, Visions of Greatness and wormhole sites 1.2.1. The file ledger lists every pack path.

Several pack manifests explicitly disclose build/provenance limits: Homefront client build 3503375 and authored encounter estimates (`...homefront-1.0.5.json:18-33`); AIR native-client verification false and donor graph build 3487903 (`...air-tutorial-1.0.11.json:19-30`); wormhole dungeon build 3475087 (`...wormhole-sites-1.2.1.json:26-32`). These pack records are useful implementation material, but they do not override the requested V24.01 source priority.

## Verification record

- Verified statically: package versions; old commit identity; path counts; additions/removals/content/EOL classification; no rename candidates under the checks described above; cited source locations and the specific client/Host behavior summarized here.
- Not checked: EveJS runtime execution, full semantic review of all 697 content changes, every individual GM command, live graphical-client behavior, and all event/site content against build 3396210.
- The path inventory includes 35 removed test/fixture files (26 under `server/tests`) and no additions in the tests/fixtures category. This is a test-surface inventory fact, not evidence of a gameplay change; replacement coverage was not audited.
- Tests/builds were not run. This commit changes documentation only; no touched project or test class is associated with this file. `git diff --check` is the local verification for the document.

## File-level manifest

Paths are relative to the EveJS source roots. The line-ending-only section lists common paths whose contents are byte-different but become identical after CRLF/CR-to-LF normalization.

<details>
<summary>Added: 482 files</summary>


### Space, combat and sites

- `server/src/abyssal/domain/abyssalBoundaryGeometry.js`

- `server/src/abyssal/runtime/abyssalArrivalProtection.js`

- `server/src/abyssal/runtime/abyssalSetStateDelivery.js`

- `server/src/services/activity/filamentAdmission.js`

- `server/src/services/activity/pvpEventCatalog.js`

- `server/src/services/activity/pvpFilamentQueueRuntime.js`

- `server/src/services/activity/pvpJumpErrors.js`

- `server/src/services/activity/voidReplayAdmission.js`

- `server/src/services/activity/voidReplayRoom.js`

- `server/src/services/activity/voidReplayRuntime.js`

- `server/src/services/activity/voidReplayWeather.js`

- `server/src/services/dungeon/capturedSecurityLegacyScenes.js`

- `server/src/services/dungeon/capturedSecurityLegacyScenes.json`

- `server/src/services/dungeon/careerLessonRuntime.js`

- `server/src/services/dungeon/containerLootTables.js`

- `server/src/services/dungeon/dungeonCuratedHackableContent.js`

- `server/src/services/dungeon/dungeonEditorAuthority.js`

- `server/src/services/dungeon/dungeonInstanceTable.js`

- `server/src/services/dungeon/dungeonLevelEditorService.js`

- `server/src/services/dungeon/dungeonRuntimeIndexes.js`

- `server/src/services/dungeon/epicSceneActions.js`

- `server/src/services/dungeon/fracturedLegacyRuntime.js`

- `server/src/services/dungeon/fracturedLegacySceneActions.js`

- `server/src/services/dungeon/homefrontPopulationPolicy.js`

- `server/src/services/dungeon/missionAccessGrants.js`

- `server/src/services/dungeon/missionMiningSettlement.js`

- `server/src/services/dungeon/missionSalvage.js`

- `server/src/services/dungeon/requiredActorPolicy.js`

- `server/src/services/dungeon/securityMissionReconHazards.js`

- `server/src/services/dungeon/securityMissionWeather.js`

- `server/src/services/dungeon/siteBlueprintLootSources.js`

- `server/src/services/dungeon/siteHazardOutcomes.js`

- `server/src/services/dungeon/sovProspectingNameRepair.js`

- `server/src/services/dungeon/visionsCaldariCapture.json`

- `server/src/services/dungeon/visionsCaldariNative.json`

- `server/src/services/dungeon/visionsCaldariProgress.js`

- `server/src/services/dungeon/visionsCaldariRuntime.js`

- `server/src/services/dungeon/visionsSceneRuntime.js`

- `server/src/services/faction/factionWarfareAuthority.js`

- `server/src/services/faction/fwOpenModel.js`

- `server/src/services/faction/fwOpenRewards.js`

- `server/src/services/faction/fwOpenRuntime.js`

- `server/src/services/faction/fwQueryPayloads.js`

- `server/src/services/homefront/homefrontAdmission.js`

- `server/src/services/homefront/homefrontArtifact.js`

- `server/src/services/homefront/homefrontCargo.js`

- `server/src/services/homefront/homefrontClient.js`

- `server/src/services/homefront/homefrontDread.js`

- `server/src/services/homefront/homefrontEngine.js`

- `server/src/services/homefront/homefrontHooks.js`

- `server/src/services/homefront/homefrontMessages.js`

- `server/src/services/homefront/homefrontNotifications.js`

- `server/src/services/homefront/homefrontRewards.js`

- `server/src/services/homefront/homefrontRuntime.js`

- `server/src/services/homefront/homefrontStore.js`

- `server/src/services/homefront/homefrontWeather.js`

- `server/src/services/homefront/homefrontWorld.js`

- `server/src/services/homefront/installHomefront.js`

- `server/src/services/incursion/incursionActorCleanup.js`

- `server/src/services/incursion/incursionAdmission.js`

- `server/src/services/incursion/incursionCargo.js`

- `server/src/services/incursion/incursionCivilianDrops.js`

- `server/src/services/incursion/incursionClock.js`

- `server/src/services/incursion/incursionEncounterModel.js`

- `server/src/services/incursion/incursionEncounterRuntime.js`

- `server/src/services/incursion/incursionFactory.js`

- `server/src/services/incursion/incursionFinalSiteRuntime.js`

- `server/src/services/incursion/incursionFinalSiteSlots.js`

- `server/src/services/incursion/incursionFocusPlacement.js`

- `server/src/services/incursion/incursionFocusRuntime.js`

- `server/src/services/incursion/incursionFocusState.js`

- `server/src/services/incursion/incursionFriendlyActors.js`

- `server/src/services/incursion/incursionHooks.js`

- `server/src/services/incursion/incursionLifecycle.js`

- `server/src/services/incursion/incursionLifecycleContract.js`

- `server/src/services/incursion/incursionLifecycleScheduler.js`

- `server/src/services/incursion/incursionLifecycleStore.js`

- `server/src/services/incursion/incursionLogistics.js`

- `server/src/services/incursion/incursionLogisticsSupport.js`

- `server/src/services/incursion/incursionMining.js`

- `server/src/services/incursion/incursionParticipation.js`

- `server/src/services/incursion/incursionPresentation.js`

- `server/src/services/incursion/incursionProjectionRuntime.js`

- `server/src/services/incursion/incursionReinforcements.js`

- `server/src/services/incursion/incursionRepairPresentation.js`

- `server/src/services/incursion/incursionRewardReports.js`

- `server/src/services/incursion/incursionRewardScheduler.js`

- `server/src/services/incursion/incursionRewardStore.js`

- `server/src/services/incursion/incursionRewards.js`

- `server/src/services/incursion/incursionSiteAdmission.js`

- `server/src/services/incursion/incursionSiteMechanics.js`

- `server/src/services/incursion/incursionSiteModel.js`

- `server/src/services/incursion/incursionSiteRuntime.js`

- `server/src/services/incursion/incursionSiteSlots.js`

- `server/src/services/incursion/incursionStructureRepair.js`

- `server/src/services/incursion/incursionStructures.js`

- `server/src/services/incursion/incursionSystemEffectContract.js`

- `server/src/services/incursion/incursionSystemEffects.js`

- `server/src/services/incursion/incursionTerminalContract.js`

- `server/src/services/incursion/incursionTerminalRuntime.js`

- `server/src/services/incursion/installIncursions.js`

- `server/src/services/sovereignty/skyhookCleanup.js`

- `server/src/services/sovereignty/skyhookRaidRules.js`

- `server/src/services/sovereignty/skyhookRaidRuntime.js`

- `server/src/services/sovereignty/sovHubFuelBurn.js`

- `server/src/services/sovereignty/sovHubFuelRuntime.js`

- `server/src/space/FireShow/fireShowRuntime.js`

- `server/src/space/FireShow/showIdentity.js`

- `server/src/space/asteroids/beltV1Geometry.json`

- `server/src/space/asteroids/beltV1GeometryMigration.js`

- `server/src/space/asteroids/naturalBeltAuthority.js`

- `server/src/space/asteroids/naturalBeltComposition.json`

- `server/src/space/combat/airCombatPolicy.js`

- `server/src/space/combat/breacherPodState.js`

- `server/src/space/combat/breacherPods.js`

- `server/src/space/combat/visionsCombatPolicy.js`

- `server/src/space/cynoArrivalSpawnState.js`

- `server/src/space/destiny/batching/destinyHistory.js`

- `server/src/space/destiny/commands/movementQueueing.js`

- `server/src/space/destiny/commands/warpTransmissionReceipt.js`

- `server/src/space/destiny/delivery/sessionClock.js`

- `server/src/space/destiny/dispatch/batchDelivery.js`

- `server/src/space/destiny/protocol/eventClasses.js`

- `server/src/space/destiny/simulation/evolve.js`

- `server/src/space/destiny/simulation/warpDescription.js`

- `server/src/space/destiny/simulation/warpJitter.js`

- `server/src/space/destiny/visibility/gridBubbles.js`

- `server/src/space/empireGatePresence/customsOperatorLayouts.js`

- `server/src/space/encounterSurveillanceSystem.js`

- `server/src/space/modules/bombRuntime.js`

- `server/src/space/modules/defenderMissileRuntime.js`

- `server/src/space/npc/abyssal/abyssalNpcBoundaryNavigation.js`

- `server/src/space/npc/empireSecurity/empireSentryRuntime.js`

- `server/src/space/npc/homefrontNpcCatalog.js`

- `server/src/space/npc/incursionDeathWrecks.js`

- `server/src/space/npc/nativeNpcAbilities.js`

- `server/src/space/npc/nativeNpcDeathWrecks.js`

- `server/src/space/npc/npcBlueprintLootSources.js`

- `server/src/space/npc/npcStartupRuleIndex.js`

- `server/src/space/runtime/breacherPodDamage.js`

- `server/src/space/runtime/destinyTick.js`

- `server/src/space/runtime/destinyTickDriver.js`

- `server/src/space/runtime/dogmaEvents.js`

- `server/src/space/runtime/pendingShipMotion.js`

- `server/src/space/runtime/savedWarpLanding.js`

- `server/src/space/runtime/scene/destinyHistory.js`

- `server/src/space/runtime/scene/evolve.js`

- `server/src/space/runtime/scene/missionAccess.js`

- `server/src/space/runtime/scene/movementQueue.js`

- `server/src/space/runtime/scene/sceneFeatureHooks.js`

- `server/src/space/runtime/siteHazardDamage.js`

- `server/src/space/runtime/visionsEwarNotice.js`

- `server/src/space/transientStargates.js`


### Industry and market

- `server/src/services/industry/industryClock.js`

- `server/src/services/industry/industryModifierAuthority.json`

- `server/src/services/industry/industryMonitorNotices.js`


### Characters, agents and events

- `server/src/services/agent/careerAgentChains.json`

- `server/src/services/agent/careerAgentMissions.js`

- `server/src/services/agent/careerRetailMissionData.js`

- `server/src/services/agent/careerRetailMissionData.json`

- `server/src/services/agent/fracturedLegacy.data.json`

- `server/src/services/agent/fracturedLegacy.js`

- `server/src/services/agent/visionsCrates.js`

- `server/src/services/agent/visionsOfGreatness.data.json`

- `server/src/services/agent/visionsOfGreatness.js`

- `server/src/services/campaign/gallenteElectionLoginCampaign.js`

- `server/src/services/campaign/gallenteElectionLoginRewards.json`

- `server/src/services/campaign/loginCampaignCatalog.js`

- `server/src/services/campaign/loginCampaignPayloads.js`

- `server/src/services/campaign/loginCampaignRedemption.js`

- `server/src/services/campaign/loginCampaignRuntime.js`

- `server/src/services/campaign/warpathLoginRewards.js`

- `server/src/services/career/airCareerCrates.js`

- `server/src/services/career/careerAbyssalSources.js`

- `server/src/services/career/careerCatalog.data.json`

- `server/src/services/career/careerCatalog.js`

- `server/src/services/career/careerCombatSources.js`

- `server/src/services/career/careerEventLedger.js`

- `server/src/services/career/careerExplorationSources.js`

- `server/src/services/career/careerGameplaySources.js`

- `server/src/services/career/careerHackingEvidence.js`

- `server/src/services/career/careerIdentityMigration.js`

- `server/src/services/career/careerIndustrySources.js`

- `server/src/services/career/careerMissionSources.js`

- `server/src/services/career/careerOwnerReads.js`

- `server/src/services/career/careerOwnerSources.js`

- `server/src/services/career/careerProgress.js`

- `server/src/services/career/careerRetailIdentities.json`

- `server/src/services/career/careerRewardRuntime.js`

- `server/src/services/career/careerRewardSettlement.js`

- `server/src/services/career/careerSocialSources.js`

- `server/src/services/career/careerStandingSources.js`

- `server/src/services/character/characterOwnAttributes.js`

- `server/src/services/character/characterRecordCache.js`

- `server/src/services/character/characterRecordInputs.js`

- `server/src/services/character/characterResculpt.js`

- `server/src/services/character/homeStationSelfDestructRuntime.js`

- `server/src/services/character/projectDiscoveryCorpus.js`

- `server/src/services/character/projectDiscoveryRewards.js`

- `server/src/services/character/projectDiscoveryRuntime.js`

- `server/src/services/character/projectDiscoverySamples.js`

- `server/src/services/npe/airCombatRuntime.js`

- `server/src/services/npe/airEconomyRuntime.js`

- `server/src/services/npe/airEncounterJobs.js`

- `server/src/services/npe/airEncounterRuntime.js`

- `server/src/services/npe/airFleetBattle.js`

- `server/src/services/npe/airGuidanceRuntime.js`

- `server/src/services/npe/airMiningRuntime.js`

- `server/src/services/npe/airNpeRuntime.js`

- `server/src/services/npe/airOpportunityRuntime.js`

- `server/src/services/npe/tutorialLifecycle.js`

- `server/src/services/seasons/campaignTrailCatalog.js`

- `server/src/services/seasons/campaignTrailPayloads.js`

- `server/src/services/seasons/campaignTrailRewards.js`

- `server/src/services/seasons/campaignTrailRuntime.js`

- `server/src/services/seasons/campaignTrailWaypoints.json`

- `server/src/services/seasons/eventHooks.js`

- `server/src/services/seasons/eventLifecycle.js`

- `server/src/services/seasons/eventReadiness.js`

- `server/src/services/seasons/eventSchedule.js`

- `server/src/services/seasons/eventWorldRuntime.js`

- `server/src/services/seasons/fabricatorBonuses.js`

- `server/src/services/seasons/fabricatorModel.js`

- `server/src/services/seasons/gallenteCombatCatalog.js`

- `server/src/services/seasons/gallenteCratePools.json`

- `server/src/services/seasons/gallenteCrateRewards.js`

- `server/src/services/seasons/gallenteDepotRuntime.js`

- `server/src/services/seasons/gallenteEventBonuses.js`

- `server/src/services/seasons/gallenteExplorationCatalog.js`

- `server/src/services/seasons/gallenteFabricatorRuntime.js`

- `server/src/services/seasons/gallenteFwCatalog.js`

- `server/src/services/seasons/gallenteHistoricalRewards.json`

- `server/src/services/seasons/gallenteHomefrontCatalog.js`

- `server/src/services/seasons/gallenteMiningCatalog.js`

- `server/src/services/seasons/gallenteMissionCatalog.js`

- `server/src/services/seasons/gallenteMissionRuntime.js`

- `server/src/services/seasons/gallenteOutreachCatalog.js`

- `server/src/services/seasons/gallenteOutreachRecipes.json`

- `server/src/services/seasons/gallenteOutreachRuntime.js`

- `server/src/services/seasons/gallenteTradingCatalog.js`

- `server/src/services/seasons/homefrontEventAdapter.js`

- `server/src/services/seasons/installEventRuntime.js`

- `server/src/services/seasons/warpathMissionCatalog.js`

- `server/src/services/seasons/warpathMissionPayloads.js`

- `server/src/services/seasons/warpathMissionRuntime.js`


### Wormholes and exploration

- `server/src/services/exploration/depotHackingBoard.js`

- `server/src/services/exploration/gas/kspaceGasPlacementAuthority.js`

- `server/src/services/exploration/hackingBoardEngine.js`

- `server/src/services/exploration/hackingContainerState.js`

- `server/src/services/exploration/signatures/privateMissionAnomalies.js`

- `server/src/services/exploration/wormholes/kspaceWanderingProfiles.json`

- `server/src/services/exploration/wormholes/systemWideEffectsSync.js`

- `server/src/services/tale/taleSessionNotices.js`


### GM, chat and nodegraph

- `server/src/services/chat/commands/changeOwner.js`

- `server/src/services/chat/commands/copyShip.js`

- `server/src/services/chat/commands/dogma.js`

- `server/src/services/chat/commands/fireShow.js`

- `server/src/services/chat/commands/nativeCompanionRetirement.js`

- `server/src/services/chat/commands/nativeDestroy.js`

- `server/src/services/chat/commands/nativeSpawns.js`

- `server/src/services/chat/commands/onlineModules.js`

- `server/src/services/chat/commands/orbitalDiscoveryCommand.js`

- `server/src/services/chat/commands/reportDesync.js`

- `server/src/services/chat/commands/shipSetup.js`

- `server/src/services/chat/commands/skinrComponents.js`

- `server/src/services/chat/commands/skinrDrafts.js`

- `server/src/services/chat/commands/transientGates.js`

- `server/src/services/chat/commands/unspawn.js`

- `server/src/services/chat/commands/voidReplay.js`

- `server/src/services/nodegraph/nativeAtomSignatures.json`

- `server/src/services/nodegraph/nodeGraphAuthority.js`

- `server/src/services/nodegraph/nodeGraphCompiler.js`

- `server/src/services/nodegraph/nodeGraphEffects.js`

- `server/src/services/nodegraph/nodeGraphEvents.js`

- `server/src/services/nodegraph/nodeGraphRuntime.js`


### Content, config, static tables

- `content-packs/evejs-air-tutorial-1.0.11.json`

- `content-packs/evejs-career-agents-1.4.4.json`

- `content-packs/evejs-fractured-legacy-1.0.2.json`

- `content-packs/evejs-fw-open-1.0.0.json`

- `content-packs/evejs-gallente-depots-1.0.0.json`

- `content-packs/evejs-gallente-fabricators-1.0.1.json`

- `content-packs/evejs-homefront-1.0.5.json`

- `content-packs/evejs-incursions-1.0.0.json`

- `content-packs/evejs-security-missions-level-1-1.1.10.json`

- `content-packs/evejs-security-missions-level-2-1.1.9.json`

- `content-packs/evejs-security-missions-level-3-1.1.9.json`

- `content-packs/evejs-security-missions-level-4-1.1.9.json`

- `content-packs/evejs-security-missions-level-5-1.1.9.json`

- `content-packs/evejs-visions-of-greatness-1.0.4.json`

- `content-packs/evejs-wormhole-sites-1.2.1.json`

- `server/src/config/eventSchedules.js`

- `server/src/config/loginCampaigns.js`

- `server/src/config/securityMissionChains.js`

- `server/src/config/securityMissionChains.json`

- `server/src/config/securityMissionCorporationStanding.json`

- `server/src/config/securityMissionDeclineBaseline.js`

- `server/src/config/securityMissionDeclineBaseline.json`

- `server/src/config/securityMissionDeclineMeasured.json`

- `server/src/config/securityMissionDestinationMeasured.json`

- `server/src/config/securityMissionDestinations.js`

- `server/src/config/securityMissionDestinations.json`

- `server/src/config/securityMissionOfferEvidence.js`

- `server/src/config/securityMissionOfferExceptions.json`

- `server/src/config/securityMissionOfferObservations.json`

- `server/src/config/securityMissionOfferPolicy.json`

- `server/src/config/securityMissionOfferProjection.js`

- `server/src/config/securityMissionOfferProjection.json`

- `server/src/config/securityMissionRewardBaseline.js`

- `server/src/config/securityMissionRewardBaseline.json`

- `server/src/config/securityMissionStandingBaseline.js`

- `server/src/config/securityMissionStandingBaseline.json`

- `server/src/config/securityMissionStandingMeasured.json`

- `server/src/contentPacks/careerMissionContent.js`

- `server/src/contentPacks/dropFolderFingerprint.js`

- `server/src/contentPacks/packReadRun.js`

- `server/src/contentPacks/securityMissionBinding.js`

- `server/src/gameStore/data/agent-spawn-report.json`

- `tools/DatabaseCreator/staticTables/characterCreationSchools/data.json`

- `tools/DatabaseCreator/staticTables/clientMicroJumpComponents/data.json`

- `tools/DatabaseCreator/staticTables/clientPvpFilaments/data.json`

- `tools/DatabaseCreator/staticTables/clientShipLinkComponents/data.json`

- `tools/DatabaseCreator/staticTables/clientTowComponents/data.json`

- `tools/DatabaseCreator/staticTables/shipSkinComponentAuthority/data.json`

- `tools/DatabaseCreator/staticTables/shipSkinComponentListingAuthority/data.json`

- `tools/DatabaseCreator/staticTables/shipSkinMarketAuthority/data.json`

- `tools/DatabaseCreator/staticTables/shipSkinSequencingAuthority/data.json`

- `tools/DatabaseCreator/staticTables/shipSkinStoreAuthority/data.json`


### Other server services

- `server/src/services/_shared/averageMarketPrices.js`

- `server/src/services/_shared/clientCdnUrl.js`

- `server/src/services/_shared/clientSpaceComponentRules.js`

- `server/src/services/account/accountingEntryTypes.json`

- `server/src/services/account/nodeGraphReward.js`

- `server/src/services/contracts/contractAuctionClaims.js`

- `server/src/services/contracts/contractAuctionJobs.js`

- `server/src/services/contracts/contractAuctionRuntime.js`

- `server/src/services/contracts/contractCourierVolumeReconciliation.js`

- `server/src/services/corporation/allianceAffiliationSettlement.js`

- `server/src/services/corporation/applicationArchiveScope.js`

- `server/src/services/corporation/corporationMemberLimit.js`

- `server/src/services/corporation/corporationRoleChecks.js`

- `server/src/services/corporation/corporationTax.js`

- `server/src/services/corporation/warAuthority.js`

- `server/src/services/corporation/warDeclarationState.js`

- `server/src/services/corporation/warGmCommands.js`

- `server/src/services/corporation/warHeadquartersAuthority.js`

- `server/src/services/corporation/warPayloads.js`

- `server/src/services/dogma/crystalDurability.js`

- `server/src/services/dogma/gmDogmaOverrides.js`

- `server/src/services/dogma/shipConditionAmounts.js`

- `server/src/services/dynamic/dynamicBountyPolicy.js`

- `server/src/services/dynamic/dynamicBountyRuntime.js`

- `server/src/services/dynamic/essAccessRuntime.js`

- `server/src/services/dynamic/essEligibility.js`

- `server/src/services/dynamic/essFieldRuntime.js`

- `server/src/services/dynamic/essPresentationRuntime.js`

- `server/src/services/dynamic/essRepository.js`

- `server/src/services/dynamic/essReserveKeyAuthority.json`

- `server/src/services/dynamic/essReserveRuntime.js`

- `server/src/services/dynamic/essSettlementRuntime.js`

- `server/src/services/fighter/fighterDamageApplication.js`

- `server/src/services/fleetShare/fleetShare.js`

- `server/src/services/fleets/fleetIdentityAllocator.js`

- `server/src/services/fleets/fleetWarpFollowers.js`

- `server/src/services/freelance/freelanceDelivery.js`

- `server/src/services/freelance/freelanceFunding.js`

- `server/src/services/freelance/freelanceProjectState.js`

- `server/src/services/freelance/freelanceRuntime.js`

- `server/src/services/freelance/freelanceSettlement.js`

- `server/src/services/inventory/blueprintRewardState.js`

- `server/src/services/inventory/careerCombatOutcomes.js`

- `server/src/services/inventory/fracturedLegacyCargo.js`

- `server/src/services/inventory/incursionCombatOutcomes.js`

- `server/src/services/inventory/incursionFactoryCustody.js`

- `server/src/services/inventory/invBroker/assetSafetyRecovery.js`

- `server/src/services/inventory/invBroker/fitRefusalNotice.js`

- `server/src/services/inventory/invBroker/plexVault.js`

- `server/src/services/inventory/invBroker/tradeInventory.js`

- `server/src/services/inventory/itemMutationEvents.js`

- `server/src/services/inventory/lootOwnerTransferMetadata.js`

- `server/src/services/inventory/materializedMissionCargoContainer.js`

- `server/src/services/inventory/missionMiningItemCustody.js`

- `server/src/services/inventory/missionRescueCargo.js`

- `server/src/services/inventory/missionSalvageItemCustody.js`

- `server/src/services/inventory/shipRackHeatState.js`

- `server/src/services/mining/airMiningSettlement.js`

- `server/src/services/mining/miningCargoCommits.js`

- `server/src/services/mining/npcMiningCargo.js`

- `server/src/services/mining/playerMiningCargo.js`

- `server/src/services/mining/shipResourceStateCache.js`

- `server/src/services/newEdenStore/licenseLifecycle.js`

- `server/src/services/newEdenStore/licenseLifecycleRuntime.js`

- `server/src/services/newEdenStore/rewardRedeemTokens.js`

- `server/src/services/notifications/notificationYaml.js`

- `server/src/services/planet/planetOrbitalPlacement.js`

- `server/src/services/planet/planetOrbitalReinforcement.js`

- `server/src/services/planet/planetResourceAbundance.js`

- `server/src/services/planet/planetResourceDistribution.json`

- `server/src/services/planet/skyhookOfficeReplacement.js`

- `server/src/services/reprocessing/compressionAuthority.js`

- `server/src/services/reprocessing/compressionAuthority.json`

- `server/src/services/rewards/groupRewardCatalog.js`

- `server/src/services/rewards/groupRewardCatalog.json`

- `server/src/services/rewards/groupRewardEvaluator.js`

- `server/src/services/security/customsInspectionAuthority.js`

- `server/src/services/security/customsInspectionCaseStore.js`

- `server/src/services/security/customsInspectionEnforcement.js`

- `server/src/services/security/customsInspectionEvaluator.js`

- `server/src/services/security/customsInspectionPresentation.js`

- `server/src/services/security/customsInspectionRuntime.js`

- `server/src/services/security/duelAchievementRuntime.js`

- `server/src/services/security/securityTax.js`

- `server/src/services/ship/airDeathRestrictions.js`

- `server/src/services/ship/breacherPodDeathRuntime.js`

- `server/src/services/ship/interdictionVolumes.js`

- `server/src/services/ship/mercenaryDenRuntime.js`

- `server/src/services/ship/safeLogoffRuntime.js`

- `server/src/services/ship/selfDestructRuntime.js`

- `server/src/services/ship/shipSkinComponentAuthority.js`

- `server/src/services/ship/shipSkinComponentRuntime.js`

- `server/src/services/ship/shipSkinDesignFiles.js`

- `server/src/services/ship/shipSkinDesignValidation.js`

- `server/src/services/ship/shipSkinDraftRuntime.js`

- `server/src/services/ship/shipSkinLayoutValidation.js`

- `server/src/services/ship/shipSkinMarketAuthority.js`

- `server/src/services/ship/shipSkinSequencingAuthority.js`

- `server/src/services/ship/shipSkinSequencingRuntime.js`

- `server/src/services/ship/shipSkinSkillPolicy.js`

- `server/src/services/ship/shipSkinState.js`

- `server/src/services/ship/shipSkinStoreAuthority.js`

- `server/src/services/ship/shipSkinStoreTerms.js`

- `server/src/services/ship/shipSkinThirdPartyRuntime.js`

- `server/src/services/ship/warpFailureUserError.js`

- `server/src/services/skills/omegaTrainingIntervals.js`

- `server/src/services/spaceComponents/appliedProximityEffectAuthority.json`

- `server/src/services/spaceComponents/appliedProximityEffectsRuntime.js`

- `server/src/services/spaceComponents/linkWithShipRuntime.js`

- `server/src/services/spaceComponents/nativeItemTraderAuthority.json`

- `server/src/services/spaceComponents/nativeItemTraderRuntime.js`

- `server/src/services/spaceComponents/shipLinkPolicy.js`

- `server/src/services/spaceComponents/shipLinkRestrictions.js`

- `server/src/services/spaceComponents/towGameObjectiveRuntime.js`

- `server/src/services/starbase/controlTowerResourceAuthority.json`

- `server/src/services/structure/npcStationDirectory.js`

- `server/src/services/structure/structureAccessNotifications.js`

- `server/src/services/structure/structureAssetSafetyNotifications.js`

- `server/src/services/structure/structureAssetSafetyRecovery.js`

- `server/src/services/structure/structureAssetSafetyScheduler.js`

- `server/src/services/structure/structureFlexBuiltIns.js`

- `server/src/services/structure/structureJumpBridgeLinks.js`

- `server/src/services/structure/structureRemovedFromSpaceNotice.js`

- `server/src/services/structure/structureSelfDestructRuntime.js`

- `server/src/services/structure/structureTimerInventoryFanout.js`

- `server/src/services/subscription/subscriptionNotices.js`

- `server/src/services/subscription/subscriptionState.js`

- `server/src/services/user/mctCertificateActivation.js`


### Runtime, network and packaging

- `ProfileServer.bat`

- `server/src/_secondary/express/gatewayServices/auraGuidanceGatewayService.js`

- `server/src/_secondary/express/gatewayServices/careerGatewayProto.js`

- `server/src/_secondary/express/gatewayServices/careerGoalGatewayService.js`

- `server/src/_secondary/express/gatewayServices/careerProtocol.generated.json`

- `server/src/_secondary/express/gatewayServices/shipSkinComponentLicenseGatewayService.js`

- `server/src/_secondary/express/gatewayServices/shipSkinDraftGatewayService.js`

- `server/src/_secondary/express/gatewayServices/shipSkinGatewayHelpers.js`

- `server/src/_secondary/express/gatewayServices/shipSkinGatewayProto.js`

- `server/src/_secondary/express/gatewayServices/shipSkinMarketGatewayService.js`

- `server/src/_secondary/express/gatewayServices/shipSkinProtocol.generated.json`

- `server/src/_secondary/express/gatewayServices/shipSkinSequencingGatewayService.js`

- `server/src/_secondary/express/gatewayServices/shipSkinThirdPartyGatewayService.js`

- `server/src/_secondary/express/gatewayServices/userLicenseGatewayService.js`

- `server/src/common/dogmaAttributeBounds.js`

- `server/src/network/attributeChangeWireValues.js`

- `server/src/network/gameFrame.js`

- `server/src/network/tcp/accountLoginClose.js`

- `server/src/utils/pauseProfiler.js`


### Mods and tools

- `scripts/Diagnostics/capture-process-counters.ps1`

- `tools/ClientCodeGrabber/uncompyle6_py2_strings.py`

- `tools/ErrorReport/profile-server.js`

- `tools/Maintenance/repair-sov-prospecting-names.js`

- `tools/market-seederv4/BuildMarketSeedV4.bat`

- `tools/market-seederv4/config/market-seederv4.local.toml`


### Documentation

- `doc/PAUSE_PROFILING.md`

- `tools/market-seederv4/README.md`


</details>

<details>
<summary>Removed: 211 files</summary>


### Space, combat and sites

- `server/src/services/incursion/incursionAuthority.js`

- `server/src/services/incursion/incursionPlanner.js`

- `server/src/services/incursion/incursionSitePopulation.js`

- `server/src/space/destiny/authority/destinyAuthority.js`

- `server/src/space/destiny/authority/destinyContracts.js`

- `server/src/space/destiny/authority/destinyJourneyLog.js`

- `server/src/space/destiny/authority/destinySessionState.js`

- `server/src/space/destiny/batching/systemHistory.js`

- `server/src/space/destiny/commands/missile.js`

- `server/src/space/destiny/commands/warpTick.js`

- `server/src/space/destiny/delivery/deliveryPolicy.js`

- `server/src/space/destiny/delivery/deliveryTransaction.js`

- `server/src/space/destiny/delivery/explodingDestructionStamp.js`

- `server/src/space/destiny/delivery/fallbackStampAllocator.js`

- `server/src/space/destiny/delivery/postWarpDemotion.js`

- `server/src/space/destiny/delivery/removeBallsSessionStamp.js`

- `server/src/space/destiny/delivery/sameSceneShipHandoff.js`

- `server/src/space/destiny/delivery/sendOptions.js`

- `server/src/space/destiny/delivery/sessionAlignedStamp.js`

- `server/src/space/destiny/delivery/sessionStamps.js`

- `server/src/space/destiny/delivery/sessionWindows.js`

- `server/src/space/destiny/delivery/sync.js`

- `server/src/space/destiny/delivery/transitionStamps.js`

- `server/src/space/destiny/dispatch/contractDispatch.js`

- `server/src/space/destiny/dispatch/destinyDispatch.js`

- `server/src/space/destiny/dispatch/observerReconciliation.js`

- `server/src/space/destiny/dispatch/ownerDispatch.js`

- `server/src/space/destiny/dispatch/sceneRefresh.js`

- `server/src/space/destiny/dispatch/ticker.js`

- `server/src/space/destiny/dispatch/watcherCorrections.js`

- `server/src/space/destiny/presentation/addBallsTimeline.js`

- `server/src/space/destiny/presentation/shipBoarding.js`

- `server/src/space/destiny/projection/kinematics.js`

- `server/src/space/destiny/simulation/activeWarpTick.js`

- `server/src/space/destiny/simulation/movement.js`

- `server/src/space/destiny/simulation/nativeSubwarp.js`

- `server/src/space/destiny/simulation/pilotWarpPhase.js`

- `server/src/space/destiny/simulation/warpBuilders.js`

- `server/src/space/destiny/simulation/warpDiagnostics.js`

- `server/src/space/destiny/simulation/warpNativeBoundary.js`

- `server/src/space/destiny/simulation/warpRamp.js`

- `server/src/space/destiny/simulation/warpSerialization.js`

- `server/src/space/destiny/simulation/warpState.js`

- `server/src/space/destiny/visibility/acquisition.js`

- `server/src/space/destiny/visibility/bubbleUpdater.js`

- `server/src/space/destiny/visibility/constants.js`

- `server/src/space/destiny/visibility/controlState.js`

- `server/src/space/destiny/visibility/delta.js`

- `server/src/space/destiny/visibility/membershipState.js`

- `server/src/space/destiny/visibility/referencePosition.js`

- `server/src/space/destiny/visibility/sceneTickCoordinator.js`

- `server/src/space/destiny/visibility/warpHandoff.js`

- `server/src/space/destiny/visibility/warpLanding.js`

- `server/src/space/runtime/destinyStamps.js`

- `server/src/space/runtime/visibilityMembership.js`


### Characters, agents and events

- `server/src/services/agent/agentInSpaceRuntime.js`

- `server/src/services/agent/missionLevelResolver.js`

- `server/src/services/agent/missionPoolResolver.js`

- `server/src/services/character/certificateMgrService.js`


### Content, config, static tables

- `content-packs/evejs-wormhole-sites-1.2.0.json`

- `tools/DatabaseCreator/assign-mission-levels.js`

- `tools/DatabaseCreator/staticTables/incursionSpawnHistory/data.json`

- `tools/DatabaseCreator/sync-agent-authority-from-sde.js`

- `tools/DatabaseCreator/sync-mission-authority-from-sde.js`


### Other server services

- `server/src/services/bookmark/bookmarkService.js`

- `server/src/services/ship/crabRewardAuthority.js`

- `server/src/services/ship/crabWaveAuthority.js`

- `server/src/services/structure/classicPirateFobRuntime.js`

- `server/src/services/structure/insurgencyFobRuntime.js`

- `server/src/services/structure/insurgencyFobRuntimeController.js`

- `server/src/services/structure/pirateFobRuntimeController.js`


### Runtime, network and packaging

- `.cbmignore`

- `server/src/_secondary/lonelyFleetCommand/abilityCooldown.js`

- `server/src/_secondary/lonelyFleetCommand/actionExecutor.js`

- `server/src/_secondary/lonelyFleetCommand/agentController.js`

- `server/src/_secondary/lonelyFleetCommand/ammoSelector.js`

- `server/src/_secondary/lonelyFleetCommand/behaviorEngine.js`

- `server/src/_secondary/lonelyFleetCommand/behaviorEngine.test.js`

- `server/src/_secondary/lonelyFleetCommand/broadcastAdapter.js`

- `server/src/_secondary/lonelyFleetCommand/capChainCoordinator.js`

- `server/src/_secondary/lonelyFleetCommand/capChainCoordinator.test.js`

- `server/src/_secondary/lonelyFleetCommand/chargeLoader.js`

- `server/src/_secondary/lonelyFleetCommand/chatCoordinator.js`

- `server/src/_secondary/lonelyFleetCommand/companionControl.js`

- `server/src/_secondary/lonelyFleetCommand/configLoader.js`

- `server/src/_secondary/lonelyFleetCommand/controlApi.js`

- `server/src/_secondary/lonelyFleetCommand/destinationResolver.js`

- `server/src/_secondary/lonelyFleetCommand/doctrineRuntime.js`

- `server/src/_secondary/lonelyFleetCommand/doctrineRuntime.test.js`

- `server/src/_secondary/lonelyFleetCommand/engagementPlanner.js`

- `server/src/_secondary/lonelyFleetCommand/fighterPolicy.js`

- `server/src/_secondary/lonelyFleetCommand/fitCatalog.js`

- `server/src/_secondary/lonelyFleetCommand/fleetClientJoinCompatibility.js`

- `server/src/_secondary/lonelyFleetCommand/fleetCoordinator.js`

- `server/src/_secondary/lonelyFleetCommand/fleetStatus.js`

- `server/src/_secondary/lonelyFleetCommand/instructionUi.js`

- `server/src/_secondary/lonelyFleetCommand/lfcLog.js`

- `server/src/_secondary/lonelyFleetCommand/lockPlanner.js`

- `server/src/_secondary/lonelyFleetCommand/moduleCatalog.js`

- `server/src/_secondary/lonelyFleetCommand/pilotController.js`

- `server/src/_secondary/lonelyFleetCommand/pilotPerception.js`

- `server/src/_secondary/lonelyFleetCommand/pilotRuntime.js`

- `server/src/_secondary/lonelyFleetCommand/pocketPlanner.js`

- `server/src/_secondary/lonelyFleetCommand/provisioning.js`

- `server/src/_secondary/lonelyFleetCommand/recoveryCoordinator.js`

- `server/src/_secondary/lonelyFleetCommand/roleControllers.js`

- `server/src/_secondary/lonelyFleetCommand/runtimeStats.js`

- `server/src/_secondary/lonelyFleetCommand/server.js`

- `server/src/_secondary/lonelyFleetCommand/shipCapabilityResolver.js`

- `server/src/_secondary/lonelyFleetCommand/shipCapabilityResolver.test.js`

- `server/src/_secondary/lonelyFleetCommand/shipProvisioning.js`

- `server/src/_secondary/lonelyFleetCommand/stationServices.js`

- `server/src/_secondary/lonelyFleetCommand/summonPlanner.js`

- `server/src/_secondary/lonelyFleetCommand/survivalPolicy.js`

- `server/src/_secondary/lonelyFleetCommand/t3ddProvisioning.js`

- `server/src/_secondary/lonelyFleetCommand/targetLifecycle.js`

- `server/src/_secondary/lonelyFleetCommand/targetSelector.js`

- `server/src/_secondary/lonelyFleetCommand/threatEvaluator.js`

- `server/src/_secondary/lonelyFleetCommand/threatEvaluator.test.js`

- `server/src/_secondary/lonelyFleetCommand/travelPlanner.js`

- `server/src/_secondary/lonelyFleetCommand/wireDiagnostics.js`

- `server/src/_secondary/lonelyFleetCommand/worldController.js`

- `server/src/_secondary/lonelyFleetCommand/xmppPresenceBridge.js`

- `server/src/network/tcp/gameRuntimeLifecycle.test.js`


### Mods and tools

- `mods/eft-fit-spawner/evejs-launcher.mod.json`

- `mods/eft-fit-spawner/helper.js`

- `mods/eft-fit-spawner/package.json`

- `mods/eft-fit-spawner/payload/server/controlApi.js`

- `mods/eft-fit-spawner/payload/server/eftFit.js`

- `mods/eft-fit-spawner/payload/server/fitSpawner.js`

- `mods/eft-fit-spawner/payload/server/fitTemplateStore.js`

- `mods/eft-fit-spawner/payload/server/server.js`

- `mods/lonely-fleet-command/evejs-launcher.mod.json`

- `mods/lonely-fleet-command/helper.js`

- `mods/lonely-fleet-command/payload/server/abilityCooldown.js`

- `mods/lonely-fleet-command/payload/server/actionExecutor.js`

- `mods/lonely-fleet-command/payload/server/agentController.js`

- `mods/lonely-fleet-command/payload/server/behaviorEngine.js`

- `mods/lonely-fleet-command/payload/server/behaviorEngine.test.js`

- `mods/lonely-fleet-command/payload/server/broadcastAdapter.js`

- `mods/lonely-fleet-command/payload/server/capChainCoordinator.js`

- `mods/lonely-fleet-command/payload/server/capChainCoordinator.test.js`

- `mods/lonely-fleet-command/payload/server/chargeLoader.js`

- `mods/lonely-fleet-command/payload/server/chatCoordinator.js`

- `mods/lonely-fleet-command/payload/server/controlApi.js`

- `mods/lonely-fleet-command/payload/server/doctrineRuntime.js`

- `mods/lonely-fleet-command/payload/server/doctrineRuntime.test.js`

- `mods/lonely-fleet-command/payload/server/engagementPlanner.js`

- `mods/lonely-fleet-command/payload/server/fighterPolicy.js`

- `mods/lonely-fleet-command/payload/server/fitCatalog.js`

- `mods/lonely-fleet-command/payload/server/fleetClientJoinCompatibility.js`

- `mods/lonely-fleet-command/payload/server/fleetCoordinator.js`

- `mods/lonely-fleet-command/payload/server/instructionUi.js`

- `mods/lonely-fleet-command/payload/server/moduleCatalog.js`

- `mods/lonely-fleet-command/payload/server/pilotRuntime.js`

- `mods/lonely-fleet-command/payload/server/provisioning.js`

- `mods/lonely-fleet-command/payload/server/recoveryCoordinator.js`

- `mods/lonely-fleet-command/payload/server/roleControllers.js`

- `mods/lonely-fleet-command/payload/server/server.js`

- `mods/lonely-fleet-command/payload/server/shipCapabilityResolver.js`

- `mods/lonely-fleet-command/payload/server/shipCapabilityResolver.test.js`

- `mods/lonely-fleet-command/payload/server/summonPlanner.js`

- `mods/lonely-fleet-command/payload/server/t3ddProvisioning.js`

- `mods/lonely-fleet-command/payload/server/targetLifecycle.js`

- `mods/lonely-fleet-command/payload/server/threatEvaluator.js`

- `mods/lonely-fleet-command/payload/server/threatEvaluator.test.js`

- `mods/lonely-fleet-command/payload/server/wireDiagnostics.js`

- `mods/lonely-fleet-command/payload/server/worldController.js`

- `mods/lonely-fleet-command/payload/server/xmppPresenceBridge.js`

- `tools/IncursionData/ingest_incursion_history.py`

- `tools/IncursionData/test_ingest_incursion_history.py`

- `tools/run-isolated-tests.js`


### Tests and fixtures

- `mods/eft-fit-spawner/tests/eftFit.test.js`

- `mods/eft-fit-spawner/tests/fitSpawner.test.js`

- `mods/eft-fit-spawner/tests/fitTemplateStore.test.js`

- `server/tests/agentAuthorityInSpace.test.js`

- `server/tests/agentAuthoritySdeSync.test.js`

- `server/tests/agentInSpaceRuntime.test.js`

- `server/tests/classicPirateFobRuntimeControllerIntegration.test.js`

- `server/tests/crabNpcSpawnerIntegration.test.js`

- `server/tests/customAgencyProviderService.test.js`

- `server/tests/dynamicItemAttributeSync.test.js`

- `server/tests/fixtures/agentAuthority/_sde.jsonl`

- `server/tests/fixtures/agentAuthority/agentsInSpace.fixture.jsonl`

- `server/tests/fixtures/incursion-history/2026/2026-09-26/incursions-2026-09-26_00-00-07.json.bz2`

- `server/tests/fixtures/incursion-history/2026/2026-09-26/incursions-2026-09-26_00-05-07.json.bz2`

- `server/tests/fixtures/incursion-history/2026/2026-09-26/index.json`

- `server/tests/fwWarzoneSolarsystemService.test.js`

- `server/tests/helpers/isolatedGameStore.js`

- `server/tests/incursionActivation.test.js`

- `server/tests/incursionAuthority.test.js`

- `server/tests/incursionPlanner.test.js`

- `server/tests/incursionRuntime.test.js`

- `server/tests/incursionScheduler.test.js`

- `server/tests/insurgencyFobRuntimeController.test.js`

- `server/tests/insurgencySolarsystemService.test.js`

- `server/tests/missionAcceptanceA31A41.test.js`

- `server/tests/missionAcceptanceA42A49.test.js`

- `server/tests/missionAcceptanceA50A59.test.js`

- `server/tests/missionEncounterBindings.test.js`

- `server/tests/missionLevelResolver.test.js`

- `server/tests/missionPoolResolver.test.js`

- `server/tests/mobileAnalysisBeaconRuntime.test.js`

- `server/tests/pirateFobRuntimeController.test.js`

- `server/tests/structureClassicPirateFobRuntime.test.js`

- `server/tests/structureInsurgencyFobRuntime.test.js`

- `server/tests/taleMgrService.test.js`


### Documentation

- `doc/NON_DOCKER_SETUP_AUDIT.md`

- `mods/eft-fit-spawner/README.md`

- `mods/lonely-fleet-command/README.md`

- `tools/IncursionData/README.md`


</details>

<details>
<summary>Content changed: 697 files</summary>


### Space, combat and sites

- `server/src/abyssal/policy/abyssalSpaceRuntimeIntegration.js`

- `server/src/abyssal/policy/abyssalWeatherPolicy.js`

- `server/src/abyssal/ports/abyssalEnginePort.js`

- `server/src/abyssal/runtime/abyssalAdmissionValidation.js`

- `server/src/abyssal/runtime/abyssalBallparkPriming.js`

- `server/src/abyssal/runtime/abyssalCleanupPreparation.js`

- `server/src/abyssal/runtime/abyssalClientReadyRequest.js`

- `server/src/abyssal/runtime/abyssalEntranceRequest.js`

- `server/src/abyssal/runtime/abyssalEntryMovement.js`

- `server/src/abyssal/runtime/abyssalEnvironmentControllerAdapters.js`

- `server/src/abyssal/runtime/abyssalGateActivationRange.js`

- `server/src/abyssal/runtime/abyssalManagerRuntimeGroup12.js`

- `server/src/abyssal/runtime/abyssalManagerRuntimeGroup4.js`

- `server/src/abyssal/runtime/abyssalManagerRuntimeGroup6.js`

- `server/src/abyssal/runtime/abyssalNpcControllers.js`

- `server/src/abyssal/runtime/abyssalNpcDestruction.js`

- `server/src/abyssal/runtime/abyssalNpcLifecycleMetadata.js`

- `server/src/abyssal/runtime/abyssalNpcMaterialization.js`

- `server/src/abyssal/runtime/abyssalNpcWatchdog.js`

- `server/src/abyssal/runtime/abyssalRelogInspection.js`

- `server/src/abyssal/runtime/abyssalRoomDelivery.js`

- `server/src/abyssal/runtime/abyssalRoomProgression.js`

- `server/src/abyssal/runtime/abyssalRoomTransferMovement.js`

- `server/src/abyssal/runtime/abyssalRoomTransferPublication.js`

- `server/src/abyssal/runtime/abyssalRuntimeFacade.js`

- `server/src/abyssal/runtime/abyssalSceneDiagnostics.js`

- `server/src/abyssal/runtime/abyssalSpawnGeometry.js`

- `server/src/abyssal/runtime/abyssalWeatherRuntime.js`

- `server/src/abyssal/runtime/controllers/abyssalBoundaryMechanicsController.js`

- `server/src/abyssal/runtime/controllers/abyssalSuppressorDiagnostics.js`

- `server/src/abyssal/runtime/controllers/abyssalSuppressorMechanicsController.js`

- `server/src/services/activity/pvpFilamentMgrService.js`

- `server/src/services/activity/voidSpaceMgrService.js`

- `server/src/services/dungeon/dungeonAuthority.js`

- `server/src/services/dungeon/dungeonInstanceCacheMgrService.js`

- `server/src/services/dungeon/dungeonRuntime.js`

- `server/src/services/dungeon/dungeonRuntimeState.js`

- `server/src/services/dungeon/dungeonService.js`

- `server/src/services/dungeon/dungeonSiteAdapter.js`

- `server/src/services/dungeon/dungeonSiteSpawnPolicy.js`

- `server/src/services/dungeon/dungeonTrackingRuntime.js`

- `server/src/services/dungeon/dungeonUniverseRuntime.js`

- `server/src/services/dungeon/dungeonUniverseSiteService.js`

- `server/src/services/dungeon/dungeonVisibilityPolicy.js`

- `server/src/services/dungeon/globalSiteSchedulerRuntime.js`

- `server/src/services/dungeon/keeperService.js`

- `server/src/services/dungeon/operationSiteRuntime.js`

- `server/src/services/faction/facWarMgrService.js`

- `server/src/services/faction/fwVictoryPointMgrService.js`

- `server/src/services/incursion/incursionPayloads.js`

- `server/src/services/incursion/incursionRuntime.js`

- `server/src/services/sovereignty/mercenaryDenState.js`

- `server/src/services/sovereignty/skyhookHarvest.js`

- `server/src/services/sovereignty/skyhookRuntime.js`

- `server/src/services/sovereignty/sovAutoState.js`

- `server/src/services/sovereignty/sovCampaignNotifications.js`

- `server/src/services/sovereignty/sovCampaignState.js`

- `server/src/services/sovereignty/sovCaptureState.js`

- `server/src/services/sovereignty/sovChatCommands.js`

- `server/src/services/sovereignty/sovConstants.js`

- `server/src/services/sovereignty/sovFlexStructures.js`

- `server/src/services/sovereignty/sovGmState.js`

- `server/src/services/sovereignty/sovHubFuelDeposit.js`

- `server/src/services/sovereignty/sovHubLossDrops.js`

- `server/src/services/sovereignty/sovHubUpgradeInstall.js`

- `server/src/services/sovereignty/sovModernState.js`

- `server/src/services/sovereignty/sovNotifications.js`

- `server/src/services/sovereignty/sovPlayerDeployment.js`

- `server/src/services/sovereignty/sovSpaceInterop.js`

- `server/src/services/sovereignty/sovState.js`

- `server/src/services/sovereignty/sovUpgradeSupport.js`

- `server/src/space/abyssalEngineAdapter.js`

- `server/src/space/asteroids/asteroidService.js`

- `server/src/space/combat/damage.js`

- `server/src/space/combat/killmailTracker.js`

- `server/src/space/combat/weaponDogma.js`

- `server/src/space/defaultEmpireScenery.js`

- `server/src/space/destiny/authority/README.md`

- `server/src/space/destiny/authority/scenePositionAuthority.js`

- `server/src/space/destiny/authority/sceneRangeDecision.js`

- `server/src/space/destiny/batching/README.md`

- `server/src/space/destiny/batching/parkUpdateBatcher.js`

- `server/src/space/destiny/commands/drone.js`

- `server/src/space/destiny/commands/droneState.js`

- `server/src/space/destiny/commands/fighter.js`

- `server/src/space/destiny/commands/mobileTractorUnit.js`

- `server/src/space/destiny/commands/starbaseForceField.js`

- `server/src/space/destiny/commands/stopSpeedCommands.js`

- `server/src/space/destiny/commands/subwarpCommands.js`

- `server/src/space/destiny/commands/teleport.js`

- `server/src/space/destiny/commands/tractorBeam.js`

- `server/src/space/destiny/commands/warpCommands.js`

- `server/src/space/destiny/constants.js`

- `server/src/space/destiny/delivery/michelleContract.js`

- `server/src/space/destiny/delivery/stamps.js`

- `server/src/space/destiny/dispatch/dispatchUtils.js`

- `server/src/space/destiny/identity/entityID.js`

- `server/src/space/destiny/index.js`

- `server/src/space/destiny/presentation/activeSpecialFxReplay.js`

- `server/src/space/destiny/presentation/addBalls.js`

- `server/src/space/destiny/presentation/freshAcquire.js`

- `server/src/space/destiny/presentation/mode.js`

- `server/src/space/destiny/presentation/removeBalls.js`

- `server/src/space/destiny/presentation/shipPrime.js`

- `server/src/space/destiny/presentation/specialFxPayloads.js`

- `server/src/space/destiny/projection/entityProjection.js`

- `server/src/space/destiny/projection/range.js`

- `server/src/space/destiny/protocol/payloadIdentity.js`

- `server/src/space/destiny/simulation/README.md`

- `server/src/space/destiny/simulation/motionState.js`

- `server/src/space/destiny/simulation/warpPhase.js`

- `server/src/space/destiny/simulation/warpTargets.js`

- `server/src/space/destiny/stream/actions.js`

- `server/src/space/destiny/stream/ballEncoding.js`

- `server/src/space/destiny/stream/statePayloads.js`

- `server/src/space/destiny/visibility/README.md`

- `server/src/space/gateSkinCommand.js`

- `server/src/space/modules/assistanceModuleRuntime.js`

- `server/src/space/modules/commandBurstRuntime.js`

- `server/src/space/modules/entosisLinkRuntime.js`

- `server/src/space/modules/hostileModuleRuntime.js`

- `server/src/space/modules/hudIconRuntime.js`

- `server/src/space/modules/jammerModuleRuntime.js`

- `server/src/space/modules/liveModuleAttributes.js`

- `server/src/space/modules/microJumpDriveRuntime.js`

- `server/src/space/modules/salvagerRuntime.js`

- `server/src/space/modules/smartbombRuntime.js`

- `server/src/space/modules/superweapons/superweaponRuntime.js`

- `server/src/space/modules/tractorBeamRuntime.js`

- `server/src/space/npc/beltRatRuntime.js`

- `server/src/space/npc/capitals/capitalNpcMovement.js`

- `server/src/space/npc/capitals/capitalNpcWrecks.js`

- `server/src/space/npc/empireSecurity/empireSecurityNpcCatalog.js`

- `server/src/space/npc/nativeNpcService.js`

- `server/src/space/npc/nativeNpcStore.js`

- `server/src/space/npc/nativeNpcWreckService.js`

- `server/src/space/npc/npcAmbientMaterialization.js`

- `server/src/space/npc/npcAnchorRelevance.js`

- `server/src/space/npc/npcBehaviorLoop.js`

- `server/src/space/npc/npcCombatDormancy.js`

- `server/src/space/npc/npcData.js`

- `server/src/space/npc/npcDefaultConcordRules.js`

- `server/src/space/npc/npcEquipment.js`

- `server/src/space/npc/npcLoot.js`

- `server/src/space/npc/npcRegistry.js`

- `server/src/space/npc/npcRuntime.js`

- `server/src/space/npc/npcSceneActivity.js`

- `server/src/space/npc/npcSentryPolicy.js`

- `server/src/space/npc/npcService.js`

- `server/src/space/npc/npcTestService.js`

- `server/src/space/runtime.js`

- `server/src/space/runtime/abyssalRoomEntity.js`

- `server/src/space/runtime/activationSpaceRestrictions.js`

- `server/src/space/runtime/activeModuleSpecialFx.js`

- `server/src/space/runtime/activeShipModifiers.js`

- `server/src/space/runtime/attributeChangeNotification.js`

- `server/src/space/runtime/autoTargetingMissiles.js`

- `server/src/space/runtime/characterStateBridge.js`

- `server/src/space/runtime/combatDamageMessages.js`

- `server/src/space/runtime/combatDestruction.js`

- `server/src/space/runtime/commandBurst.js`

- `server/src/space/runtime/crimewatchAdapter.js`

- `server/src/space/runtime/cynosuralField.js`

- `server/src/space/runtime/destinyDeliveryEligibility.js`

- `server/src/space/runtime/destinyMovementEngine.js`

- `server/src/space/runtime/entityDistance.js`

- `server/src/space/runtime/entityScopeTags.js`

- `server/src/space/runtime/entitySpacePersistence.js`

- `server/src/space/runtime/firstSightPresentation.js`

- `server/src/space/runtime/forceFieldContainment.js`

- `server/src/space/runtime/godmaShipEffectNotification.js`

- `server/src/space/runtime/hudIconStates.js`

- `server/src/space/runtime/inventoryLootRights.js`

- `server/src/space/runtime/inventorySpaceEntity.js`

- `server/src/space/runtime/loadedChargeState.js`

- `server/src/space/runtime/microJumpDriveEligibility.js`

- `server/src/space/runtime/missileEntity.js`

- `server/src/space/runtime/moduleAttributes.js`

- `server/src/space/runtime/moduleChargeReload.js`

- `server/src/space/runtime/moduleConsequenceDelivery.js`

- `server/src/space/runtime/moduleEffectShutdown.js`

- `server/src/space/runtime/moduleHeat.js`

- `server/src/space/runtime/moduleRuntimeCallbacks.js`

- `server/src/space/runtime/moduleSpecialFx.js`

- `server/src/space/runtime/nativeRecordSceneOwnership.js`

- `server/src/space/runtime/passiveDerivedState.js`

- `server/src/space/runtime/propulsionModule.js`

- `server/src/space/runtime/runtimeDebugInstrumentation.js`

- `server/src/space/runtime/scene/abyssalTrace.js`

- `server/src/space/runtime/scene/cloaking.js`

- `server/src/space/runtime/scene/clock.js`

- `server/src/space/runtime/scene/dynamicEntities.js`

- `server/src/space/runtime/scene/expiryIndex.js`

- `server/src/space/runtime/scene/fleetWarp.js`

- `server/src/space/runtime/scene/modules.js`

- `server/src/space/runtime/scene/pilotCommands.js`

- `server/src/space/runtime/scene/presentation.js`

- `server/src/space/runtime/scene/sessions.js`

- `server/src/space/runtime/scene/shipDerivedState.js`

- `server/src/space/runtime/scene/spatialTopology.js`

- `server/src/space/runtime/scene/staticEntities.js`

- `server/src/space/runtime/scene/targeting.js`

- `server/src/space/runtime/scene/tick.js`

- `server/src/space/runtime/scene/visibility.js`

- `server/src/space/runtime/sessionNotificationTime.js`

- `server/src/space/runtime/shipActionRestrictions.js`

- `server/src/space/runtime/shipCloak.js`

- `server/src/space/runtime/shipEntity.js`

- `server/src/space/runtime/shipHealthState.js`

- `server/src/space/runtime/solarSystemScene.js`

- `server/src/space/runtime/spaceRuntime/docking.js`

- `server/src/space/runtime/spaceRuntime/dynamicEntities.js`

- `server/src/space/runtime/spaceRuntime/entityQueries.js`

- `server/src/space/runtime/spaceRuntime/pilotCommands.js`

- `server/src/space/runtime/spaceRuntime/sceneDisposal.js`

- `server/src/space/runtime/spaceRuntime/sceneRegistry.js`

- `server/src/space/runtime/spaceRuntime/sessions.js`

- `server/src/space/runtime/spaceRuntime/specialFx.js`

- `server/src/space/runtime/spaceRuntime/stargateJump.js`

- `server/src/space/runtime/spaceRuntime/tick.js`

- `server/src/space/runtime/spaceRuntime/universeSites.js`

- `server/src/space/runtime/squadronRuntimeBridge.js`

- `server/src/space/runtime/staticSceneEntity.js`

- `server/src/space/runtime/stationDockingGeometry.js`

- `server/src/space/runtime/structureLifecycleFanout.js`

- `server/src/space/runtime/structureSessionHost.js`

- `server/src/space/runtime/structureTether.js`

- `server/src/space/runtime/targetDamageApplication.js`

- `server/src/space/runtime/targeting.js`

- `server/src/space/runtime/timedInvulnerability.js`

- `server/src/space/runtime/visibility.js`

- `server/src/space/runtime/weaponCycle.js`

- `server/src/space/runtime/weaponSnapshot.js`

- `server/src/space/shipDestruction.js`

- `server/src/space/structureTethering.js`

- `server/src/space/transitions.js`

- `server/src/space/wreckUtils.js`


### Industry and market

- `externalservices/market-server/README.md`

- `externalservices/market-server/crates/market-common/src/lib.rs`

- `externalservices/market-server/src/rpc.rs`

- `externalservices/market-server/src/state.rs`

- `externalservices/market-server/src/state/order_book_tests.rs`

- `externalservices/market-server/src/state/transaction_tests.rs`

- `server/src/services/industry/blueprintManagerService.js`

- `server/src/services/industry/industryAccess.js`

- `server/src/services/industry/industryConstants.js`

- `server/src/services/industry/industryFacilityModifiers.js`

- `server/src/services/industry/industryMonitorService.js`

- `server/src/services/industry/industryNotifications.js`

- `server/src/services/industry/industryParityHelpers.js`

- `server/src/services/industry/industryPricing.js`

- `server/src/services/industry/industryRuntimeState.js`

- `server/src/services/industry/starbaseAssemblyProfiles.json`

- `server/src/services/market/marketEscrowState.js`

- `server/src/services/market/marketProxyService.js`

- `server/src/services/market/marketRules.js`

- `server/src/services/market/marketTopology.js`


### Characters, agents and events

- `server/src/services/agent/agentAuthority.js`

- `server/src/services/agent/agentMgrService.js`

- `server/src/services/agent/agentMissionRuntime.js`

- `server/src/services/agent/epicArcStatusService.js`

- `server/src/services/agent/missionAuthority.js`

- `server/src/services/agent/missionRewardSettlementRecovery.js`

- `server/src/services/agent/missionRewardSettlementState.js`

- `server/src/services/agent/missionRuntimeState.js`

- `server/src/services/agent/missionTrackerMgrService.js`

- `server/src/services/agent/researchRuntime.js`

- `server/src/services/campaign/crateService.js`

- `server/src/services/campaign/loginCampaignService.js`

- `server/src/services/character/accessGroupBookmarkMgrService.js`

- `server/src/services/character/accessGroupStore.js`

- `server/src/services/character/charMgrGlobalAssets.js`

- `server/src/services/character/charMgrService.js`

- `server/src/services/character/charService.js`

- `server/src/services/character/characterDeletionRuntime.js`

- `server/src/services/character/characterEnergyMgrService.js`

- `server/src/services/character/characterEnergyState.js`

- `server/src/services/character/characterSettingsState.js`

- `server/src/services/character/characterState.js`

- `server/src/services/character/dogmaInvalidationVersion.js`

- `server/src/services/character/homeStationService.js`

- `server/src/services/character/npcStandingsAuthority.js`

- `server/src/services/character/operationsManagerService.js`

- `server/src/services/character/ownerGroupManagerService.js`

- `server/src/services/character/paperDollPayloads.js`

- `server/src/services/character/paperDollServerService.js`

- `server/src/services/character/projectDiscoveryService.js`

- `server/src/services/character/seasonManagerService.js`

- `server/src/services/character/standingRuntime.js`

- `server/src/services/npe/airNpeService.js`

- `server/src/services/npe/nesIntroService.js`

- `server/src/services/npe/tutorialRuntime.js`


### Wormholes and exploration

- `server/src/services/exploration/hackingMgrService.js`

- `server/src/services/exploration/probes/probeSceneRuntime.js`

- `server/src/services/exploration/scanMgrService.js`

- `server/src/services/exploration/signatures/providers/sceneAnomalySiteProvider.js`

- `server/src/services/exploration/signatures/signatureRuntime.js`

- `server/src/services/exploration/wormholes/wormholeAuthority.js`

- `server/src/services/exploration/wormholes/wormholeMgrService.js`

- `server/src/services/exploration/wormholes/wormholeRuntime.js`

- `server/src/services/exploration/wormholes/wormholeRuntimeState.js`

- `server/src/services/tale/taleMgrService.js`


### GM, chat and nodegraph

- `server/src/services/admin/slashService.js`

- `server/src/services/chat/chatCommands.js`

- `server/src/services/chat/commands/fitModule.js`

- `server/src/services/chat/commands/itemSeeding.js`

- `server/src/services/chat/commands/shipHealth.js`

- `server/src/services/chat/commands/shipSkins.js`

- `server/src/services/chat/commands/spaceObjects.js`

- `server/src/services/chat/commands/transport.js`

- `server/src/services/chat/commands/unfitModule.js`

- `server/src/services/chat/sessionChatSync.js`

- `server/src/services/chat/sessionRegistry.js`

- `server/src/services/chat/xmppChatMgrService.js`

- `server/src/services/chat/xmppStubServer.js`

- `server/src/services/nodegraph/nodeGraphService.js`


### Content, config, static tables

- `config/gameplay.json`

- `config/mining.json`

- `config/server.json`

- `config/version.json`

- `config/world.json`

- `server/src/config/dataRoot.js`

- `server/src/config/manager.js`

- `server/src/config/productionMissionPolicy.js`

- `server/src/config/schema/gameplay.js`

- `server/src/config/schema/mining.js`

- `server/src/config/schema/npc.js`

- `server/src/config/schema/server.js`

- `server/src/config/schema/world.js`

- `server/src/contentPacks/autoInstallContentPacks.js`

- `server/src/contentPacks/contentPackManager.js`

- `server/src/gameStore/index.js`

- `server/src/gameStore/sqliteStore.js`

- `server/src/gameStore/syncStaticTables.js`

- `server/src/gameStore/tableOwnership.js`

- `server/src/gameStore/tableRepository.js`

- `tools/DatabaseCreator/database-creator.js`

- `tools/DatabaseCreator/staticTables/agentAuthority/data.json`

- `tools/DatabaseCreator/staticTables/dungeonAuthority/data.json`

- `tools/DatabaseCreator/staticTables/evermarksCatalog/data.json`

- `tools/DatabaseCreator/staticTables/explorationAuthority/data.json`

- `tools/DatabaseCreator/staticTables/explorationWormholeStatic/data.json`

- `tools/DatabaseCreator/staticTables/missionAuthority/data.json`

- `tools/DatabaseCreator/staticTables/npcBehaviorProfiles/data.json`

- `tools/DatabaseCreator/staticTables/npcLootTables/data.json`

- `tools/DatabaseCreator/staticTables/npcProfiles/data.json`

- `tools/DatabaseCreator/staticTables/npcSpawnGroups/data.json`

- `tools/DatabaseCreator/staticTables/npcStandingsAuthority/data.json`


### Other server services

- `server/src/services/_other/contractMgrService.js`

- `server/src/services/_other/contractProxyService.js`

- `server/src/services/_other/searchService.js`

- `server/src/services/_shared/conduitJumpRuntime.js`

- `server/src/services/_shared/emergencyWarpRuntime.js`

- `server/src/services/_shared/godmaMultiEvent.js`

- `server/src/services/_shared/guestLists.js`

- `server/src/services/_shared/identityAllocator.js`

- `server/src/services/_shared/referenceData.js`

- `server/src/services/_shared/serviceHelpers.js`

- `server/src/services/_shared/sessionDisconnect.js`

- `server/src/services/_shared/spaceLootEntitlement.js`

- `server/src/services/account/accountRoleProfiles.js`

- `server/src/services/account/accountService.js`

- `server/src/services/account/billMgrService.js`

- `server/src/services/account/billRuntimeState.js`

- `server/src/services/account/vaultManagerService.js`

- `server/src/services/account/walletAuthorityOwnerState.js`

- `server/src/services/account/walletAuthorityProcess.worker.js`

- `server/src/services/account/walletAuthorityProjection.js`

- `server/src/services/account/walletAuthorityReceiptState.js`

- `server/src/services/account/walletState.js`

- `server/src/services/achievement/achievementRuntime.js`

- `server/src/services/achievement/achievementState.js`

- `server/src/services/agency/customAgencyProviderService.js`

- `server/src/services/baseService.js`

- `server/src/services/bookmark/bookmarkNotifications.js`

- `server/src/services/bookmark/bookmarkRuntimeAccess.js`

- `server/src/services/bookmark/bookmarkRuntimeState.js`

- `server/src/services/bookmark/bookmarkRuntimeStore.js`

- `server/src/services/bounty/bountyProxyService.js`

- `server/src/services/bounty/bountyRuntime.js`

- `server/src/services/bounty/killRightNotifications.js`

- `server/src/services/cache/objectCacheRuntime.js`

- `server/src/services/calendar/calendarAccess.js`

- `server/src/services/config/configService.js`

- `server/src/services/contracts/contractNotifications.js`

- `server/src/services/contracts/contractRuntimeState.js`

- `server/src/services/contracts/contractSettlementState.js`

- `server/src/services/corporation/allianceRegistryRuntime.js`

- `server/src/services/corporation/contactLabelState.js`

- `server/src/services/corporation/corpApplicationNotifications.js`

- `server/src/services/corporation/corpColorsState.js`

- `server/src/services/corporation/corpGoalsState.js`

- `server/src/services/corporation/corpLifecycleNotifications.js`

- `server/src/services/corporation/corpMemberActionState.js`

- `server/src/services/corporation/corpMemberQueryState.js`

- `server/src/services/corporation/corpRecProxyService.js`

- `server/src/services/corporation/corpRegistryRuntime.js`

- `server/src/services/corporation/corpService.js`

- `server/src/services/corporation/corpStationMgrService.js`

- `server/src/services/corporation/corpWalletState.js`

- `server/src/services/corporation/corpmgrService.js`

- `server/src/services/corporation/corporationAuthority.js`

- `server/src/services/corporation/corporationGoalsState.js`

- `server/src/services/corporation/corporationLiquidationState.js`

- `server/src/services/corporation/corporationNotifications.js`

- `server/src/services/corporation/corporationRuntimeState.js`

- `server/src/services/corporation/corporationState.js`

- `server/src/services/corporation/fwCharacterEnlistmentMgrService.js`

- `server/src/services/corporation/itemLockingService.js`

- `server/src/services/corporation/lpService.js`

- `server/src/services/corporation/lpWalletState.js`

- `server/src/services/corporation/mutualWarInviteMgrService.js`

- `server/src/services/corporation/officeManagerService.js`

- `server/src/services/corporation/officeRentalBilling.js`

- `server/src/services/corporation/officeRentalSettlementState.js`

- `server/src/services/corporation/peaceTreatyMgrService.js`

- `server/src/services/corporation/voteManagerService.js`

- `server/src/services/corporation/voteRuntimeState.js`

- `server/src/services/corporation/warNegotiationRuntimeState.js`

- `server/src/services/corporation/warNotificationCenter.js`

- `server/src/services/corporation/warRegistryService.js`

- `server/src/services/corporation/warRuntimeState.js`

- `server/src/services/corporation/warStatisticMgrService.js`

- `server/src/services/corporation/warsInfoMgrService.js`

- `server/src/services/dailyGoals/dailyGoalsCatalog.js`

- `server/src/services/dailyGoals/dailyGoalsProto.js`

- `server/src/services/dailyGoals/dailyGoalsState.js`

- `server/src/services/dogma/activationHeatState.js`

- `server/src/services/dogma/brain/characterBrainRuntime.js`

- `server/src/services/dogma/brain/providers/industryBrainProvider.js`

- `server/src/services/dogma/dogmaService.js`

- `server/src/services/dogma/dogmaService/implantsAndBoosters.js`

- `server/src/services/dogma/dogmaService/itemInfo.js`

- `server/src/services/dogma/dogmaService/moduleOperation.js`

- `server/src/services/dogma/dogmaService/moduleRepair.js`

- `server/src/services/dogma/dogmaService/weaponBanks.js`

- `server/src/services/dogma/dynamicItemService.js`

- `server/src/services/dogma/implants/activeImplantModifiers.js`

- `server/src/services/drone/droneDogma.js`

- `server/src/services/drone/droneRuntime.js`

- `server/src/services/dynamic/dynamicBountyMgrService.js`

- `server/src/services/dynamic/dynamicResourceState.js`

- `server/src/services/dynamic/essMgrService.js`

- `server/src/services/eveOffline/transportLocationResolver.js`

- `server/src/services/evermarks/lpStorePurchaseState.js`

- `server/src/services/fighter/fighterDogma.js`

- `server/src/services/fighter/fighterInventory.js`

- `server/src/services/fighter/fighterMgrService.js`

- `server/src/services/fighter/fighterRuntime.js`

- `server/src/services/fighter/npc/npcSupercarrierDirector.js`

- `server/src/services/fitting/allianceFittingMgrService.js`

- `server/src/services/fitting/charFittingMgrService.js`

- `server/src/services/fitting/chargeCompatibilityPolicy.js`

- `server/src/services/fitting/corpFittingMgrService.js`

- `server/src/services/fitting/fittingMgrServiceHelpers.js`

- `server/src/services/fitting/liveFittingState.js`

- `server/src/services/fleets/fleetObjectHandlerService.js`

- `server/src/services/fleets/fleetPayloads.js`

- `server/src/services/fleets/fleetRuntime.js`

- `server/src/services/fleets/fleetWarpCommandAssociation.js`

- `server/src/services/insurance/insuranceRuntime.js`

- `server/src/services/inventory/contrabandItemCustody.js`

- `server/src/services/inventory/contractItemCustody.js`

- `server/src/services/inventory/deathItemCustody.js`

- `server/src/services/inventory/deliveryItemCustody.js`

- `server/src/services/inventory/invBroker/fittingOperations.js`

- `server/src/services/inventory/invBroker/planetaryImportExport.js`

- `server/src/services/inventory/invBrokerItemCustody.js`

- `server/src/services/inventory/invBrokerService.js`

- `server/src/services/inventory/inventoryVisibilityRules.js`

- `server/src/services/inventory/itemCustody.js`

- `server/src/services/inventory/itemStore.js`

- `server/src/services/inventory/itemTypeRegistry.js`

- `server/src/services/inventory/lpStoreItemCustody.js`

- `server/src/services/inventory/missionRewardItemCustody.js`

- `server/src/services/inventory/mobileItemCustody.js`

- `server/src/services/inventory/planetaryCustomsItemCustody.js`

- `server/src/services/inventory/reprocessingItemCustody.js`

- `server/src/services/inventory/simulationInventoryProjection.js`

- `server/src/services/inventory/spaceDebrisState.js`

- `server/src/services/inventory/tradeItemCustody.js`

- `server/src/services/killmail/deathOutcomeResolver.js`

- `server/src/services/killmail/killmailPreparationTasks.js`

- `server/src/services/killmail/killmailState.js`

- `server/src/services/killmail/killmailWorkerPool.js`

- `server/src/services/machoNet/machoNetService.js`

- `server/src/services/mail/mailMgrService.js`

- `server/src/services/mail/mailNotifications.js`

- `server/src/services/mail/mailState.js`

- `server/src/services/mail/mailingListsMgrService.js`

- `server/src/services/map/fwWarzoneSolarsystemService.js`

- `server/src/services/map/insurgencySolarsystemService.js`

- `server/src/services/map/mapService.js`

- `server/src/services/map/mapTagsAuthority.js`

- `server/src/services/map/mapTelemetryState.js`

- `server/src/services/map/solarSystemInterferenceState.js`

- `server/src/services/map/solarsystemInterferenceMgrService.js`

- `server/src/services/map/sovMgrService.js`

- `server/src/services/mining/corpMiningLedgerService.js`

- `server/src/services/mining/miningLedgerState.js`

- `server/src/services/mining/miningNpcOperations.js`

- `server/src/services/mining/miningResourceSiteService.js`

- `server/src/services/mining/miningRuntime.js`

- `server/src/services/mining/miningRuntimeState.js`

- `server/src/services/mining/oreAnomalySecurityAuthority.js`

- `server/src/services/moduleGrouping/moduleGroupingRules.js`

- `server/src/services/moduleGrouping/moduleGroupingState.js`

- `server/src/services/newEdenStore/storePurchaseSettlementState.js`

- `server/src/services/newEdenStore/storeState.js`

- `server/src/services/notifications/notificationConstants.js`

- `server/src/services/notifications/notificationMgrService.js`

- `server/src/services/notifications/notificationState.js`

- `server/src/services/online/onlineStatusRuntime.js`

- `server/src/services/planet/planetMgrService.js`

- `server/src/services/planet/planetOrbitalRegistryBrokerService.js`

- `server/src/services/planet/planetOrbitalState.js`

- `server/src/services/planet/planetRuntimeStore.js`

- `server/src/services/planet/planetStaticData.js`

- `server/src/services/planet/planetaryCustomsSettlementState.js`

- `server/src/services/raffles/raffleInventory.js`

- `server/src/services/raffles/raffleNotifications.js`

- `server/src/services/reprocessing/reprocessingSettlementState.js`

- `server/src/services/reprocessing/reprocessingStaticData.js`

- `server/src/services/rewards/rewardMgrService.js`

- `server/src/services/scheduler/durableScheduler.js`

- `server/src/services/scheduler/durableSchedulerProcess.worker.js`

- `server/src/services/scheduler/productionSchedulerRuntime.js`

- `server/src/services/security/contrabandInspectionRuntime.js`

- `server/src/services/security/contrabandPenaltyState.js`

- `server/src/services/security/crimewatchRuntimeState.js`

- `server/src/services/security/crimewatchService.js`

- `server/src/services/security/crimewatchState.js`

- `server/src/services/security/securityMgrService.js`

- `server/src/services/serviceManager.js`

- `server/src/services/ship/beyonceService.js`

- `server/src/services/ship/cargoContainerRuntime.js`

- `server/src/services/ship/controlTowerRuntime.js`

- `server/src/services/ship/deployableCynoRuntime.js`

- `server/src/services/ship/ejectRuntime.js`

- `server/src/services/ship/jettisonRuntime.js`

- `server/src/services/ship/mobileAnalysisBeaconRuntime.js`

- `server/src/services/ship/mobileCynoInhibitorRuntime.js`

- `server/src/services/ship/mobileDepotRuntime.js`

- `server/src/services/ship/mobileMicroJumpUnitRuntime.js`

- `server/src/services/ship/mobileObservatoryRuntime.js`

- `server/src/services/ship/mobilePhaseAnchorRuntime.js`

- `server/src/services/ship/mobileScanInhibitorRuntime.js`

- `server/src/services/ship/mobileSiphonUnitRuntime.js`

- `server/src/services/ship/mobileTractorUnitRuntime.js`

- `server/src/services/ship/mobileWarpDisruptorRuntime.js`

- `server/src/services/ship/orbitalLaunchRuntime.js`

- `server/src/services/ship/rookieShipRuntime.js`

- `server/src/services/ship/shipCosmeticsMgrService.js`

- `server/src/services/ship/shipCosmeticsState.js`

- `server/src/services/ship/shipKillCounterState.js`

- `server/src/services/ship/shipService.js`

- `server/src/services/ship/shipStanceRuntime.js`

- `server/src/services/ship/spaceShipSwapRuntime.js`

- `server/src/services/skills/boosters/boosterRuntime.js`

- `server/src/services/skills/certificates/certificateRuntime.js`

- `server/src/services/skills/expertSystems/expertSystemNotifications.js`

- `server/src/services/skills/expertSystems/expertSystemRuntime.js`

- `server/src/services/skills/expertSystems/expertSystemState.js`

- `server/src/services/skills/plans/skillPlanState.js`

- `server/src/services/skills/skillMgr2Service.js`

- `server/src/services/skills/skillMgrService.js`

- `server/src/services/skills/skillState.js`

- `server/src/services/skills/trading/skillTradingRuntime.js`

- `server/src/services/skills/training/skillQueueNotifications.js`

- `server/src/services/skills/training/skillQueueRuntime.js`

- `server/src/services/skills/training/skillTrainingMath.js`

- `server/src/services/starbase/starbaseControlRuntime.js`

- `server/src/services/starbase/starbaseDefenseController.js`

- `server/src/services/starbase/starbaseForceFieldBoundary.js`

- `server/src/services/starbase/starbaseInventoryRuntime.js`

- `server/src/services/starbase/starbaseModuleRuntime.js`

- `server/src/services/starbase/starbaseReinforcementRuntime.js`

- `server/src/services/station/jumpCloneRuntime.js`

- `server/src/services/station/jumpCloneService.js`

- `server/src/services/station/repairRuntime.js`

- `server/src/services/station/stationService.js`

- `server/src/services/structure/autoMoonMiningService.js`

- `server/src/services/structure/moonExtractionsService.js`

- `server/src/services/structure/moonMiningSpaceRuntime.js`

- `server/src/services/structure/structureAssetSafetyService.js`

- `server/src/services/structure/structureAssetSafetyState.js`

- `server/src/services/structure/structureCargoDeliveryService.js`

- `server/src/services/structure/structureChatCommands.js`

- `server/src/services/structure/structureConstants.js`

- `server/src/services/structure/structureControlService.js`

- `server/src/services/structure/structureCynoBeaconMgrService.js`

- `server/src/services/structure/structureDeliveriesService.js`

- `server/src/services/structure/structureDeliverySaga.js`

- `server/src/services/structure/structureDestructionLootState.js`

- `server/src/services/structure/structureDirectoryService.js`

- `server/src/services/structure/structureDockedRecoveryState.js`

- `server/src/services/structure/structureDockingService.js`

- `server/src/services/structure/structureFlexFuelRuntime.js`

- `server/src/services/structure/structureJumpBridgeMgrService.js`

- `server/src/services/structure/structureLocatorGeometry.js`

- `server/src/services/structure/structurePaintworkState.js`

- `server/src/services/structure/structurePayloads.js`

- `server/src/services/structure/structureProfilesService.js`

- `server/src/services/structure/structureProfilesState.js`

- `server/src/services/structure/structureService.js`

- `server/src/services/structure/structureServiceAuthority.js`

- `server/src/services/structure/structureServiceModules.js`

- `server/src/services/structure/structureSettingsService.js`

- `server/src/services/structure/structureState.js`

- `server/src/services/structure/structureWreckState.js`

- `server/src/services/subscription/subscriptionMgrService.js`

- `server/src/services/support/publicQaToolsServerService.js`

- `server/src/services/trade/tradeMgrService.js`

- `server/src/services/user/userService.js`


### Runtime, network and packaging

- `.gitattributes`

- `.gitignore`

- `BuildMarketSeed.bat`

- `Play.bat`

- `StartMarketServer.bat`

- `docker/entrypoint.sh`

- `package-lock.json`

- `package.json`

- `server/bootstrap.js`

- `server/contracts/evejs-web-bridge-contract.json`

- `server/index.js`

- `server/package-lock.json`

- `server/package.json`

- `server/src/RemoteRepShow/remoteRepairFleetCommands.js`

- `server/src/_secondary/chat/chatRuntime.js`

- `server/src/_secondary/chat/chatStore.js`

- `server/src/_secondary/express/evejsWebGatewayRuntime.js`

- `server/src/_secondary/express/gatewayServices/achievementGatewayService.js`

- `server/src/_secondary/express/gatewayServices/compatibilityGatewayService.js`

- `server/src/_secondary/express/gatewayServices/corpGoalsGatewayService.js`

- `server/src/_secondary/express/gatewayServices/corpGoalsProto.js`

- `server/src/_secondary/express/gatewayServices/corpSkillPlanGatewayService.js`

- `server/src/_secondary/express/gatewayServices/dailyGoalsGatewayService.js`

- `server/src/_secondary/express/gatewayServices/evermarksGatewayService.js`

- `server/src/_secondary/express/gatewayServices/freelanceGatewayService.js`

- `server/src/_secondary/express/gatewayServices/index.js`

- `server/src/_secondary/express/gatewayServices/insurgencyGatewayService.js`

- `server/src/_secondary/express/gatewayServices/localChatGatewayService.js`

- `server/src/_secondary/express/gatewayServices/mercenaryDenGatewayService.js`

- `server/src/_secondary/express/gatewayServices/shipLogoGatewayService.js`

- `server/src/_secondary/express/gatewayServices/sovereigntyGatewayService.js`

- `server/src/_secondary/express/publicGatewayLocal.js`

- `server/src/_secondary/express/server.js`

- `server/src/_secondary/fitting/fittingRuntime.js`

- `server/src/_secondary/fitting/fittingStore.js`

- `server/src/_secondary/image/imageRequestResolver.js`

- `server/src/common/clone.js`

- `server/src/common/random.js`

- `server/src/network/clientSession.js`

- `server/src/network/marshalWorkerPool.js`

- `server/src/network/packetDispatcher.js`

- `server/src/network/spatialTrace.js`

- `server/src/network/syncLedger.js`

- `server/src/network/tcp/directGameSocketTransport.js`

- `server/src/network/tcp/gameRuntimeLifecycle.js`

- `server/src/network/tcp/handshake.js`

- `server/src/network/tcp/index.js`

- `server/src/network/tcp/socketLifecycle.js`

- `server/src/network/tcp/utils/decode/index.js`

- `server/src/network/tcp/utils/marshal.js`

- `server/src/utils/logger/index.js`

- `server/src/utils/rotatingLog.js`


### Mods and tools

- `tools/ClientCodeGrabber/extract.py`

- `tools/ClientSETUP/ClientSetup.ps1`

- `tools/ClientSETUP/scripts/EvEJSConfig.example.bat`

- `tools/ClientSETUP/scripts/Measure-EvEJSOffline.ps1`

- `tools/ClientSETUP/scripts/Set-EvEJSFirewall.ps1`

- `tools/ClientSETUP/scripts/Test-EvEJSOfflineServer.ps1`

- `tools/ClientSETUP/scripts/Test-EvEJSResources.ps1`

- `tools/ErrorReport/collect-error-report.js`

- `tools/Setup/Setup-EvEJS.ps1`

- `tools/market-seed/BuildMarketSeed.bat`

- `tools/market-seed/MarketSeedGui.ps1`

- `tools/market-seed/src/main.rs`

- `tools/market-seederv2/BuildMarketSeedV2.bat`

- `tools/market-seederv2/src/main.rs`


### Tests and fixtures

- `server/src/services/corporation/coreFixtureSeeder.js`


### Documentation

- `README.md`

- `doc/CONTENT_PACKS.md`

- `doc/CommandGuide.txt`

- `doc/LAUNCHERS.md`

- `doc/MARKET_SEEDER.md`

- `doc/MARKET_SETUP.md`

- `doc/OFFLINE.md`

- `doc/OFFLINE_CLIENT_FLAGS.md`

- `doc/SETUP.md`

- `doc/TOOLS.md`

- `doc/TROUBLESHOOTING.md`

- `doc/UPDATING.md`

- `tools/ErrorReport/README.md`

- `tools/market-seed/README.md`

- `tools/market-seederv2/README.md`

- `tools/market-seederv3/README.md`


</details>

<details>
<summary>Line-ending-only changes: 969 files</summary>


### Space, combat and sites

- `server/src/abyssal/catalog/abyssalBaseRoomLayoutCatalog.js`

- `server/src/abyssal/catalog/abyssalClientErrorCodes.js`

- `server/src/abyssal/catalog/abyssalClientFxCatalog.js`

- `server/src/abyssal/catalog/abyssalContentAuthority.js`

- `server/src/abyssal/catalog/abyssalDarkWeatherCatalog.js`

- `server/src/abyssal/catalog/abyssalEnvironmentProfiles.js`

- `server/src/abyssal/catalog/abyssalEnvironmentalCatalog.js`

- `server/src/abyssal/catalog/abyssalFilamentCatalog.js`

- `server/src/abyssal/catalog/abyssalLootTables.generated.data/tier-0.json`

- `server/src/abyssal/catalog/abyssalLootTables.generated.data/tier-1.json`

- `server/src/abyssal/catalog/abyssalLootTables.generated.data/tier-2.json`

- `server/src/abyssal/catalog/abyssalLootTables.generated.data/tier-3.json`

- `server/src/abyssal/catalog/abyssalLootTables.generated.data/tier-4.json`

- `server/src/abyssal/catalog/abyssalLootTables.generated.data/tier-5.json`

- `server/src/abyssal/catalog/abyssalLootTables.generated.data/tier-6.json`

- `server/src/abyssal/catalog/abyssalLootTables.generated.js`

- `server/src/abyssal/catalog/abyssalLootTables.js`

- `server/src/abyssal/catalog/abyssalProceduralEnvironment.js`

- `server/src/abyssal/catalog/abyssalProceduralLoot.js`

- `server/src/abyssal/catalog/abyssalRuntimeConstants.js`

- `server/src/abyssal/catalog/abyssalSceneryProbeProfile.js`

- `server/src/abyssal/catalog/abyssalSuppressorMechanicsProfile.js`

- `server/src/abyssal/catalog/abyssalSuppressorPresentationProfile.js`

- `server/src/abyssal/catalog/abyssalTraceProfiles.js`

- `server/src/abyssal/catalog/abyssalTransitionProfiles.js`

- `server/src/abyssal/catalog/content/abyssalCompatibilityEncounterModels.js`

- `server/src/abyssal/catalog/content/abyssalCompatibilityExtractionTemplates.js`

- `server/src/abyssal/catalog/content/abyssalCompatibilityProfileModels.js`

- `server/src/abyssal/catalog/content/abyssalCompatibilityRewardTemplates.js`

- `server/src/abyssal/catalog/content/abyssalContentGenerationIdentity.js`

- `server/src/abyssal/catalog/content/abyssalContentReportModel.js`

- `server/src/abyssal/catalog/content/abyssalContentValues.js`

- `server/src/abyssal/catalog/content/abyssalFilamentCompletenessCore.js`

- `server/src/abyssal/catalog/content/abyssalFilamentCompletenessDetails.js`

- `server/src/abyssal/catalog/content/abyssalFilamentDefinitions.js`

- `server/src/abyssal/catalog/content/abyssalFilamentMetadata.js`

- `server/src/abyssal/catalog/content/abyssalFilamentResolution.js`

- `server/src/abyssal/catalog/content/abyssalProceduralContentComposer.js`

- `server/src/abyssal/catalog/content/abyssalRewardRegistry.js`

- `server/src/abyssal/catalog/content/abyssalRewardTables.generated.json`

- `server/src/abyssal/catalog/content/abyssalTelemetryCatalogueIdentity.js`

- `server/src/abyssal/catalog/content/authoredAbyssalContentRegistry.js`

- `server/src/abyssal/catalog/content/baseRoomGeometryFactsV1.json`

- `server/src/abyssal/catalog/content/environmentFactsV1.json`

- `server/src/abyssal/catalog/content/qsnaAbyssalSpawnRegistry.js`

- `server/src/abyssal/catalog/content/qsnaAbyssalSpawnTables.generated.json`

- `server/src/abyssal/catalog/content/retailAuthoredContent.generated.json`

- `server/src/abyssal/catalog/fixtures/t0TranquilRetailCapturedSceneryFixture.json`

- `server/src/abyssal/catalog/t0DarkDevMenuCatalog.js`

- `server/src/abyssal/catalog/t0DarkEncounterCatalog.js`

- `server/src/abyssal/catalog/t0DarkLayoutCatalog.js`

- `server/src/abyssal/catalog/t0DarkRetailCapturedLayout.js`

- `server/src/abyssal/catalog/t0TranquilRetailCapturedScenery.js`

- `server/src/abyssal/domain/abyssalBoundaryProfile.js`

- `server/src/abyssal/domain/abyssalDiagnosticValues.js`

- `server/src/abyssal/domain/abyssalGateActivationPolicy.js`

- `server/src/abyssal/domain/abyssalGatePresentation.js`

- `server/src/abyssal/domain/abyssalHostLocation.js`

- `server/src/abyssal/domain/abyssalRewardQuantityPolicy.js`

- `server/src/abyssal/domain/abyssalRoomEntityRoles.js`

- `server/src/abyssal/domain/abyssalRunScope.js`

- `server/src/abyssal/domain/abyssalRuntimeStateCodec.js`

- `server/src/abyssal/domain/abyssalRuntimeValues.js`

- `server/src/abyssal/domain/abyssalSceneKey.js`

- `server/src/abyssal/domain/abyssalStructuralClone.js`

- `server/src/abyssal/domain/abyssalTerminalTransitionPolicy.js`

- `server/src/abyssal/domain/abyssalValueUtils.js`

- `server/src/abyssal/index.js`

- `server/src/abyssal/policy/abyssalBeyonceIntegration.js`

- `server/src/abyssal/policy/abyssalChatIntegration.js`

- `server/src/abyssal/policy/abyssalJettisonIntegration.js`

- `server/src/abyssal/policy/abyssalLifecycleHooks.js`

- `server/src/abyssal/policy/abyssalOrbitalLaunchPolicy.js`

- `server/src/abyssal/policy/abyssalPublishedActivationPolicy.js`

- `server/src/abyssal/policy/abyssalScanIntegration.js`

- `server/src/abyssal/policy/abyssalScenePolicy.js`

- `server/src/abyssal/policy/abyssalStaffTools.js`

- `server/src/abyssal/policy/abyssalTransitionIntegration.js`

- `server/src/abyssal/policy/abyssalTransportActions.js`

- `server/src/abyssal/policy/abyssalVisibilityPolicy.js`

- `server/src/abyssal/policy/engineSupport.js`

- `server/src/abyssal/policy/publishedFacade.js`

- `server/src/abyssal/policy/transport.js`

- `server/src/abyssal/runtime/abyssalActivationInputs.js`

- `server/src/abyssal/runtime/abyssalActivationPolicy.js`

- `server/src/abyssal/runtime/abyssalActiveStatusLines.js`

- `server/src/abyssal/runtime/abyssalAdmissionComposition.js`

- `server/src/abyssal/runtime/abyssalAdmissionIdentity.js`

- `server/src/abyssal/runtime/abyssalAdmissionPolicies.js`

- `server/src/abyssal/runtime/abyssalArrivalEffects.js`

- `server/src/abyssal/runtime/abyssalCleanupWeather.js`

- `server/src/abyssal/runtime/abyssalClientEnvironment.js`

- `server/src/abyssal/runtime/abyssalCompatibilityRecovery.js`

- `server/src/abyssal/runtime/abyssalConduitPolicy.js`

- `server/src/abyssal/runtime/abyssalContentCompletenessCiGate.js`

- `server/src/abyssal/runtime/abyssalContentMaterializationViews.js`

- `server/src/abyssal/runtime/abyssalContentViews.js`

- `server/src/abyssal/runtime/abyssalDeadlinePolicy.js`

- `server/src/abyssal/runtime/abyssalDeathRecovery.js`

- `server/src/abyssal/runtime/abyssalDeploymentPlacement.js`

- `server/src/abyssal/runtime/abyssalDestructionCallback.js`

- `server/src/abyssal/runtime/abyssalDestructionCommits.js`

- `server/src/abyssal/runtime/abyssalDestructionReceipts.js`

- `server/src/abyssal/runtime/abyssalDestructionSessionSelection.js`

- `server/src/abyssal/runtime/abyssalDungeonNotifications.js`

- `server/src/abyssal/runtime/abyssalDurableReadbackCommit.js`

- `server/src/abyssal/runtime/abyssalEncounterSelection.js`

- `server/src/abyssal/runtime/abyssalEncounterViews.js`

- `server/src/abyssal/runtime/abyssalEntryBootstrap.js`

- `server/src/abyssal/runtime/abyssalEntryComposition.js`

- `server/src/abyssal/runtime/abyssalEntryReadiness.js`

- `server/src/abyssal/runtime/abyssalEnvironmentComposition.js`

- `server/src/abyssal/runtime/abyssalEnvironmentModel.js`

- `server/src/abyssal/runtime/abyssalEnvironmentObjects.js`

- `server/src/abyssal/runtime/abyssalExtractionNodes.js`

- `server/src/abyssal/runtime/abyssalFilamentCompensation.js`

- `server/src/abyssal/runtime/abyssalFinalExitCoordinator.js`

- `server/src/abyssal/runtime/abyssalFinalExitExecution.js`

- `server/src/abyssal/runtime/abyssalFinalExitInputs.js`

- `server/src/abyssal/runtime/abyssalFinalExitSettlement.js`

- `server/src/abyssal/runtime/abyssalHostRecovery.js`

- `server/src/abyssal/runtime/abyssalInactiveStatusLines.js`

- `server/src/abyssal/runtime/abyssalInventoryTypeViews.js`

- `server/src/abyssal/runtime/abyssalItemMetadata.js`

- `server/src/abyssal/runtime/abyssalLayoutDiagnostics.js`

- `server/src/abyssal/runtime/abyssalMaintenanceControl.js`

- `server/src/abyssal/runtime/abyssalManagerRuntime.js`

- `server/src/abyssal/runtime/abyssalManagerRuntimeGroup1.js`

- `server/src/abyssal/runtime/abyssalManagerRuntimeGroup10.js`

- `server/src/abyssal/runtime/abyssalManagerRuntimeGroup11.js`

- `server/src/abyssal/runtime/abyssalManagerRuntimeGroup13.js`

- `server/src/abyssal/runtime/abyssalManagerRuntimeGroup2.js`

- `server/src/abyssal/runtime/abyssalManagerRuntimeGroup3.js`

- `server/src/abyssal/runtime/abyssalManagerRuntimeGroup5.js`

- `server/src/abyssal/runtime/abyssalManagerRuntimeGroup7.js`

- `server/src/abyssal/runtime/abyssalManagerRuntimeGroup8.js`

- `server/src/abyssal/runtime/abyssalManagerRuntimeGroup9.js`

- `server/src/abyssal/runtime/abyssalNpcDeathState.js`

- `server/src/abyssal/runtime/abyssalNpcOutcomeComposition.js`

- `server/src/abyssal/runtime/abyssalNpcProfiles.js`

- `server/src/abyssal/runtime/abyssalParticipantAdmission.js`

- `server/src/abyssal/runtime/abyssalParticipantDestructionExecutor.js`

- `server/src/abyssal/runtime/abyssalParticipantDestructionOperationCoordinator.js`

- `server/src/abyssal/runtime/abyssalParticipantExecution.js`

- `server/src/abyssal/runtime/abyssalParticipantReturn.js`

- `server/src/abyssal/runtime/abyssalParticipantRoster.js`

- `server/src/abyssal/runtime/abyssalParticipantSession.js`

- `server/src/abyssal/runtime/abyssalPodSettlement.js`

- `server/src/abyssal/runtime/abyssalProbeCatalogue.js`

- `server/src/abyssal/runtime/abyssalProbeCleanup.js`

- `server/src/abyssal/runtime/abyssalProtocolErrors.js`

- `server/src/abyssal/runtime/abyssalRecoveryExecutionCoordinator.js`

- `server/src/abyssal/runtime/abyssalRecoveryPolicy.js`

- `server/src/abyssal/runtime/abyssalRecoveryTombstones.js`

- `server/src/abyssal/runtime/abyssalReturnObligations.js`

- `server/src/abyssal/runtime/abyssalReturnProtection.js`

- `server/src/abyssal/runtime/abyssalRewardArtifactCompletion.js`

- `server/src/abyssal/runtime/abyssalRewardArtifactDestruction.js`

- `server/src/abyssal/runtime/abyssalRewardArtifactDestructionOperationCoordinator.js`

- `server/src/abyssal/runtime/abyssalRewardCache.js`

- `server/src/abyssal/runtime/abyssalRewardCacheCreation.js`

- `server/src/abyssal/runtime/abyssalRewardCacheReconciliation.js`

- `server/src/abyssal/runtime/abyssalRewardComposition.js`

- `server/src/abyssal/runtime/abyssalRewardGrantCompletionCoordinator.js`

- `server/src/abyssal/runtime/abyssalRewardLifecycle.js`

- `server/src/abyssal/runtime/abyssalRewardLootResolution.js`

- `server/src/abyssal/runtime/abyssalRewardMaterialization.js`

- `server/src/abyssal/runtime/abyssalRewardMaterializationExecutor.js`

- `server/src/abyssal/runtime/abyssalRewardPlanModels.js`

- `server/src/abyssal/runtime/abyssalRewardPlanningPolicy.js`

- `server/src/abyssal/runtime/abyssalRewardProfiles.js`

- `server/src/abyssal/runtime/abyssalRewardSelection.js`

- `server/src/abyssal/runtime/abyssalRewardStateViews.js`

- `server/src/abyssal/runtime/abyssalRoomClientEffects.js`

- `server/src/abyssal/runtime/abyssalRoomDiagnostics.js`

- `server/src/abyssal/runtime/abyssalRoomMarkers.js`

- `server/src/abyssal/runtime/abyssalRoomMaterialization.js`

- `server/src/abyssal/runtime/abyssalRoomPrewarming.js`

- `server/src/abyssal/runtime/abyssalRoomReadiness.js`

- `server/src/abyssal/runtime/abyssalRoomStateModel.js`

- `server/src/abyssal/runtime/abyssalRoomTransferActivation.js`

- `server/src/abyssal/runtime/abyssalRoomTransferComposition.js`

- `server/src/abyssal/runtime/abyssalRoomTransferContent.js`

- `server/src/abyssal/runtime/abyssalRoomTransferExecution.js`

- `server/src/abyssal/runtime/abyssalRoomTransitionCoordinator.js`

- `server/src/abyssal/runtime/abyssalRunCreation.js`

- `server/src/abyssal/runtime/abyssalRunEvacuation.js`

- `server/src/abyssal/runtime/abyssalRunMaintenance.js`

- `server/src/abyssal/runtime/abyssalRunOutcomeCommitCoordinator.js`

- `server/src/abyssal/runtime/abyssalRunReconstruction.js`

- `server/src/abyssal/runtime/abyssalRunRegistration.js`

- `server/src/abyssal/runtime/abyssalRuntimeDiagnostics.js`

- `server/src/abyssal/runtime/abyssalSceneMode.js`

- `server/src/abyssal/runtime/abyssalSceneryModel.js`

- `server/src/abyssal/runtime/abyssalSessionAudit.js`

- `server/src/abyssal/runtime/abyssalSessionDisconnect.js`

- `server/src/abyssal/runtime/abyssalSessionRecovery.js`

- `server/src/abyssal/runtime/abyssalStaffControls.js`

- `server/src/abyssal/runtime/abyssalStaleCleanup.js`

- `server/src/abyssal/runtime/abyssalStartupReconciliation.js`

- `server/src/abyssal/runtime/abyssalStartupReturn.js`

- `server/src/abyssal/runtime/abyssalStatusReport.js`

- `server/src/abyssal/runtime/abyssalSurvivorControllerHandoffCoordinator.js`

- `server/src/abyssal/runtime/abyssalSuspectEffects.js`

- `server/src/abyssal/runtime/abyssalTerminalArtifactCleanupCoordinator.js`

- `server/src/abyssal/runtime/abyssalTerminalCleanup.js`

- `server/src/abyssal/runtime/abyssalTerminalEvidence.js`

- `server/src/abyssal/runtime/abyssalTimeoutLifecycle.js`

- `server/src/abyssal/runtime/abyssalTimerModels.js`

- `server/src/abyssal/runtime/abyssalTraceActivation.js`

- `server/src/abyssal/runtime/abyssalTraceDeployment.js`

- `server/src/abyssal/runtime/abyssalTraceLifecycle.js`

- `server/src/abyssal/runtime/abyssalTraceRetirementCoordinator.js`

- `server/src/abyssal/runtime/abyssalTraceRollback.js`

- `server/src/abyssal/runtime/abyssalTraceValidation.js`

- `server/src/abyssal/runtime/abyssalTransitionAuthority.js`

- `server/src/abyssal/runtime/abyssalTransitionEffects.js`

- `server/src/abyssal/runtime/abyssalTransitionScheduling.js`

- `server/src/abyssal/runtime/abyssalTransitionSettlement.js`

- `server/src/abyssal/runtime/abyssalVisibilityRestrictions.js`

- `server/src/abyssal/runtime/abyssalVisualDiagnostics.js`

- `server/src/abyssal/runtime/abyssalVisualPolicy.js`

- `server/src/abyssal/runtime/abyssalWeatherModel.js`

- `server/src/abyssal/runtime/content/abyssalEncounterDefinitions.js`

- `server/src/abyssal/runtime/content/abyssalEncounterNpcCatalog.js`

- `server/src/abyssal/runtime/content/abyssalEncounterPolicy.js`

- `server/src/abyssal/runtime/content/abyssalEncounterResolver.js`

- `server/src/abyssal/runtime/content/abyssalFilamentDefinitionResolver.js`

- `server/src/abyssal/runtime/controllers/abyssalSuppressorPresentationLifecycle.js`

- `server/src/abyssal/runtime/controllers/abyssalSuppressorTargetAdapter.js`

- `server/src/abyssal/state/abyssalDiagnosticsState.js`

- `server/src/abyssal/state/abyssalEngineBindingState.js`

- `server/src/abyssal/state/abyssalFilamentCompensationStore.js`

- `server/src/abyssal/state/abyssalHostSlotAllocator.js`

- `server/src/abyssal/state/abyssalMaintenanceState.js`

- `server/src/abyssal/state/abyssalPendingWorkRegistry.js`

- `server/src/abyssal/state/abyssalRecoveryTombstoneStore.js`

- `server/src/abyssal/state/abyssalRunRegistry.js`

- `server/src/abyssal/state/abyssalRunWorkStore.js`

- `server/src/abyssal/state/abyssalTraceStateRegistry.js`

- `server/src/abyssal/transport/abyssalContentManagerService.js`

- `server/src/abyssal/transport/abyssalMgrService.js`

- `server/src/services/activity/abyssalContentManagerService.js`

- `server/src/services/activity/abyssalMgrService.js`

- `server/src/services/activity/activityMgrService.js`

- `server/src/services/activity/communityTrackMgrService.js`

- `server/src/services/activity/encodedItemsService.js`

- `server/src/services/activity/randomJumpMgrService.js`

- `server/src/services/activity/rwManagerService.js`

- `server/src/services/activity/warpVectorMgrService.js`

- `server/src/services/dungeon/deadspaceWarpPolicy.js`

- `server/src/services/dungeon/dungeonAuthorityPayload.js`

- `server/src/services/dungeon/dungeonExplorationMgrService.js`

- `server/src/services/dungeon/dungeonShipRestrictionResolver.js`

- `server/src/services/faction/factionImageStore.js`

- `server/src/services/faction/factionState.js`

- `server/src/services/shipcaster/shipCasterLandingPadMgrService.js`

- `server/src/services/shipcaster/shipCasterLauncherMgrService.js`

- `server/src/services/shipcaster/shipcasterRuntime.js`

- `server/src/services/shipcaster/shipcasterTravelMgrService.js`

- `server/src/services/shipcaster/starterShipcasterTravelMgrService.js`

- `server/src/services/sovereignty/allianceSovereigntyLifecycle.js`

- `server/src/services/sovereignty/colonyResourcesMgrService.js`

- `server/src/services/sovereignty/sovAnchorLayout.js`

- `server/src/services/sovereignty/sovAutoCommands.js`

- `server/src/services/sovereignty/sovAutoNavigation.js`

- `server/src/services/sovereignty/sovGatewayProto.js`

- `server/src/services/sovereignty/sovHubPlacement.js`

- `server/src/services/sovereignty/sovLog.js`

- `server/src/services/sovereignty/sovParityReset.js`

- `server/src/services/sovereignty/sovPayloads.js`

- `server/src/services/sovereignty/sovResetMarker.js`

- `server/src/services/sovereignty/sovStaticAuthority.js`

- `server/src/services/sovereignty/sovStaticData.js`

- `server/src/services/sovereignty/sovStore.js`

- `server/src/services/sovereignty/sovSuppressionState.js`

- `server/src/services/sovereignty/sovSystemRules.js`

- `server/src/space/asteroids/asteroidData.js`

- `server/src/space/asteroids/index.js`

- `server/src/space/authoredSpaceProps.js`

- `server/src/space/combat/laserTurrets.js`

- `server/src/space/combat/missiles/missileReloads.js`

- `server/src/space/combat/missiles/missileSolver.js`

- `server/src/space/combat/precursorTurrets.js`

- `server/src/space/combat/vortonProjectors.js`

- `server/src/space/corpseUtils.js`

- `server/src/space/destiny/batching/packagedAction.js`

- `server/src/space/destiny/bootstrap/initialBallpark.js`

- `server/src/space/destiny/bootstrap/staticBootstrap.js`

- `server/src/space/destiny/commands/docking.js`

- `server/src/space/destiny/commands/mining.js`

- `server/src/space/destiny/commands/npc.js`

- `server/src/space/destiny/commands/npcWarpCompletion.js`

- `server/src/space/destiny/commands/probe.js`

- `server/src/space/destiny/commands/shipDerivedMotion.js`

- `server/src/space/destiny/commands/superweapon.js`

- `server/src/space/destiny/delivery/coordinateFrame.js`

- `server/src/space/destiny/delivery/sessionOrigin.js`

- `server/src/space/destiny/identity/interactionScope.js`

- `server/src/space/destiny/native/README.md`

- `server/src/space/destiny/presentation/siteEnvironment.js`

- `server/src/space/destiny/protocol/direction.js`

- `server/src/space/destiny/protocol/payloadSummary.js`

- `server/src/space/destiny/protocol/payloads.js`

- `server/src/space/destiny/simulation/carbonIntegration.js`

- `server/src/space/destiny/simulation/warp.js`

- `server/src/space/destiny/simulation/warpContract.js`

- `server/src/space/destiny/simulation/warpInPointParity.js`

- `server/src/space/destiny/stream/ballConfig.js`

- `server/src/space/destiny/stream/freeBallHeader.js`

- `server/src/space/destiny/stream/primitives.js`

- `server/src/space/destiny/stream/staticBallTail.js`

- `server/src/space/empireGatePresence/empireSentryLayout.js`

- `server/src/space/empireGatePresence/everMoreGatePresence.js`

- `server/src/space/empireGatePresence/gateSceneryLayout.js`

- `server/src/space/empireGatePresence/stationSceneryLayout.js`

- `server/src/space/entityConstants.js`

- `server/src/space/lcoTransforms.js`

- `server/src/space/modules/genericModuleFuelRuntime.js`

- `server/src/space/modules/localCycleReloads.js`

- `server/src/space/modules/localCycleRuntime.js`

- `server/src/space/modules/moduleAttributeDiff.js`

- `server/src/space/modules/moduleClientParityAuthority.js`

- `server/src/space/modules/moduleLoadParity.js`

- `server/src/space/modules/pointDefenseRuntime.js`

- `server/src/space/modules/projectedWeaponModifiers.js`

- `server/src/space/modules/reactiveArmorHardenerRuntime.js`

- `server/src/space/modules/scannerModuleRuntime.js`

- `server/src/space/modules/sharedFuelRuntime.js`

- `server/src/space/modules/spaceAttachHydration.js`

- `server/src/space/modules/superweapons/disruptiveLanceDebuff.js`

- `server/src/space/modules/targetingModuleRuntime.js`

- `server/src/space/npc/abyssal/abyssalNpcCatalog.js`

- `server/src/space/npc/capitals/capitalNpcAuthority.js`

- `server/src/space/npc/capitals/capitalNpcBehavior.js`

- `server/src/space/npc/capitals/capitalNpcCatalog.js`

- `server/src/space/npc/capitals/capitalNpcDoctrine.js`

- `server/src/space/npc/capitals/capitalNpcEngagement.js`

- `server/src/space/npc/capitals/capitalNpcRuntimeConfig.js`

- `server/src/space/npc/capitals/capitalNpcState.js`

- `server/src/space/npc/capitals/capitalNpcTargeting.js`

- `server/src/space/npc/index.js`

- `server/src/space/npc/legacySyntheticNpcCleanup.js`

- `server/src/space/npc/miningNpcCatalog.js`

- `server/src/space/npc/npcAnchors.js`

- `server/src/space/npc/npcCapabilityResolver.js`

- `server/src/space/npc/npcChaseSpeedModifiers.js`

- `server/src/space/npc/npcCombatDebug.js`

- `server/src/space/npc/npcControlState.js`

- `server/src/space/npc/npcEntityEwarCatalog.js`

- `server/src/space/npc/npcHardwareCatalog.js`

- `server/src/space/npc/npcHostileUtilityCatalog.js`

- `server/src/space/npc/npcHullDamageability.js`

- `server/src/space/npc/npcPresentation.js`

- `server/src/space/npc/npcRuntimeCapabilityAudit.js`

- `server/src/space/npc/npcRuntimeCapabilityRegistry.js`

- `server/src/space/npc/npcSdeCapabilityResolver.js`

- `server/src/space/npc/npcSelection.js`

- `server/src/space/npc/npcWarpOrigins.js`

- `server/src/space/npc/pirateFactionPolicy.js`

- `server/src/space/npc/pirateHullProfiles.js`

- `server/src/space/npc/trigDrifter/trigDrifterNpcCatalog.js`

- `server/src/space/npc/trigDrifter/trigDrifterSpawnAuthority.js`

- `server/src/space/runtime/abyssalRunReporting.js`

- `server/src/space/runtime/abyssalServicePort.js`

- `server/src/space/runtime/dogmaAttributeIds.js`

- `server/src/space/runtime/droneAssistOffensiveNotice.js`

- `server/src/space/runtime/entityCapacitor.js`

- `server/src/space/runtime/entityDogmaView.js`

- `server/src/space/runtime/entityOwnerSession.js`

- `server/src/space/runtime/entosisCapture.js`

- `server/src/space/runtime/groupedWeaponBank.js`

- `server/src/space/runtime/industrialCore.js`

- `server/src/space/runtime/inventoryCustomInfo.js`

- `server/src/space/runtime/marshalWireValues.js`

- `server/src/space/runtime/minHeap.js`

- `server/src/space/runtime/moduleActivationEffectRecord.js`

- `server/src/space/runtime/moduleCycleBoundarySnapshot.js`

- `server/src/space/runtime/moduleCycleSchedule.js`

- `server/src/space/runtime/runtimeClock.js`

- `server/src/space/runtime/runtimeEntityIdentity.js`

- `server/src/space/runtime/runtimeModuleBoundary.js`

- `server/src/space/runtime/scene/commandBurstDbuff.js`

- `server/src/space/runtime/scene/stargateActivation.js`

- `server/src/space/runtime/sceneIdentity.js`

- `server/src/space/runtime/sessionIdentity.js`

- `server/src/space/runtime/shipModuleFuel.js`

- `server/src/space/runtime/shipMotionKinematics.js`

- `server/src/space/runtime/solarSystemSceneLifecycle.js`

- `server/src/space/runtime/spaceRuntime/abyssalTrace.js`

- `server/src/space/runtime/spaceRuntime/clock.js`

- `server/src/space/runtime/spaceRuntime/modules.js`

- `server/src/space/runtime/spaceRuntime/scenePreload.js`

- `server/src/space/runtime/spaceRuntime/shipState.js`

- `server/src/space/runtime/spaceRuntime/stargateActivation.js`

- `server/src/space/runtime/spaceRuntime/structureSync.js`

- `server/src/space/runtime/spaceRuntime/targeting.js`

- `server/src/space/runtime/spaceRuntime/timedInvulnerability.js`

- `server/src/space/runtime/starbaseDefenseTick.js`

- `server/src/space/runtime/stargateJumpRange.js`

- `server/src/space/runtime/startupPreloadPlan.js`

- `server/src/space/runtime/state.js`

- `server/src/space/runtime/systemSecurityBand.js`

- `server/src/space/runtime/typeAttributeLookup.js`

- `server/src/space/runtime/universeSiteAttach.js`

- `server/src/space/runtime/vortonArcChain.js`

- `server/src/space/stargateOrientation.js`

- `server/src/space/stargateRadius.js`

- `server/src/space/stargateVisualOverrides.js`

- `server/src/space/stationRadius.js`

- `server/src/space/tickProfiler.js`

- `server/src/space/worldData.js`


### Industry and market

- `externalservices/market-server/config/market-server.benchmark.toml`

- `externalservices/market-server/config/market-server.example.toml`

- `externalservices/market-server/config/market-server.local.toml`

- `externalservices/market-server/src/config.rs`

- `server/src/services/industry/facilityManagerService.js`

- `server/src/services/industry/industryChatCommands.js`

- `server/src/services/industry/industryFacilityState.js`

- `server/src/services/industry/industryInstallSettlementState.js`

- `server/src/services/industry/industryInstalledLocationMigration.js`

- `server/src/services/industry/industryJobSpeed.js`

- `server/src/services/industry/industryManagerService.js`

- `server/src/services/industry/industryPayloads.js`

- `server/src/services/industry/industryRestrictions.js`

- `server/src/services/industry/industryStaticData.js`

- `server/src/services/industry/industrySystemCostIndex.js`

- `server/src/services/industry/starbaseIndustryRuntime.js`

- `server/src/services/market/marketDaemonClient.js`

- `server/src/services/market/marketRuntimeState.js`

- `server/src/services/market/marketService.js`

- `server/src/services/market/marketState.js`


### Characters, agents and events

- `server/src/services/agent/loadedMissionRuntime.js`

- `server/src/services/agent/missionRewardSettlementJobs.js`

- `server/src/services/agent/researchAuthority.js`

- `server/src/services/agent/researchDialogue.js`

- `server/src/services/agent/researchRuntimeState.js`

- `server/src/services/agent/storylineAgentSelector.js`

- `server/src/services/campaign/loginRewardFacilitiesService.js`

- `server/src/services/character/achievementTrackerMgrService.js`

- `server/src/services/character/characterCreationData.js`

- `server/src/services/character/characterIdentity.js`

- `server/src/services/character/characterNameRuntime.js`

- `server/src/services/character/characterNoteState.js`

- `server/src/services/character/expertSystemMgrService.js`

- `server/src/services/character/factionKillStandingRuntime.js`

- `server/src/services/character/homeStationRuntime.js`

- `server/src/services/character/milestoneMgrService.js`

- `server/src/services/character/monolithTaskTrackerMgrService.js`

- `server/src/services/character/photoUploadService.js`

- `server/src/services/character/portraitImageStore.js`

- `server/src/services/character/standingMgrService.js`

- `server/src/services/npe/tutorialEntrySpawnRuntime.js`

- `server/src/services/npe/tutorialHandoffRuntime.js`

- `server/src/services/npe/tutorialState.js`


### Wormholes and exploration

- `server/src/services/exploration/explorationAuthority.js`

- `server/src/services/exploration/probes/probeRuntimeState.js`

- `server/src/services/exploration/probes/probeScanRuntime.js`

- `server/src/services/exploration/probes/probeScanWarpAuthority.js`

- `server/src/services/exploration/probes/surveyProbeRuntime.js`

- `server/src/services/exploration/signatures/providers/miningAnomalyProvider.js`

- `server/src/services/exploration/signatures/providers/sceneProviderRuntime.js`

- `server/src/services/exploration/signatures/providers/sceneSignatureSiteProvider.js`

- `server/src/services/exploration/signatures/providers/sceneStaticSiteProvider.js`

- `server/src/services/exploration/signatures/providers/structureOverlayProvider.js`

- `server/src/services/exploration/signatures/providers/wormholeSignatureProvider.js`

- `server/src/services/exploration/signatures/signaturePlacement.js`

- `server/src/services/exploration/signatures/targetIdRuntime.js`

- `server/src/services/exploration/wormholes/wormholeEnvironmentRuntime.js`

- `server/src/services/exploration/wormholes/wormholePresentation.js`

- `server/src/services/exploration/wormholes/wormholeRuntimeMigration.js`

- `server/src/services/exploration/wormholes/wormholeSpaceAuthority.js`

- `server/src/services/tale/observedTaleData.js`


### GM, chat and nodegraph

- `server/src/services/chat/capitalNpc/capitalNpcCommandFormatter.js`

- `server/src/services/chat/capitalNpc/capitalNpcCommandHandlers.js`

- `server/src/services/chat/capitalNpc/capitalNpcCommandParser.js`

- `server/src/services/chat/capitalNpc/capitalNpcCommandResolver.js`

- `server/src/services/chat/capitalNpc/index.js`

- `server/src/services/chat/channelRules.js`

- `server/src/services/chat/chatAuthenticationServiceService.js`

- `server/src/services/chat/chatHub.js`

- `server/src/services/chat/commandSessionEffects.js`

- `server/src/services/chat/commands/characterTargets.js`

- `server/src/services/chat/commands/commandAccess.js`

- `server/src/services/chat/commands/commandArguments.js`

- `server/src/services/chat/commands/commandFormatting.js`

- `server/src/services/chat/commands/commandInventorySync.js`

- `server/src/services/chat/commands/commandItemTypes.js`

- `server/src/services/chat/commands/commandReplies.js`

- `server/src/services/chat/commands/commandSuggestions.js`

- `server/src/services/chat/commands/crimewatch.js`

- `server/src/services/chat/commands/fittingPresets.js`

- `server/src/services/chat/commands/npcCommands.js`

- `server/src/services/chat/commands/skills.js`

- `server/src/services/chat/commands/spaceVectors.js`

- `server/src/services/chat/localChannelMove.js`

- `server/src/services/chat/lscService.js`

- `server/src/services/chat/shipTypeRegistry.js`

- `server/src/services/chat/solarSystemRegistry.js`

- `server/src/services/chat/specialFxRegistry.js`

- `server/src/services/chat/trigDrifter/index.js`

- `server/src/services/chat/wormhole/index.js`

- `server/src/services/chat/wormhole/wormholeCommandHandlers.js`

- `server/src/services/chat/xmppConfig.js`


### Content, config, static tables

- `config/pos-legacy-production.json`

- `server/src/config/dataRootMigration.js`

- `server/src/gameStore/README.md`

- `server/src/gameStore/data/authoredSpaceProps/Amarr.json`

- `server/src/gameStore/data/authoredSpaceProps/Bei.json`

- `server/src/gameStore/data/authoredSpaceProps/Colelie.json`

- `server/src/gameStore/data/authoredSpaceProps/Hati.json`

- `server/src/gameStore/data/authoredSpaceProps/Irgrus.json`

- `server/src/gameStore/data/authoredSpaceProps/Jark.json`

- `server/src/gameStore/data/authoredSpaceProps/Jita.json`

- `server/src/gameStore/data/authoredSpaceProps/Manifest.json`

- `server/src/gameStore/data/authoredSpaceProps/Mehatoor.json`

- `server/src/gameStore/data/authoredSpaceProps/Odatrik.json`

- `server/src/gameStore/data/authoredSpaceProps/Ohide.json`

- `server/src/gameStore/data/authoredSpaceProps/Pakhshi.json`

- `server/src/gameStore/data/authoredSpaceProps/Perimeter.json`

- `server/src/gameStore/data/authoredSpaceProps/Synchelle.json`

- `server/src/gameStore/data/authoredSpaceProps/Yuhelia.json`

- `server/src/gameStore/data/authoredSpaceProps/Yulai.json`

- `server/src/gameStore/data/fighterAbilities/authority.json`

- `server/src/gameStore/exportPlayers.js`

- `server/src/gameStore/importPlayers.js`

- `server/src/gameStore/migrateJsonToSqlite.js`

- `server/src/gameStore/migrateLegacyNewDatabase.js`

- `server/src/gameStore/ownerProcessShutdown.js`

- `server/src/gameStore/persistenceWorker.js`

- `server/src/gameStore/persistenceWorker.worker.js`

- `server/src/gameStore/playerTransferShared.js`

- `server/src/gameStore/storeRoot.js`

- `tools/DatabaseCreator/CreateDatabase.bat`

- `tools/DatabaseCreator/EnsureSde.ps1`

- `tools/DatabaseCreator/ResetLocalDatabases.bat`

- `tools/DatabaseCreator/ResetLocalDatabases.ps1`

- `tools/DatabaseCreator/build-research-field-authority.js`

- `tools/DatabaseCreator/production-mission-policy.js`

- `tools/DatabaseCreator/staticTables/asteroidTypesBySolarSystemID/data.json`

- `tools/DatabaseCreator/staticTables/capitalNpcAuthority/data.json`

- `tools/DatabaseCreator/staticTables/clientEntityStandings/data.json`

- `tools/DatabaseCreator/staticTables/expertSystems/data.json`

- `tools/DatabaseCreator/staticTables/fighterAbilities/authority.json`

- `tools/DatabaseCreator/staticTables/fighterAbilities/data.json`

- `tools/DatabaseCreator/staticTables/newEdenStore/data.json`

- `tools/DatabaseCreator/staticTables/npcHostileUtilities/data.json`

- `tools/DatabaseCreator/staticTables/npcLoadouts/data.json`

- `tools/DatabaseCreator/staticTables/npcStartupRules/data.json`

- `tools/DatabaseCreator/staticTables/reprocessingClientRandomizedMaterials/data.json`

- `tools/DatabaseCreator/staticTables/researchFieldAuthority/data.json`

- `tools/DatabaseCreator/staticTables/shipInsurancePrices/data.json`

- `tools/DatabaseCreator/staticTables/skillTrainingAlphaCaps/data.json`

- `tools/DatabaseCreator/staticTables/stargateVisualOverrides/data.json`

- `tools/DatabaseCreator/staticTables/stationDockingPlacements/data.json`

- `tools/DatabaseCreator/staticTables/stationGraphicLocators/data.json`

- `tools/DatabaseCreator/staticTables/stationStandingsRestrictions/data.json`

- `tools/DatabaseCreator/staticTables/structureGraphicLocators/data.json`

- `tools/DatabaseCreator/staticTables/trigDrifterSpawnAuthority/data.json`


### Other server services

- `server/src/services/_other/alphaInjectorMgrService.js`

- `server/src/services/_other/nonDiminishingInjectionMgrService.js`

- `server/src/services/_shared/abyssalDeployablePolicy.js`

- `server/src/services/_shared/abyssalNpcOutcomePolicy.js`

- `server/src/services/_shared/abyssalPlayerLaunchScope.js`

- `server/src/services/_shared/atomicFilePublication.js`

- `server/src/services/_shared/auditLog.js`

- `server/src/services/_shared/clientEntityStandings.js`

- `server/src/services/_shared/clientNameUtils.js`

- `server/src/services/_shared/durableSchedulerBinding.js`

- `server/src/services/_shared/jumpTimerRuntime.js`

- `server/src/services/_shared/newCharacterStartOverride.js`

- `server/src/services/_shared/orderedProcessClient.js`

- `server/src/services/_shared/orderedProcessHost.js`

- `server/src/services/_shared/presenceReconciler.js`

- `server/src/services/_shared/serviceCallShapeCapture.js`

- `server/src/services/_shared/sessionIdentity.js`

- `server/src/services/_shared/settlementSequenceWatermark.js`

- `server/src/services/_shared/stationStaticData.js`

- `server/src/services/account/billNotifications.js`

- `server/src/services/account/billingMaintenance.js`

- `server/src/services/account/plexVaultLogState.js`

- `server/src/services/account/storeManagerService.js`

- `server/src/services/account/tutorialSvcService.js`

- `server/src/services/account/walletBalanceLimit.js`

- `server/src/services/achievement/achievementAuthority.js`

- `server/src/services/alert/alertService.js`

- `server/src/services/bookmark/bookmarkChatCommands.js`

- `server/src/services/bookmark/bookmarkConstants.js`

- `server/src/services/bookmark/bookmarkPayloads.js`

- `server/src/services/bookmark/bookmarkTargetResolver.js`

- `server/src/services/bounty/bountyNotifications.js`

- `server/src/services/bounty/killRightMgrService.js`

- `server/src/services/bounty/killRightState.js`

- `server/src/services/bounty/playerBountyState.js`

- `server/src/services/browser/browserLockdownService.js`

- `server/src/services/cache/cacheService.js`

- `server/src/services/cache/objCacheService.js`

- `server/src/services/calendar/calendarChatCommands.js`

- `server/src/services/calendar/calendarConstants.js`

- `server/src/services/calendar/calendarMgrService.js`

- `server/src/services/calendar/calendarNotifications.js`

- `server/src/services/calendar/calendarPayloads.js`

- `server/src/services/calendar/calendarProxyService.js`

- `server/src/services/calendar/calendarRuntimeState.js`

- `server/src/services/calendar/calendarSchedulerRuntime.js`

- `server/src/services/calendar/calendarStore.js`

- `server/src/services/contracts/contractSettlementJobs.js`

- `server/src/services/corporation/aggressionSettingsState.js`

- `server/src/services/corporation/allianceImageStore.js`

- `server/src/services/corporation/allianceRegistryService.js`

- `server/src/services/corporation/allianceViewState.js`

- `server/src/services/corporation/allianceWarInheritance.js`

- `server/src/services/corporation/corpAssetState.js`

- `server/src/services/corporation/corpRegistryService.js`

- `server/src/services/corporation/corporationDividendState.js`

- `server/src/services/corporation/corporationIdRanges.js`

- `server/src/services/corporation/corporationSessionAffiliationSync.js`

- `server/src/services/corporation/lookupSvcService.js`

- `server/src/services/corporation/officeImpoundReleaseState.js`

- `server/src/services/corporation/warCostState.js`

- `server/src/services/corporation/warPermitState.js`

- `server/src/services/dogma/brain/providers/fittingBrainProvider.js`

- `server/src/services/dogma/brain/providers/implantBrainProvider.js`

- `server/src/services/dogma/brainEffectPickle.js`

- `server/src/services/dogma/dogmaService/droneSettings.js`

- `server/src/services/dogma/dogmaService/layerDamageValues.js`

- `server/src/services/dogma/dogmaService/overload.js`

- `server/src/services/dogma/dogmaService/requiredSkillLevels.js`

- `server/src/services/dogma/dogmaService/shared.js`

- `server/src/services/dogma/dogmaService/targeting.js`

- `server/src/services/dogma/implants/implantRuntime.js`

- `server/src/services/dogma/industryBrainRuntime.js`

- `server/src/services/drone/entityService.js`

- `server/src/services/dynamic/dynamicResourceCacheMgrService.js`

- `server/src/services/evermarks/evermarkPurchaseState.js`

- `server/src/services/evermarks/evermarksCatalog.js`

- `server/src/services/evermarks/evermarksConstants.js`

- `server/src/services/evermarks/evermarksEntitlements.js`

- `server/src/services/evermarks/evermarksGatewayProto.js`

- `server/src/services/evermarks/evermarksNotices.js`

- `server/src/services/evermarks/evermarksStore.js`

- `server/src/services/evermarks/lpStoreMgrService.js`

- `server/src/services/fighter/fighterAbilities.js`

- `server/src/services/fighter/npc/npcFighterSessionAdapter.js`

- `server/src/services/fighter/npc/npcSupercarrierTubeState.js`

- `server/src/services/fitting/fittingIntegrity.js`

- `server/src/services/fitting/fittingState.js`

- `server/src/services/fleets/fleetConstants.js`

- `server/src/services/fleets/fleetHelpers.js`

- `server/src/services/fleets/fleetMgrService.js`

- `server/src/services/fleets/fleetProxyService.js`

- `server/src/services/fleets/fleetSetupRuntime.js`

- `server/src/services/fleets/fleetWarpFormation.js`

- `server/src/services/insurance/insuranceNotifications.js`

- `server/src/services/insurance/insurancePriceAuthority.js`

- `server/src/services/insurance/insuranceService.js`

- `server/src/services/inventory/cargoLootAuthority.js`

- `server/src/services/inventory/corporationOfficeInventoryObservers.js`

- `server/src/services/inventory/fleetHangarInventory.js`

- `server/src/services/inventory/fuelBayInventory.js`

- `server/src/services/inventory/invBroker/corporationDelivery.js`

- `server/src/services/inventory/invBroker/corporationHangarTakeAccess.js`

- `server/src/services/inventory/invBroker/ownerTransferIdempotencyKey.js`

- `server/src/services/inventory/invBroker/packaging.js`

- `server/src/services/inventory/invBroker/secureContainers.js`

- `server/src/services/inventory/invBroker/trash.js`

- `server/src/services/inventory/inventoryAssetHoldingMgrService.js`

- `server/src/services/inventory/researchDatacoreItemCustody.js`

- `server/src/services/inventory/salvageItemCustody.js`

- `server/src/services/inventory/shipMaintenanceBayInventory.js`

- `server/src/services/inventory/specialShipHoldRegistry.js`

- `server/src/services/inventory/typeListAuthority.js`

- `server/src/services/inventory/wreckRadius.js`

- `server/src/services/killmail/killmailItemPayload.js`

- `server/src/services/killmail/killmailNotificationFanout.js`

- `server/src/services/logging/clientStatLoggerService.js`

- `server/src/services/logging/eventLogService.js`

- `server/src/services/login/accountStore.js`

- `server/src/services/login/authenticationService.js`

- `server/src/services/login/multiLoginBlockerService.js`

- `server/src/services/loyalty/lpStoreOfferCatalog.js`

- `server/src/services/loyalty/lpStoreStaticOffers.json`

- `server/src/services/machoNet/externalQueueMgrService.js`

- `server/src/services/machoNet/globalConfig.js`

- `server/src/services/machoNet/sessionMgrService.js`

- `server/src/services/machoNet/tcpRawProxyService.js`

- `server/src/services/map/devIndexManagerService.js`

- `server/src/services/mining/characterMiningLedgerService.js`

- `server/src/services/mining/iceSystemAuthority.js`

- `server/src/services/mining/iceSystemSyntheticNullsecAuthority.json`

- `server/src/services/mining/inSpaceCompressionMgrService.js`

- `server/src/services/mining/miningCommandService.js`

- `server/src/services/mining/miningConstants.js`

- `server/src/services/mining/miningDogma.js`

- `server/src/services/mining/miningIndustry.js`

- `server/src/services/mining/miningInventory.js`

- `server/src/services/mining/miningMath.js`

- `server/src/services/mining/miningNpcStandings.js`

- `server/src/services/mining/miningScanMgrService.js`

- `server/src/services/mining/miningStartupSummary.js`

- `server/src/services/mining/miningStaticData.js`

- `server/src/services/mining/miningVisuals.js`

- `server/src/services/mining/structureCompressionMgrService.js`

- `server/src/services/moduleGrouping/moduleGroupingBootstrap.js`

- `server/src/services/moduleGrouping/moduleGroupingRuntime.js`

- `server/src/services/newEdenStore/FastCheckoutService.js`

- `server/src/services/newEdenStore/contextualOfferMgrService.js`

- `server/src/services/newEdenStore/kiringMgrService.js`

- `server/src/services/newEdenStore/storeFulfillment.js`

- `server/src/services/newEdenStore/storeGatewayProto.js`

- `server/src/services/newEdenStore/storeMarshal.js`

- `server/src/services/online/characterCommandRuntime.js`

- `server/src/services/online/characterControlRuntime.js`

- `server/src/services/online/characterEventRuntime.js`

- `server/src/services/online/onlineStatusService.js`

- `server/src/services/overview/overviewMotdBootstrap.js`

- `server/src/services/overview/overviewPresetMgrService.js`

- `server/src/services/ping/pingService.js`

- `server/src/services/planet/planetCostCalculator.js`

- `server/src/services/planet/posMgrService.js`

- `server/src/services/populationCap/populationCapService.js`

- `server/src/services/raffles/raffleAvailability.js`

- `server/src/services/raffles/raffleConstants.js`

- `server/src/services/raffles/raffleHelpers.js`

- `server/src/services/raffles/raffleMarshal.js`

- `server/src/services/raffles/raffleMgrService.js`

- `server/src/services/raffles/raffleProxyService.js`

- `server/src/services/raffles/raffleRepository.js`

- `server/src/services/raffles/raffleRuntime.js`

- `server/src/services/raffles/raffleRuntimeSingleton.js`

- `server/src/services/raffles/raffleRuntimeState.js`

- `server/src/services/raffles/raffleSeed.js`

- `server/src/services/raffles/raffleSeedManager.js`

- `server/src/services/raffles/raffleSettlement.js`

- `server/src/services/raffles/raffleState.js`

- `server/src/services/raffles/raffleSubscriptions.js`

- `server/src/services/raffles/raffleValidation.js`

- `server/src/services/reprocessing/index.js`

- `server/src/services/reprocessing/reprocessingChatCommands.js`

- `server/src/services/reprocessing/reprocessingConstants.js`

- `server/src/services/reprocessing/reprocessingFacilityState.js`

- `server/src/services/reprocessing/reprocessingGatewayProto.js`

- `server/src/services/reprocessing/reprocessingNotices.js`

- `server/src/services/reprocessing/reprocessingRuntime.js`

- `server/src/services/scheduler/scheduledJobStates.js`

- `server/src/services/security/jumpTimersService.js`

- `server/src/services/security/systemSecurity.js`

- `server/src/services/settings/sharedSettingsMgrService.js`

- `server/src/services/ship/devCommandShipRuntime.js`

- `server/src/services/ship/ejectService.js`

- `server/src/services/ship/interdictionProbeRuntime.js`

- `server/src/services/ship/shipAppearanceGmCommands.js`

- `server/src/services/ship/shipDirtState.js`

- `server/src/services/ship/shipKillCounterService.js`

- `server/src/services/ship/shipLogoFittingState.js`

- `server/src/services/ship/shipLogoGatewayProto.js`

- `server/src/services/ship/shipMaintenanceBayRules.js`

- `server/src/services/ship/shipServiceAccess.js`

- `server/src/services/ship/shipStanceMgrService.js`

- `server/src/services/ship/starterShipFittingState.js`

- `server/src/services/ship/superWeaponMgrService.js`

- `server/src/services/ship/warpDisruptFieldGeneratorRuntime.js`

- `server/src/services/skills/boosters/boosterSkillRuntime.js`

- `server/src/services/skills/certificates/skillChangeTracker.js`

- `server/src/services/skills/expertSystems/expertSystemCatalog.js`

- `server/src/services/skills/expertSystems/expertSystemProjection.js`

- `server/src/services/skills/expertSystems/expertSystemSerializer.js`

- `server/src/services/skills/plans/corpSkillPlanState.js`

- `server/src/services/skills/publicGatewayService.js`

- `server/src/services/skills/skillAdminRuntime.js`

- `server/src/services/skills/skillHandlerService.js`

- `server/src/services/skills/skillTransport.js`

- `server/src/services/skills/skillbooks/skillbookRuntime.js`

- `server/src/services/skills/trading/skillTradingAuthority.js`

- `server/src/services/skills/trading/skillTradingNotifications.js`

- `server/src/services/skills/trading/skillTradingState.js`

- `server/src/services/skills/training/skillCloneRestrictions.js`

- `server/src/services/skills/training/skillQueueState.js`

- `server/src/services/skills/training/skillTrainingSpeed.js`

- `server/src/services/starbase/posChatCommands.js`

- `server/src/services/starbase/posGmState.js`

- `server/src/services/starbase/starbaseAccessRuntime.js`

- `server/src/services/starbase/starbaseAnchoringPolicy.js`

- `server/src/services/starbase/starbaseAnchoringRestrictions.json`

- `server/src/services/starbase/starbaseCombatRuntime.js`

- `server/src/services/starbase/starbaseDefenseRuntime.js`

- `server/src/services/starbase/starbaseFuelRuntime.js`

- `server/src/services/starbase/starbaseItemIndex.js`

- `server/src/services/starbase/starbaseProcessingInventory.js`

- `server/src/services/starbase/starbaseProductionAuthority.js`

- `server/src/services/starbase/starbaseProductionCycles.js`

- `server/src/services/starbase/starbaseProductionRuntime.js`

- `server/src/services/starbase/starbaseRepairRuntime.js`

- `server/src/services/starbase/starbaseResistanceRuntime.js`

- `server/src/services/starbase/starbaseResourceRuntime.js`

- `server/src/services/starbase/starbaseStorageKinds.js`

- `server/src/services/starbase/starbaseTravelRuntime.js`

- `server/src/services/station/jumpCloneRules.js`

- `server/src/services/station/repackagingService.js`

- `server/src/services/station/repackagingSupport.js`

- `server/src/services/station/repairService.js`

- `server/src/services/station/reprocessingService.js`

- `server/src/services/station/stationLocatorGeometry.js`

- `server/src/services/structure/loadedStructureState.js`

- `server/src/services/structure/metenoxBuiltIns.js`

- `server/src/services/structure/metenoxHarvest.js`

- `server/src/services/structure/moonCompositionPackGenerator.js`

- `server/src/services/structure/moonCompositionRuntime.js`

- `server/src/services/structure/moonExtractionRules.js`

- `server/src/services/structure/moonMiningBindingRuntime.js`

- `server/src/services/structure/moonMiningEligibility.js`

- `server/src/services/structure/moonResourceAuthority.js`

- `server/src/services/structure/moonResourceShadowRead.js`

- `server/src/services/structure/moonResourceSqliteAdapter.js`

- `server/src/services/structure/moonStructureBinding.js`

- `server/src/services/structure/structureAutoCommands.js`

- `server/src/services/structure/structureAutoMoonFuelRuntime.js`

- `server/src/services/structure/structureAutoState.js`

- `server/src/services/structure/structureControlState.js`

- `server/src/services/structure/structureDeathTestRuntime.js`

- `server/src/services/structure/structureDeliveryRuntimeState.js`

- `server/src/services/structure/structureDeploymentPlacement.js`

- `server/src/services/structure/structureDeploymentService.js`

- `server/src/services/structure/structureFullPowerDogma.js`

- `server/src/services/structure/structureGuestsService.js`

- `server/src/services/structure/structureHangarViewManagerService.js`

- `server/src/services/structure/structureHangarViewState.js`

- `server/src/services/structure/structureIndustryJobs.js`

- `server/src/services/structure/structureLocation.js`

- `server/src/services/structure/structureLog.js`

- `server/src/services/structure/structureServiceOnlineRequirements.js`

- `server/src/services/structure/structureServiceProximity.js`

- `server/src/services/structure/structureSpaceInterop.js`

- `server/src/services/structure/structureTetherRestrictionState.js`

- `server/src/services/structure/structureVulnerabilityService.js`

- `server/src/services/superweapons/npc/npcTitanSuperweaponRuntime.js`

- `server/src/services/superweapons/superweaponCatalog.js`

- `server/src/services/superweapons/superweaponCommands.js`

- `server/src/services/support/bannedwordsService.js`

- `server/src/services/support/eveguardReportService.js`

- `server/src/services/support/infoGatheringMgrService.js`

- `server/src/services/support/ingamereportService.js`

- `server/src/services/support/loadServiceService.js`

- `server/src/services/support/localizationServerService.js`

- `server/src/services/support/petitionerService.js`

- `server/src/services/support/serviceGatewayMgrService.js`

- `server/src/services/support/surveyService.js`

- `server/src/services/tournament/tourneyMgrService.js`

- `server/src/services/trade/tradeRuntimeState.js`


### Runtime, network and packaging

- `.env.example`

- `ContentPacks.bat`

- `LICENSE`

- `PlayDebug.bat`

- `SetupEveJS.bat`

- `StartServer.bat`

- `UpdateEveJS.bat`

- `compose.yaml`

- `docker/market-seed-v3.toml`

- `docker/market-server-candidate.toml`

- `docker/market-server.toml`

- `server/proxy-only.js`

- `server/src/RemoteRepShow/remoteRepairShowRuntime.js`

- `server/src/_secondary/chat/server.js`

- `server/src/_secondary/chat/staticChannelContracts.js`

- `server/src/_secondary/express/companionDiag.js`

- `server/src/_secondary/express/evejsWebGateway.js`

- `server/src/_secondary/express/gatewayServices/corpColorsGatewayService.js`

- `server/src/_secondary/express/gatewayServices/corpSkillPlanProto.js`

- `server/src/_secondary/express/gatewayServices/fwAdvantageGatewayService.js`

- `server/src/_secondary/express/gatewayServices/gatewayServiceHelpers.js`

- `server/src/_secondary/express/gatewayServices/mapTagsGatewayService.js`

- `server/src/_secondary/express/gatewayServices/newEdenStoreGatewayService.js`

- `server/src/_secondary/express/gatewayServices/plexVaultGatewayService.js`

- `server/src/_secondary/express/gatewayServices/skillPlanGatewayService.js`

- `server/src/_secondary/express/gatewayServices/skillPlanProto.js`

- `server/src/_secondary/express/gatewayServices/structurePaintworkGatewayService.js`

- `server/src/_secondary/express/localTlsCertificate.js`

- `server/src/_secondary/express/playerConnectEndpoints.js`

- `server/src/_secondary/express/publicGatewayNotices.js`

- `server/src/_secondary/express/sessionEventStream.js`

- `server/src/_secondary/fitting/assumedActiveFittingEffects.js`

- `server/src/_secondary/fitting/fittingBrainBuilder.js`

- `server/src/_secondary/fitting/fittingSnapshotBuilder.js`

- `server/src/_secondary/image/generated/Alliance/.gitkeep`

- `server/src/_secondary/image/generated/agents/manifest.json`

- `server/src/_secondary/launcher/server.js`

- `server/src/_secondary/redshift/server.js`

- `server/src/common/machoAddress.js`

- `server/src/common/machoErrors.js`

- `server/src/common/marshalStringTable.js`

- `server/src/common/numbers.js`

- `server/src/common/packetTypes.js`

- `server/src/common/pyPacket.js`

- `server/src/common/pyTypes.js`

- `server/src/common/vector.js`

- `server/src/edge/chat/chatEdgeRuntime.js`

- `server/src/edge/chat/xmppTlsCredentials.js`

- `server/src/edge/edgeProcessClient.js`

- `server/src/edge/edgeProcessHost.js`

- `server/src/edge/gateway/gatewayAuthorityBridge.js`

- `server/src/edge/gateway/gatewayEdgeProcess.worker.js`

- `server/src/edge/gateway/remoteGatewayGrpcTransport.js`

- `server/src/network/callResponseControl.js`

- `server/src/network/marshalEncodingTasks.js`

- `server/src/network/nativeTimeDilation.js`

- `server/src/network/tcp/socketTuning.js`

- `server/src/network/tcp/utils/crypto.js`

- `server/src/network/tcp/utils/encode/index.js`

- `server/src/utils/gameServerHealth.js`

- `server/src/utils/processLifecycle.js`

- `server/src/utils/startupDataSummary.js`

- `server/src/utils/synchronizedTimeDilation.js`

- `server/src/utils/tidiAutoscaler.js`


### Mods and tools

- `tools/ClientCodeGrabber/StartCodeGrabber.bat`

- `tools/ClientSETUP/PatchBlueDllGui.bat`

- `tools/ClientSETUP/StartClientSetup.bat`

- `tools/ClientSETUP/blue_dll_patch.ps1`

- `tools/ClientSETUP/crash_upload_patch_recipe.json`

- `tools/ClientSETUP/launchdarkly_localhost_upgrade_recipe.json`

- `tools/ClientSETUP/launchdarkly_patch_recipe.json`

- `tools/ClientSETUP/legacy-python/PatchBlueDllGui.bat`

- `tools/ClientSETUP/scripts/Install-EvEJSCerts.ps1`

- `tools/ClientSETUP/scripts/InstallCerts.bat`

- `tools/ClientSETUP/scripts/Prepare-EvEJSOfflinePreferences.ps1`

- `tools/ClientSETUP/scripts/Resolve-EvEJSEndpoints.ps1`

- `tools/ClientSETUP/scripts/Set-EvEJSClientNetworkPatches.ps1`

- `tools/ClientSETUP/scripts/build-gateway-cert.js`

- `tools/ConfigEditor/OpenServerConfig.bat`

- `tools/ConfigEditor/OpenServerConfigV2.ps1`

- `tools/ConfigEditor/config-editor-prefs.json`

- `tools/ConfigEditor/config-manager-cli.js`

- `tools/ConfigEditor/extract-client-icons.js`

- `tools/ConfigEditor/extract-ui-icons.js`

- `tools/ErrorReport/BugReportService.bat`

- `tools/ErrorReport/ErrorReport.bat`

- `tools/ErrorReport/bug-report-service.js`

- `tools/ErrorReport/error-report.ps1`

- `tools/InstallRustForMarket.bat`

- `tools/Maintenance/reset-sovereignty.js`

- `tools/Maintenance/vacuum-gamestore.js`

- `tools/NewEdenStoreEditor/StartStoreEditor.bat`

- `tools/NewEdenStoreEditor/build_seed_authority.js`

- `tools/NewEdenStoreEditor/build_store_icon_cache.js`

- `tools/NewEdenStoreEditor/editor.js`

- `tools/NewEdenStoreEditor/editor.py`

- `tools/NewEdenStoreEditor/public/app.js`

- `tools/NewEdenStoreEditor/public/index.html`

- `tools/NewEdenStoreEditor/public/styles.css`

- `tools/Updater/Update-EvEJS.ps1`

- `tools/Updater/update-policy.json`

- `tools/market-seed/BuildMarketSeedGui.bat`

- `tools/market-seed/config/market-seed.benchmark.toml`

- `tools/market-seed/config/market-seed.example.toml`

- `tools/market-seed/config/market-seed.local.toml`

- `tools/market-seed/src/config.rs`

- `tools/market-seederv2/config/market-seederv2.local.toml`

- `tools/market-seederv3/BuildMarketSeedV3.bat`

- `tools/market-seederv3/config/market-seederv3.example.toml`

- `tools/market-seederv3/config/market-seederv3.local.toml`

- `tools/market-seederv3/src/audit.rs`

- `tools/market-seederv3/src/build.rs`

- `tools/market-seederv3/src/classify.rs`

- `tools/market-seederv3/src/config.rs`

- `tools/market-seederv3/src/cost.rs`

- `tools/market-seederv3/src/manifest.rs`

- `tools/market-seederv3/src/seedplan.rs`

- `tools/moon-data/generate-moon-composition-pack.js`


### Tests and fixtures

- `server/src/services/_shared/fixtureIdentities.js`


### Documentation

- `tools/ContentPacks/README.md`


</details>
