# Current TODOs

## Project status

Built from the sections below, as of 2026-10-19. **Status** is what the area does today. **Project rule** is the rule it is built under (the client, the SDE, CCP or the EVE University wiki, or an owner decision). **Cut or open** lists what is not built; the section named holds the evidence. Every cut or open item in this file has a row in the table or a line in its section, and a cut that changes what the client sees also has an entry in `docs/reference/compat/DEVIATIONS.md`.

| Area (sections) | Status | Project rule | Cut or open |
|---|---|---|---|
| Ruleset checkpoint and research reconciliation | PARTLY DONE | Q-16 adopts `24.01@2026-06-16` per item for client build 3396210; official 24.01 notes confirm the post-checkpoint dates listed in RULESET_BOUNDARY.md. | Post-checkpoint rules are excluded, and FW Battlefield LP is 50,000. Pre-checkpoint SDE changes and report claims remain open; see RULESET_BOUNDARY.md and RESEARCH_RECONCILIATION.md. |
| Security regression matrix (security agent) | OPEN | In progress: inbound byte and weighted RPC budgets covering inventory, assets, and bookmark work plus 500-ID corporation, bookmark, and 256-member fleet bulk bounds have focused regressions; broad RPC ownership, resource-bound, payload-isolation, and login-handshake review remains. | Zero-ISK donation must not reveal balances; response projection; idempotency keys; CALL_REQ before SessionInit; admin-only service access; nested substream bounds. |
| Performance SLO and benchmark scenarios (perf agent) | OPEN | Proposed SLOs are engineering targets, not CCP limits. | Add handshake storm, soak, movement-heavy, dense grid, weapon burst, market hotspot, jump storm, mission wave, restart, cold boot and malformed protocol scenarios; link from PERFORMANCE.md. |
| Protocol parser state machine and limits (security/protocol agent) | OPEN | Frame, marshal and packet behavior must follow observed V24.01 contract. | Document and cover length/payload/decode/session/auth/dispatch states, oversized inputs, nested substreams, partial/coalesced frames. |
| FW, CRAB and insurgency reconciliation (FW/CRAB agents) | OPEN | Use checkpoint-specific rules and source-backed values. | Battlefield LP is reconciled to 50,000 at build 3396210. CRAB link status, resistance, resource limits and timed reward wreck are covered by Host/simulation tests; the SDE recipe is recorded. Per-type bounties, beacon damage, and attacks on the beacon remain open. FW capture blockers/pods/respawn and insurgency stage effects and unknown thresholds also remain open. |
| Weapon-bank RPC check (weapon-bank agent) | OPEN | Group semantics require client RPC evidence. | Trace grouped activation, reload and offline flow before implementation. |
| Login, character selection and entry ("Native Windows character-entry regression", 40) | Working on the start-up path. The Windows inventory fix is in; its response-shape fixes need the next Host rebuild. | What the client can observe matches EveJS; each difference is in DEVIATIONS with its evidence. | Native Undock acceptance; the recorded RPC and protobuf gaps (the Windows section). |
| Runtime state and notifications (1) | Partly built. Home station and insurgency snapshot are real. `onlineStatus.GetOnlineStatus` reads live character sessions, applies mutual watchlist access for selected observers and raw presence for unselected sessions; its initial rowset uses the configured watchlist provider. The rest of the list is open or future. | A missing producer answers the empty shape the client expects. No invented data, no fake success. | Open: abyssal client-ready, scan signal tracker, control towers, contract escrow, expert systems, raffle bans. Future: SSO login, blacklisted planets. |
| Space cadence, warp and docking (S1) | The sim link holds `DoDestinyUpdate` until the next whole-second boundary and holds later notifications behind it to preserve order; command replies remain immediate. Out-of-range docking completes on arrival; selected warp range has a boundary regression. Focused Rust tests pass. The simulation's 4 Hz poll remains for due work. | Matches EveJS 261018.1 tick alignment and movement transitions. | Graphical-client timing remains open. See `EVEJS_261018_DELTA.md` and `DEVIATIONS.md`. |
| Mutation authorities (2) | Dynamic items, mutated modules and drones, and in-space compression are built and tested headless and on PostgreSQL. | One transaction per mutation with its provenance; a refusal is a named `CustomNotify`, never `None`. | Graphical-client runs of these; abyssal gate activation. Abyssal room-entry and room-transition protection is verified by a focused simulator regression; overall Abyssal acceptance remains open. Structure services (sections 43 to 48). |
| Accounts, dungeons, content data (3) | Partly built. | Data comes from the SDE or the client's own files; no invented rows. | Multi-training by item certificate (refused as NotSupported); dungeon, keeper, RW and Victory Point providers (future). |
| Method-level evidence (4, 37) | Every method has a route. Wire shapes are proven only where the client source establishes them. | A method is unknown until a source establishes its output. | Service table rows in 37c, each opened with its feature. |
| Ships, fitting, modules (7 to 9, 16, 17, 19 to 22, 29 to 32, 36) | T3 rig removal, saved-fit slot replacement, module skill checks, and the V24 hull-skill boarding gate are covered headless; not exercised as a whole on the graphical client. | Numbers from the SDE dogma; rules from CCP or the wiki, cited in DEVIATIONS. | Section 7 effects still refused (`EFFECT_NOT_IMPLEMENTED`); open rows of section 9; Polarized pin and reactive hardener not checked against CCP (8, 8b). |
| Industry jobs (EVEJS delta 5) | Client formula paths cover job cost, time and material rounding, and corporation outputs route to Corp Deliveries when no output is selected. | V24.01 `industry/job.py`, activity modules and client `industryCommon.py`; pinned SDE inputs. | Core formula table: 29 tests pass; corporation persistence/delivery: 8 Host PostgreSQL tests pass. GUI acceptance remains open. See `INDUSTRY_STATUS.md` and DEVIATIONS Industry I1–I5. |
| Drones (6) | Built; flights covered headless. | As above. | Hybrid drones (open); mining and salvage drones on the client. |
| Mining, salvage, tractor, targeting (10) | Built and tested headless. | As above. | Entosis link waits for structures (phase 3); mining scan list (open). |
| P.A.N.I.C., superweapons, fighters, bursts, scanners (11 to 13, 20 to 26) | Command burst activation, fleet delivery, timed state, receiver dogma attributes and attribute notifications are built through C8; dogmaIM snapshots sync the active buff registry. P.A.N.I.C. is built. Superweapons partly (12). Fighters, scanners and probes built (13, 21, 22, 24). | As above. | Section 12 open rows (burst projectors, Area type); section 13 open rows. The live trace located the lost charge after the final round was consumed and before Host settlement; simlink protocol 81 now carries its SDE type into DogmaWorld evaluation. The two-pilot headless walk is selected in the slow workflow and needs a green rerun to confirm receiver notifications, changed dogma and expiry; graphical-client verification remains open (section 22). |
| Windows and production start (14) | TCP defaults, Windows start/stop scripts, PostgreSQL service control, local checks and a headless test runner are present. CI has a path-selected Windows job for Rust, .NET, Python and a login-to-character walk. The golden fixture reader now selects UTF-8 explicitly. | Linux CI remains the ordinary push gate; Windows CI runs for platform-sensitive changes, manual selection, and weekly. See [WINDOWS.md](WINDOWS.md). | Review the Windows rerun after the CP1252 fixture-decoding issue; verify PowerShell 5.1 console shutdown and the pinned .NET SDK selection on the owner's Windows install. |
| Client preparation (`tools/client-patch`) | The patch exists. | Launch arguments are taken from the client. | Real-client check of the patched `start.ini` and launch arguments. |
| Alliance registry (18) | Built. | Affiliation and wars follow the reference. | Open rows of section 18. |
| Contracts (V24 contracts window; `CONTRACTS_DESIGN.md`) | Startup and personal escrow reads use persisted rows. `CreateContract` is wired to the PostgreSQL writer for item exchange, auction, and courier terms; it stages items and issuer wallet charges transactionally, persists requested-item terms, checks station and contract-manager context, and enforces the client skill-table capacity under an issuer advisory lock. | Personal capacity uses the client `NUM_CONTRACTS_BY_SKILL` plus `NUM_CONTRACTS_BY_ADVSKILL` tables; corporation capacity uses the client corporation table. | Open: complete faction/system fee adjustment, corp hangar authorization, auction bidding/settlement, courier acceptance/pickup/completion, cancellation and expiry, notifications, search/window reads, and the two-character headless walk. Item-exchange acceptance transfers requested/offered items and wallet legs atomically, clears active escrow markers, and returns the same result on duplicate calls; its concurrent path is covered. |
| Market (42) | M1 to M9 station flow is built; the live walk passed (M9). NPC station sales tax uses 7.5% base reduced by Accounting. A structure's MARKET_TAX setting is its owner-set broker rate (default 1%), separate from transaction tax; its broker fee also has a 0.5% SCC surcharge. | V24 `sellItemEntry.GetSalesTax` uses `limits['acc']`; `MarketQuote.GetBaseTax` reads the structure service setting used by the broker quote. CCP's fee article separates seller tax and Upwell broker fee. | Market order settlement currently resolves `StationCatalog` locations only; structure broker-rate settlement is open. Client flag preview, label mapping and graphical preview remain open. |
| Security review (SECURITY_STATUS.md) | `structureControl.GetStructurePilot` is scoped to the caller's current system; structure deployment ownership requires an exact nonzero corporation match; `invbroker` customs-office binding and transfers use the in-space visibility/range check; `charMgr.GetPaperdollState` and `GetPrivateInfoOnCorpChange` use only the session character; `GetPrivateInfo` requires same-corporation Director or Personnel Manager authority; The nine public `CorpMgrService` RPCs are classified in `SECURITY_STATUS.md`: public corporation profiles, affiliation, and aggression settings; Director/Auditor-gated member audit; and Accountant/Junior Accountant-gated corporate assets. Ten focused regressions passed: eight PostgreSQL tests on `eve_p2`, one `BlueprintManagerServiceTests`, and one `OnlineStatusServiceTests`; unselected sessions read raw online presence like EveJS; selected observers require mutual watchlist membership; the corporation asset RPC regression now also rejects a real member without an asset-read role and forged session authority. Public QA RPCs, alliance GML, asset-safety GML, skin-grant Staff and skin-expiry GML checks also have focused passing regressions. `mailMgr.PokePlayerAboutChatMsgGm` requires server-set GML; `itemLocking` corporate lock reads require persisted Accountant or Junior Accountant authority (focused same-corporation denial and allowance regression passed on `eve_p2`); other privileged Host RPCs remain to be inventoried. `corporationSvc.CreateMedal` and `GiveMedalToCharacters` now require persisted Director or Personnel Manager authority, require a medal owned by the caller's corporation, and restrict recipients to current corporation members; focused regression covers forged client role fields and denial/allowance paths. Cross-character `GetMedalsReceived` reads expose only public-status awards, with a focused privacy regression. `SetMedalStatus` still updates shared medal status rather than per-recipient status and needs a storage-model correction. `beyonce.AdminGetBubbleID` requires the server-set `GameMasterRoles.Staff`; a regression rejects generic client role fields and permits the authenticated GML session. `standingMgr.GetStandingTransactions` returns history only for the active session character, and `GetStandingCompositions` returns rows only for the caller's persisted corporation; `StandingMgrServiceTests` covers own and foreign identifiers. | Check each client-supplied identifier against authoritative ownership, location access, or corporation role before returning private data or mutating state. | A cx35 review found and fixed a character-ID spoof in station service standing checks (`CorpStationMgrTests.The_standing_check_cannot_use_another_pilots_standing`); this review also scoped standing history and composition reads. `encodedItem.DecodeItem` now refuses owned-item resource URLs when no character is selected, with a focused regression; the broader Host RPC identifier/ownership inventory remains open; `warRegistry.SetOpenForAllies` and `RetractMutualWar` now require persisted CEO/Director authority, including the current alliance executor for alliance wars. GM/QA inventory remains open after source review and a new gate for all seven `node_graph.qa_*` RPCs; `machoNet.SetGlobalConfigValue` now requires ROLE_ADMIN or ROLE_SERVICE in authenticated account roles. Expensive-call limits, malformed payload isolation, and placebo handshake mitigations remain open. |
| Corporation tax (6, DEVIATIONS) | NPC bounty and mission reward ISK tax read the member corporation tax profile and atomically credit the member net and player corporation tax with matching wallet journals; NPC corporation tax is a sink. FW battlefield LP awards already use the shared member/corporation LP tax settlement in the reward transaction. | EveJS 261018.1 `corporationTax.js`, `bountyRuntime.js`, and `missionRewardSettlementState.js`; V24 `transactionsUtil._GetDerivedCorporationTaxTransaction` derives the ISK tax row from the journal description. | Mission LP payout is still open: the current mission reward path does not credit LP. Focused FW tax integration verification remains open. |
| Agents (AG-1 to AG-5, DEVIATIONS) | Partly built: LP wallet, access rule, bound agentMgr and dialogues done; live dialogue walks run in AG-5. | Rules from ESI and the EVE University wiki, cited per slice in DEVIATIONS. | LP store offers (no offer rows in build 3396210); CONCORD LP exchange (refused); R\&D research dialogues (Start Research, Cancel Research, Buy Datacores) built: each agent's own fields, datacores by group 333, the purchase in one transaction (INDUSTRY_STATUS slice 7). Open: the EveJS wire comparison through tools/mitm (not exercised: the walk seeds ISK and standings in eve.rs PostgreSQL, which EVE.js has no equivalent for; see INDUSTRY_STATUS slice 8); open rows of AG-1 to AG-4. |
| PARTIAL (FW state, 2026-10-18) | Factional warfare: occupation and LP pools, donations with the hub tax, enlistment with the downtime, militia rank, the facWarMgr calls listed in FW_DESIGN, the broker fee upgrade discount, `map.GetFacWarZoneInfo` | Built and tested: `Eve.FactionWarfare` (FwRules, FwStore, FwService, migration 0001), `FacWarMgrService`, `FactionWarfareComposition`; tests `Eve.FactionWarfare.Tests` (83 passed on eve_p4), `FactionWarfareBrokerFeeTests` (3). Complexes built (spawn, NPC defenders as dungeon waves, acceleration gate, timers by presence, victory points split per pilot on the stats rows, respawn; `test_fw_capture_walk.py` runs the capture live). Statistics built (`GetStats_*`, rank overview). Battlefields built and walked live (`test_fw_battlefield_walk.py`: gated site, three points, score, the win with its awards and notices, scoreboard and point balls in space; `FwBattlefieldTickerTests` on eve_p4). Open: the battlefield's NPC blockers and frontline choice, objectives (archetype 67), the pilot-versus-pilot kills, the capture trigger (hub), focused verification of member/corporation LP tax on FW rewards | DEVIATIONS "Factional warfare: state, rules and decisions"; design `docs/project/FW_DESIGN.md` |
| Missions and epic arcs (43, 50 to 55; DEVIATIONS "Mission execution X1" to "X16") | Built: static data, arc offers, in-space agents, failure branches, rewards, courier, fetch, trade and mining kinds, storyline offers; live walks passed for X4 and X8. | Owner decisions: arcs whose kill steps use generic sites are held back unless `EVE_MISSION_INCLUDE_GENERIC_ARCS=1`; the 414 mission level tags of provenance `rule` are rule-derived (51 owner rules and 363 kill missions by their dungeon's difficulty, DEVIATIONS "Mission level tags"); in-space agents are placed by rule inside their system. | Arc 29 is not flipped on (its kill sites are generic); Owner decision 2026-10-19: Arc 132 kill sites for missions 17183–17186 remain held because dungeon IDs 14097–14100 have no authoritative NPC/site manifest; basic courier offer for the 1,130 non-arc courier missions; bonus window and LP for kill missions (LP base not in official data; configurable approximate LP formula plumbing is recorded in DEVIATIONS, with payout application still open). Owner decision 2026-10-19: the approximately 1,100 remaining missions stay untagged until an authoritative level source exists; the SDE has no level field for these rows and no untagged non-arc kill mission has a mapped dungeon difficulty. |

| Insurgency campaigns (49) | Campaign start, site points and point producers built and tested headless. | Owner decision 2026-10-19: defer the public gateway until the client call path and truthful progress/threshold rules exist; its absence is recorded in DEVIATIONS ("Insurgency campaigns"). | Public gateway for corruption and suppression (C-G1, owner decision 2026-10-19: deferred until the client call path and truthful progress/threshold rules exist); suppression producers and spread at level 3; the FOB structure (structures owner). |
| CONCORD Rogue Analysis Beacon (49, track D) | Deploy, activation, link, scan, reward hold, 120-second access window, timed wreck, expiry and restart are implemented and covered by Host tests with the simulation faked. | Wave and reward quantities are owner-decided rule constants; report 1 Q-20 is the target for the reward kinds. | Graphical client not exercised; NPC bounty payout, beacon damage and drone attacks remain open. |
| Incursions (48, track B) | Implemented; 11 host scenarios and 23 rule cases pass. | Rules from the wiki and the SDE. | not exercised on the graphical client; no live run of the incursion path. |
| Encounter Surveillance System (1, DEVIATIONS ESS-1 to ESS-3) | Built: store, rules, clock, ESS ball, link checks, weapon timer, quadrant gate, reserve-bank payout processing, and role-gated /ess grant|link commands. /ess grant selects the first published ship in SDE type list 231; /ess link submits the normal ESS main-bank link RPC. | Owner decisions in DEVIATIONS ("Encounter Surveillance System, slice ESS-2"). The official SDE has no active ESS link module type; Q-25 uses the data-driven hull-list fallback. | Open: successful live link and reserve-bank payout walk, and single-transaction key consumption. |
| Structures (43 to 48, DEVIATIONS structure sections) | Partly built: structure record and timers, the structure ball in the simulation, docking, deployment and vulnerability, moon extraction records, service fuel, cyno beacon, compression plant, cosmic sites. | Owner decision 8: structures are touched only when a task strictly requires it. | Anchoring and unanchoring, notices, control and deliveries, structure cargo, structure compression, extraction mutations (ST-6), graphical-client checks. |
| Wormhole space (13, "Wormhole space: final status") | Built: J-space classes, statics, connection generator, lifecycle jobs, jumps, Thera connections, signatures and sites; a live jump in and back passed headless through the MITM proxy. Local chat has delayed speaker membership and message delivery; corporation, alliance, fleet, wing, and squad channels are wired through the Host gateway. | Project rules where nothing is published: signature counts 4 to 9, category shares, five combat sites in a Drifter system, ends 50 km out, 1,000-unit clouds (DEVIATIONS). | Cut by the owner: wandering K-space connections; region-adjacency exclusion of statics. Remaining: XMPP room discovery and join/leave, private conversations and invitations, ignore/block enforcement, persisted MOTD and subject updates, staff presence, and the two-character Local walk. V24.01 has no LSC RPC callsite; see the chat transport inventory in DEVIATIONS. Owner decision Q-23 sets wormhole Local to DELAYED (only speakers are listed). Ice belt of a Shattered system and live check of gas and ore sites also remain open. |
| Zarzakh and Pochven (12, "Pochven, Triglavian and EDENCOM space") | Partly built: Zarzakh rules and flags from the client's data; Pochven population, capital stargate rule, toll and anchoring; the Turnur to Zarzakh jump passed live (eve.rs only). | Client-only system data comes from the embedded copy of the client export; no invented values. | Cut: gate guns without published numbers, heat and tidal forces and Point of No Return, landmarks and the Black Monolith look-at, gate sentries that shoot, Deathless Shipcaster jump. Owner decision Q-22: no bounty payout for Pochven Triglavian NPCs; loot and drops remain as they are. Open: sovereignty refusal, warp bubbles and probes in Zarzakh, live check of the flags. Emanation notices wait for the public gateway. |
| Client static data (embedded) | Embedded: the server runs without `EVE_CLIENT_SDE_DIR`. The owner decided to keep the embedded `client-*` exports as they are during the temporary public period and revisit when the repository returns to private (Q-09). | Copies built by `tools/dev/extract-client-sde.py` from build 3396210. | Live check of the Zarzakh flags on the embedded data. |
| Runtime and database performance (PERFORMANCE.md) | Dogma, Marshal, sim-link and 10,000-order market workloads have repeatable baselines; sim-link and Marshal show measured allocation improvements. | Preserve client and link bytes; optimize measured hot paths. | Market read uses two SQL queries; statement totals for login/play, startup phases, notification fan-out and live GC measurements remain open. |
| EveJS 261018.1 comparison ([delta assessment](EVEJS_261018_DELTA.md)) | Release-note feature groups, config/content-pack inventory, held-data candidates, and named shared-defect checks catalogued from the new reference. | V24.01 client contract and SDE remain authoritative; EveJS 261018.1 assets often cite client build 3475087. | All runtime parity checks and per-record content-pack provenance require follow-up; no feature is marked complete from the reference alone. Top 15 slices are ordered in the delta assessment. |

Verification run (2026-10-18), gate on origin/main 86be1658 with 967256f1: Rust 1525 passed (cargo fmt and clippy clean); .NET 6534 passed, 1 skipped, 0 warnings. Headless walks: the gate ran 23 walk files, of which 22 were green; the one red walk, test_walk_shared_bookmarks.py, is fixed (the walk waits for OnSharedBookmarksFoldersAccessLost, which the server sends after the response) and green on a fresh server. Eve.AccessGroups.Tests: 23 passed after the regression test was added. A run of the test_walk_*.py files alone gave 16 of 16 passed.

## Native Windows character-entry regression (2026-10-12)

- Observed in the real client/MITM: `godma.ProcessAllInfo -> PrimeLocation -> invCache.GetInventoryFromId(characterID, 1)` received None, then failed at `SetSessionCheck`. The server bound only ships and containers. Character inventory now binds the selected character and projects the existing skill store through ShipSnapshot skill item IDs; self rows use the saved character/active ship and SDE type data. Foreign character access and inventory mutations are refused.
- Recorded `CannotAddFieldsToATupleRowset` in agent initialization: GetAgents now supplies mutable list rows. Recorded `RecordNotFound` for the active ship: Config.GetMultiLocationsEx now reads owned item labels/SDE names and the containing station/system, wired from Host.
- Recorded missing moniker resolution: warRegistry/corpStationMgr expose MachoResolveObject; officeManager resolves/binds and dispatches its nested real office methods with station validation.
- Recorded `TypeError: must be tuple, not list` at Michelle.LoadFormations: GetFormations now returns nested tuples, including the point collection; integer coordinates match EveJS marshal encodeValue. Recorded `OnModuleAttributeChanges` quantity 805 with a normal module ID: fitting bootstrap excludes that sublocation-only notification. GetCommunityFittings now returns a fitting-ID dictionary inside the existing 15-minute cached-method result, not a list.
- Unknown/refused GetInventoryFromId targets now raise FakeItemNotFound instead of returning None to invCache.SetSessionCheck. Character, ship, station and authorized container bindings remain backed by their existing state owners.
- Verification: Inventory 47, Dogma 134, Core 677, Config 8, War/Station/Office 15 and the formations regression pass, zero failures/skips. The first character-inventory/config/agent/moniker fixes were deployed; the later response-shape fixes require the next Host rebuild. Native Undock acceptance and the remaining recorded RPC/protobuf gaps are still open.
- Sources: real Windows client trace/capture on 2026-10-12; EveJS 828823a server/src/services/inventory/invBrokerService.js Handle_GetInventoryFromId/_getCharacterContainerItems; V24.01 eve/client/script/environment/invCache.py GetInventoryFromId and native godma traceback. Initial character selection shipid timing matches the current EveJS plan; no speculative deferral change was made.


## 全サービス応答監査で追加確認する未実装経路 (2026-10-12)

監査入口と担当一覧は `docs/project/service-response-audit/README.md`。別checkoutの結果は現行mainの判定に採用しない。以下は実client/strict RPCまたは同一シナリオのMITMで確認した不足。対応するsource、wire shape、Host配線を担当監査で確定し、未実装と既存実装の破損を区別する。 2026-10-13のsource-only不足一覧は [EveJS response audit backlog](#evejs-response-audit-backlog-2026-10-13) と [日本語summary](../../docs/project/service-response-audit/evejs-only-summary.md)。この追記は既存作業のmethod別索引で、別件を重複加算しない。

| 状態 | 経路 | 必要な作業と根拠 |
|---|---|---|
| PARTIAL | `charMgr.MachoBindObject` global assets | Host resolves asset roots at static stations and persistently deployed structures using the existing structure directory source. `List` returns root items and `ListStations` reports their location rows; `ListStationItems` reads personal items stored directly at either dockable location; `ListIncludingContainers` retains nested items whose parent chain reaches a valid root. Host registration is wired through `CharacterRpc`. Regression: `Global_assets_resolve_structure_roots_and_nested_personal_items` passed on `eve_p2`; the Host wrapper end-to-end path still needs a regression. Evidence: V24 `eve-logic/eve/client/script/environment/invCache.py`; EveJS `charMgrGlobalAssets.js:_resolveRootLocation`, `_buildAssetSnapshot`, `Handle_ListStations`, `Handle_ListStationItems`, `Handle_List`, `Handle_ListIncludingContainers`; `CharMgrService.cs`, `InvBrokerService.cs`, `CharacterRpc.cs`; `InventoryServiceTests`. |
| SKIP (reference parity) | `skillHandler.CheckAndSendNotifications` | EveJS returns an empty list and the RS method returns the same empty list. V24 `notifications/client/development/skillHistoryProvider.py` calls it, ignores its result, then builds notifications from `GetSkillHistory(10)`. No missing state transition is assigned to this RPC. Evidence: EveJS `skillMgrService.js:594-597`; RS `SkillHandlerService.cs:449-450`; client `skillHistoryProvider.py:18-20, 23-44`. |
| PARTIAL | Contract system; `contractProxy.GetLoginInfo`, `GetMyContractEscrow`, `CreateContract` | PostgreSQL schema `contracts` persists contract headers, offered/requested item rows, and bids. Host composition migrates the schema and wires reads and writes. Startup and escrow reads are backed by stored rows; the login rowsets match V24 `contracts.py:190-205`. `CreateContract` parses client terms, checks station and corporation contract-manager context, applies V24 skill capacity tables, computes fees, and calls the transactional Host writer. The writer moves offered inventory, debits issuer reward escrow and fees with wallet journal entries, persists requested terms, and serializes outstanding capacity checks per issuer. Tests cover wire parsing/rules, fee calculations, PostgreSQL success, limit rejection, and rollback. Still open: faction/system broker adjustment, full corporation hangar authorization, auction bids/settlement, courier lifecycle, public search/window reads, notifications, expiry processing, and the two-character headless walk. Item-exchange acceptance is covered by a PostgreSQL Host test including concurrent replay and cleared active escrow. Evidence: [CONTRACTS_DESIGN.md](CONTRACTS_DESIGN.md); [contract compatibility record](../reference/compat/DEVIATIONS.md); V24 `eve/common/script/util/contractscommon.py`; Host `PersistenceContractReadModel.cs`, `PersistenceContractWriteModel.cs`, `CoreServices.cs`, `HostComposition.cs`. |
| DONE (CH-4) | `seasonManager.get_season_data_for_character`; `ProjectDiscovery.initialize_tutorial_status` | Tutorial state is stored per character (`project_discovery_tutorials`, migration 0010) and read by `initialize_tutorial_status`; no active season answers None (EveJS). Tests: `ProjectDiscoveryTutorialStoreTests`, `ProjectDiscoveryTutorialTests`, `ProjectDiscoveryHostTests`; live `tools/headless-client/tests/test_project_discovery_live.py`. Tutorial writers (`skip_tutorial`, `increase_tutorial_level`, `reset_tutorial`) are not in CH-4. |
| PARTIAL (CH-19) | `seasonManager` challenge calls | Reads answer the EveJS no-challenge values (`get_max_active_challenges` 0, empty challenge dict, claims false, no-season progress). Progress and claims are not implemented: no season or challenge definitions exist in the SDE, EveJS (a stub that sends a placeholder challenge id) or the repository. Tests: `The_challenge_reads_answer_no_challenge_as_the_reference_does`; live test above. |
| PARTIAL (CH-13) | `bountyProxy` player bounties | `AddToBounty` debits the placer once (a retried call is not debited again), stores the pool and the placement in PostgreSQL (`player_bounty_pools`, `player_bounty_contributions`, crimewatch migration 0004), and answers `GetBounties`, `GetMyBounties`, the pilot, corporation and alliance top lists, `SearchCharBounties` and the minimums and refusals of EveJS. Not done: the placement notifications (types 112-114, EveJS `bountyNotifications.js`), and the hunter lists are read-only until the payouts (CH-18) write them. Tests: `PlayerBountyHostTests` (2 passed, eve_p4); live `tools/headless-client/tests/test_player_bounty_live.py` (1 passed). | S6: placement notifications 112-114 are sent to the target pilot, each member of a target corporation and of an alliance; the placer is never told; a retried placement sends them once (`PlayerBountyHostTests.A_placed_bounty_tells_the_target_pilot_or_the_members_of_its_corporation_once_and_never_the_placer`, 4 passed on eve_p2). Open: a live placement notification (no live test yet).
| PARTIAL (CH-18) | `bountyProxy` bounty claims and payout | A pilot's kill of a pilot claims the victim's bounties (pilot, then corporation, then alliance pools): a fifth of the loss, paid as one pending payout per sharer. S6: the claim is shared across the killer's fleet (equal split; `PlayerBountyKillsTests`, 2 passed on eve_p2). Open: pod kills (the capsule destruction raises no event), a live kill-claim-payout walk (blocked: the test system is high-security), no kill or payout notifications (EveJS sends none for a claim).
| PARTIAL (CH-14) | standings: NPC kill standing | A pilot that destroys a faction's NPC loses standing with the faction (EveJS `factionKillStandingRuntime.js`: one raw change of -0.00006875 per kill, the standing formula of the contraband penalty, debounced per pilot, faction and system for ten minutes). Stored in the `standing` relation, so the standings rowset reads it. Not done: the live NPC kill, and the client notification of the change (OnNPCStandingChange) which the golden logs show. Standings rowset reads were not re-checked (CH-14 verification item). Tests: `NpcFactionStandingTests` (1 passed, eve_p4); Host and composition tests passed. | S6: a faction kill that lowers the standing sends the killer `OnStandingSet(faction, pilot, standing)` (EveJS `notifyStandingSet`; the client turns it into `OnNPCStandingChange` when it holds a war faction). Not tested: no Host or live test drives a real NPC kill yet.
| PARTIAL | XMPP transport, `XmppChatMgr.Hostname` and chat authentication | `Eve.Host.Xmpp` now has a bounded streaming XML parser, TCP listener with optional server TLS, SASL PLAIN, resource binding, stream limits, queued writes, and active-session token validation. Parser and session tests cover partial/coalesced stanzas, malformed/restricted XML, token rejection, binding, and link cleanup; `Eve.Host` builds cleanly; `XmppChatMgrServiceTests` cover its three RPC shapes and `XmppHostConfigurationTests` covers environment configuration. Host startup conditionally creates and disposes the listener when `EVE_XMPP_ENABLED=true` (default false); `XmppChatMgr` publishes the configured XMPP domain and socket host, and its resync response derives Local/corporation/alliance/fleet room names from the active session. Configure a PKCS#12 certificate trusted by the client or use `chatserver_disable_tls` for a development connection. Local-room discovery/join/leave, occupant presence and groupchat use session-derived current-system membership; wormhole Local hides silent occupants until they speak. Private conversations, corporation/alliance/fleet rooms, MOTD, ignore/block enforcement, and the two-character walk remain open. |
| OPEN | `eventLog.LogClientStats` | 実clientで呼ばれる経路とEveJSログ処理、現行Hostの実ログ出力先を照合し、既存ログ基盤へ接続する。 |
| DONE (headless) | `clientStatLogger.LogString` | V24.01 `eve/client/script/remote/michelle.py:GetBallpark` reports failed ballpark startup through this RPC. Host logs the supplied message and returns None, matching EveJS `clientStatLoggerService.js:16-40`. Regression: `CompatibilityRpcServiceTests.Client_stat_log_records_the_message_and_answers_none`; Host composition registers the service in `CharacterRpc.cs`. Graphical-client error-path run remains outstanding. |
| OPEN | public gateway protobuf startup | 実clientでcorporation/career/daily goals、map tags、campaign、saved skin capacity、Omega offersにエラー/timeout。各namespace/method、request/response DTO、callback/状態所有者を確認し、未実装producerを列挙する。 |
| OPEN | community fitting catalog | GetCommunityFittingsのdict/cache wire shapeは修正済み。実Hostはcommunity catalog callbackを渡していない。EveJSのcommunity owner catalogに対応する実データsourceが必要。 |
| VERIFY (advertisement difference) | `LSC`, `bookmarkMgr`, `certificateMgr`, `contractMgr`, `loadService`, `objectCaching`, `ownerGroupManager`, `planetOrbitalRegistryBroker`, `publicGatewaySvc`, `structure`, `tutorialSvc` | 既定MITM比較でEveJSはadvertiseし、eve.rsは未登録。サービス名だけを追加せず、aliasか独立handlerか、実状態とHost配線まで確認する。`tradeMgr`/`standing2`のHost aliasはcf450dc9で対応済みであり、修正待ちに数えない。広告差11件と全登録候補差29件を区別し、`publicGatewaySvc`のclient-local性、別API代替、既存NOT NEEDED/policy判断を確認する。 |

### Corporation監査で確認した未実装

根拠: `docs/project/service-response-audit/corporations.md` (現行main、14サービス/228 RPCの監査)。未実装と登録・配線の不具合は分離する。

| 状態 | 経路 | 未実装の内容 |
|---|---|---|
| OPEN | `warRegistry.GetNegotiations`, `GetWarNegotiation`; `CreateWarAllyOffer`, `RetractWarAllyOffer`, `CreateSurrenderNegotiation`, `AcceptAllyNegotiation`, `DeclineAllyOffer`, `AcceptSurrender`, `DeclineSurrender` | 現行は空/Noneまたはnegotiation unavailableで拒否。EveJSは交渉の保存・状態変更・相手への通知を行う。既存RelationStore/war authorityと整合する交渉ownerが必要。 |
| OPEN | `mutualWarInviteMgr` 全6 RPC | Hostがcallbackを渡していない。pending/blocked readsと4 mutationを実際のinvite/blocked状態へ接続する。5fce9173で4 mutationの未接続時は明示拒否へ修正済み。残るTODOは実provider接続であり、現在もNone成功fallbackだとは扱わない。 |
| OPEN | `billMgr.CharPayBill` | 個人請求支払のcallback未接続。個人billのowner、wallet引落し、paid状態、重複支払拒否を一つのtransactionで実行するauthorityが必要。corporation billの既存支払とは別経路。 |
| OPEN | `warStatisticMgr` bound `GetBaseInfo`, `GetKills`, `GetKillsByGroup` | Host登録とGetKillMailは存在。V24.01 warReport.py:198のbound report集計が不足。war/killmailの実データを集計し、正しいbindと結果を返す。 |
| OPEN (EveJS legacy API) | `corpStationMgr.GetStationServiceStates`, `GetImprovementStaticData`, `GetStationServiceIdentifiers`, `GetStationDetails`, `GetStationServiceAccessRule`, `GetStationManagementServiceCostModifiers`, `GetRentableItems`, `GetNumberOfUnrentedOffices`, `GetQuoteForRentingAnOffice`, `GetCorporateStationOffice`, `GetStationOffices`, `GetPotentialHomeStations`, `GetOwnerIDsOfClonesAtStation`, `GetStationImprovements` | EveJSにある14旧RPCが未実装。V24.01直接callsiteは未確認。既存office/station/SDE/home/clone ownerを使う実データ読取りが必要で、空stubを追加して閉じない。 |
| OPEN (EveJS legacy API) | `corporationSvc.GetNPCDivisions`; `voteManager.ActOnSanctionedAction` | EveJSとの名前差。V24.01直接callsiteを確認できていないためcompatibility未確認として保持。既存SDE/vote sanction authorityの有無を確認して接続する。 |

Allianceの3 RPC送信先不足という初回指摘は誤検出だった。同じファイルにあるCorpRegistryService partial型をAllianceRegistryServiceと取り違えていた。現行corpRegistryは3 RPCを公開しており、型名とHasMethodの回帰assertionを追加した。Office公開/remote station経路は009291c6で対応済み。OnOfficeRentalChange producerは別の未実装群(EJS-0666)として追う。member kickのsession/cache通知、domain errorのclient向け変換、corpRegistry bound parametersは別の検証・修正対象。corpStationMgr release cleanupはcf450dc9で対応済みであり、古いprivate-map cleanup不足の指摘を撤回する。legacy `CanVote` とRecruitment/Info wrapperの名前差はclient側計算・委譲があるため、未実装と断定して重複RPCを追加しない。

既存実装の修正: character inventory、owned item location priming、mutable agent rows、moniker解決、GetFormations tuple、quantity805通知、community fitting dict/cache、FakeItemNotFoundは `b636110e`。trade/standings aliasのHost登録はcf450dc9で対応済み。GlobalConfigのstore flag4項目と実client受入は別の検証対象。実clientのUndock/再入港、Fleet UpdateMemberInfo bound object、guest/notification順序の受け入れは未完了。

**Updated:** 2026-10-13
**Baseline:** main source commit `7379a70fdeee0e93d2ac38c1b7a7bd9ecd29da60`

This list distinguishes a real behavior/provider gap from an intentionally empty default and from a method whose route already exists. These items are not missing-route work.

## Status meanings

- **OPEN** — EveJS has an operation or live data path, but eve.rs has no real producer/provider connected.
- **FUTURE** — no production data source is present in the current eve.rs runtime or the relevant default path; keep the hook and compatible empty/error behavior, and connect only when a real source exists.
- **LIMITATION** — existing persisted rows lack enough source data to reconstruct their original values.

Do not fabricate successful state or fake backends to close these items.

## Account service comparison (2026-10-18)

| Method | V24 client evidence | Result |
|---|---|---|
| `account.GetWalletDivisionsInfo` | `accountsvc.GetWalletDivisionsInfo` calls the remote AccountMgr and the corporation wallet divisions panel renders its rows. | Implemented with persisted division access roles and wallet balances. The corporation protocol regression covers accountant visibility, a member without access, and a member with one division role. |
| `account.GetTransactions` | Personal and corporation transaction panels call the AccountMgr through the account service. | Implemented using persisted wallet journal rows, account key and month filters, the 30-day default, EveJS transaction fields, and persisted corporation division read roles. Corporation protocol and wallet persistence tests passed. |
| `account.GetEntryTypes` | The account client service fetches these records and uses them to render journal entries. | Implemented with the 26 metadata tuples from the EveJS account service and the shared cached-method wrapper. The regression checks the result envelope, checksum, message IDs, null fields, and row count. |
| `account.GetDefaultContactCost` | The account client service calls AccountMgr; email and contacts UI read the local value. | Omitted: EveJS returns null and the client maps that to its local default of -1. There is no server-owned setting to read. |
| `account.SetContactCost` | The account client setter updates its local default and calls AccountMgr. | Omitted: EveJS returns null without persisting state or notifying clients; the client owns the value. |
| `account.GetAurumBalance`, `GetDefaultWalletDivision`, `GetJournal`, `GetJournalForAccounts`, `GetKeyMap` | No direct calls were found in the V24 Python client source search. | Skip pending evidence of a client route; the EveJS export alone does not establish a client action. |

The V24 client loads `res:/staticdata/accountingEntryTypes.fsdbinary`. The matching 3396210 client SDE export has no accounting entry type dataset, so the service follows the EveJS metadata mapping for these rows.
The noncorporate response returns the authoritative ISK row only; the current wallet authority has no AURUM balance. Do not substitute PLEX for that client field.

The previous audit rows EJS-0008 and EJS-0009 are superseded by the implementations and tests above. Account rows EJS-0001 through EJS-0008 remain subject to the client-route findings in this table.

## 1. Runtime state and notifications

| Status | Service/method | Missing work |
|---|---|---|
| FUTURE | machoNet single sign-on login (`connection.LoginSso`, the `user_sso_token` of `machoNet.Authenticate`) | The acknowledgement now carries `access_token` and `computer_hash` (None), so the client no longer fails on the keys. The token is not read: EveJS records it only (`authenticationService.js:281`), and no token-to-account rule exists. Needs a decision on how a CCP SSO token maps to an account. |
| PARTLY | `insurgencySolarsystem.GetAllVisibleCampaigns`; `GetLocalCampaignClientSnapshot`; campaign notices | The campaign state machine, store and deadline jobs exist (section 49, `Eve.Insurgency`); the snapshot reads them through the Host and the notices go to covered systems. The start driver (hourly, SDE warzone regions) and the pirate site points are in (section 49). Open: the FOB structure (structures owner; `structureID` is None until then), the suppression producers and the spread at level 3, and the public gateway for per-system bars (C-G1, not built; see section 49). |
| PARTLY | `essMgr.GetDataForClientSolarSystem`; `GetMainBankTheftsForClientSolarSystem`; `GetReserveBankTheftsForClientSolarSystem`; `RequestMainBankUnlink`; `RequestReserveBankUnlink`; `AttemptLinkToMainBank`; `AttemptLinkToReserveBank`; `RequestUnlockReserveBank` | The ESS store, the rules, the clock, the ESS ball, the link checks (in system, linkable hull, 10 km, ship change), the weapon timer, the quadrant gate and key consumption are real (`PgEssStore`, `EssRules`, `EssComposition`, `space.ess.tick`). Open: the live walk (grant a hull permitted by the SDE ESS link module through a role-gated GM command, with the module fitted and online), reserve bank interaction, and key consumption. Owner direction for the hull grant is recorded in Q-15 and DEVIATIONS. |
| PARTLY | `abyssalMgr.ClientIsReady`; `AbyssalEntranceDeployment`; `AbyssalEntranceGateActivation` | Deployment eligibility is wired: source typeList 50/self exclusion/deduplication/center distance <= 1,000,000 m Error 8, per-system transaction lock, fresh same-link snapshot and persisted trace checks before consume; source state/session/tether/self-destruct/dungeon/Crimewatch/system gates remain connected. Previous stationId-fix Host build: 0 warnings/errors; its specified PG 22 tests passed, 0 skipped. That binary predates the protocol75 source integration; protocol79 attempt12 later passed four integration checks and verified cleanup for one fit/scenario (see ABYSSAL_DEADSPACE.md); all4 and other tiers remain open. JS manual modes 2/3 passed entry, readiness, deadline and relog expiry. The current-or-last StationId fix now distinguishes current session dock attributes from persisted last-station data while retaining active-ship ownership/location/flag/type checks. RS reconnect baseline `rs-reconnect-login-baseline-1792147356535`: 1 PASS, 91.11 s; valid entry, private-system bind/relogin, both system IDs and unchanged expiry. Invalid-item Error 3 matches the JS capture. POS forcefield remains an explicit boundary; RS all4 and full same-scenario capture parity remain open. Host private-scene origin fix: new runs use the midpoint of Rust's 7,864,320 m visibility grid; explicit saved origins and the null legacy SDE-center fallback remain unchanged. The root cause and focused gates are recorded in ABYSSAL_DEADSPACE.md. RS combat remains open. PARTLY; headless only. See [Abyssal evidence](ABYSSAL_DEADSPACE.md). |
| OPEN | `scanMgr.SignalTrackerRegister` | The Host can record the character/session/system registration and has a full-state publish hook. The live signal snapshot producer and the code that calls the publisher are still absent. |
| OPEN | `posMgr.GetControlTowers` | Connect rows from a real starbase/tower data source to `ReadControlTowerRowsCallback`. The route is present; no current tower provider was found. |
| OPEN (audit side effect) | publicQaToolsServer.MoveMeTo | EveJS records a move_me_to QA event and returns null. RS returns None with matching wire shape, but has no QA event recorder. Connect a real audit provider only if one becomes available. |
| OPEN | `expertSystemMgr.GetMyExpertSystems` | Expert-system ownership is not persisted. Connect its authoritative read path when that domain is added. Contract escrow is backed by the PostgreSQL contract read model; see the contract status row above. |
| OPEN | `raffleProxy.AmIBanned` | The current false result matches the enabled-HyperNet default, but account bans are not persisted. Connect the moderation owner before supporting per-account bans. |
| FUTURE | `config.GetBlackListedPlanets` | The empty list matches EveJS; there is no runtime planet blocklist source. Connect one only if the server adds that policy. |
| PG VERIFIED | `home_station` candidates, remote cooldown, mutation and reset; existing home reads | Shared character/SDE/structure authorities supply school, active corporation office and current dock candidates. Row-locked changes persist the 365-day remote cooldown; local/school changes preserve it. RPCs return Python datetime pickles, reject invalid choices, update session home/clone aliases and notify after commit. Characters: 104 tests; CharacterInfo: 65 tests; CharacterSelect: 58 tests; Host: 202 tests, all PostgreSQL PASS after upstream integration. The Host fixture shares the existing composition test collection and isolates the new structure/agent schemas. Executable EveJS comparison passes. Graphical-client acceptance remains open; clone self-destruct and structure destruction migration are separate flows. |

## 2. Mutation authorities and specialized service providers

| Status | Service/method | Missing work |
|---|---|---|
| OPEN | `dynamicItemService.CreateDynamicItem` | Headless and PostgreSQL-tested, not yet live-verified. Done: the authority (`Eve.Inventory/Mutation/ItemMutationService.cs`: one transaction for consume, create, provenance in `inventory.item_mutations` (migration `0005_item_mutation`) and the `item.mutated` event through an injected port, typed refusals); the Host route (`DynamicItemRpcService`: `CreateDynamicItem` through the authority, `CustomNotify` refusals, `GetDynamicItemInfo` from stored provenance only, the `PyNone` stub and the customInfo path removed); one dogma factory for mutated modules, charges and drones (`Eve.Ships/ItemDogma.cs`, base type = input, skill requirements from the result, stored rolled values as own values); `OnItemChange` for the mutaplasmid, the source and the item made, after the commit and before the reply. Tests: `ItemMutationServiceTests`, `ItemMutationStoreTests`, `MutatedModuleTests` (fit, skill refusal and acceptance, rolled values, twin unchanged), `DynamicItemRpcServiceTests` (route, refusals, announcements in order). Still open: a live graphical-client run (crafting window, tooltips, how the client removes a consumed row, the place it expects for an item made); the drone launch path (base, rolls, launch, prime, recall, reconnect, restart) is covered headless by `MutatedDroneHostTests` and the route-to-launch harness `MutationHarnessTests`; `GetDynamicItemInfo` attributes are the item's own dogma without the ship's modifiers (LIMITATION, see DEVIATIONS 143); the decisions D3 (volume), D5 (fitted and damaged sources), D6 (rounding) and D7 (location). The skill-handler sites (implants, boosters) are not mutable (the mapping covers modules and drones only) and are not routed through the factory. |
| DONE (PostgreSQL, Host) | `moonExtractions.StartNewExtraction`; `FractureChunkWithStructure`; `CancelExtraction` | Served by `StructureMoonAuthority` through the Host composition (`StructureComposition.cs`); the `CustomNotify` refusal in `StructureDataRpcServices` is only the fallback without a backend. Tests: `MoonExtractionStartHostTests`, `StructureDataRpcPersistenceTests`, `StructureDataRpcServicesTests`. Live (ST-20, `tests/test_structures.py`): `MoonminingEventTooShort` for a duration of 0 and `StructureDefenseDenied` from space; the successful start and its answer shape are not live (needs seeded moon resources and a drill). Native client not exercised. |
| PARTLY | `abyssalMgr.AbyssalGateActivation`; `AbyssalEndGateActivation` | Domain Content 41 PASS, Catalog 6 PASS; shared Inventory 55/55 and Ships 234/234. Historical protocol74/75 component checks are recorded in ABYSSAL_DEADSPACE.md. Current protocol79 mapping preserves public kinds 90..97 and uses Abyssal kinds 98..124. Attempt12 passed four integration checks with zero skips, pytest/runner exit 0, and verified three-room cleanup for one five-gun fit/scenario. Attempt11's cleanup failure remains separate; RS all4 and other fits/tiers are still open. JS combat `js-combat-origin-1792142413084` passed all three rooms and final Origin (1 PASS, 485.13 s, cleanup true). RS reconnect baseline passed entry/relogin and unchanged expiry, but RS all4, RS combat/loot/final Origin and same-scenario parity remain open. Strict 2,500 m range is tested; committed FOLLOW reconciliation remains a gap. PARTLY; no GUI/native-client launch. See [contract and evidence](ABYSSAL_DEADSPACE.md). |
| OPEN | `voidSpaceMgr.VoidSpaceEndGateActivation`; `VoidSpacePlayerJump` | The routes and EveJS-compatible missing-provider error behavior are implemented. Connect the real Void Space authority before claiming successful operations. |
| DONE (Host route, tests) | `structureCargoDelivery.DropOffItems` | Served by `StructureDeliveryBackend.DropOffItemsAsync` (ST-5 cargo deposit). Tests: `StructureDeliveryBackendTests` (Host), `StructureRpcServiceTests` (route). Native client not exercised. |
| DONE (Host tests) | `structureCompressionMgr.CompressItemInStructure`; `DecompressGasInStructure`; `GetMyGasDecompressionEfficiency` | Served by `StructureCompressionAuthority` (ore, ice and moon ore compression, gas decompression, efficiency). Tests: `StructureCompressionTests` (Host), `StructureRpcServiceTests` (route). |
| DONE (tests; ST-5) | `structureControl.GetStructurePilot`; `TakeControl`; `BoardStructure`; `EjectFromStructure`; `ReleaseControl` | Routed (`StructureRpcServices.cs`) to `StructureControlBackend` with the pilot store (protocol 65). Tests: `StructureControlBackendTests`, `StructureControlReadModelTests` (Host), `StructureControlTests` (Space). Live (ST-20, `tests/test_structures.py`): `GetStructurePilot` answers None for an uncontrolled structure; `TakeControl` from space is refused with the reference's docked notice; `BoardStructure` of an unknown structure is `TargetingAttemptCancelled`. The refusal order and labels now follow the reference (`StructureControlBackend`, ST-20). Not live: a successful take, board or eject (needs a docked pilot and a setting-17 grant). Native client not exercised. |
| DONE (Host tests) | `structureDeliveries.Deliver` | The delivery transaction in `StructureDeliveryBackend` (the owned hangar subset; the mailbox notice in the same transaction). Tests: `StructureDeliveryBackendTests`. Live (ST-20): `Deliver` from space is refused with `You must be inside the structure to deliver items.`; a successful delivery is not live. Native client not exercised. |
| PARTLY (tests) | `structureDeployment.Anchor`; `CancelUnanchor`; `Unanchor` | `StructureAnchorAuthority` (Anchor; its placement callback puts the ball in the simulation), `StructureDeploymentAuthority` (Unanchor, CancelUnanchor, rename, reinforcement times). Tests: `StructureAnchorTests`, `StructureAnchorDelayTests`, `StructureCompositionTests`. Anchor refusals and the sovereignty index delay are verified (row 1464); live (ST-20): the named refusals `DropNeedsPlayerCorp` (NPC-corporation pilot) and `CrpAccessDenied` with `Insufficient roles` run in `tests/test_structures.py`; a successful live anchor is not exercised. |
| DONE (tests; live, ST-14) | `structureDocking.Dock`; `Undock` | Routed (`StructureRpcServices.cs`) to `SpaceSessions.Structures.cs` (the simulation docks and undocks). Live (ST-14): the pilot docks at the seeded Astrahus and undocks again (`tests/test_structures.py`; default live run plus this file: 73 passed, 0 failed). The refusal `Structure docking access denied.` had one cause: a character made while the server runs had no `affiliation.corporation` relation, so its corporation was 0; `CharacterCreation` now writes it in the creation transaction (`CreationTests`, `The_new_character_gets_its_corporation_membership_written_in_the_creation_transaction`). Tests: `StructureDockingTests`, `StructureUndockingTests` (Space), `An_anchored_structure_is_dockable_by_its_owner_corporation_pilot_with_the_default_profile` (Host, the real Anchor). `handoff-ST-14.md`. |
| DONE | `structureVulnerability.UpdateReinforceTimesFromClient` | Pending reinforcement time, effective after 30 days (ST-2 a, section 48). |
| OPEN | `eject.LaunchShipOrContainerFromWreckOrContainer` | Connect the Host callback to the real container launch operation. This is separate from the tested in-space ship Eject RPC. |
| OPEN | `inSpaceCompressionMgr.CompressItemInSpace` | Connected to the ship compressor module, not a structure (SDE group 4174, category 7 ship module; EVE University wiki, Compression; EveJS `miningIndustry.js:811-870`): a running `industrialItemCompression` module makes its ship a facility for the pilots of its fleet within range (`Effects/CompressionModuleEffect.cs`, `CompressionFacilities`). `SpaceContainers.Compression.cs` checks the facility, the fleet, the surface range and the type list (3255), replaces the stack in the hold in one inventory transaction (`CharacterShips.Compression.cs`), tells the pilot and returns the six-value tuple. Every refusal is a named `CustomNotify`, never `None`. Tested headless: `CompressionHostTests`, `OperationalRpcServiceTests`. Still open: a graphical-client run (the compress action, the facility ball the client sends, the inventory update) is not checked. Structure compression is a separate row (`structureCompressionMgr`, ST-7). |

The seven structure services above account for **17 methods**. `structureHangarViewMgr.GetMyHangarViewState` is connected and is not part of this TODO list.

## 3. Account, dungeon, and content data

| Status | Service/method | Missing work |
|---|---|---|
| PARTIAL | `userSvc.GetMultiCharactersTrainingSlots`; `GetUpdatedMultiCharacterTraining`; `ActivateMultiTraining`; `ActivateSoulboundMultiTraining` | Real: the account's two extra training slots (`identity.account_training_slots`), the active-slot reads, `ActivateSoulboundMultiTraining` (consumes soulbound certificates, 30 days each, Omega required), the store grant, and the training limit (one trainer plus one per running extra slot). Item certificates are consumed from their stack (one per activation), and a running extra queue is paused when its slot ends (S5: `MultipleTrainingSlotExpiry`, wiki). |
| FUTURE | `dungeonExplorationMgr.GetMyEscalatingPathDetails`; `keeper.ActivateAccelerationGate`; `keeper.GetLevelEditor`; `keeper.GetOperationSpawnpointSolarSystem`; `keeper.IsOperationSite` | Routes exist and the remaining callback defaults are in place. Connect real dungeon/operation data if a production provider is added. `keeper.GetCurrentDungeonForCharacter` already has a Host data callback. |
| FUTURE | `abyssal_content_manager.generate_abyss_npc_spawn_report` | The empty diagnostic report remains the EveJS missing-provider fallback; it is not encounter-generation acceptance. The separate procedural catalog has 6 PASS and 477-object JS comparison evidence, but no real producer for this devtools-only RPC is verified. Do not invent report rows or count empty success as completed functionality. See [Abyssal evidence](ABYSSAL_DEADSPACE.md). |
| FUTURE | `RWManager.get_closest_rw_stations`; `RWManager.solarsystem_contains_rw_instances`; `fwVictoryPointMgr.GetAllVictoryPointStates`; `fwVictoryPointMgr.GetLocalVictoryPointState` | No production state provider was found. Current empty/false results match the absence-data default; connect only when a real RW/Victory Point producer is introduced. |

## 4. Method-level compatibility evidence

The 484/484 route result is not a 484/484 wire-shape proof. CCP's public ESI material and the Wiki do not specify internal MachoNet `RemoteSvc` signatures. For every method, keep the evidence source explicit:

1. Use EveLogic/EveJS client code for argument order, response values, and observed side effects.
2. Use CCP/Wiki material for public gameplay rules where applicable, not as an RPC signature.
3. Mark a method **unknown** if source evidence does not establish its output fields or side effects.
4. If a durable per-method ledger is needed, add a source-backed `V/M/U` row for each manifest pair; `services.tsv` itself contains names and call counts only.

## 5. Known data limitation

**LIMITATION — Fleet ads created before migration 006.** The migration can only backfill fields present in the old schema. Original leader corporation/alliance/faction, solar system, advert update time, and membergroup allow/deny lists cannot be reconstructed from those rows. New advert writes persist the full payload. Revisit legacy rows only if an authoritative source for the missing fields becomes available.

## 6. Drone remote effects (implemented, not verified with the client)

Published drones that carry a non-damage effect (a stasis webifier, a neutralizer, an ECM jammer, a sensor dampener, a weapon disruptor, a target painter, a shield, armor or hull maintenance bot) are launched and run it at their target through the projected-effect rows of `ProjectedEffect` and the simulation's remote-effect machinery; a repair-only drone is sent at friendly ships only. Numbers come from each type's SDE dogma attributes (see the test comments in `DroneHostTests.cs`).

| Status | Scope | Remaining work / evidence |
|---|---|---|
| OPEN | Client verification | Covered by .NET host tests (`DroneHostTests`, 22 drone tests, real SDE types) and Rust sim tests (web, armor repair at cycle end, refusal of hostile targets); not yet run in the graphical client. EveJS v0.12.9 has no runtime for these effects (`droneDogma.js` resolves only `targetAttack`, `entityECMFalloff` and the three repair effects), so there is no executable reference for their HUD or timing. |
| OPEN | Hybrid drones (`-I`) | A hybrid carries a turret and an EW effect; they run on separate clocks. Their combined behaviour and the client's view of both are not checked. |
| LIMITATION | Recall mid-cycle | A repairer's cycle that has begun lands its mend when the drone stops (not at the cycle's end). Hold records of a destroyed drone lapse after their duration instead of being lifted at once. |
| OPEN | Mining and salvage drones | Mining drones now route through `MiningProfile` (`SpaceDrones.cs`, the same mining numbers a mining module uses; `MiningYield` and `MiningProfile` in `Eve.Ships`), covered by `MiningYieldTests` and `MiningProfileTests`. Not yet verified in the graphical client. |

## 7. Module effects wired, body pending (exploration, hacking, scanners)

Each effect below is **OPEN — module wired, effect body pending**. Its handler (`ExplorationModuleEffects.cs`) derives from `PendingModuleEffect` (`dotnet/src/Eve.Services.Space/Effects/PendingModuleEffect.cs`) and is registered in `ModuleEffects.Default`, so the module fits and onlines as usual. The effect stays listed by `Uncovered` (`Implemented` is false): a refusal is not coverage. An activation is refused with `EFFECT_NOT_IMPLEMENTED`, which the client shows as "X cannot be activated." (the same as `UNSUPPORTED_EFFECT`); no cycle, capacitor or success is reported.

**Extension point:** in the effect's handler class, override `Build` to return the `SimEffect` (and `Refusal` for its own checks). Read the attributes listed below on the module's values after dogma; `duration` (73) and `capacitorNeed` (6) are already read by `ModuleEffectContext.Cycle()`.

| Status | Module effect (handler, SDE types) | Missing work and attributes the body must read |
|---|---|---|
| OPEN | `doHacking`: `DataAnalyzerEffect` (23 types, 10 published) | The hacking site: `maxRange` 54 (the effect's range), `accessDifficultyBonus` 902, `virusCoherence` 1909, `virusStrength` 1910, `virusElementSlots` 1911. The hacking and archaeology skills reach the virus stats through dogma. Reference: EveJS `moduleOperation.js`, analyzer section (about lines 1273-1600). |
| OPEN | `hackOrbital`: `DataSubverterEffect` (Data Subverter I, unpublished) | No EveJS reference was found for this effect name. Specify it from the client before writing the body. |
| DONE (M-6) | `cargoScan`: `CargoScanEffect` (6 types, 4 published) | Built in track M-6, section 21: the hold reveal, `OnCargoScanComplete`, range 126 as a hard cutoff. |
| DONE (M-6) | `shipScan`: `ShipScanEffect` (7 types, 3 published) | Built in track M-6, section 21: the fitted reveal with the falloff of attribute 510, `OnShipScanCompleted`. |
| DONE | `scanStrengthBonusPercentActivate` (44 types, none published) | `ScanStrengthEffect` uses the generic dogma modifier path. The SDE test checks its four ship-scope modifiers and effect registration; graphical client activation remains open. |
| OPEN | `scanStrengthTargetPercentBonus`: `ScanStrengthTargetEffect` (7 types, none published) | A target effect: `maxRange` 54 and `falloff` 158, and four `targetID` modifiers (the same percent attributes). Reference: EveJS `space/modules/assistanceModuleRuntime.js` (lines 86-100 and 190-210, projected path). |

Not wired here, and why:

- `probeLaunching` is carried by probe charges (group 479, category 8), not by fitted modules. The client launches probes with `dogmaIM.LaunchProbes` (`eve/client/script/parklife/scanSvc.py`), and the `LaunchProbes` route is built by track A5 (section 24); probe scanning is implemented in track A7 (section 29).
- `surveyScan` is carried by the mining survey chipsets (ML-3 Mining Probe, Dactyl Asteroid Analyzer) and the QA Survey Scanner. It belongs to the mining work.
- `targetHostiles` (Automated Targeting Systems), `ECMBurstJammerQA`, `pacifierDebuffQA` and `structureScanResolutionActivePercentage` (a structure module) are outside the exploration, hacking and scanner set.

## 8. Polarized pin and ship resonance propagation (implemented, not verified with the client)

A fitted Polarized weapon pins the ship's twelve resonances at one (effects 5994 and 5995, PostAssign, caps 1527/1528/2768-2771 at 1.0). The pin
holds whatever runs on the ship: hardeners, damage control, rigs, and whether the weapon is online or active (fitted is what counts, per CCP). The
numbers a hardener is told (`TypeAttributeResistanceEffect`, `DogmaModifierEffect`) are read from the world with the module assumed in the state it
will have (`DogmaWorld.Assume`), so precedence, the stacking penalty and the pin apply to them. The simulation is sent the ship's effective resonances
through one path, `SpaceSessions.SendShipAsync`, after an accepted activation, an accepted stop, and an online or offline switch in space, whenever
those resonances or hit points change. The started modules are kept per pilot (`Pilot.ActiveModules`), which is in-memory only.

**No wire change for this section.** The resend uses the existing `ShipChanged` (`SimShipChanged`, kind 8). The pin flag that the simulation needs for section 8b is the one wire change (W2, see 8b).

| Status | Scope | Remaining work / evidence |
|---|---|---|
| OPEN | Client verification | Covered by .NET tests only: `PolarizedPinTests` and `AssumeTests` (Eve.Dogma.Tests, real SDE for 34274, 2293, 2295, 2291, 2046, 587), `PolarizedShipTests` and `PolarizedHostTests` (Eve.Services.Space.Tests). not exercised in the graphical client. |
| LIMITATION | Stop of a hardener | The host takes a module out of the active set when the simulation accepts the stop, but the simulation keeps the effect until its cycle ends. The sim's resonances therefore return to the bare values up to one cycle early. Exact timing needs a simulation-to-host "effect stopped" message: a new wire message, for the next wire batch. |
| LIMITATION | Whole-ship resend | A resend carries everything dogma has for the ship, so an activation effect that changes a number other than the resistances (mass, speed, sensors) is in the resend too while it runs. That is consistent with the sim only where the sim applies the same effect through dogma. Each activation effect has not been checked for this. |
| LIMITATION | Overload | Overloaded state is not in the active set; an overload does not refresh the ship. No resonance effect of an overload is known from the SDE; not checked. |
| LIMITATION | Pilot state across a jump or a survivor | A new `Pilot` (system jump arrival, a survivor flying the wreck's ship) starts with no active modules. If the simulation keeps a hardener running across that, the next refresh drops it. Not checked against the client. |
| LIMITATION | Fitting in space | No route fits a module in space; a fit made while docked reaches the simulation at undock (spawn). The refresh covers online and offline switches in space. |
| DECISION | Pin is fitted, not online | The owner rule R5 (an offline weapon restores the resistances) conflicts with CCP's "when fitted" wording and was not implemented. Kept fitted (see the Polarized spec, D1). |
| CHANGED | Adaptive hardener (effect 4928) | It is no longer a world modifier: the simulation owns its profile and multiplies the ship's own armor resonances by it (section 8b). The world, and so the ship the simulation is sent, holds the ship's own resonances. Two hardeners of one ship cannot be fitted (group limit), so no stacking of the two arises. |
| FIXED | Emergency hull energizer | Its multiplier (commit a9f7d33): it multiplies the ship's 113/110/109/111 by the module's 974-977. The burn-out that is not kept is in section 9. |

## 8b. Reactive armor hardener: per-cycle resistance shift (implemented, not verified with the client)

Type 4403 (`adaptiveArmorHardener`, effect 4928). While it runs the armor's resonances are the ship's own times the module's 0.85 (the seed). At the end of each
cycle (10 s) the resonances move by the SDE's `resistanceShiftAmount` (1849 = 6 points, 0.06): the kinds the armor took least rise, the others fall by
what they rose, and the total is kept. The simulation owns the profile (`crates/eve-space/src/resistance_shift.rs`, `Health::shift_cycle`); the host
(`ReactiveArmorHardenerEffect`) sends the seed, the step and the four armor attributes, and tells the client the armor's resonances at the start and the
stop. A fitted Polarized weapon pins the armor at one: the profile is then frozen (no tally, no shift), and the pin is sent with the ship (`armor_pinned`).

**Wire (W2, bumped to 22).** `SimAction` tag 12 (`ResistanceShift`) and one armor-pin byte at the end of the ship's vitals, in `link.rs`, `SimCodec.cs` and
`oracle/fixtures/simlink/messages.json` (regenerated by `tools/dev/gen-simlink-fixtures.py`). `ProtocolVersion` is 34 after track B's 33 (`crates/eve-space/src/link/mod.rs`); each bump since 22 is listed in ADR 0013's wire epoch log.

| Status | Scope | Remaining work / evidence |
|---|---|---|
| OPEN | The redistribution rule | CCP gives the six points a cycle and the zero-sum total (patch notes Retribution 1.0). The donor count (at least two), the tie order (em, explosive, kinetic, thermal) and the donor cap (one) are the EVE.js and pyfa model, not CCP. A hit equal in every kind still moves the resonances (the two first declared kinds rise). Checked by the unit tests in `resistance_shift.rs` (the reference table, the total, the bounds, the stall) only. |
| OPEN | Client verification | not exercised in the graphical client. Open checks: whether the client re-derives 267-270 from its own PreMul of the module (snap-back after a told change), and that the told values are shown. |
| LIMITATION | Restart and death | The profile is in memory and not persisted. A host restart ends the pilot's running modules (the active set is in memory), and a death removes the ship's health. A new activation starts from the seed. |
| LIMITATION | Gate jump | The CCP patch notes say a stargate jump resets the hardener (Inferno 1.1). Not tested: whether a jump removes the pilot's running modules in the simulation, and so the hardener, is not checked. |
| LIMITATION | Overload and stacking with static hardeners | Overload changes the cycle length, not the shift (not modelled). The armor's base includes the static armor hardeners, the profile multiplies it without a stacking penalty (not checked). |
| LIMITATION | Stop timing | As for section 8: the simulation keeps the effect to the end of its cycle; the armor is told the base when it ends. |
| DEVIATION | Model | Recorded in `docs/reference/compat/DEVIATIONS.md`. |

## 9. Module effects: gaps left after the parity fixes

Each row records its current status. OPEN rows identify missing behavior; completed and changed rows cite the implementation and regression evidence.

| Status | Gap | Evidence and what it needs |
|---|---|---|
| DONE (simulation and link) | Shield ancillary booster continues at base repair after loaded charges run out and pays capacitor on each fallback cycle | `dotnet/src/Eve.Services.Space/Effects/AncillaryRemoteShieldBoosterEffect.cs` sets charged `CapacitorNeed` to 0 and includes `FallbackCapacitorNeed`; `crates/eve-space/src/modules/cycle.rs` checks affordability and draws fallback cost when rounds are exhausted. `SimEffect.FallbackCapacitorNeed` carries the per-cycle fallback cost in protocol 80. Rust `Effect` and `finish_cycle` apply it after charges run out. Tests: `ancillary_repair_consumes_loaded_charges_each_cycle_then_uses_its_fallback` checks base repair and capacitor draw; `The_ancillary_remote_shield_booster_switches_between_cap_free_charges_and_capacitor` checks Host effect packaging; `A_charged_action_roundtrips_its_cycle_charge_and_fallback` and `charged_actions_roundtrip_cycle_charge_and_fallback_in_the_shared_link` check both codecs. No graphical-client walk recorded. |
| DONE (simulation) | A second cloak device is refused while a cloak is active; a running module with `disallowEarlyDeactivation` 906 also blocks cloak activation. | EveJS `activateGenericModule` uses `MODULE_ALREADY_ACTIVE` for the second cloak. Rust `Scene::activate` tests `a_second_cloak_device_is_refused_while_the_ship_is_cloaked` and `a_cloak_is_refused_while_a_module_that_cannot_be_deactivated_early_runs`. |
| DONE | Warp is refused while a cloak that is not warp-safe (`cloaking`, `cloakingPrototype`) runs: `WARP_BLOCKED_BY_CLOAK`, the covert ops cloak (`cloakingWarpSafe`) does not hold a ship | `crates/eve-space/src/scene/warping.rs` (`cloak_blocks_warp`), `SimAction.Cloak` tag 5 (protocol 34); `Eve.Services.Space.Effects.CloakingEffect`; test `deployable::tests::a_cloak_that_is_not_warp_safe_holds_its_ship_and_a_covert_one_does_not`. Not checked in the graphical client. |
| DONE (Host tests) | Emergency hull energizer: accepted activation persists full module damage in the existing `module_condition` store; full-damage modules are refused before simulation activation with `MODULE_INCAPACITATED`. Tests: `An_emergency_hull_energizer_burns_out_once_and_the_damage_survives_a_ship_reload` and `A_module_at_full_hull_damage_is_refused_before_the_simulation_starts_it` (eve_p3). EveJS `moduleHeat.js:913-926` applies one structure-HP of damage on activation; V24 `emergencyHullEnergizer.py:Start` sets module damage to hit points. `ModuleRefusals.Of` maps `MODULE_INCAPACITATED` to the client message. No graphical-client walk recorded. |
| OPEN | Launchers still refused by name: scan and survey probe launchers (481, 1226: LaunchProbes, scan, recall and expiry paths are built; graphical-client verification remains open), structure launchers (1327, 1328, 1562, 1974) are served by the shared missile, bomb and festival handlers when fitted to a ship (`MissileEffect.cs`, `BombEffect.cs`); Host tests cover controlled-structure multirole missiles (35922, charge 37848) and guided bombs (section 56), while ST-8 still has an open lifecycle case and client checks. DONE in this change: the festival launcher (501: snowball charges, no damage, no aggression, a target), the countermeasure launcher (308: missiles and torpedoes by MissileEffect, defenders by DefenderEffect, chosen by the loaded charge), and F.O.F. missiles (fofMissileLaunching: no lock; the launch goes for the nearest valid hostile in the missile's flight range) | `dotnet/src/Eve.Services.Space/Effects/MissileEffect.cs` (`RefusedLauncherGroups`, `OtherLauncherNames`, `AutoTargetId`), `DefenderEffect.cs`; `crates/eve-space/src/missile_entity.rs` (`nearest_hostile`, `AUTO_TARGET_ID`); tests `deployable::tests::an_fof_*`, `a_launch_that_does_no_damage_*`; DEVIATIONS, Bombs, bubbles and warp disruption. |
| DONE | Bombs (launcher 862: damage, ECM and neutralising charges, released at the ship and detonating where it is), the defender launcher (512: target-less intercept of a hostile missile in reach), the stasis webification burst (charge 4088: a web of 15 km for 30 s), the interdiction sphere launcher (589, warp disruption probes) | `Effects/BombEffect.cs`, `DefenderEffect.cs`, `InterdictionSphereEffect.cs`, `DeployableEffects.cs`; `crates/eve-space/src/deployable.rs` (`detonate`, `stasis_burst`, `defender_intercept`); tests `deployable::tests::*`; protocol 34 tags 18 to 22. No V24.01 trace of a bomb or a defender yet (DEVIATIONS, Bombs, bubbles and warp disruption). |
| DONE (simulation and wire; the client's attacker list is not yet) | Breacher pod launchers (4807; charges SCARAB Breacher Pod S 85088 and M 85089, charge effect `dotMissileLaunching` 12174): a hit does no damage and starts a damage over time on the ship it reaches: 40 s (SDE 5735), one tick a second, the lower of the charge's percentage of the target's total hit points (0.6, SDE 5737) and its cap (SDE 5736: 200 for S, 600 for M), rounded down, through the layers without resistances; one working application per ship, the strongest, the others inert (wiki); the effect ends when the ship leaves the scene; the slim item shows `dotHpPercentageApplied`. Protocol 42: the missile carries a damage-over-time block. Not built: `OnDotVictimUpdated` (its payload is a Python set, which eve-marshal does not encode and no source in the repo gives the opcode for), and the SDE skill bonuses (5749, 5750) | `crates/eve-space/src/dot_weapons.rs` (`apply_dot`, `advance_dots`, `dot_tick`, `dot_slim`), `missile_entity.rs` (`missile_lands`, the aggression of a launch), `health/mod.rs` (`apply_unresisted`), `link/encode.rs` and `decode.rs` (the block), `dotnet/src/Eve.Services.Space/Effects/MissileEffect.cs` (`BreacherLauncherGroup`, `MissileOf`), `dotnet/src/Eve.Space/SimCodec.Encode.cs`; tests `dot_weapons::tests::*`, `health::tests::a_damage_over_time_tick_*`, simlink frame "activate a breacher pod launcher", `MissileHostTests.A_breacher_pod_launcher_*`; DEVIATIONS, Breacher pods. |
| DONE (headless) | Warping is refused inside a bubble (`WARP_DISRUPTED_BY_BUBBLE`); nullifiers bypass bubbles; a warp core stabilizer removes two scramble points; the field generator creates the 16 km SDE bubble and applies its ship signature bonus. | Rust: `cargo test -p eve-space --lib bubble` (7 passed) and `...stabilizer` (1 passed). PostgreSQL: `InterdictionNullifierTests` (2 passed). The graphical client was not checked. EveJS's mobile-disruptor runtime has launch, clear and expiry operations, with no pickup or unanchor operation. |
| DONE | The missile family is derived from the static data (`LauncherFamilies`, `DogmaStatic.MissileLauncherGroups`), not from a list of groups. Served set on the pinned SDE: 506-511, 524, 771, 1245, 1673, 1674 | `dotnet/src/Eve.Dogma/LauncherFamilies.cs`; `Eve.Dogma.Tests/LauncherFamilyTests.cs` (synthetic) and `Eve.Services.Space.Tests/MissileFamilyDataTests.cs` (PgFact, real SDE) |
| CHANGED | Armor hardeners and other dogma modifiers reach the simulation only at spawn | Their told values are the world's with the module assumed active (`DogmaWorld.Assume`, section 8), and the simulation is sent the resonances after an accepted activation (`SpaceSessions.ActivateAsync`). The stop timing stays the section 8 limitation. |
| CHANGED | A barrage is refused, not fired as a plain shot | `TurretEffect.cs` `BarragePendingEffect` (a pending handler: refused `EFFECT_NOT_IMPLEMENTED`, listed as uncovered). The barrage falloff (328) and damage multiplier (326) are not exercised. Evidence: `ModuleRuleTests.A_module_whose_effect_has_no_body_yet_is_refused_and_stays_uncovered`. OPEN: the barrage body, when a reference or recording exists. |
| CHANGED | A mutadaptive repairer's first cycle is x1.0 (was x1.1) | `ProjectedEffect.cs` `MutadaptiveRepairSpool.TryNextCycle` applies the bonus, then advances (`assistanceModuleRuntime.js` `advanceAssistanceRepairSpool`). Evidence: `ModuleEffectsTests` and `RepairHostTests` mutadaptive tests. |
| CHANGED | The spool starts on an accepted activation only | `IEffectHandler.Accepted` (default no-op) runs after the simulation accepts the start (`ModulePackages.Result.Accepted`); the name branch in `SpaceSessions.ActivateAsync` is gone. |
| CHANGED | Refusal wording: a microwarpdrive held by a scrambler, and a module too damaged to start | `dotnet/src/Eve.Services.Dogma/ModuleRefusals.cs` (texts from `moduleOperation.js`). Evidence: `ModuleOrderTests`. |
| CHANGED | The group limit on online modules (maxGroupOnline, 978) is enforced at the online | `CharacterShips.MaxGroupOnlineRefusal`, refused as `CannotOnlineReachedMaxGroupOnline {type: (4, typeId), group: (7, groupId), maxGroupOnline}` (`eveexceptions/const.py`). Evidence: `Eve.Ships.Tests/MaxGroupOnlineTests` (real SDE, capacitor boosters, 10 limit). OPEN: lowering a limit does not offline the excess (`_OfflineExcessModulesInGroup`). |
| CHANGED | A target whose type has `disallowOffensiveModifiers` (872) refuses offensive module activation; `SpaceSessions.ActivateAsync` uses the target's static attributes. | `SpaceServiceTests.A_target_that_disallows_offensive_modifiers_refuses_a_gun_before_the_simulation` tests the Host path with a real starter hull and a test-only 872 overlay, because the pinned SDE has no ship type carrying 872. The separate module `disallowAgainstEwImmuneTarget` (1798) case has a Host regression that asserts both SDE attribute IDs, opts a real starter gun into the rule and refuses before simulation activation (`SpaceServiceTests.A_target_that_disallows_ew_immune_effects_refuses_remote_sensor_boost` activates the SDE Remote Sensor Booster I (type 1963; attribute 1798 = 1), a non-offensive remote action, against a real starter hull overlaid with SDE attribute 872; Host refusal precedes simulation activation. The complementary pass against a normal target is covered by `RepairHostTests.A_remote_sensor_booster_packages_positive_range_scan_and_sensor_strength_without_hostility`; both paths passed on eve_p3. |
| CHANGED | A module that may run in a gate cloak (canActivateInGateCloak, 3123) keeps a jump cloak | `SpaceSessions.EndInvulnerability(keepsJumpCloak)`. `SpaceServiceTests.A_module_that_can_activate_in_gate_cloak_keeps_a_jump_cloak` applies SDE attribute 3123 to a real starter gun, observes an accepted activation after `SimJumped`, and asserts that neither cloak-cancel notification is sent; `A_module_without_gate_cloak_permission_ends_a_jump_cloak_when_accepted` checks the default cancellation notification order, and `A_refused_module_activation_keeps_a_jump_cloak` checks a rejected simulation activation leaves it intact (both passed on eve_p3). |
| DONE | A module that burns a resource (consumptionType 713) asks the host for the fuel of its first cycle at activation and of each following cycle at its end, takes the module's consumption quantity (714) from the ship's fuel bay or cargo in one transaction with the answer, and stops with reason 6 when it cannot pay. Its refusal is `NO_FUEL` at activation. | `SimEffect.BurnsFuel` (flags bit 16) set for every handler in `ShipEffect`; ask = `SimModuleWorkCycle` command 9; `Scene::park_start`/`park_fuel_cycle` (`modules.rs`); `CharacterShips.ConsumeModuleFuelAsync` (`CharacterShips.Fuel.cs`); ADR 0013 epoch 24. Tests: `modules::tests` fuel cases; `ModuleFuelHostTests`; `ModuleRuleTests.Each_fuel_family...`. Not checked in the client: the notice of a stop at a cycle end (reason 6; no client text is sent, DEVIATIONS 125); the cyno field, jump portal and clone vat bodies stay `EFFECT_NOT_IMPLEMENTED` (no fuel is taken for them). Known gaps (ADR 0013 epoch 24): a capacitor refusal after the fuel is taken loses it; a target is not re-checked at the fuel answer; a deactivation racing an accepted answer does not refund it. |
| OPEN | The propulsion group limit (afterburner and microwarpdrive together, maxGroupActive 763) is not enforced | Running state is in the simulation (`crates/eve-space/src/modules.rs` `activate` has no group count). Needs group fields on `SimEffect` and a link wire bump (audit A FP-2a and FP-2b). |
| DONE (simulation classification) | Tracking computers are not hostile; tracking and guidance disruptors follow their signed effect modifiers. | `crates/eve-space/src/modules/tests.rs` (`a_computer_is_not_hostile_and_a_disruptor_is_whatever_its_percentages_say`). Focused Rust test passes. Graphical-client HUD behavior is not covered by that test. |
| OPEN | Module rules that need the running state (906, 104, 2354, 669, 763, 3265, 1245, 1349, 1334, 2608) | DEVIATIONS 126. Needs the simulation and one wire bump. |

## 10. Mining, salvage, tractor, targeting and entosis modules (mining track)

Each effect below was in the gap manifest (`dotnet/tests/Eve.Services.Space.Tests/ModuleEffectsTests.cs`, `currentSdeGaps`), so its activation was refused. The rows
marked DONE run through the host and the simulation and are covered by headless tests; none of them has been checked in the graphical client.

| Status | Gap | Evidence and what it needs |
|---|---|---|
| DONE | Mining lasers, ore and ice (`miningLaser`) | The handler (`MiningLaserEffect`), the host cycle (`MineAsteroidAsync`, one transaction for the grant, the ledger row and the `mining.ore_mined` fact), `OnOreMined` to the miner and `OnOreMinedByOther` to the other pilots, the family check at activation, the short cycle for ore (M2, M3, M4, M6a, M6b, M7, M8). Tests: `MiningProfileTests`, `MiningLedgerTests`, `MiningActivationTests`, `WorkCycleHostTests`. |
| DONE | Frequency mining lasers (group 483, crystal-fed) | Refused `NO_AMMO` with no crystal; `CHARGE_NOT_COMPATIBLE` when the crystal's SDE list does not name the rock; the crystal's volatility rolled per cycle, its damage kept as the item's custom info and the crystal removed at full damage (M9). Tests: `CrystalVolatilityTests`, `SpaceServiceTests` (frequency laser). The crystal's yield multiplier (attribute 782) is not applied directly (see DEVIATIONS). |
| DONE | Gas harvesters (`miningClouds`) | A harvestable cloud is mined as a rock is, and yields its SDE material or its `Compressed <name>` gas. A cloud is seeded beside its belt's rock (a shortcut, see DEVIATIONS). Tests: `GasCloudMiningTests`, `SpaceContainerTests` (gas cloud seeding). |
| DONE | Salvager (`salvaging`) and salvage notices and events (S1, S2) | A failed roll tells the pilot `SalvagingFailure` and a kept grant `CustomNotify`; `salvage.wreck_salvaged` and `salvage.salvage_failed` are announced with their transactions. Tests: `WorkCycleHostTests`. The client text of the notices is not checked in the graphical client. |
| DONE | Passive targeter (`targetPassively`, A1) and auto-targeting system (`targetHostiles`, A2) | A passive lock locks its target without telling it (`LockOrigin::Passive`); the auto-target sweep locks the ships that target the pilot within the module's range at each cycle start (`LockOrigin::Auto`). Tests: `modules.rs` (`a_passive_targeter_locks...`, `an_auto_targeter_locks...`), `SpaceServiceTests`. |
| DONE | Belt rocks reseed (B5) | A belt whose rocks are all depleted is seeded again when a pilot arrives (`SpaceContainers.SeedAsteroidBeltAsync`). Test: `SpaceContainerTests`. |
| DONE | Tractor beam (`tractorBeamCan`, T1, T2, T3) | A beam pulls the wreck or can its pilot holds a lock on (`tractor.rs`): the ball follows the ship at 500 m (EVE.js `TRACTOR_FOLLOW_RANGE_METERS`) at the module's `maxTractorVelocity`; a target past the module's range is `TARGET_OUT_OF_RANGE`; the pull ends when the beam stops, the target is lost, or the puller leaves. The host refuses a wreck the pilot may not take from (`TARGET_LOOT_RIGHTS_REQUIRED`). Wire: SimAction tag 9 carries the range (protocol 30). Tests: `modules.rs` (`a_tractor_*`, `a_wreck_pulled_by_a_ship_that_leaves_the_scene_is_let_go`), `motion.rs`, `SimCodecTests`, `ModuleEffectsTests`. Not checked in the graphical client. |
| DONE (ST-9 b-e; native client not exercised) | Entosis link (`entosisLink`, E1) | Served by `EntosisLinkEffect` (`Effects/StructureModuleEffects.cs`) against the sovereignty hub: the capture, the handover and the owner notices of section 50. Tests: `EntosisCaptureTests`, `StructureModuleEffectsTests`, `ModuleEffectsTests`. The `STRUCTURES_PHASE_3` refusal is gone from the code. |
| PARTLY | Compression (`industrialItemCompression`, `CompressItemInSpace`, C1) | The ship compressor module runs and `CompressItemInSpace` is served (`CompressionModuleEffect.cs`; section 2, last row of the first table). The structure compressor (`structureCompressionMgr`) is built (section 2, row for `structureCompressionMgr`). |
| DONE | Belt ore model (D8) | A belt holds the ores of its system's true security from the EVE University belt rows (`BeltOres`), one rock each (15,000 m3, a shortcut: the belt radius is not passed to the host). Seeded when the store holds no live rock in the belt, so a belt emptied in one process is seeded again. The gas cloud keeps 1,000 units (shortcut, DEVIATIONS). Mercoxit and the Drone regions are not seeded. Tests: `BeltOresTests`, `SpaceContainerTests` (seeding, reseed, cloud). |
| DONE | Daily goals of mining and salvage (X-6) | `DailyGoalRecorder` (Eve.Host): "Mine 2000 units of Ore" and "Salvage 5 Wrecks" (EVE.js `dailyGoalsCatalog.js`, industrialist pool) as content goals, one of each per pilot per period (11:00 UTC to 11:00 UTC, `DOWNTIME_HOUR_UTC`). Advanced by the subscriber `mining.daily_goal` on `mining.ore_mined` (ore only) and `salvage.wreck_salvaged`, in its own transaction. `ContentRuntime.FindAsync` finds a finished goal so it is not started again in its period. Both goals run every day (DEVIATIONS). The client's daily goal window is not wired: no RPC reads these goals yet. Tests: `DailyGoalTests`. |
| DONE | Mining ledger as a table (X-7) | `mining_ledger_entries` and `mining_ledger_observers` (migration 0003, space schema). The writer is the subscriber `mining.ledger` in its own transaction (`MiningLedgerStore`: replay-safe, 90-day prune). The readers are `MiningLedgerQueries` on the rowset read side (`ReadSide.ToKeyValList` for the EVE.js list of KeyVals). The game-store `miningLedger` rows are copied at start-up (adding only the missing entries) and kept as an archive. Deviations in DEVIATIONS (mining tracks). Tests: `MiningLedgerTests`, `MiningLedgerServiceTests`. |
| OPEN | Mining scan list (X-7, scan) | `MiningScanRpcReadModel` (Eve.Host) keeps its loop: the survey is a distance test on the live simulation positions (`SpaceSessions.PositionOfAsync`), which a SQL rowset cannot answer. Open: the candidate rows (the stored asteroids of the system) can come from one `RowsetQuery`; the distance stays in code. |

## 11. Pulse Activated Nexus Invulnerability Core (P.A.N.I.C., type 42522): rules done, body pending (needs a fleet-buff action and the buff registry, DEVIATIONS 140)

SDE facts (`typeDogma` of type 42522, effect 6719 `moduleBonusIndustrialInvulnerability`, category 1 activation, no `modifierInfo`): `canFitShipGroup01` 1298 = 883 (Capital Industrial Ship: the Rorqual 28352 and the unpublished Rorqual ORE Development Edition 33687), `maxTypeFitted` 2431 = 1, `requiredSkill1` 182 = 44067 (Invulnerability Core Operation) at level 1, `disallowActivateOnWarp` 1245 = 1, `disallowRepeatingActivation` 1014 = 1, `panicDuration` 2788 = 240000 ms (the effect's duration attribute), range 54 = 200000 m (the effect's range attribute), `disallowEarlyDeactivation` 906 = 1, `disallowDocking` 2354 = 1 (on the module). No security attribute (1074, 1970, 5561) is on the type: the static data names no security class, so no security refusal exists.

| Status | Item | Evidence and what it needs |
|---|---|---|
| VERIFIED (tests) | Fitting: only a Capital Industrial Ship (a Rorqual; an Orca is refused with `InvalidShipGroup`), one per ship, and only a pilot trained in 44067 (`SkillRequired`). Enforced by `FittingRules` (generic). | `IndustrialInvulnerabilityFittingTests` (4 tests, real SDE, `PgFact`). |
| VERIFIED (tests) | Activation while the ship warps: refused with `MODULE_WARP_BLOCKED` before any package is built, nothing sent to the simulation. The rule reads 1245 of the module (`ModuleRestrictions.CheckWarp`) and the host's warp state (`SpaceSessions.IsWarping`, set when a warp command is accepted). | `ModuleRestrictionTests`-style unit tests in `IndustrialInvulnerabilityTests` (real SDE value of 1245), host tests in `IndustrialInvulnerabilityHostTests`. The client wording `Notify` is ours: the client has no UserError for 1245 (constant only in `dogma/const.py`), so the wording is the server's own and the graphical client is not checked for it. |
| VERIFIED (tests) | The effect is refused as `EFFECT_NOT_IMPLEMENTED` (no cycle, no capacitor, no success) at a target and without one, and it stays in the gap manifest. | `IndustrialInvulnerabilityTests`, `IndustrialInvulnerabilityHostTests`, `ModuleEffectsTests` (`currentSdeGaps`). |
| BLOCKED (needs a fleet-buff action and the buff registry, DEVIATIONS 140) | The fleet effect itself: while it runs, every subcapital mining and hauling ship of the Rorqual's fleet within 200 km loses weapons, cloak, warp, dock, jump and tether, takes drone damage -100 %, gains shield resistance (-99.99 %) and -90 % shield recharge, and gets +100 % mass, -50 % top speed, +100 % warp penalty and -75 % scan resolution. | The numbers are the V24.01 client's `dbuff/common/industrialInvulnConst.py` (`DBUFF_VALUES`); the SDE text states the set in words (the attributes do not). The sim cannot apply a buff to a list of ships: `SimAction` and `SimRemote` address one target, and the flags and shield and mass numbers have no sim effect. Needs a new `SimAction` (recipient list from the host's fleet membership, the buff set and a cycle) and the codec, which is a wire change (`SimCodec.cs` and `link.rs`, one bump by the integrator). |
| OPEN | Which ships are recipients: "subcapital mining and hauling ships in the same fleet as the Rorqual", excluding other Rorquals. | The SDE text states it; no attribute or group list in the static data and no server-side code in the client tree (`dbuff` and `FakeIndustrialInvulnerability` only). Group choice (mining barge, exhumer, hauler and transport groups) is not stated by the static data and must be decided with the body. |
| OPEN | Activation needs an active lock on an ore or ice asteroid (SDE text, not an attribute). | Needs the rock's type in the host: rock ids and body ids are separate spaces (section 10). |
| OPEN | Burn-out: the module burns out completely after one activation (SDE text). | Same mechanism as the emergency hull energizer (module damage set to its hit points as the effect starts; `TypeAttributeResistanceEffect`). Not written because the effect is refused. |
| OPEN | Effect lifetime: expiry in `LocalTimers` (in-process), reset on stop, death, ship change, jump, undock, and the buff set re-sent when a recipient joins or leaves the fleet. | Not written; the buff expiry belongs to the body (decision D4, DEVIATIONS 140). |
| OPEN | `disallowEarlyDeactivation` 906 (SDE: "prevent ejection from the ship ... extend the log out ship removal timer") and `disallowDocking` 2354 on the module. | Not enforced. The client uses 906 only for cloaking (`FakeCloaking.PreStartChecks`); no ejection or logout timer is written for modules. See section 9. |
| VERIFIED (SDE) | Fuel and consumption: type 42522 carries none of the consumption attributes (`consumptionType` 713, `consumptionQuantity` 714, `hasFuelCargo` 1085, `fuelCargoCapacity` 1086, `specialFuelBayCapacity` 1549, `serviceModuleFuel*` 2108-2110, `jumpDriveConsumption*` 866/868, `groupJumpConsumptionType` 3130), and the effect 6719 has no `modifierInfo`; the English description names no fuel. So the SDE does not say the core burns fuel and no fuel refusal is written. The core is refused as `EFFECT_NOT_IMPLEMENTED` at every activation, so it cannot run free. If the body is written and a consumption attribute appears, the shared per-cycle consumption mechanism (not PANIC-specific code) decides it. | Checked by query on `sde.typeDogma` / `sde.dogmaEffects` for build of this worktree. |
| LIMITATION | The warp refusal also covers a ship that is in warp (the host's warp state is set from the accepted warp until arrival, stop, dock, jump or destruction), a superset of "aligning to warp" in the SDE. | `SpaceSessions.IsWarping`. Whether the client refuses an activation in warp is not read from the client yet. |
| OPEN | Client verification of all of the above. | not exercised in the graphical client. |

**LIMITATION — Character clone and worldspace state.** The character store has a home station but no separate clone station or worldspace field. Character entry currently uses the home station as `cloneStationID` and `None` for `worldspaceid`; populate these from an authoritative store if one is added.

**LIMITATION — In-space resume fidelity.** The active ship's position is persisted and restored, but the current space-item store does not preserve velocity or warp state. Direction reuses any prior stored vector or falls back to +Z. The first active-space ship List now triggers a visual-only AddBalls2 prime before full bind; native-client login ordering is not checked in the graphical client.

## 12. Superweapons, group 588: the strike, the Bosonic cone, the titan doomsdays, the lances, the reapers, the disruptive lances and the transport field have bodies; published ship HOG 40634 runs, while Standup HOG 47068 remains unpublished

SDE facts (build 3396210): group 588 "Super Weapon" has 20 types. Catmageddon (37474) is unpublished, so the published group listing has 19; the tests add it by id. The effect rows and the attributes are read from `sde.typeDogma`, `sde.dogmaEffects` and `sde.dogmaAttributes`; the family table is in `dotnet/src/Eve.Services.Space/Effects/SuperWeaponEffects.cs`. Protocol 25 adds the strike (`SimAction` tag 13, ADR 0013 epoch 25): the simulation runs the area, the pulses, the windows and the neutralising wave (`crates/eve-space/src/strike.rs`, `modules.rs` `strike_start` and `strike_pulse`).

| Status | Item | Evidence and what it needs |
|---|---|---|
| VERIFIED (tests) | Bosonic Field Generator 40633 (`doomsdayConeDOT`): the cone body. Aimed at a point, it builds one strike from its SDE data: cone shape 3, range 30000 m (`maxRange`), warning 10000 ms, 21 pulses at 1000 ms (the reference's window is closed at its end, 20000 ms), damage em, thermal, kinetic, explosive 10000 each, weapon signature 10000, immobility 30000 ms, no jump or cloak 300000 ms, energy neutralisation 30000 within 10000 m against signature 20000. The fuel (2000 of 16275 per cycle) is the shared per-cycle mechanism (`BurnsFuel`). Without a point it is refused `TARGET_POINT_REQUIRED`; in high security the empire rule refuses it first (`CantInEmpireSpace`). | `SuperWeaponEffectsTests.A_hog_is_refused...and_a_cone_is_built...` (PgFact, real SDE numbers); `ModuleRuleTests` (aimed: `BurnsFuel`); `SuperWeaponHostTests.A_cone_through_superWeaponMgr...` and the empire-rule host test; Rust `strike::tests`, `weapons::tests::a_strike_*` (pulses after the warning, no hit behind or off the axis, the window holds the ship, no cloak or jump in its window, the neutralising wave); `SimCodecTests` and `simlink_fixtures` (wire). Mutation: widening the cone fails `a_cone_widens_to_45_degrees_along_its_length`. Not checked in the client: the visuals (the cone has no graphic in the SDE, the client table or EveJS); the windows' client messages. The accepted path through the host is tested in ZERO security (`SuperWeaponHostTests.A_cone_in_zero_security_starts_through_the_host...`, fuel taken when the simulation asks): the cone carries `disallowInEmpireSpace` 1074, so the client's rule refuses it in LOW security too (a low-security fixture is a refusal, not an accept). Docking is refused in the window now (DEVIATIONS 131); the target's capacitor resistance is applied since protocol 27 (DEVIATIONS 132). |
| VERIFIED (tests) | The strike action (protocol 25): `SimAction` tag 13, the shape, aim, length, radius, warning, pulse cadence and count, damage, signature, the two windows and the neutralising wave. Both codecs, the fixtures (`oracle/fixtures/simlink/messages.json`, generated by `tools/dev/gen-simlink-fixtures.py`), `Hello` and `HelloAck` at 25. | `SimCodecTests.A_doomsday_strike_roundtrips...` and `A_strike_that_the_simulation_would_refuse...`; `simlink_fixtures` (valid and invalid strike frames). ADR 0013 epoch 25. |
| VERIFIED (tests) | Point targeting: `superWeaponMgr` `ActivateSinglePointTargetedModule` carries the client's point (a tuple of three numbers, from the ship) into the package (`EffectContext.Aim`, `ModulePackages.Of`, `SpaceSessions.ActivateAsync`). An activation that the simulation accepts reports success; a refused one is its refusal as a user error. A slash is still refused by its handler. | `SuperWeaponHostTests.The_client_point_is_a_tuple...`; `SuperWeaponHostTests.A_point_targeted_doomsday...`; `SuperWeaponHostTests.A_cone_through_superWeaponMgr...`. |
| VERIFIED (tests) | The group 588 families: every activation is refused by the reason its family names, or built. P.A.N.I.C. 42522 keeps `EFFECT_NOT_IMPLEMENTED`. The structure variants (category 66) are refused by name. A titan without a target is `TARGET_REQUIRED`. | `SuperWeaponEffectsTests.Every_activation_of_every_group_588_type...` (PgFact; 20 activations, each refused or built as its family says). |
| VERIFIED (tests) | Gravitational Transportation Field Oscillator 40634 (`doomsdayHOG`): a transport field, protocol 29 (`SimAction` tag 15, `Field`: a sphere strike of shape 4, centred on the aimed point, radius 10 km, one pulse at the 15 s warning). The capsuleer ships of the system inside it are carried to the aimed point (the host lists them: `TransportNeighbour`); the capital ships inside it cannot warp for 60 s (`WARP_BLOCKED`). The firing ship is held for its windows and takes the strike's energy neutralisation. Sources and the resolution of their conflicts: DEVIATIONS 135 and 141. Not built: the tether exclusion (142). The structure variant (47068) is refused by name (`STRUCTURE_SUPERWEAPON_NOT_IMPLEMENTED`). | `SuperWeaponEffectsTests.A_hog_builds_a_transport_field_...` (PgFact, real SDE); `SuperWeaponHostTests.A_hog_aimed_at_a_point_...` (the field reaches the simulation, fuel settled per cycle); Rust `weapons::tests::a_transport_field_carries_the_ships_of_its_sphere_and_holds_the_capitals_in_it`, `strike::tests::a_sphere_covers...`; `simlink_fixtures` and `SimCodecTests` (the field frame). |
| VERIFIED (tests) | Target's capacitor resistance (protocol 27): each ship's `energyWarfareResistance` (2045) is a sixth number of the sensors block (`SimSensors.EnergyWarfareResistance`, read from the hull's attributes; 0 is immune). The neutraliser's amount and the strike's neutralising wave are multiplied by it. The nosferatu is not weakened (reference resistance not read). | `modules::tests::an_energy_neutralizer_is_weakened...`, `weapons::tests::a_strike_neutralisation_is_weakened...`, `SimCodecTests`, `simlink_fixtures` (0.25 on the Ibis), `RepairHostTests` (ordinary hull = 1). ADR 0013 epoch 27. |
| VERIFIED (tests) | Test wart from protocol 26 (a mutation that stops a titan hit being recorded made `a_titan_doomsday_hits...` loop forever): the test now runs its body on a thread with a 10 s deadline (`weapons.rs` `finishes_within`) and fails with `did not finish within 10 s`. Re-run as a mutation: fails in about 10 s. Production is bounded in `Scene::settle_modules_for_pilots`: each ship advances at most `MAX_MOMENTS_PER_SETTLE` (10,000) moments per settle, and a repeated stalled moment ends its effect so charge processing can continue; later settles process any remaining due moments. Tests: `modules::tests::a_settle_takes_a_bounded_number_of_moments_and_the_next_one_takes_the_rest` and `a_stalled_moment_ends_its_effect_and_a_charge_moment_is_left_to_the_settle` (both passed on this check); `weapons::tests::a_titan_doomsday_hits_its_locked_target...` retains the 10 s deadline. |
| VERIFIED (tests) | `superWeaponMgr` (X0): an unwired route answers a user error and never `PyNone`. | `UnportedFeatureRpcServicesTests` (Core). |
| VERIFIED (tests) | The structure cyno beacon route (`structureCynoBeaconMgr`, P2-6) answers the user error `Structure travel is not wired on this server.` for each of its three calls while no backend is wired, and forwards to a backend when one is given. | `StructureTravelTests` (new file). |
| VERIFIED (tests) | Attribute constants for the superweapon, cyno, jump portal and clone rules are the SDE's attributes (`ModuleAttributes`). | `SuperWeaponAttributeIdsTests` (PgFact, real SDE). |
| VERIFIED (tests) | Superweapon graphics (`SuperWeaponFx`): the titan doomsday guids are the SDE effect rows; lance and disruptive lance graphics follow the module's raceID. Reaper graphics are EveJS's catalog only (EveJS's catalog, not read from the client's effect data). The cone has none. Nothing reads the table yet. | `SuperWeaponFxTests` (PgFact). |
| VERIFIED (tests) | Cynosural field types from the SDE: 21094, 28650, 52696, each radius 250. Fields are spawned by the generator's cycle (section 22, track B7). | `CynoFieldTypesTests` (PgFact). |
| DONE | Fuel of a superweapon with a body goes through the shared per-cycle mechanism (`ShipEffect.Build` sets `BurnsFuel` for 713 > 0). | `ModuleFuelHostTests`; `ModuleRuleTests` (the DDD module is aimed and burns its fuel). |
| DONE | Module gap manifest (manifest test): the cone is removed from the uncovered list because it has a body; the refusals stay listed. | `ModuleEffectsTests` (the manifest test). |
| VERIFIED (tests) | Titan doomsdays (`superWeapon{Amarr,Caldari,Gallente,Minmatar}`: Judgment 24550, Gjallarhorn 23674, Oblivion 24552, Aurora Ominae 24554), protocol 26 (`SimAction` tag 14, `Doomsday`). The client activates a titan with its LOCKED TARGET, not a point (no `isPointTargeted` on the type; `shipmodulebutton.py` calls `def_effect.Activate(targetID)`), so the brief's "point targeting" does not apply to them. One hit on the target after `damageDelayDuration` (7500 to 12000 ms), its one damage kind 2 400 000, no signature factor; the immobility (30 s) and no-jump window (300 s) at activation; fuel 16274 x 50000 per cycle through the shared mechanism. No chain: the titans carry no lightning attributes (2104 to 2106), so EveJS hits the primary target alone. Not checked in the client: the titan graphic (`SuperWeaponFx`) and the texts of the refusals. The structure variant (category 66) is still refused by name (O5). | `SuperWeaponEffectsTests.A_titan_doomsday_is_built...` (PgFact, SDE numbers); `SuperWeaponHostTests.A_titan_doomsday_on_a_locked_target...` (through the host, fuel taken when the simulation asks) and `..._without_a_target...`; Rust `weapons::tests::a_titan_doomsday_hits...` and `..._is_refused_without_a_locked_target...`; `SimCodecTests.A_titan_doomsday_roundtrips...`; `simlink_fixtures` (a valid hit, a negative delay). ADR 0013 epoch 26. |
| VERIFIED (tests) | Titan lances (`doomsdayBeamDOT`: 40631, 41439, 41440, 41441) as a cylinder strike (`DoomsdayLanceEffect.cs`, `DoomsdayStrike.cs`): radius 2500 m (`doomsdayDamageRadius`), length 200 km (`maxRange`, fixed), warning 15 s, one pulse per second through the 15 s window (16 pulses), 50 000 EM each, signature 5000, the immobility and no-jump windows, the energy neutralisation (35 000 within 10 km against 20 km), fuel 16274 x 50 000 per cycle. Empire space: `disallowInEmpireSpace` (1074) is set, so the client's rule refuses it in LOW and high security (`CantInEmpireSpace`); it runs in zero security only. The reference names the low-security case `MODULE_RESTRICTED_IN_LOWSEC`: the same outcome under the client's name (see DEVIATIONS 136). BUILT (track C3, protocol 37): the lance beacon (EveJS `spawnLanceBeacon`), a transient "Modular Effect Beacon" container (type 41233) at the aim clamped to the lance's range, put in space when the strike starts and taken out when its effect ends; the beam is played on it. Not checked in the client (DEVIATIONS, Lance beacon). | `SuperWeaponEffectsTests.A_lance_is_built_from_its_static_data...` (PgFact); `SuperWeaponHostTests.A_lance_is_refused_by_the_empire_space_rule_in_high_and_low_security...`; `..._in_zero_security_starts_through_the_host...` (fuel taken when the simulation asks); Rust `strike::tests` (cylinder) and the strike window tests. |
| VERIFIED (tests) | Reapers and Catmageddon (`doomsdaySlash`: 40632, 41442, 41443, 41444, 37474), protocol 28: the two-point slash (`ActivateSlashModule`, the client's arc checked against the fixed geometry: `SLASH_POINTS_REQUIRED`, `SLASH_POINTS_INVALID`), the cylinder that sweeps from the start point to the end point over the damage window (11 pulses, 10 s window, 5 km radius), the windows, the neutralising wave, fuel 50 000 of 16 274 per cycle. The sweep is the simulation's (`SimAction` tag 13, `SweepEnd`). The empire rule refuses it in high and low security (1074). Catmageddon (37474) is not built by its data (its shape and radius are missing: `SUPERWEAPON_REAPER_DATA_MISSING`). The graphic is EveJS's catalog only (DEVIATIONS 138). | `SuperWeaponEffectsTests.A_reaper_is_a_sweep...`; `SuperWeaponHostTests.A_reaper_slash_through_superWeaponMgr...`; Rust `weapons::tests::a_sweeping_strike...`, `strike::tests::a_sweep_turns...`; `simlink_fixtures`. |
| VERIFIED (tests) | Disruptive lances (`debuffLance`: 77398 to 77401), protocol 28: the strike with its 60 s debuff (no warp, jump or dock, half the remote repair; DEVIATIONS 139). The siege requirement (5426) is read from the ship's own state: an active `moduleBonusSiegeModule` module, otherwise `ACTIVE_SIEGE_MODULE_REQUIRED`. The cloak is refused `CLOAK_DISALLOWED` while a lance is fitted (5425). Not a buff of the registry (the reference has no dbuff collection for it). | `SuperWeaponEffectsTests.A_disruptive_lance_needs_an_active_siege_module_...`, `SuperWeaponEffectsTests.A_fitted_disruptive_lance_disallows_the_cloak_...` (PgFact, real SDE); Rust `weapons::tests::a_disruptive_pulse_disrupts...`, `scene::tests::a_disrupted_ship...`, `weapons::tests::a_disrupted_ship_takes_half...`. |
| PARTLY (track C3 remains; C4–C8 command-burst work is built) | The SDE buff registry, charge-derived strength, burst activation and cycle charge use, in-range recipient exchange, fleet filtering, timed Dogma modifiers, expiry, persistence, `OnDbuffUpdated` state, and receiver `OnModuleAttributeChanges` are implemented. dogmaIM snapshots sync the timed modifiers. P.A.N.I.C. activation and fleet grants are implemented. | C6/C8 tests; `CommandBurstFamiliesTests` covers every published charge, and `CommandBurstHostTests.Every_burst_family_delivers_its_charge_values_to_a_fleet_member` checks delivered family values including collection 2474; DEVIATIONS tracks C4–C8 | The level-V Armor, Shield and Mining strength regression is added to the focused .NET test suite. The two-pilot live walk runs in the selected slow workflow. Its first traced failure found a consumed-charge settlement gap; simlink protocol 81 carries the SDE charge type into Host evaluation. The next trace confirmed both pilots received the -8.16% Bifrost Shield burst and nested Destiny notifications; the test had expected -8%. Run 46’s live test step passed with the SDE-derived expectation and expiry check. Graphical-client verification and other checks are listed in section 22. |
| PARTLY | Strike follow-ups: docking is refused in the no-jump window (`DOCKING_BLOCKED`) and in the immobility window (`IMMOBILE`), a simulation rule with no wire change (Rust `scene::tests::a_ship_in_a_doomsdays_windows...`). Tethering: eve.rs has no tether at all (no `tether` in `crates/eve-space`), so there is nothing for the reference's `disallowTethering` to refuse; that flag is a gap until a tether exists (DEVIATIONS 142). The target's capacitor resistance is applied since protocol 27 (see the protocol 27 row). The cone's graphic is absent from the static data and from EveJS, so none is sent (DEVIATIONS 133). | DEVIATIONS 131 to 133. |
| PARTLY | Published ship HOG 40634 (effect 6474) recomputes in-range participants on each pulse from current geometry and the SDE `typeDogma` `isCapitalSize` attribute 1785: non-capital hulls move, capital-sized hulls are held. Controlled structure Field pulses request fresh all-target candidates and accept a complete moved/held partition from the correlated Host kind 86 result; the optional v1 tail preserves the old bytes when empty. The Host still refuses unpublished Standup HOG 47068; no graphical-client run. | Rust `modules::field_tests::published_ship_hog_recomputes_moved_and_held_ships_each_pulse`, `modules::field_tests::controlled_field_uses_a_fresh_candidate_exchange_and_only_the_host_partition`, `world::field_tests::capital_size_is_loaded_from_type_dogma_attribute_1785`, `link::field_partition_tests::*`; new C# `SimCodecFieldPartitionTests` not exercised here; Host authorization tests are parent-owned. |
| DONE (track C3, protocol 39) | Lance beacon (EveJS `spawnLanceBeacon`): a Deployable of the simulation (`crates/eve-space/src/deployable.rs`, role `Beacon`, the ball of type 41233), released at the strike's start at the aim clamped to the lance's range; the lance's FX is played on it, and the beacon is removed when the effect ends. Titan lances, disruptive lances and the Bosonic cone (EVE.js family `lance`). | `deployable::tests::a_lances_beacon_*`, `deployable::tests::a_strike_with_no_beacon_*`, `SuperWeaponHostTests.A_lance_carries_its_beacon_*`, `SimCodecTests` and `simlink_fixtures` (the beacon flag); DEVIATIONS, Lance beacon |
| DONE | P2-10 (the interdiction nullifier's warp-bubble immunity test): `InterdictionNullifierTests.A_ship_with_warpBubbleImmune_is_not_held_by_a_bubble` reads the nullifier's 1538 from its 3120 through the package (`WarpImmunity`), and the simulation test `deployable::tests::an_immune_ship_is_not_held_by_a_bubble` holds the ship in a bubble without it. | decision D10 and DEVIATIONS (Bombs, bubbles and warp disruption). |
| OPEN | Burst projectors (`doomsdayAOE*`, categories 7) and the Area type (ADR 0011): the strike is the first area action; the general `Area` type with recipient lists (decision D1, D3) is not built. | ADR 0011. |

## 13. Player fighters (2026-10-09)

Decisions: ADR 0015 and its amendments. Deviations and their evidence: DEVIATIONS "Player fighters".

| Status | Item | Where |
|---|---|---|
| DONE | Fighter bay and launch tubes: load, unload, the tube rules and the bay volume check, in the database | `dotnet/src/Eve.Ships/CharacterShips.Fighters.cs`, `FighterTubes.cs` |
| DONE | `fighterMgr` refuses every call it cannot serve with a user error; no fallback answers success | `ClientStartupServices.cs`, `ISpaceFighters.cs`, `SpaceFighters.cs` |
| DONE | Ability slot table (EVE.js data, families checked against the SDE) | `dotnet/src/Eve.Services.Space/Effects/FighterAbilityCatalogue.cs` |
| DONE (not exercised in the client) | Squadrons in space: launch, recall and landing, orbit, follow, stop, GOTO_POINT (a point course), abandon, scoop back within 5000 m of the ship it came from, recovery on logoff (within 5000 m lands, else let go) | ADR 0015; `crates/eve-space/src/fighters.rs`, `dotnet/src/Eve.Services.Space/SpaceFighters.cs` |
| DONE (not exercised in the client) | Squadron health: the points of all members together; a member lost is told as `OnInSpaceSquadronSizeChanged` and the item keeps the size; the last member destroys the squadron and its item is removed | `fighters.rs` (`tell_squadron_size`), `SpaceFighters.cs` (`OnReceived`, `LoseAsync`) |
| DONE (not exercised in the client) | Abilities that run in the simulation: warp disruption, stasis webifier, energy neutralizer, ECM, tackle (one remote: a scramble and a web), microwarpdrive, afterburner, evasive maneuvers, micro jump drive, missiles and AttackM (per member, on the locked target) | `Effects/FighterAbilityEffects.cs`, `Effects/ProjectedEffect.cs`, `ewar.rs`, `modules.rs` (`Remote::Tackle`) |
| DONE | Ability charges and cooldowns are kept on the squadron's item and resumed after a restart (DEVIATIONS, Player fighters and Track C remainder). | `SpaceFightersRemainderTests` |
| DONE | A squadron let go writes the size it loses to its item; a pilot entering space takes up its squadrons out in the system (position not kept). | `fighters.rs`, `SpaceFightersRemainderTests` |
| DONE (protocol 36) | Kamikaze: the squadron closes in on its target to its range and detonates (`SimAction` tag 23); missiles apply the explosion's application factor and check the reach (tag 24). | `fighters.rs`, `SpaceFightersRemainderTests` |
| DONE (track C3, protocol 37) | LaunchBomb: the blast is the shared area of ADR 0011 (`SimAction.Blast`, tag 25; `Scene::blast_at` over `ships_in_area` and `damage_ship`). The squadron names a point (`ActivateAbilitySlotsAtPoint`), the bomb is the squadron type's bomb (SDE 2324), the blast is its `empFieldRange` (else `explosionRange`) and four damages, applied once when the 60 s window ends; refused in high and low security by the ability's flags. not exercised in the client. | `FighterLaunchBombTests` (host), `deployable::tests::a_fighter_blast_*` (sim), `SimCodecTests`, `simlink_fixtures`; DEVIATIONS, Fighter LaunchBomb |
| DONE (protocol 32) | Protocol: fighter kinds 54 to 60, `LaunchFighter` with the squadron size, GOTO_POINT (kind 59), `FighterSize` (kind 60), Tackle remote tag 12 | `link.rs`, `SimCodec.cs`, `oracle/fixtures/simlink/messages.json` |
| REPLACED | Live recording against EVE.js (WP-S, `oracle/fixtures/scenarios/fighter_flight.json`): no graphical client runs on this host, so no recording was made. The wire shapes are pinned by `oracle/fixtures/simlink/messages.json`, whose fighter entries were written from the EVE.js and eve-logic source (see DEVIATIONS, Player fighters) | ADR 0015 WP-S |

## 14. Windows and the production start (2026-10-09)

The start scripts are `tools/windows/` (`start-server.bat`, `start-server-dev.bat`, `stop-server.bat`, the `.ps1` files) and
`tools/server/start-server.sh`. Windows usage and CI coverage are described in [WINDOWS.md](WINDOWS.md).

| Status | Item | Missing work |
|---|---|---|
| OPEN | Windows CI result | Linux passed on run 174, while Windows reached the PowerShell 5.1 headless entry point and failed its exit-code check. The script now initializes and captures the native exit code; rerun Windows coverage and review a successful run before closing this row. Windows coverage remains selected on sensitive pushes, manual selection and the weekly run while the repository is public. On return to private, Q-10 requires all workflows, including Windows jobs, to be manual only; use `tools/dev/ci-mode.py`. |
| OPEN | Windows Python fixture decoding | Run 134 passed Rust and .NET on Windows but the Python golden test used the Windows default code page. The loader now specifies UTF-8; the focused Python suite passed on Linux and the Windows rerun will confirm it. |
| OPEN | Windows PowerShell and console behavior | PowerShell developer scripts now cover service control, checks, and headless tests. Verify Ctrl+C, the batch prompt, and window-close behavior on the owner's Windows setup. |
| OPEN | `global.json` SDK path `../.dotnet` | Check SDK selection on the owner's Windows checkout path. |
| DONE | Windows test entry points | `tools/windows/check-all.ps1`, `pg18.ps1`, and `run-headless.ps1` provide PowerShell 5.1 counterparts for local checks, installed PostgreSQL 18 service control, and headless tests. |
| DONE | Host `tcp:` listener | `HostListener` binds numeric IPv4 TCP endpoints; Windows tests cover its defaults and binding. |
| DONE | Platform-aware defaults | `eve-net` (`DEFAULT_HOST`), `eve-sim` (`127.0.0.1:26011` off Unix) and `HostConfiguration.DefaultEndpoint`/`DefaultSimSocket` default to TCP on Windows; unit tests cover them. |
| DONE | Ctrl-C of the simulation | `eve-sim` races the connection against Ctrl-C; before, it ignored Ctrl-C while a host was connected. |
| DONE | Line endings | `.gitattributes`: `* text=auto`, `.sh` LF, `.bat`/`.ps1` CRLF. |

## Closed items that should stay closed

- `storeManager.buy_offer` and `insuranceSvc.InsureShip` / `UnInsureShip`: existing persistence providers are connected.
- FleetProxy's seven operations plus FleetObject advert creation/setup callbacks: connected and tested; only the legacy-row limitation above remains.
- `station.GetGuests`: alias route, wire shape, observer ledger seed, live-session `warfactionid`, and station/structure join/leave broadcasts are implemented and unit-tested.
- `CreateCharacterWithDoll`: starter inventory, modules, skills and wallet persist; the new character's welcome mail/notification is durable before selection, and `GetCharacterSelectionData` reads the live unread-mail count.
- `SelectCharacterID`: named arguments, zero-argument `OnCharacterSelected` before session change, session state, station/structure/space selection, deferred docked ship-id and fitting bootstrap, first-space-List visual prime, ship/module Dogma hydration, and saved-pose space resume are covered by PostgreSQL and TCP headless tests. Entry failures restore the full session attribute snapshot (including deferred ship/bootstrap flags), cancel the new online reservation, and reenter previous hooks; the previous character remains reserved until entry succeeds. Registry regressions cover concurrent claims, commit, rollback, and disconnect. Graphical-client acceptance remains open.
- The observed post-selection bootstrap calls `config.GetBlackListedPlanets`, `ProjectDiscovery.is_enabled`, `charMgr.MachoResolveObject` for global assets container 10002, and `jumpCloneSvc.MachoResolveObject` have focused wire-shape tests. Paper-doll normalization has a regression test for missing sculpt weights and `SculptingRow` headers. Data-backed gaps remain listed above.
- `GetCharacterSelectionData`: full rows keep a renamed ship, report live skill-training projection and relation-backed security status, and derive the latest finished-skill count from history; same-FILETIME completion batches remain indistinguishable.
- `industryMonitor.ConnectJob`: inventory-flag filtering and blueprint-category exclusion are implemented and tested.
- `structureHangarViewMgr.GetMyHangarViewState`: existing persistence callback is connected.
- publicQaToolsServer.SlashCmd delegates to the existing slash service. CanGiveItemForMultifit returns false in EveJS and RS, so those responses are aligned.
- SignalTracker registration: Host callback and session registration are implemented; the snapshot producer remains open.
- `machoNet.SetGlobalConfigValue` (slice A, 2026-10-10): the write is saved in PostgreSQL (`zsystem.global_config`, `EVE_GLOBAL_CONFIG_SCHEMA`), read back at start-up, sent to connected clients as `OnGlobalConfigUpdated` (None for a removal), republished to the fronts for the next handshake, limited to ROLE_ADMIN or ROLE_SERVICE, and the bug-report server and Honeycomb key are protected. Tests: `GlobalConfigTests` (store, socket, PostgreSQL). Differences from EVE.js are in DEVIATIONS.md ("Handshake, machoNet and the global config").
- A failing handler on a non-strict host (slice B, B1): answered with the V24.01 wrapped `RuntimeError` under an error ID, not None; the host logs the exception under that ID. Tests: `HostServerTests`, `StrictRpcTests`, `DispatchGoldenTests`.
- Unknown service and unknown method on a non-strict host: answered with the V24.01 wrapped error (`eveexceptions.ServiceNotFound` / `exceptions.AttributeError`), not None (slice A). Tests: `HostServerTests`, `StrictRpcTests`, and the golden replays with `ErrorAnswers`. DEVIATIONS.md ("Unknown service and method").
- The login acknowledgement carries `access_token` and `computer_hash` (slice A; None for a password login). Test: `crates/eve-net/tests/handshake.rs`.
- The advertised service table is checked against the registry (slice A): every advertised name reaches a registered service, and the 23 names the reference advertises but eve.rs does not register are documented, not advertised. Test: `ServiceAdvertisementTests`.
- `tools/dev/run-eve-live.sh` exits non-zero when the host socket or the front port does not come up (slice A), with a hint when the host was in its market seed.
- `BeyonceService.CurrentFileTime`: the alleged compile failure does not reproduce on main; the FILETIME helper and Fleet tests/builds pass.

## Source files for the open provider checks

Re-read these files before implementing a provider; the Host composition root determines whether an optional callback is actually connected.

| TODO group | Current source to inspect |
|---|---|
| Global config and dynamic-item mutations | [MachoNetService.cs](../../dotnet/src/Eve.Host/MachoNetService.cs), [DynamicItemRpcService.cs](../../dotnet/src/Eve.Host/DynamicItemRpcService.cs), [Program.cs](../../dotnet/src/Eve.Host/Program.cs) |
| Insurgency, ESS, Abyssal readiness, and SignalTracker | [FactionActivityRpcServices.cs](../../dotnet/src/Eve.Services.Core/FactionActivityRpcServices.cs), [EssMgrService.cs](../../dotnet/src/Eve.Services.Space/EssMgrService.cs), [LoginInitializationRpcServices.cs](../../dotnet/src/Eve.Services.Core/LoginInitializationRpcServices.cs), [SignalTrackerRegistrationProvider.cs](../../dotnet/src/Eve.Host/SignalTrackerRegistrationProvider.cs) |
| POS, station guest notifications, and public QA | [PosMgrService.cs](../../dotnet/src/Eve.Services.Core/PosMgrService.cs), [StationService.cs](../../dotnet/src/Eve.Services.Station/StationService.cs), [StationGuestLedger.cs](../../dotnet/src/Eve.Services.Station/StationGuestLedger.cs), [UnportedFeatureRpcServices.cs](../../dotnet/src/Eve.Services.Core/UnportedFeatureRpcServices.cs), [Program.cs](../../dotnet/src/Eve.Host/Program.cs) |
| Structure operations and container/compression hooks | [StructureRpcServices.cs](../../dotnet/src/Eve.Services.Core/StructureRpcServices.cs), [StructureDataRpcServices.cs](../../dotnet/src/Eve.Services.Core/StructureDataRpcServices.cs), [BeyonceService.cs](../../dotnet/src/Eve.Services.Space/BeyonceService.cs), [Program.cs](../../dotnet/src/Eve.Host/Program.cs) |
| MCT, dungeon defaults, and extraction authorities | [UserSvcCompatibility.cs](../../dotnet/src/Eve.Services.Core/UserSvcCompatibility.cs), [StoreOffersService.cs](../../dotnet/src/Eve.Services.Core/StoreOffersService.cs), [DungeonRpcServices.cs](../../dotnet/src/Eve.Services.Core/DungeonRpcServices.cs), [GameRpcServices.cs](../../dotnet/src/Eve.Services.Core/GameRpcServices.cs), [CoreServices.cs](../../dotnet/src/Eve.Services.Core/CoreServices.cs) |

## Client preparation (tools/client-patch)

- **OPEN** — Real-client check of `tools/client-patch`: the patched `start.ini` (`server`, `cryptoPack = Placebo`) and the launch arguments have not been run against a V24.01 client. Verify that login reaches eve-net on port 26000.
- **OPEN** — Decide whether the unmodified client needs EveJS's `blue.dll` branch change (EveJS `Play.bat` refuses to launch without it; its purpose is not documented). If it is needed, record a deviation in `docs/reference/compat/DEVIATIONS.md`. Do not commit a patched binary.
- See [tools/client-patch/README.md](../../tools/client-patch/README.md) for the patch list, evidence and limits.

## Debugging tools

- `EVE_STRICT_RPC=1` (eve-host): an unknown service, an unknown method, a call with no service and an unhandled
  exception are answered with a Macho error carrying a correlation id, and logged with the stack trace to stderr. The
  default answers None, as the reference does. Not covered: services that answer None on missing callbacks by design
  (for example `StructureRpcServices`), and the unknown-method check trusts each service's `HasMethod`, which has not
  been audited against all hand-written services.
- `EVE_TRACE_RPC=1|full`: `full` adds each call's arguments and each result's type tree to the trace lines.
- `tools/mitm` captures and diffs the client's traffic against the reference. Rule and recipe:
  `docs/design/REFERENCE-TRACES.md`.

## 15. CI hygiene and the pre-push check (2026-10-09)

The primary check before a push is `tools/dev/check-all.sh`: the jobs of `.github/workflows/ci.yml` (push-triggered on `main` except documentation-only changes, with manual job selection) in the same order and environment, on a local PostgreSQL 18. `CHECK_SKIP=live,dotnet` leaves steps out.

| Status | Item | Where |
|---|---|---|
| DONE | `cargo fmt --all --check` clean, with the pinned toolchain 1.94.1 (17 eve-space files were unformatted, not 8) | `rustfmt.toml`; commit "Format the Rust workspace" |
| DONE | `cargo clippy --workspace --all-targets -- -D warnings` clean on 1.94.1 (no blanket `allow`) | `crates/eve-space` |
| DONE | `Sim::handle` split into `handle_session`, `handle_fitting`, `handle_command`, `handle_fleet`, `handle_world`; arms moved verbatim; golden fixtures unchanged | `crates/eve-space/src/sim.rs` |
| DONE | Rust job has the PostgreSQL service and `EVE_PG_URL` (postgres:// form); the .NET test step gets the Npgsql keyword string | `.github/workflows/ci.yml` |
| DONE | Live script sets `EVE_DEV_AUTO_CREATE=1`: the host refused every test login, as its header promises accounts are created | `tools/dev/run-eve-live.sh` |
| DONE | `.NET` build is warnings-as-errors already (`dotnet/Directory.Build.props`); CI does not exercised `dotnet format`, so it is not checked here | |
| FIXED | `test_call_request_wire_shape` expected `isBound` clear; direct calls set it since 4d289a7 | `tools/headless-client/tests/test_handshake_mock.py` |
| FIXED | Live E2E: the first order after an undock produced one extra `DoDestinyUpdate` after `OnInvulnCancelled` (the bump of `resolve_collisions`: `SetBallPosition`, `SetBallVelocity`, `SetSpeedFraction`, `Stop` or `GotoDirection`). Cause: the station bump of commit 153afc6. The undock protection, which spared the ship from its station, ended at the first accepted order while the ship was still inside the station sphere (the undock point lies in it: 0.34 of the radius in the recording), so the station pushed it out. Now a ship is spared by the station it is leaving until it is out of that station's sphere (`Pilot::leaving_station`). Verified: `tools/headless-client/tests/test_undock.py` (race-1, race-4) and `test_movement.py` (race-1) pass against eve.rs; `eve-space` tests including `a_ship_leaving_its_station_is_not_bumped_by_it_when_it_first_moves` | `crates/eve-space/src/scene/collision.rs`, `scene/docking.rs`, `scene/mod.rs`, `scene/tests.rs` |
| OBSERVED ONCE | Live run: the host's event pump reported `relation "live_<pid>_events.outbox" does not exist` at start (PostgreSQL log, one statement); the same schema was complete in the next run, and logins then failed only because of the auto-create flag. Not reproduced; watch for it | `dotnet/src/Eve.Events/EventBus.cs` (horizon query), `Eve.Data/Migrations.cs` |

Known failures in the .NET runs of 2026-10-09 (4,244 tests). The first full run had 4 failures, all on the baseline list; the final run had those 4 and 2 Safe-logoff tests:

| Test | Status |
|---|---|
| `Eve.Corporations.Tests` `Character_login_restores_direct_war_faction_then_corporation_faction` (CorpWarFaction) | fails |
| `Eve.Services.Tests` `SessionChangeTests.The_client_visible_set_is_the_reference_list` (SessionChange) | fails |
| `Eve.Services.Space.Tests` `When_a_session_ends_in_space_the_simulation_is_told_and_the_ship_is_put_back_in_the_hangar` | fails |
| ConfigPrime, SessionEndsHangar, capsule death | not failing in this run |
| Safe logoff: `A_movement_command_aborts_a_running_safe_logoff`, `Safe_logoff_closes_the_space_session_after_its_timer` and `The_module_orders_of_a_pilot_are_asked_one_at_a_time` (final run); `Aborting_safe_logoff_keeps_the_session_and_ship_in_space` (one rerun). Flaky: each passed when rerun alone | flaky |
| NpcBounty, Mutadaptive | not failing in these runs |

Rust: 1,272 tests passed, 0 failed. Python client: 1,066 passed, 97 skipped (the live tests need a server).

## 16. Module rules and audit remainders (track B, 2026-10-09)

Done (tests in the crates named): settle budget and stall guard, M-1 (`crates/eve-space` `modules`); warp, dock and module-activation locks from running
overlays (104, 2354, 1349), WP6 (`scene/warping.rs`, `scene/docking.rs`); the dock refusal notice, WP7 (`SpaceSessions.Movement.cs`); the module rules payload
(flag 32, protocol 33) with the group limit (763, FP-2), early deactivation (906, cloak), industrial core (3265, compressors stop with their core), reactivation
delay (669, generic), burnt-out modules (energizer), WP3, WP4, WP5 (in part), WP14; fixed 1014 repeat cap (FP-C2); tracking computer not hostile (FP-3); guidance
disruptor is an attack (FP-C6, Rust and host); the fixture generator carries the rules (`tools/dev/gen-simlink-fixtures.py`).

Track B2 (2026-10-09), done (tests in the crates named; source and mutation checks in DEVIATIONS "Module rules, track B2"): WG-1 entropic disintegrator stops off optimal (`scene`/`weapons`); WG-2 charge exemption pinned to the V24.01 client (`Eve.Dogma.Tests`); a stargate jump stops every running module, so a reactive armor hardener's profile resets (`scene/jump.rs`); a dock leaves no rack heat; FP-C4a overheat is Overloaded to dogma for a running module (`SpaceSessions.Modules.cs`); FP-10 tracking disruption reaches gunnery weapons only (protocol 35, `SimTurret.RequiresGunnery`, ADR 0013 epoch 35). FP-C3 closed: the V24.01 client reads 5804/5818 nowhere.

Open (each with its exact blocker):
- WG-3 module scripts: the tracking speed (29001) and optimal range (28999) scripts are verified with a loaded script (`ModuleScriptTests`, PostgreSQL). Sensor booster and ECCM (group 910, 41155), guidance and flex hardener (armour resistance scripts, group 1701): not started; each needs its own SDE-backed case.
- WG-4 smartbomb near gates and stations: not built; no sourced rule (wiki distances carry a [Verify] mark; EVE.js and the client have no check).
- WG-5 nanite paste while overheated: not built; no refusal text in EVE.js or the V24.01 client.
- WG-6 safety-colour gating of remote assistance and WG-7 jam weapons timer: not started; both need the Crimewatch assistance facts and the timer code, and WG-7 follows WG-6.
- WG-8 fitting restrictions on the server: not started (low priority).
- FP-8 target resistances for ewar and assistance (2112 to 2116, 2135): not started; needs `resist` fields on the ship's sensors block and on each `Remote` (a second wire change, not in this bump).
- FP-C4b and FP-C5 (refresh running effects when overheat or a computer changes): not started; needs a new `SimUpdateEffect` message (wire).
- FP-C7 ECM burst aggression and its offensive gate: DONE (track B6a, protocol 46, section 24).
- FP-C8 automatic reload from the hold: not started. EVE.js queues the reload when the last round is spent (`localCycleRuntime.js`) and resumes at the next cycle; eve.rs would need the host to reload and re-activate and to suppress the no-charges error that the simulation sends now.
- Cloak types (covert, prototype, non-warp cloak states): not started.
- M-6 scanner, analyzer and relic modules: not started (Cargo and Ship Scanner results, Data Analyzer and Subverter bodies).
- M-3 leftovers outside track A: not started.
- WP13 mode events (MR-D2): stays deferred. No subscriber exists in the reference, and a subscriber with no consumer is the no-op AGENTS rule 5 forbids.
- WP16 bastion micro jump drive block: needs the client's modifier 5788 to 6658 mapping (MR-D7), which the decisions brief marks as a client-derived table needing sign-off; not built.
- WP17 stacking: the penalty curve, operations and unpenalised categories match the V24.01 client (`eve-logic dogma/attributes/stackingNurfedAttribute.py`, `const.py`); the SDE `stackable=false` flag selects the penalty (`DogmaWorld.ApplyModifiers`). The assistance and jam overlays are stackable=true in the SDE (2112 to 2424, 2253), so their modifiers multiply unpenalised (`itemAttributeFactory.py:35`). The sim-side overlays themselves are not built.
- WP19 velocity limits (1334, 2608): the bomb launcher (2608) is refused outside its band and the refusal is tested (`deployable::tests::a_bomb_launcher_refuses_a_ship_at_rest_and_releases_its_bomb_once_the_ship_moves`). The cynosural field generator (1334) has a body since track B7 (section 22); its band is reached through the generic velocity rule.
- 906 eject and logoff, 1934, WP12: 1934 is the SDE text (default 1: an offensive module deactivates when its pilot disconnects). The path is `OnSessionClosedAsync` (`SpaceSessions.cs:820`) to `_sim.Leave(LoggedOff)` to `Scene::leave` (`crates/eve-space/src/scene/docking.rs:64`), which drops the pilot and its running effects (`modules::tests::a_ship_that_leaves_takes_its_effects_with_it`). Open: a bastion (1934 = 0) keeps running after its pilot logs off in CCP; eve.rs has no logged-off ship left in space to keep it running. The 906 log-out timer is not built.
- Reactivation refusal and MAX_GROUP_ACTIVE: the refusal does not carry the time since the stop or the running count yet (the client's `ModuleReactivationDelayed2` and `EffectCrowdedOut` need them).
- Reactive armor hardener: restart clears the profile (in memory, by design); death and gate jump are covered (section 8b). A host-level test of a jump with a running module is not in this change.
- Protocol: main is at 34 (the bombs and bubbles track, `mod-a`); this track's bump is 35 (ADR 0013 epoch 35). Re-check `main` before a push.

## 17. Module rules track B3 (2026-10-09)

Done (decisions and tests in DEVIATIONS "Module rules, track B3"): WG-4 smartbomb refused within the larger of 5 km and its range of a station or stargate (`crates/eve-space` `modules/activation.rs`, host notice in `ModuleRefusals.cs`); WG-6 safety-colour policy pinned by a test (the Crimewatch policy already applied it); the bastion micro jump drive block, WP16 (`BuiltInEffects.ClientModifiers`, the loader in `DogmaStatic.cs`, the host refusal `MODULE_BLOCKED` in `SpaceSessions.Modules.cs`); cloak types verified present.

Decided, not applied: WG-7 (no source says a jam gives the target a weapons timer; EveJS starts none); WG-8 (no fitting restriction in the SDE for the heavy assault launchers); the 906 eject rule (eve.rs has no eject command).

Open, each with its exact blocker:
- WG-5 nanite paste repair: the client's `godma.RepairModule` and `StopRepairModule` have no host handler; paste repair, its clock, its cancellation on a jump and the overheat interlock need building. Heat repair on a dock is done (B2).
- WG-3 module scripts: PostgreSQL cases per script family are in section 22 (track B7); the disruption scripts are open there.
- FP-8, FP-C4b, FP-C5, FP-C7: each needs a wire change (resist fields, `SimUpdateEffect`, a burst's jammed ships).

- FP-8, FP-C4b, FP-C5: each needs a wire change (resist fields, `SimUpdateEffect`). FP-C7 is closed (section 24).
- FP-C8 automatic reload: the simulation ends the effect at the last round (`NO_CHARGES`) before the host sees the spent charge; a pending-reload state is needed in the simulation.
- M-6 scanner, analyzer and relic modules: no simulation bodies.
- WP17 assistance and jam overlays (the sim-side overlays are not built); WP19 cynosural field (the body is built in track B7, section 22, with open items there); WP13 mode events (no subscriber, so none is published).
- Host-level database tests for the smartbomb refusal and the bastion refusal are not written (unit and simulation tests only).

## 18. Alliance registry (2026-10-10)

The client-facing allianceRegistry paths used by V24.01 alliance wrappers now persist bulletins, executor support, member departure, alliance war bills, bill payment and GML application acceptance. Member-affiliation, alliance and corporation notifications are emitted after the database transaction. alliance_employment records a null-alliance row when a corporation leaves so GetEmploymentRecord retains the transition.

Open before calling Alliance parity complete:

- Maintenance invoices, executor-wallet auto-payment, paid-cycle renewal and bill-issued notifications are implemented. Unpaid-alliance closure and last-member closure now run at daily downtime; they snapshot members, close applications, release corporation/character affiliations, drop SOV claims, inherit active wars to member corporations and end source Alliance wars. Confirmed by `Unpaid_alliance_maintenance_closes_at_downtime_and_releases_member_affiliations`, `Last_alliance_member_leaves_but_closure_waits_for_next_downtime`, `Unpaid_alliance_closure_inherits_current_wars_before_ending_the_alliance_wars`, and `Closing_an_alliance_drops_only_its_sovereignty_claims_and_hub_projection`. Sources: CCP [Corporation/Alliance Closure](https://support.eveonline.com/hc/en-us/articles/203209062-Corporation-Alliance-Closure) and [Mutual War](https://support.eveonline.com/hc/en-us/articles/360005659379-Mutual-War); EveJS v0.12.9 commit `828823a75f211fdecb945b087a26c225f111491f`, `allianceMaintenanceBilling.js` (273-475), `allianceDepartureState.js` (588-688, 1156-1436), `allianceWarInheritance.js` (108-434), `allianceSovereigntyLifecycle.js` (1098-1346).
- Recurring type-4 war bills now use transactionally scheduled 7-day due jobs, debtor auto-pay (executor wallet for Alliance bills), prior-due renewal, and durable default/inactive/orphaned/superseded processing. Unpaid wars carry `reasonEnded=10` through the official 24-hour end window before relation expiry. PostgreSQL coverage: `A_departing_alliance_member_inherits_the_active_war_and_processes_due_war_bills`. Sources: CCP [Wars](https://support.eveonline.com/hc/en-us/articles/115004152745-Wars); EveJS v0.12.9 commit `828823a75f211fdecb945b087a26c225f111491f`, `warRuntimeState.js` (217-235, 974-1064).
- Intentional EveJS difference: unpaid war bills do not create its 14-day `PEACE_REASON_UNPAID_BILL` treaty. CCP's [Wars](https://support.eveonline.com/hc/en-us/articles/115004152745-Wars) rules allow re-declaration once the 24-hour cooldown ends; the 14-day restriction applies to surrender or War HQ loss. V24.01 `eve/client/script/ui/shared/neocom/corporation/war/warDeclare.py` disables normal war targets with outgoing treaty rows, so copying EveJS here would also disable the declaration UI.
- When a corporation leaves an otherwise active alliance, its SOV system and core TCU/IHub ownership now transfers to the active executor through a durable job scheduled in the departure transaction. Verified by `Alliance_departure_schedules_sovereignty_transfer_to_the_active_executor`, `Alliance_departure_transfer_still_runs_if_leaver_joins_a_different_alliance_before_job`, `Alliance_departure_transfer_waits_until_the_suspended_alliance_has_an_executor`, and Host persistence test `Leaving_corporation_sovereignty_and_core_structure_owners_move_to_the_executor`. Reference: EveJS v0.12.9 commit `828823a75f211fdecb945b087a26c225f111491f`, `allianceSovereigntyLifecycle.js` (387-471, 753-1063).
- `GetAllianceSovereigntyStructuresInfo` returns the alliance TCU/IHub rows and campaign scores from the SOV read model. V24.01 client evidence: `Fokiiiiiii/eve-logic` commit `1dbb5711a072a146e1d228365668ac05655eefe1`, `eve/client/script/ui/services/sovSvc.py` (332-371) and `corporationWindow.py` (402-414). EveJS v0.12.9 commit `828823a75f211fdecb945b087a26c225f111491f`, `allianceRegistryRuntime.js` (1082-1087) and `sovState.js` (1109-1404). PostgreSQL test: `Alliance_sov_and_owner_queries_return_rows_from_their_authoritative_sources`.
- Alliance `GetEveOwners` returns requested character, corporation, alliance, NPC-character and faction owner rows from persisted stores and the matching SDE. EveJS v0.12.9 commit `828823a75f211fdecb945b087a26c225f111491f`, `allianceRegistryRuntime.js` (1222-1236). The direct Alliance-side V24.01 callsite is not checked; the observed corporation owner-row consumer is `Fokiiiiiii/eve-logic` commit `1dbb5711a072a146e1d228365668ac05655eefe1`, `eve/client/script/ui/services/corporation/bco_members.py` (134-143).
- Real graphical-client verification remains unrun.

## 19. Module rules track B4 (2026-10-09)

Done (decisions in DEVIATIONS "Module rules, track B4"; tests in the projects named): WG-5 nanite paste repair end to end (`dotnet/src/Eve.Ships/CharacterShips.Repair.cs`, `Eve.Services.Space/SpaceSessions.Repair.cs`, `Eve.Services.Dogma/DogmaIMService.Repair.cs`; tests `RepairTests`, `ModuleRepairHostTests`); the 906 eject refusal (`SpaceSessions.Ships.cs`); WP13 mode events (`ShipModeTopics`, `ShipModeEventTests`); FP-8 target resistance, host-side (`Effects/RemoteResistance.cs`, `RemoteResistanceTests`); WG-3 sensor booster strengths (`SensorBoosterEffect.cs`, `ModuleEffectsTests`); WG-7 and WG-8 decided (DEVIATIONS). The protocol is not bumped in this track.

Open, each with its exact blocker:
- WG-5: a repair ended by a leave takes its paste and damage, but the client is not told the cargo change then (no notification path from the space layer to the client's cargo for a ship that has left). The rate and cost come from the character's dogma world; only the no-skill values are tested.
- WG-3: the tracking and optimal range scripts are verified with a loaded script (`ModuleScriptTests`, through the charge pairing in `Eve.Dogma/DogmaWorld.cs`, `otherID`). Guidance, flex hardener and ECCM are verified with a loaded script in track B7 (section 22).
- FP-C4b and FP-C5 (refresh a running effect when the overheat or a computer changes): not built. Closed in track B5 (section 20). Needs a new `SimUpdateEffect` message: `crates/eve-space/src/link`, `SimCodec`, the Python generator `tools/dev/gen-simlink-fixtures.py`, and one protocol bump at the next free number (37 if track C2 takes 36).
- FP-C7 (an ECM burst: the ships it jams, and its offensive gate): closed in track B6a (section 24).
- FP-C8 (automatic reload from the hold): not built. Closed in track B5 (section 20). The simulation ends the run at the last round with `NO_CHARGES` (`crates/eve-space/src/modules/effects.rs` around 613) before the host sees `ChargeSpent`. The design: a pending-reload state that waits for the next cycle boundary for an `AdjustCharge` from the host (the message exists, `SimAdjustCharge`), and resumes if one arrives. Needs the simulation change and host tests.
- M-6 (Cargo Scanner, Ship Scanner, Data Analyzer, Subverter, relics): not built. The reference is EVE.js `server/src/space/modules/scannerModuleRuntime.js` (ranges, rows, and the client notifications `OnCargoScanComplete` and `OnShipScanCompleted`); none of the simulation bodies exist.
- WP17 (assistance and jam overlays): the resistance part is FP-8. The stacking rule is the SDE stackable flag (all overlay attributes are stackable=true, so no penalty; client `itemAttributeFactory.py:35`). EveJS gives one resistance per remote (`hostileModuleRuntime.js`, `assistanceModuleRuntime.js`).
- WP19 (velocity limits 1334 and 2608): the rule and the simulation check are in track B5 (section 20). The bomb launcher has a body (`BombEffect`) and its band is tested; the cynosural field generator has no body.
- WP12 and 1934 (logout and the 906 log-out timer): the disconnect path is located (TODO line 388); the bastion continuation (1934 = 0) and the 906 timer are not built.
- Host PG tests for the sensor booster strengths in a fitted scene: the effect is tested through `ModulePackages` only.

## 20. Module rules track B5 (2026-10-09)

Done (decisions in DEVIATIONS "Module rules, track B5"; wire in ADR 0013 epoch 38, protocol 37 to 38 in one bump; track C3 took 37):
- FP-C4b and FP-C5 (refresh a running effect when an overheat or a computer changes): `SimUpdateEffect` (kind 61), applied by the simulation at the start of the effect's next cycle; the host rebuilds each running effect with `ModulePackages.Of` after overheat, activation, deactivation, offline and a simulation end, and sends it only when the numbers differ (`crates/eve-space/src/modules/cycle.rs` `refresh_running`, `dotnet/src/Eve.Services.Space/SpaceSessions.Refresh.cs`). Tests: `a_refresh_of_a_running_effect_takes_effect_at_the_start_of_its_next_cycle`, `RefreshHostTests`, the codec fixtures. The computer path shares the activation refresh and has no test yet.
- FP-C8 (automatic reload from the hold): the simulation parks a reloading turret when its rounds run out (`Charge.reloads`, `crates/eve-space/src/modules/activation.rs` `park_for_reload`, `adjust_charge`); the host loads the kind from the hold when a gun runs dry (`GunRanDry`, `DogmaIMService.Reloads.cs` `ReloadEmptiedGunAsync`) and always answers the simulation. Tests: `a_turret_that_reloads_waits_for_its_load_and_resumes_with_it_at_the_next_cycle`, `a_reloading_gun_that_is_switched_off_while_it_waits_stops_at_once`, and `ReloadServiceTests` (two PostgreSQL cases). Client reload window: the in-space load's notices, not checked against the client. Track B8: the host reloads from the hold only for the accounts named in `EVE_AUTO_RELOAD_ACCOUNTS` (a bot with no client UI); a client reloads its own gun (`shipmodulebutton.py`), and its gun stops with `NoCharges` when the rounds are spent (DEVIATIONS "Module rules, track B8").
- WP19 (velocity limits 1334 and 2608): the rule fields are on the wire (protocol 38) and the simulation refuses an activation outside the band (`velocity_refusal`). The bomb launcher reaches it (tested); the cynosural field generator does not (no body).

Open, each with its exact blocker:
- FP-C7: closed in track B6a (section 24).
- M-6 (Cargo Scanner, Ship Scanner, Data Analyzer, Subverter, relics): no simulation bodies; EVE.js `scannerModuleRuntime.js` is the reference and was not ported in this track.
- WP17 (stacking of overlapping assistance and jam modifiers): answered by the SDE stackable flag (all overlay attributes are stackable=true, so no penalty; client `itemAttributeFactory.py:35`).
- WG-3 (scripts and charges with a loaded script): tracking and optimal range have PostgreSQL cases (`ModuleScriptTests`); flex, ECCM and guidance have none yet.
- WP12 and 1934 (`deactivateIfOffensive`, SDE default 1): located. `OnSessionClosedAsync` (`SpaceSessions.cs:820`) calls `_sim.Leave(LoggedOff)`, and `Scene::leave` (`scene/docking.rs:64`) drops the pilot, so default-1934 modules stop. A bastion (1934 = 0) is not kept running after logoff (open, DEVIATIONS).
- Host PostgreSQL run (the Safe logoff refusal): closed in track B6a (section 24).
- FP-C8 live check (track B8): `tools/headless-client/tests/test_reload.py` replays the recorded reference against a live host (`EVE_LIVE_HOST`, `EVE_LIVE_SLOW=1`, about three minutes); not exercised in B8, so the client-side stop is covered by the host tests only.

## 21. Scanner modules (track M-6, 2026-10-09)

Done (decisions in DEVIATIONS "Scanner modules, track M-6"; wire in ADR 0013 epoch 40, protocol 39 to 40 in one bump; track C3 took 39):
- Cargo Scanner (effect 47) and Ship Scanner (effect 46). Ranges and falloff come from the SDE effect records (cargo 126 with no falloff; ship 125 with the
  falloff 510). One cycle (attribute 1014). The simulation decides the lock and the reach at activation and at the cycle's end (`crates/eve-space/src/modules/`,
  `Action::Scan`). The host settles the reveal through the one work-cycle dispatcher (`dotnet/src/Eve.Services.Space/SpaceContainers.Scan.cs`, rules in
  `Scanning/ScanRows.cs`). `OnCargoScanComplete` and `OnShipScanCompleted` go to the pilot alone, once per granted cycle (a repeat is answered from the record).
  This is the cargo and ship part of the wiring gap E-08.
- Refusals by name: `TARGET_REQUIRED`, `TARGET_SELF` (`DeniedTargetSelf`), `TARGET_NOT_FOUND`, `TARGET_NOT_LOCKED`, `TARGET_OUT_OF_RANGE` (the client's
  `TargetNotWithinRangeGeneric` with the reach), `UNSUPPORTED_EFFECT` for a scanner with no range, and `That ship is resistant to your scan.` when the target's
  scan resistance (188, 511) takes the cycle.
- Tests: sim `modules::tests` (the six `a_scan*` and `a_scanner*` tests), simlink fixtures (two valid scans, two invalid scan frames, an unknown work command),
  `ScanRowsTests` and `ModuleEffectsTests` (the scanners' build), PostgreSQL `ScannerHostTests` (cargo, ship, refusals and replay).

Open, each with its blocker:
- Data Analyzer (`doHacking`, effect 1738), Relic Analyzer (same effect) and Data Subverter (`hackOrbital`, effect 4773): not started. The reference is EVE.js
  `moduleOperation.js` (the analyzer section) and the hacking window of the client; the minigame and the loot container interior are the only named refusal
  allowed for them, and neither is written. The EVE University wiki "Hacking" page gives the analyzer numbers (base virus coherence 40 and strength 20 for tier 1,
  60 and 30 for tier 2; Hacking and Archaeology add 10 coherence per level; the minigame's failure penalties). It does not give the range, the cycle, the activation
  or the Data Subverter: those stay on the SDE attributes (54, 73, 902, 1909 to 1911). The body is blocked: eve.rs has no hackable site entity (a materialised
  container with a hacking state) for the target to be validated against.
- Survey Scanner: not part of this runtime. The reference's scanner module runtime has no survey effect; `surveyScan` belongs to the mining chipsets (section 7).
- Resistance at activation: EVE.js refuses the start before a cycle is spent. The host reads the target's resistance at the cycle's end, so the eve.rs scanner
  spends one cycle. A pre-activation check needs the target's resistance in the activation request (a wire field).
- NPC targets: a target needs an item record for its hold and fittings. NPC ships have none, so they are refused as `NO_TARGET`.
- Probe lifecycle notifications (E-06: `OnNewProbe`, `OnProbeWarpStart` and the rest): built through the scan and recovery paths in sections 24 and 29; graphical-client check remains open.
- Client check: not exercised. The MITM recipe (tools/mitm/README.md) needs a live EVE.js run and a headless scenario that scripts a scanner; neither exists.

## 20. Track C4: command bursts, buff registry, P.A.N.I.C. (2026-10-09): historical specification, superseded by C5–C8

This section records the evidence and initial correction. Its 2026-10-09 open-item snapshot was superseded by section 22 (C6) and C8; current state and remaining checks are recorded there and in DEVIATIONS, tracks C4–C8.

- The earlier zero-value claim read `warfareBuffNValue` (2469 etc.) without tracing effect 6737 through charge multipliers 2596–2599 and module dogma inputs. Q-06 was superseded on 2026-10-19 by the owner direction to build command bursts. The value chain is covered by `CommandBurstValueTests`; implementation is recorded in sections 21 and 22 and DEVIATIONS, tracks C4–C8.
- BLOCKED (wire, one batch, one bump at the next free epoch): a burst action carrying the fleet's recipient list per cycle and the buff set; a live-numbers message for running recipient modules; the codecs, `tools/dev/gen-simlink-fixtures.py` and the fixtures.
- NOT BUILT: the buff registry (persistent source list on `DogmaWorld` items, expiry by the scheduler, restart drops it, client `OnDogmaAttributeChanged` for the recipients). Built with its first consumer, the burst.
- NOT BUILT: P.A.N.I.C. (42522). Its fleet effect needs the registry, the wire batch, and the simulation state for shield resistance, shield recharge, drone damage, weapons, mass, scan resolution, entosis and jump/dock/tether flags.
- not exercised: a MITM capture of a burst or P.A.N.I.C. cycle (`tools/mitm/README.md`). No scenario was driven.

## 22. Module rules track B7: ECCM, scripts, the cynosural field generator (2026-10-09)

Done (decisions and sources in DEVIATIONS "Module rules, track B7"; ADR 0017 for the wire):
- WG-3 ECCM, self-applied (effect 2231): runs through the dogma modifiers (`DogmaModifierEffect`); the projected ECCM stays pending. Tests: `ModuleScriptTests` (ECCM Script on ECCM - Radar I, category 7 with its category 8 script).
- WG-3 flex armour and shield scripts (41517 to 41524): PostgreSQL cases in `ModuleScriptTests` (the hardener's bonus and the ship's resonance it sets).
- WG-3 guidance scripts (Missile Range and Missile Precision, 35794 and 35795) on the Missile Guidance Computer: PostgreSQL cases in `ModuleScriptTests`.
- The cynosural field generator (21096, 28646, 52694) has a body: `CynoGeneratorEffect` (host) and `SimAction.CynoField` (tag 27, protocol 41). The field is released at the end of the cycle, lives explosionDelay (600000 ms), costs the cycle's fuel through the shared mechanism, and is announced with `AddBalls2` and removed with `RemoveBalls`. Tests: `deployable::tests::a_cynosural_field_is_lit_...` (sim), the golden entry in `messages.json` (codec), `CynoFieldHostTests` (fleet and high-security refusals, PostgreSQL).
- Refusals: a pilot outside a fleet (`CYNO_NOT_IN_FLEET`, the host's fleet store through `SpaceSessions.IsInFleetAsync`), a fleet member in high-security space (`CantInHighSecSpace`). The generic 500 m/s rule (1334) applies.

Open, each with its exact blocker:
- Jump-in restrictions (normal fields to fleet members with jump drives, covert to Black Ops, industrial to jump freighters and capital industrial ships): no capital jump drive in eve.rs.
- Mobile Cynosural Inhibitor (200 km lighting block): no inhibitor body.
- Cynosural System Jammer (E-03): the host callback `BeyonceService.CynoJammerOnlineSimTimeForSystem` is not assigned, so the jammed state has no source.
- Fleet beacon state when a field is lit (EveJS `setFleetCynoBeaconState`): no beacon state in eve.rs.
- Persistence (ADR 0012): the field is a sim-timed deployable, so a restart drops it; the `Deployed` record and the sweep are not built.
- The positive lighting path has no host PostgreSQL test: the rig's station is in Manifest (high-sec), and no low-sec undock is in the rig.
- The Guidance Disruption scripts (40334, 40335): not tested; their launcher group is the computer's (1396) but the computer's charge group is 1400, so the pairing is not decided.
- The projected ECCM (2246): pending.
- WP12 bastion and 1934 (keep running after logoff): not built. The wiki page says nothing about logoff; the SDE sets 1934 = 0. Needs a logged-off ship that stays in space.
- WP17 sim-side assistance and jam overlays: not started; the part to build is not settled in the sources read.
- E-06 probe lifecycle notifications: built with `LaunchProbes` in section 24 and scan/recovery in section 29; graphical-client verification remains open.
## 21. Track C5: command bursts and the buff registry (2026-10-09): built

The dogma library and recipient rule were implemented in C5. Section 22 supersedes the remaining C5 open items with the end-to-end implementation; C8 records persistence across entry and restart. The C4 reading of zero was wrong: effect 6737 derives strength from charge multiplier 2596 and module value 2469. Sources and choices are in DEVIATIONS, tracks C4–C8.

DONE (dogma library and the host's recipient rule, tested on the real SDE where the data is):
- Burst strength from the SDE: `CommandBurstValueTests` (Tech I Shield Harmonizing -8, Tech II -10; the shield resistance buff scales the ship's resonances by 0.9 and runs out at 60 s).
- The buff collections (`sde.dbuffCollections`) load as `BuffCollection` (operation, aggregate mode, item, group and skill modifiers).
- `BuffRegistry`: timed inputs per item and collection; the strongest (Maximum) or weakest (Minimum) active input; active up to and including the expiry; a refresh is a new input; prune; forget; restart drops everything. EVE.js rules. `BuffRegistryTests` (8: aggregation, expiry, invalid input, restart, sync, the stacking penalty).
- `DogmaWorld.SetBuffs`: an item's buffs run as passive effects with their own values (`Modifier.Literal`); a buff's stacking penalty follows its attribute, as EVE.js does.
- The per-cycle recipients, `BurstRecipients.Resolve`: the source, fleet or affinity members in range (surface distance plus one metre), at most 256 ships with the source among them (wiki). `BurstRecipientsTests` (8).

The open items in this C5 checkpoint were superseded by C6/C8; current implementation and remaining checks are in section 22.

## 22. Track C6: command bursts end to end, P.A.N.I.C. (2026-10-09)

Supersedes the OPEN items of section 21. Sources and choices: DEVIATIONS, "Command bursts end to end and P.A.N.I.C. (track C6, 2026-10-09)". Wire: protocol 43 (ADR 0013, epoch 42).

DONE (each has tests; the PostgreSQL-backed ones are listed as run under the shared lock in the track's report):
- The burst's activation and cycle: `CommandBurstEffect` for the five families and the expedition bursts (`Charged`, one round a cycle; no charge is `NO_AMMO`). At each cycle's end the simulation asks the host with the ships it found in reach (`WORK_BURST`, the recipients on `ModuleWorkCycle`) and the SDE charge type, retained through charge consumption (simlink protocol 81). The host keeps the source's fleet, caps at 256, applies the buffs through the registry for the buff duration, sends each granted ship its numbers and running modules, and tells its pilot its state (`BuffState`, the simulation's `OnDbuffUpdated`). When the final round was consumed before settlement, Host adds the carried charge to the settlement's local dogma world so effect 6737 can resolve the charge-derived profile. The sweep ends buffs that ran out and tells their pilots. Tests: `modules::tests` (burst cycle, reach, refusal), `CommandBurstHostTests`, `CommandBurstFamiliesTests` (SDE: Armor, Information, Mining Foreman, Shield, Skirmish, Expedition, named and presidential), `BuffRegistryTests`, the fixtures (`simlink_fixtures`, `SimCodecTests`).
- P.A.N.I.C. (42522): the start needs a locked asteroid (the simulation: `TARGET_REQUIRED`) and parks until the host grants the fleet (`WORK_PANIC`, the ships within 200 km). The grant gives the source and the subcapital mining and hauling ships of its fleet the twelve SDE collections with the client's values, for the core's duration. The numbers reach the simulation through the ship (resonances, recharge, mass, scan resolution, warp speed). Refused by the host: warp (`WARP_SCRAMBLED`), docking, jump, and the cloak (existing check). Running modules that the buffs block are stopped. Tests: `modules::tests` (four P.A.N.I.C. tests), `CommandBurstHostTests` (grant, group, not running, SDE collections), the fixtures.
- Found on the way and fixed here: a pending start was marked active only after the simulation's answer, which a parked start gives only once it is granted, so the host's own check of a fuel ask or a grant saw the module as not running. The module is now marked while the start is pending and unmarked on refusal. The Rust decoder of module work cycles accepted only commands 7 to 9, refusing the scans (10, 11) of protocol 40; its set is now the C# codec's (7 to 13).

C8 implementation and remaining checks:
- Login and re-entry (C8, DEVIATIONS "Track C8"): done. The buffs are kept in `space.ship_buffs` and restored on undock, login in space and a gate jump; the pilot is told the state on each entry. Tests: `BuffRecordHostTests`, `BuffRegistryTests`.
- Drone damage (C8): done. Attribute 2580 is read through SDE effect 6789 (the Drones skill for a bonus; a penalty for every pilot, the wiki), sent as `SimShip.DroneDamageMultiplier` (protocol 47). Tests: `drones/tests.rs`, `IndustrialInvulnerabilityTests`.
- Velocity (C8): done. The wiki says "unable to move"; the collection now moves the current top speed (maxVelocity 37) at -100 %, not the deprecated 3171 (DEVIATIONS "Track C8"). Test: `IndustrialInvulnerabilityTests`.
- The jump refusal's words ("You cannot jump at this time.") are ours: the client's notice is not in the tables read.
- Tether (57) and entosis (61) are applied; eve.rs has no tether or entosis use. The SCARAB Breacher bypass does not apply (no such pods in eve.rs).
- No separate state for the buffs in the simulation (the request for "general status effects" in the simulation is not built): the numbers and the host's checks carry them (DEVIATIONS).
- Recipient attribute mutation (C8): dogmaIM's fresh ship world now synchronizes the ship's active buff registry before building `GetAllInfo`; a buff change also sends current ship and fitted-module attributes through `OnModuleAttributeChanges`. `BuffRegistryTests` covers SDE modifier scopes and a fitted item's location modifier. `CommandBurstHostTests` covers a fleet recipient's changed resonance and its return after expiry.
- Burst strength modifiers: effect 6737 applies the charge multiplier; effects 6776, 6777 and 6780 apply the level-V Armor, Shield and Mining strength skills. `ModuleBonusTests.Armor_shield_and_mining_burst_strength_uses_the_level_five_sde_skill_bonus` expects -12, -12 and +60 from pinned SDE values. Existing tests cover command-destroyer and command-ship hull modifiers; no T3 subsystem modifier for burst strength occurs in this SDE. The two-pilot live walk checks fleet delivery through `OnModuleAttributeChanges` and `GetAllInfo`, then confirms the shield resonance returns after expiry. It waits for the receiver's `OnTarget` event and follows the target before activation. The first traced walk exposed the consumed-charge settlement gap, fixed by carrying the SDE charge type on simlink protocol 81. Run 45 confirmed the receiver changed from 0.92 to 0.844928 and emitted both dogma and buff updates; its assertion still expected 0.92 × 0.92. The live assertion now uses the SDE-derived -8.16% from Bifrost at Command Destroyers I and searches nested Destiny actions. Run 46’s live test step passed the corrected expectation and expiry check; graphical-client verification remains open.
- Existing burst inputs continue until expiry if the source stops or the recipient leaves range, following CCP's Command Bursts devblog (DEVIATIONS, Track C5/C8); docking or a system change clears the ship's buffs.
- MITM (C8): not driven; no graphical client check is recorded.
- The burst wiki pages were not re-read in this track (the rules are those C4 and C5 recorded); the Capital Ship Modules page was re-read for P.A.N.I.C.
## 23. Track A5: OnDotVictimUpdated, the cynosural field record, probes, cyno jammer, jump drive and mobile objects (2026-10-09)

Done (tests in the crates named; decisions in DEVIATIONS "Track A5"):
- The Python `set` in both marshal codecs: `Value::python_set` (`crates/eve-marshal`) and `PyValue.PythonSet` (`Eve.Marshal`); `InventoryWire.Set` delegates. Round-trip tests in both.
- OnDotVictimUpdated (A4's open item): the simulation tells a ship its breacher applications as a destiny action on each hit, on each change of the working application and on each end (`crates/eve-space/src/dot_weapons.rs`, `dot_victim_update`). Tests: `dot_weapons/tests.rs`. The client was not exercised.
- The cynosural field record (ADR 0012, kind `cyno_field`): the simulation says a field was lit (`Deployed`, kind 63), the host keeps a row in `space.deployed` (migration 0004), the expiry sweep takes the field out and forgets the row (`RemoveDeployed`, kind 64), and a ship coming into the system puts a field back with its id and what is left of its life (`crates/eve-space/src/deployable.rs` `restore_field`, `remove_field`; `dotnet/src/Eve.Services.Space/SpaceContainers.Deployed.cs`). Protocol 44 (track C6 took 43). Tests: `deployable/tests.rs` (three), `CynoFieldRecordHostTests` (PostgreSQL), codec fixtures.

Open, each with its exact blocker:
- LaunchProbes (RC-7, E-06, section 24): built; Host launch, probe records, simulation placement, scan, recall and expiry are covered by `ScanProbeHostTests`, simulation tests and codec fixtures. The eve_p3 launch check is recorded in section 24. Graphical-client verification remains open.
- GetCynoJammerState (E-03): the host callback stays unassigned, so the answer is None (no jammer), correct while no jammer exists. A source would be the Tenebrex cyno suppression (EveJS `services/sovereignty/sovSuppressionState.js` `getCynoJammerOnlineSimTime`, line 259) or the Mobile Cynosural Inhibitor. Blocker: neither is built.
- WP-8 mobile warp disruptors: not started. The record store exists (`space.deployed`), so a new kind is the store side. Blockers: ADR 0012's addendum says the SDE has no lifetime for them and the EveJS table is not checked; the anchoring job `space.deployed.anchor`, the custody path from cargo, the item-level anchorDrop activation and the scramble source are not built.
- Capital jump drive, cyno targeting, jump range and fuel, and the jump to the cyno field: not started. The field exists (B7, persisted by this track), so the jump can target it. Blocker: the jump-drive module body and the cross-system jump still to build; the restrictions follow the drive.
- Breacher pods (A4): the per-tick damage message (`OnDamageMessage`) has no wire shape in the sources read.
- Known: `IndustrialInvulnerabilityTests` and `IndustrialInvulnerabilityHostTests` (`Eve.Services.Space.Tests`) expect the Nexus core's `EFFECT_NOT_IMPLEMENTED` refusal. C6 (`ee324cc`) gave it a body and left those two tests stale on origin/main; they fail there too. Not changed by track A5.

## 24. Track A5: scanner probes, launch and recall (2026-10-10)

Done (tests in `ScanProbeHostTests`, the simulation tests in `deployable/tests.rs`, codec fixtures; decisions in DEVIATIONS "Track A5: scanner probes"): `dogmaIM.LaunchProbes`. `The_pilot_controls_at_most_eight_probes_and_a_launcher_with_too_few_probes_refuses` passed on `eve_p3` on 2026-10-19.
with the reference's checks and messages; eight active probes per pilot per system; probes put in space on a ring of ten kilometres (`PlaceProbe`, protocol 45); the
charge stack spent; `OnNewProbe` told to the pilot; recall through `scanMgr.DestroyProbe` (`OnRemoveProbe`); expiry at `space.probes.sweep`.

Open, each with its blocker removed by the next step:
- DONE (restart): the probe records survive a restart and their balls are put back in space when a ship comes into the system with the same ids, places and remaining life (`SpaceProbes.RestoreAsync`, `ScanProbeHostTests`). Not tested with a real restart of the process.
- Scanning: built in track A7 (section 29): `RequestScans`, `OnSystemScanStarted`, `OnSystemScanStopped`. `OnProbesIdle` is sent by the reconnection (section 29), not by the scan flow.
- Probe warp and formations: `OnProbeWarpStart` and `OnProbeWarpEnd` are built in track A7 (section 27), for the move before a scan; `UpdateProbePosition` is client-side (the reference sends none). The states warping and returning are not built.
- Reconnection to lost probes (`ReconnectToLostProbes`): built in track A7 (section 29). Moon probes (`OnNewMoonProbe`, `OnRemoveMoonProbe`, `OnMoonScanComplete`, `OnScanNoMaterials`): not built (section 7).
- The client was not exercised on this host.

## 25. Track A5 continued: WP-8 mobile warp disruptors, E-03 Mobile Cynosural Inhibitor, the probe gate (2026-10-10)

Done (tests `MobileWarpDisruptorHostTests`, `InhibitorHostTests`, `ScanProbeHostTests`, sim tests `deployable/tests.rs` and `sim/tests.rs`; decisions in DEVIATIONS "Track A5 (continued)"): a mobile warp disruptor and a Mobile Cynosural Inhibitor are dropped from the hold 2,500 m ahead of the ship on its heading (`ship.LaunchFromShip`, command 25 in the simulation); each is anchored when its anchoring time is over (durable job `space.deployed.anchor`); the disruptor scrambles warp in its SDE range while anchored; the inhibitor refuses a cynosural field within 100 km of it (not a covert field); each leaves with its item when its time is up (the sweep); each comes back after a restart with its record's state. The radius is the SDE's, not a fallback. Fixes: the host's warp and position queries (commands 21 to 23) were refused by the simulation as unknown; the probe restore and launch are serialised per system, with a regression test that fails on the ungated code.

Not in the reference, so not built: scooping a disruptor or an inhibitor back into the hold, and unanchoring either (EVE.js v0.12.9 has neither; the inhibitor's wiki page says it cannot be reclaimed).

Open, each with its blocker:
- `GetCynoJammerState`: the reference answers from the sovereignty Tenebrex structures and the starbase cyno jammers, neither of which eve.rs builds. Until one of them exists the answer is None for every system (`BeyonceService.CynoJammerOnlineSimTimeForSystem` is unset on purpose).
- The inhibitor's 75 km (station, stargate, upwell) and 40 km (control tower) placement blockers: the host does not hold those objects' positions in space.
- The destruction of a deployable (EVE.js clears a destroyed mobile deployable): the simulation does not damage deployables.
- A process restart of the host is not exercised by a test (the restore is called directly).
- The client was not exercised on this host.

## 26. Track A6: the capital jump drive to a fleet's cynosural field (2026-10-10)

Done (tests: `JumpDriveRulesTests`, `CynoJumpHostTests` (PostgreSQL), `Scene` cyno tests in `crates/eve-space/src/scene/tests.rs`; decisions in DEVIATIONS "Track A6: the capital jump drive"):
- The simulation carries a cynosural jump (command 26, protocol 51): the drive spools for the SDE's jump duration, the ship leaves and appears on the ring round the field lit in the destination system, facing it; with no field lit there the ship goes back to where it was.
- The host answers `CmdJumpToFleetModuleBeacon` and `CmdJumpToFleetDeployableBeacon` (BeyonceService) and refuses by name where the wiki gives the rule: the field must be lit, the pilot must be in the field's fleet, the origin must be outside high-security space, the hull must be allowed the field's kind (covert: black ops; industrial: jump freighter, capital industrial, black ops), the range must hold (SDE range, 20 % a calibration level), the drive must not be cooling down, and the isotopes must be in the fuel bay or cargo.
- Fuel: the shared fuel debit (`CharacterShips.TakeFuelAsync`, factored out of the module cycles) takes the isotopes; the fuel and the new fatigue are written in one transaction (`DebitJumpAsync`, `space.jump_fatigue`, migration 0009). The fatigue and the activation cooldown follow the wiki (`JumpDriveRules.AfterJump`).
- The fleet is told when a field is lit or gone (`OnFleetJumpBeaconModuleChange`, `SpaceContainers.FieldAnnounced`), so the client's jump menu lists it.
- The jump lands through the existing session change (`FinishJumpAsync`), as a stargate jump does.

Open, each with its blocker:
- NOT BUILT: the capacitor cost of the activation (95 % of the capacitor, attribute 898, less the drive operation skill): the host does not read the capacitor. Blocker: no capacitor query from the simulation to the host.
- NOT BUILT: jump bridges (CmdBridgeToFleetModuleBeacon, CmdBridgeToFleetDeployableBeacon, the Titan, black ops and Rorqual bridges), group jumps and conduit jumps (CmdGroupJumpTo..., CmdJumpThroughFleet), the Jump Portal Generator (Titan, portals and the portal array), covert and industrial beacons from deployables. The client calls them (eve-logic `cynoTravelSvc.py`); the host refuses none of them by name yet.
- NOT BUILT: the jump drive economizers (the wiki's JDE terms), so the fuel is the base formula.
- LIMITATION: the spool is held in the simulation's memory; a restart of the process ends it without a jump. The fields are put back on a restart, but the fleet is not told again.
- LIMITATION: a fuel debit that fails after the simulation accepts the spool lets the jump stand (the check and the debit are two steps; the module cycles are the only other debit).
- not exercised: the client. No graphical client or MITM capture on this host; the menu, the spool effect (`effects.JumpDriveOut`) and the arrival are not seen in the client.

## 28. Track A8: the capacitor, the spool kept across a restart, the economizers, the skill numbers from the SDE (2026-10-10)

Done (tests: scene/tests.rs cyno tests (capacitor), JumpDriveRulesTests, CynoJumpHostTests (PostgreSQL); decisions in DEVIATIONS "Track A8: the capacitor, the spool, the economizers and the skill numbers"; protocol 52; migration 0010):
- The capacitor of the capital jump: the host sends the share (SDE 898 of the hull, 0.95, less 5 percent a level of Jump Drive Operation, SDE 899 of the skill) as the fourth argument of command 26. The simulation refuses NOT_ENOUGH_CAPACITOR when the charge is short and draws the share when the spool starts. This closes the capacitor item of section 26.
- The spool is persisted: space.jump_spool (migration 0010) is written with the fuel and the fatigue in one transaction. It is forgotten when the ship jumps. At the host start every spool still in the table is cancelled: the fuel is refunded into the fuel bay and the fatigue state the jump replaced is restored. A spool a minute past its end that the simulation did not complete is refunded by the sweep space.jump_spool.sweep (every 30 seconds). The choices are recorded in DEVIATIONS (the wiki is silent on restarts).
- The economizers (SDE group 1299, attribute 1296: -4, -7, -10 percent) fitted in the ship reduce the fuel, with the wiki stacking penalty (100, 86.9, 57.1 percent), before the round-up.
- The skill numbers come from the SDE attributes (Calibration 870, Fuel Conservation 1296, Jump Drive Operation 899), not from constants; the values are unchanged.

Open, each with its blocker:
- NOT BUILT: jump bridges (CmdBridgeToFleetModuleBeacon, CmdBridgeToFleetDeployableBeacon; the Titan, black ops and Rorqual bridges), the Titan Jump Portal Generator (SDE 23953, group 590; fits Titans), the Covert Jump Portal Generator (SDE 28652, group 4127; fits black ops), portals and conduits, group jumps (CmdGroupJumpTo) and CmdJumpThroughFleet. Blocker: no world object with a lifetime in the simulation for a portal or a bridge open state; the fuel per passing mass (the wiki formula, DEVIATIONS A8) has no caller yet; the client calls (eve-logic cynoTravelSvc.py) are not read in full.
- NOT BUILT: covert and industrial beacons from deployables (CmdJumpToFleetDeployableBeacon answers for the lit field only, as in section 26).
- NOT CHECKED: the wiki rule that a jump cannot be activated inside a warp interdiction bubble. Blocker: the host has no bubble object for a jump to test against.
- NOT CHECKED: the SDE canFit attributes of the economizers (1298, 1299) at the jump; the fitting service refuses a fit the economizer does not allow, and a jump does not re-check it.
- SHORTCUT: a second spool before the first is swept (a lost spool less than a minute old) fails the debit after the simulation accepted it; the spool stands. Upgrade: refund the stale row inside the debit.
- The client was not exercised on this host (no graphical client, no MITM capture): the capacitor bar, the refusal text, the refund and the spool after a restart are not seen in the client.
## 29. Track A7: probe scanning, directional scan, recovery and warp to a result (2026-10-10)

Done (tests: `ProbeScanMathTests` (no database); `ScanProbeHostTests` (PostgreSQL, 22 tests: the scan flow, the tiers, the absent targets, a scan asked while one runs, recover to the launcher and to the hold, reconnect, restart, the warp to a result, the directional cone); `scene::scan_targets` and `scene::directional` tests; the golden frames of commands 27 and 28 in `oracle/fixtures/simlink/messages.json`; decisions in DEVIATIONS "Track A7: probe scanning"):
- Wire: `scanMgr.RequestScans`, `SetActivityState`, `RecoverProbes` and `ReconnectToLostProbes` are answered through `SpaceProbeScans` (`dotnet/src/Eve.Services.Space/SpaceProbeScans.cs`); `scanMgr.ConeScan` through `SpaceDirectionalScans` (`dotnet/src/Eve.Services.Space/Scanning/SpaceDirectionalScans.cs`). `IncursionAnomalyService` ("scanMgr") routes all of them.
- Scan timing: two durable jobs (`space.probes.scan.start`, `space.probes.scan.stop`), so a restart keeps a scan that is running. Duration: the launcher's SDE attribute 73 times each scanning skill's attribute 66.
- Probe scan targets: command 27 (protocol 52, `ScanTargets`) answers the ships, drones, fighter squadrons and scannable deployables of the scene; a cloaked ship is not one. Core probes find no ship: a ship needs a combat probe (SDE 1413).
- Directional scan: command 28 (protocol 54, `DirectionalScan`) answers `DirectionalTargets` (kind 72): every object of the scene a directional scan can see (ships, NPC hull ships, drones, fighters, scanner probes, wrecks, and the system's bodies). The host decides what the cone shows: the scan angle and range, clamped to a sphere at most, 10 km at least, and the ship's SDE 5796 range (14.3 AU when none), nearest first, rows of id, type and group only. Immune types (SDE 1958) and the wiki's excluded groups are not shown.
- Recover and reconnect: `RecoverProbes` restores each probe that left to the launcher it came from while the launcher has room, and the rest to the cargo hold as a stack (`CharacterShips.RestoreProbesAsync`, inventory writes in one transaction); the pilot is told the stacks that changed and each probe that left. `ReconnectToLostProbes` tells the pilot each probe as new (`OnNewProbe`), then all as idle (`OnProbesIdle`).
- Warp to a result: `ProbeScanWarpAuthority` (kept per session, in memory) records the 100 percent results of each completed scan; `BeyonceService` `CmdWarpToStuff` takes a "scan" warp only to a place of that record, at its exact position. A restart drops the record.
- Results: `ProbeScanEngine` (certainty from the probes that reach a target, the spread of the probes, the tiers: group from 25 percent, name from 75, exact at 100). A result below 100 percent is placed off the target by at most the probes' largest deviation (the wiki's formula with the SDE numbers and the skills).
- Cosmic signatures and anomalies: one interface, `ICosmicSiteSource` (`dotnet/src/Eve.Services.Space/Scanning/ICosmicSiteSource.cs`); the server binds `NoCosmicSites`, so no signature is found. The site generator is a separate system and is not built.
- Shortcut: the scan's bonuses are the skills' and the launcher's (strength and deviation of attributes 846 and 1156, duration of 66). The rigs, arrays, implants, boosters, the ship's own role bonus (the Probe frigate's 7.5 percent per level) and the Mobile Scan Inhibitor are not counted.

Not built, each with its blocker:
- Directional scan, not shown: normal NPCs (the wiki shows only the diamond-named ones; the simulation keeps no NPC names), fighter squadrons, containers, corpses, beacons, cosmic signatures (section 27 `ICosmicSiteSource`), and the star (group 6). The scope check of EVE.js `canEntityDiscoverTarget` is always true here: eve.rs has no abyssal or dungeon scopes. The 5-second cycle is the client's; the server does not limit the scan rate.
- The probe states warping and returning, and the moon survey probes (mining; section 7).
- The client was not exercised: the probe scanner's windows, the result rows, the directional palette and the warp to a result are not seen in the client. No MITM capture was made: the headless client has no scenario that scans.
- A process restart in the middle of a scan is covered by the durable jobs but not exercised by a test.

## 31. Track A8 continued: the jump bridges, the bubble check and the economizer fit (protocol 55, 2026-10-10)

Done (tests: CynoJumpHostTests (A_titan_bridge_opens..., A_bridge_is_closed...), JumpDriveRulesTests (A_bridge_takes..., A_passenger_flag..., An_economizer_counts...), scene/tests.rs (a_titan_bridge_opens..., a_fleet_ship_in_range..., a_passenger_beyond..., a_cynosural_jump_is_refused_inside...); decisions in DEVIATIONS "Track A8: the jump bridges, the bubble check and the economizer fit"):
- Titan Jump Portal Generator (SDE 23953) and Black Ops Covert Jump Portal Generator (SDE 28652): CmdBridgeToFleetModuleBeacon and CmdBridgeToFleetDeployableBeacon open a bridge where the ship is (command 28 in the simulation: a deployable of role JumpBridge for the module's duration, a Titan held still for its minute, the module's capacitor). The host checks the generator and its fitting, the skill, a lit field of the fleet, the Titan's standard-field and high-security rules, the jump range and the Strontium (500 less 10 percent a level, SDE 714 and 1775).
- CmdJumpThroughFleet: a fleetmate within 2 500 metres passes through to the field (command 29; the passenger leaves at once). The isotopes come from the bridging ship (the SDE mass factor of the generator, the hull's 868, the passenger's mass); the passenger's fatigue is the wiki's with the passenger's 1971 and the bridge's 1972.
- The fleet is told the bridge is open (OnBridgeModeChange) and closed by the sweep at its time.
- The bubble check: a warp disruption over a ship refuses its cynosural jump, the bridge and the passage.
- The economizers count only for a hull they fit (SDE 1298 and 1299).

Open, each with its blocker:
- NOT BUILT: conduit and group jumps (CmdGroupJumpToFleetModuleBeacon, CmdGroupJumpToFleetDeployableBeacon; Black Ops, Rorqual and carrier conduits; the passengers leave with the anchor). Blocker: the spool and departure rule for the passengers is not in the wiki pages read (passenger count and radius are), and EveJS conduitJumpRuntime.js (568 lines) is not read in full yet.
- NOT BUILT: the industrial bridge (Rorqual, generator 63140, passenger flag 3325).
- NOT BUILT: the structure cynosural beacons and the structure bridge (structureCynoBeaconMgr), which are the Upwell structures.
- The client was not exercised on this host: the bridge notice, the jump menu and the passage are not seen in the client.
## 29. Track S1: ships and fitting, part 1 (2026-10-10)

Done (test: `RealSdeShipSweepTests`, PostgreSQL with the pinned SDE; decisions in DEVIATIONS "Track S1"):
- SF-0: the real-SDE ship sweep. All 423 published ships are read from the SDE and checked against the fitting rules: the slots each family offers equal the hull's static data (attributes 12, 13, 14, 1137, and the clamp of 1367 for non-Strategic-Cruiser hulls); one module per family fits its first flag, and a flag beyond the count is refused; each Strategic Cruiser takes one subsystem per class (flags 125 to 128) and its high, medium and low slots move by the subsystems' 1374, 1375 and 1376 modifiers; the 48 published subsystems are twelve per class; the 18 mode items of group 1306 are three for each of the six hulls.

Open, each with its blocker:
- KNOWN GAP (closes with SF-4): the four Strategic Cruisers (Tengu 29984, Legion 29986, Proteus 29988, Loki 29990) offer five subsystem flags, because the SDE hull attribute 1367 is 5 and the engine clamps it to eight. The SDE has four subsystem classes. The test asserts these four hulls exactly; remove them from `OpenSubsystemHulls` when SF-4 lands.
- Not in SF-0 (later packages): the probes use one module per family (the Damage Control I, Afterburner, Small Energy Neutralizer, and one rig of the hull's size); the probes do not walk every module a hull can take.
- SF-1 (listing): `FittingCoverageTests` (Eve.Services.Core.Tests) asserts that the 21 client fitting-manager calls (charFittingMgr, allianceFittingMgr, corpFittingMgr; seven each) are answered by a method; the open list is now empty. `ShipCoverageTests` (Eve.Services.Space.Tests) lists the `ship` calls; `ship.AssembleShip` is the only one open (SF-4, not this track). `ship.GetShipFittingInfo` returns the saved fitting's name and hull (SF-2); an unknown fitting keeps the reference's default `Fitting {id}` (EveJS `Handle_GetShipFittingInfo`, shipService.js:1248), which `ShipFittingInfoRpcTests` pins. The silent `None` answers of `ship.Undock`, `Board` and `LeaveShip` when the object is not bound are unchanged (not verified against the client).
- SF-2 (fitting persistence, done in code; PG tests and the client are the gate): table `fitting.saved_fitting` (migration 0005, `PgSavedFittingStore`, `SavedFittingRules`), the services `charFittingMgr`, `corpFittingMgr` and `allianceFittingMgr` (`FittingMgrCalls`), the Host adapter `FittingHost` (`dotnet/src/Eve.Host/Rpc/FittingRpc.cs`). Seven calls each; the owner scopes, the limits (500 character, 600 corporation and alliance), the Fitting Manager role and the alliance executor are checked as EveJS checks them; refusals are the client's UserErrors; each change notifies every session of the owner (`OnFittingAdded`, `OnFittingDeleted`, `OnManyFittingsDeleted`). Tests: `SavedFittingStoreTests` (rules without a database; store round trip, owners, limit, restart), `FittingMgrServicesTests` (service calls, refusals, roles, executor, notifications; PostgreSQL).
- SF-2 open: the graphical client has not seen a save, load, rename or delete. Corporation `GetFittings` is cached for 15 minutes, and each change sends the client's cache drop before the change is announced (`FittingMgrServicesTests`, PostgreSQL).
- SF-3 (apply a saved fitting to a docked ship) and SF-12 (charges, drones and fighters on the bound dogma location): see section 30.
- not exercised: the graphical client (assemble, undock, fitting save and load) and any MITM capture.


## 30. Track S2: ships and fitting, part 2 (2026-10-10)

Done (tests: `ShipStanceWorldTests`, `ShipStanceNotifyTests`, `ShipStanceTableTests`, `StrategicCruiserAssemblyTests`, `StrategicCruiserRulesTests`, `ShipEffectSweepTests`, `HullDataTests`, `SpecialHullBonusTests`; PostgreSQL and the pinned SDE; decisions and sources in DEVIATIONS "Track S2: ships and fitting, part 2").
- SF-4 (T3C assembly): `ship.AssembleShip` on the station `ship` moniker (`ShipService.AssembleShip`, `CharacterShips.AssembleAsync`). A packaged hull in the hangar becomes assembled; a Strategic Cruiser takes one subsystem per class (125 to 128), each for its hull (SDE 1380). The answer is the ship's id, a list, or None. The name is normalised as EveJS does. An incomplete undock is refused (`SpaceSessions.UndockAsync`). A subsystem class is at most four. A swap in the station goes through the move path, and the fit check refuses a subsystem of another hull.
- SF-5 (hidden modifiers): flag-156 items are in the ship's dogma world (`ShipSnapshot.LoadAsync`), not in `Fitted`. The Sharpshooter and Defence modes move the hull's numbers as their SDE effects say.
- SF-6 (T3D stance): a changed stance sends `OnStanceActive (shipID, stanceID)` to the pilot after the commit, and a ship in space is sent again (`ShipStanceMgrService`, `RefreshShipAsync`). The table matches the SDE groups and the client. The switch is refused in station (the wiki: "while in space"; EveJS allows station, a recorded deviation).
- SF-7 (sweep): every ship-scope effect is passive, and every modifier is applied (`ShipEffectSweepTests`, real SDE).
- SF-8 and SF-9 (hull data): cargo is the SDE `types.capacity` (attribute 38, loaded from `types`); the packaged volumes are the client's table and are kept. The SDE gaps are pinned (three hulls with no capacity, four with no skill, Opux Luxury Yacht with no type bonus and no effect).
- SF-11 (special hulls, partial): the role bonuses of the command destroyer, blockade runner, black ops and jump freighter reach the hull's numbers (`SpecialHullBonusTests`).

Open, each with its blocker:
- NOT BUILT: SF-10, the module-driven transformations. Siege, triage, bastion and core: the fuel of the cycle (consumption types 713 and 714; `FUEL_NOT_IMPLEMENTED`), the warp and speed locks (852, 104), the one-per-ship rule (763) and the deactivation rules. Blocker: the simulation's rules and one wire bump (module_rules.md WP list, section 4); the integrator's batch.
- NOT BUILT: SF-11, the command bursts, the cloak and the jump drive of these hulls. Blocker: the buff track (section 21, 22) and the jump drive (sections 26 and 28); the restrictions (no warp, no bumping) are not in the SDE effects of the hulls.
- NOT SENT: the slim `shipStance` field in Destiny (the client reads it, `environment/spaceObject/ship.py:214`; a missing one is no stance). Blocker: a Destiny change in the simulation wire (the integrator's batch; `SimCodec.cs`). The stance button in space shows the new stance from `OnStanceActive`.
- LIMITATION: AssembleShip refusals are silent, as EveJS's are. The client shows nothing for a ship that is not assembled.
- LIMITATION: undock does not check the CPU, powergrid, turret, launcher and drone or fighter overloads of a Strategic Cruiser (EveJS `_getStrategicCruiserUseStatus`). The fit check makes those when a module is fitted.
- not exercised: the graphical client. Not seen: the assembly window and its name prompt, the default skin after assembly, the stance button, the undock text, the swap of a subsystem. No MITM capture was made on this host.

## 30. Track S1, part 3: fitting a saved fitting onto a docked ship (SF-3, increments 1 and 2)

Done (tests: `FitFittingTests`, PostgreSQL, real SDE: each module in its slot with the one that has no hangar item reported; a fit for another hull refused by name; a ship in space refused; a charge stack split into the hold with the shortfall reported by quantity; a drone split into the drone bay; a rig not fitted displaced into the hold; decisions and sources in DEVIATIONS "Track S1: fitting a docked ship"):
- `ship` fitting by the client's `FitFitting` on the bound ship inventory (`InvBrokerService.Fitting.cs`): modules and rigs by slot in the reference's order, then the cargo, drone, ice, implant, fighter and charge legs. The reply is the client's failed-to-load list.

Added (tests: `FitFittingTests`, `ReloadServiceTests`, PostgreSQL, real SDE): the re-online pass after the modules (`The_modules_of_a_fit_are_switched_on_where_the_grid_allows`); one batch at the end of the apply (`A_fit_tells_the_pilot_its_changes_in_one_batch_at_the_end`); a rig fitted into its slot when rigs are asked for (Cockroach, flag 92); fighters into the fighter bay, with a module refused there and reported (Cockroach, flag 158); a ship in space refused with the in-space fitting service's not-found notify (EveJS text, the Mobile Depot and maintenance-bay providers are not modelled). Strategic Cruiser rig removal moves the same rig item to the hangar (`A_tengu_rig_can_be_removed_to_the_hangar_without_being_destroyed`); saved fits move an occupied high, mid or low module to the hangar before fitting its replacement (`A_saved_fit_replaces_an_occupied_module_slot_and_keeps_the_old_module_in_the_hangar`). `FittingRules.Check` enforces fitted-item skill requirements. V24 `menuFunctions.Board` checks the target hull required skills and returns after prompting, before calling `ship.Board`; the Host now enforces the same requirement from the target hull SDE attributes and the pilot's persisted skill levels. The separate warning for modules lacking skills remains a fitting path.
- SF-12: a docked gun loaded from the station hangar, with a drone and a fighter given as charges left alone, and unloaded back to the hangar (`A_docked_gun_is_loaded_from_the_hangar_and_a_drone_or_a_fighter_given_as_a_charge_is_left_alone`).

Open, each with its blocker:
- NOT BUILT: the space-loot and structure access checks, and the ship-service checks of the reference (DEVIATIONS "Track S1: fitting a docked ship" gives the reason for each). Blocker: eve.rs has no space loot, structure fitting or ship-service state.
- NOT BUILT: in-space fitting services (Mobile Depot, maintenance-bay ship, Ship Maintenance Array). Blocker: those providers are not modelled; an in-space fit is refused until they are.
- not exercised: the graphical client (the fit button, the buy-all box for the failed list, the hangar deltas).



## 32. Track S2b: module rules, the stance on the wire, special hulls (2026-10-10)

Done (tests: `SiegeModuleTests`, `SiegeSpeedLockTests`, `ShipStanceSlimTests`, `JumpDriveRulesTests`, `SpecialHullBonusTests`, the Rust `scene::tests` stance test and the simlink replay; decisions and sources in DEVIATIONS "Track S2b").
- Protocol 56: the stance is in the ship spec and the slim item (`shipStance`), and a switch shows the slim item again. `messages.json` is regenerated by the generator.
- The siege's speed lock reaches the simulation: a start sends the ship again (the maximum velocity and the mass are compared with the resonances).
- The jump drive reads the hull's own numbers (a dogma world with the hull alone), so a hull bonus on the fuel reaches the jump while the skills and the economizers are taken once each; a Jump Freighter does not jump into high-security space (the wiki's rule).
- Corrected from section 30: the module fuel per cycle (713, 714) is built (`WORK_FUEL`), and the warp, dock and speed locks of a siege are dogma while the module runs. Section 30's "NOT BUILT" for the fuel is withdrawn.

Open, each with its blocker:
- Verified at module level in section 36 (triage, bastion, core, command burst, cloak, covert cyno).
- Blockade runner: the wiki names no restriction, so none is built (DEVIATIONS Track S2b and S2c).
- LIMITATION: the old stance of a switch after a restart is the current one (the custom info keeps only the current stance).
- not exercised: the graphical client. The stance button in space, the speed of a siege-locked ship and the jump freighter's refusal text are not seen in the client.

## 33. Track A9: the bridge range, the Black Ops conduit, the industrial bridge (protocol 57, 2026-10-10)

Done (tests: CynoJumpHostTests (A_black_ops_conduit_takes..., A_rorqual_bridges_to_an_industrial_field..., An_industrial_bridge_is_refused..., A_bridge_does_not_survive_a_restart...), scene/tests.rs (a_conduit_jump_..., a_bridge_takes_only..., a_bridge_range_is_refused...); decisions in DEVIATIONS "Track A9: the bridge range, the Black Ops conduit, the industrial bridge"):
- The bridge range is an argument of command 30 (2 500 metres, the wiki); the passage (command 31) is refused beyond it.
- The passenger list (command 32): a Black Ops with its Covert generator makes a conduit jump; the fleet ships in its 10 km that are eligible (3322) and ready go with it at once, the group taken whole or refused whole. The fuel is the anchor's, once (3131 a light year); each ship takes the jump drive's fatigue with its own 1971.
- The industrial bridge: a Rorqual's Industrial generator (SDE 63140) opens a bridge to an industrial field (20 seconds, 100 GJ, no Strontium, no hold), and its passengers take flag 3325.
- Every bridge checks the bridging hull's field rule (JumpDriveRules.MayJumpTo) and the high-security refusal for the capitals (a Titan and a Rorqual).
- A bridge and a conduit do not survive a restart (the host and the simulation both hold them in memory).

Open, each with its blocker:
- NOT BUILT: the Rorqual's conduit. Blocker: the wiki requires the Rorqual's industrial core to be inactive, and the simulation does not expose the core's active state to the host.
- BUILT in section 34: the mobile cynosural beacon as a jump destination (the standard, the highsec authorized and the covert ones).
- NAMED REFUSAL in section 34: the Pharolux and Upwell cyno beacons (group 2017, the structure service is not built).
- The client was not exercised on this host: the passenger list, the fleet's notices and the arrival are not seen in the client.

## 34. Track A9 continued: the mobile cynosural beacon, the structure beacon refusal (protocol 58, 2026-10-10)

Done (tests: BeaconHostTests (5), StructureTravelTests, InhibitorHostTests, scene deployable/tests.rs a_mobile_cynosural_beacon_...; decisions in DEVIATIONS "Track A9 continued"):
- The mobile cynosural beacon (group 4093, 57319, 58906, 59630): dropped from the hold 2 500 m ahead, anchored after its activation time (2 min; covert 1 min), a field only while anchored, gone after its life (1 h; covert 30 min). Skills read from the SDE.
- Security cutoff 0.45 for the standard and covert beacons; the highsec authorized beacon is exempt (its own SDE type, Polaris 5).
- Restart: a beacon in space comes back unanchored or anchored as its record says.
- Protocol 58, kind 73 (PlaceCynoBeacon); migration 0011 widens the deployed kind check.
- Named refusal for the Pharolux and Upwell cyno beacons: the structure service that keeps their state is not built.

Open, each with its blocker:
- NOT BUILT: a beacon as the target of a bridge or a conduit (the field-only check refuses it).
- NOT BUILT: fleet notices for a beacon (no field announcement); the mobile inhibitor's effect on a beacon's activation.
- NOT BUILT: the wormhole and Pochven refusals of the beacon (EveJS lines 287 to 291).
- NOT TESTED: a host jump to an anchored beacon and the simulation's arrival at it (the code path exists; no test drives it).
- not exercised: the client.

## 37. Service table against the reference: the names eve.rs lacks and the names it adds (MITM parity, 2026-10-11)

The MITM run of `tests/test_live.py` (EVE.js 143 advertised names, eve.rs 171) gives 21 names the reference advertises that eve.rs
does not, and 49 names eve.rs advertises that the reference does not. A name matters only if the V24.01 client calls it through a
MachoNet route (`RemoteSvc`, `ProxySvc`, `ConnectToRemoteService`, `Moniker`) or a route table (`eveMachoNet.py`). Call sites are the
first match in `/home/user/eve-logic`. Statuses: **OPEN** = the client calls it and eve.rs has no provider; **NOT NEEDED** = no
remote call from the V24.01 client (reason given). Advertised names stay limited to registered services (DEVIATIONS.md, "Service
table: the names left out").

### 37a. Advertised by EVE.js, not by eve.rs (21)

| Status | Name | Client evidence (eve-logic) | Owner / next step |
|---|---|---|---|
| NOT NEEDED | `objectCaching` | Client-local service: `sm.GetService('objectCaching')` (`eve/client/script/ui/shared/bountySvc.py:236`) resolves to `eveObjectCaching` (`__replaceservice__`). A remote `GetCachableObject` is reached only for a `CachedObject` reference (`carbon/common/script/net/objectCaching.py:299-332`), which eve.rs never returns | DEVIATIONS "objectCaching is not a service here". Reopen with the proxy cache (section 37c) |
| NOT NEEDED | `loadService` | Cluster load tool: `carbon/common/script/sys/loadService.py:70-321` (`ConnectToRemoteService`), no player path | none |
| NOT NEEDED | `tutorialSvc` | No reference in the V24.01 client (only the marshal string table) | none |
| NOT NEEDED | `bookmarkMgr` | No reference in the client; bookmarks call `accessGroupBookmarkMgr` (37a row below) | none |
| NOT NEEDED | `certificateMgr` | No reference in the client | none |
| NOT NEEDED | `contractMgr` | No reference in the client | none |
| NOT NEEDED | `standing2` | No service reference (`standing2to1` is a field name). The client uses `standingMgr` (eve.rs serves it) | none |
| PARTIAL (Local/group gateway and XMPP transport) | public gateway Local plus corporation, alliance, fleet, wing, and squad membership/list/message handling; XMPP parsing, SASL PLAIN, resource binding, and active-session token checks | V24.01 `chat/client/local_chat/messenger.py` defines the Local protobuf requests and notices. Host lists current K-space characters and only speakers in wormhole systems, sanitizes messages, and updates membership on system and affiliation changes. `Eve.Host.Xmpp` adds bounded stream parsing, an optional TLS TCP listener, queued writes, SASL PLAIN, resource binding, and token validation against the active character session. `XmppTransportTests` exercises the parser and session handshake. Host startup reads `EVE_XMPP_*`, conditionally creates and disposes the listener, and registers `XmppChatMgr.Hostname`, `GetDeprecatedPrefsFallback`, and `ResyncSystemChannelAccess`. The resync list derives Local/corporation/alliance/fleet channel names from the active session. The client verifies TLS against certifi unless `chatserver_disable_tls` is set. Local MUC discovery/join/leave, occupant presence and groupchat use current-system session membership; wormhole Local hides silent occupants until they speak. Private conversations, corporation/alliance/fleet rooms, MOTD, ignore/block enforcement, channel administration, and a two-character walk remain open. The client has no Macho `LSC` callsite; EveJS v261018.1 LSC response-shape methods are a separate compatibility surface. Tests: `LocalChatGatewayTests`, `XmppTransportTests`, `XmppChatMgrServiceTests`, `XmppHostConfigurationTests`, and `XmppLocalRoomTests`. |
| NOT NEEDED | `publicGatewaySvc` | Client-local service (`publicGateway/publicGatewaySvc.py:25`, `svc.publicGatewaySvc`) that talks to the public gateway over gRPC, not MachoNet. The gateway itself is not built (owner decision 4, Insurgency) | DEVIATIONS (owner decision 4). Its start-up use is in `gameui.py:709-718` |
| DONE | `trademgr` (`tradeMgr` routes here) | Direct trade end to end: `InitiateTrade` returns the trade moniker and notifies the target; the bound trade serves `List`, `Add`, `MultiAdd`, `GetSelfInvItem`, `OfferMoney`, `MakeOffer` and `Abort`; items are staged in the trade container and go back on abort; completion moves the items and the ISK in one transaction (Eve.Trade, schema `trade`, migration 001). Open: the graphical-client walk; ship-cargo staging and the item tradability check are not in the server rules (DEVIATIONS "Direct trade"). Tests: Eve.Trade.Tests (9, PostgreSQL), Eve.Services.Station.Tests (29). |
| OPEN | `structure` | `Moniker('structure', ...)`, `eve/client/script/ui/services/menuSvcExtras/invItemFunctions.py:79` | Structures track the owning implementation |
| DONE (store and service, PostgreSQL tests; graphical check open) | `ownerGroupManager` | `REMOTE_SERVICE_NAME`, `eve/client/script/ui/structure/accessGroups/accessGroupsController.py:14, 22`; store `dotnet/src/Eve.AccessGroups`, service `Eve.Services.Core/OwnerGroupManagerService.cs`; DEVIATIONS "Owner groups: ownerGroupManager". `CreateProfile`/`SaveProfileSettings` stay with the structure track. | Owner groups (access lists) |
| DONE (3a6d509) | `corpStationMgr` | `Moniker('corpStationMgr', stationID)`, `eve/common/script/net/eveMoniker.py:111`; routed to the station (`eveMachoNet.py:249`). Served: `DoStandingCheckForStationService` (SDE `stationStandingsRestrictions`), `MoveCorpHQHere`. Tests: `Eve.Corporations.Tests` (`CorpStationMgrTests`). DEVIATIONS "Corporation station door". | Corporation offices (done) |
| DONE (3dc9b9b) | `itemLocking` | `sm.RemoteSvc('itemLocking')`, `eve/client/script/ui/services/corporation/itemLocking.py:27` (service `svc.lockedItems`, `Run`), lazy. Served: `GetItemsByLocation`, `GetLockedItemLocations`; locks from blueprint votes (migration 013, `item_locks`); push `OnLockedItemChange` after commit. Tests: `Eve.Corporations.Tests` (`ItemLockTests`). DEVIATIONS "Item locking". Open: ALSC container locks (inventory). | Corporation hangar locks (done) |
| OPEN | `repairSvc` | `Moniker('repairSvc', ...)`, `eve/common/script/net/eveMoniker.py:227`, `eve/client/script/ui/station/repairshop/base_repairshop.py:58`; repair cycles touch ships (OUT-SHIP) | Station services; the ship part belongs to the ships track |
| DONE | `repairSvc` | NPC station repair: `MachoBindObject` binds the repair shop of the docked station; `GetRepairQuotes` quotes ships (with their fitted and drone-bay items) and modules by damage in HP and unit cost (adjusted price x 0.1 / max HP); `RepairItemsInStation` charges and repairs in one PostgreSQL transaction (module state store, ref type 15), a fitted module in full only, a partial payment in the reference order. Open (DEVIATIONS "Repair"): structures refused, no repair without a price, no durable settlement, fitted module ids not aliased. Tests: `RepairServiceTests` (Station project, PostgreSQL). |
| DONE | `reprocessingSvc` | Station reprocessing: `MachoBindObject` binds the plant of the docked NPC station; `GetQuotes` and `Reprocess` quote and consume the caller's items rooted at the station (hangar, containers, ship cargo), in one PostgreSQL transaction, and charge the tax through `WalletTransactions` (ref type 127). Yields: SDE station efficiency, skills, implants (legacy path), the tax from the standing; stacks above the client limit are split. Active ship refused on the server. Open (DEVIATIONS "Reprocessing"): corporation hangars, structures, randomized outputs, per-chunk quotes, live-client walk not exercised. Tests: `ReprocessingServiceTests`, `ReprocessingRulesTests` (Station project). |
| DONE (543f31e) | `blueprintManager` | `sm.RemoteSvc('blueprintManager')`, `eve/client/script/industry/blueprintSvc.py:24`; `sm.GetService('blueprintManager').GetBlueprintType`, `eve/common/script/util/industryCommon.py:526` (lazy) Served: `GetLimits`, `GetBlueprintData`, `GetBlueprintDataByOwner`; facilities not modelled. Tests: `Eve.Services.Core.Tests` (`BlueprintManagerServiceTests`). DEVIATIONS "War registry, NES intro and blueprint manager". | Industry (done) |
| DONE WITH CUTS (55e26a1) | `warRegistry` | `Moniker('warRegistry', ...)`, `eve/common/script/net/eveMoniker.py:198`, and the server-side check `sm.GetService('warRegistry').IsAllianceOrCorpLocal` at `eveMoniker.py:197` on alliance and corporation binds Served: `GetWars`, `IsAllianceOrCorpLocal`, `SetOpenForAllies`, `RetractMutualWar`; negotiations refused (not built). Tests: `Eve.Corporations.Tests` (`WarRegistryTests`). DEVIATIONS "War registry, NES intro and blueprint manager". | Wars (negotiations open) |
| PARTIAL | `planetOrbitalRegistryBroker` | `ConnectToRemoteService('planetOrbitalRegistryBroker', nodeID)`, `eveplanet/orbital.py:35`; `Moniker` at `eveMoniker.py:220`. Built: `MachoBindObject`, `GetTaxRate` for NPC and player-owned offices, persisted corporation settings, and same-transaction corporation wallet credit for the owner share. PostgreSQL Host restart coverage covers office rates. Open: import/export live walk and owner-wallet settlement regression. | Planetary interaction (not on the start-up path) |
| DONE WITH CUTS | `planetMgr` (planetary interaction) | `eveMoniker.GetPlanet` (`eveMoniker.py:211`) binds one planet; `clientPlanet.py` and `planetSvc.py` read and change the colony: `GetPlanetsForChar`, `GetMyLaunchesDetails`, `DeleteLaunch`, `GetPlanetInfo`, `GetFullNetworkForOwner`, `GetCommandPinsForPlanet`, `GetExtractorsForPlanet`, `UserUpdateNetwork` (commands 1 to 13), `UserAbandonPlanet`, `UserLaunchCommodities`, `UserTransferCommodities`, `GetProgramResultInfo`, `GetResourceData` (no band: D1). Simulation in `Eve.Planets` (extractor programs with heads, decay and noise, processors with schematics, storage, launchpads, links, routes, command center levels), persistence in schema `planets`, pins as item rows, ISK through the wallet (ref 98). Customs import and export, NPC and POCO rates, persistent owner settings, standing selection, and same-transaction owner-corporation wallet credit are built (`planetOrbitalRegistryBroker.GetTaxRate`, `ImportExportWithPlanet`); Host rate settings survive restart in a PostgreSQL regression. The import/export live walk and owner-wallet settlement regression remain open; the office is an orbital in space (its ball reaches the pilots of its system); launches are real cans in space (five days, owner only). PI audit (2026-10-13): every non-GM remote call the V24.01 client makes on `planetMgr` and the bound planet has a handler. The four GM calls (`GMForceInstallProgram`, `GMConvertCommandCenter`, `GMAddCommodity`, `GMGetSynchedServerState`) are not served: the client shows them to ROLE_GML only and the server has no GM role (DEVIATIONS, "GM planet calls"). Owner decision 2026-10-19: keep PI survey limited to rule-derived values; do not synthesize an SH buffer. Owner decision Q-24: apply the EVE University Wiki default rates and standing discounts. `Customs.OwnerTaxRates` now models corporation, alliance, and 10/5/0/-5/-10 standing bands with unit coverage. Open: office-rate persistence and lookup, Host use of both tax portions, corporation-wallet credit, PostgreSQL coverage, and the live PI walk. Cut: native resource survey (D9), the container `Add` into an office (walked in slice 4; the drag from the hangar is not). DEVIATIONS "Planetary interaction". Tests: `Eve.Planets.Tests` (36, 3 need PostgreSQL), `tools/headless-client/tests/test_planet_walk.py`. Design: `docs/project/PI_DESIGN.md`. | Planetary interaction (cuts above) |
| DONE (543f31e) | `nes_intro` | `sm.RemoteSvc('nes_intro')` in the controller's `__init__`, `storylines/client/nes_intro.py:45`; the controller is built on the first intro check (lazy) Served: `get_nes_intro_state`, `skip_nes_intro` (state table `nes_intro_states`, migration 0005); nothing sets an introduction to active, so the storyline is not exercised. Tests: `Eve.Characters.Tests` (`NesIntroStoreTests`), `Eve.Services.Core.Tests` (`NesIntroServiceTests`). | Storylines (intro not exercised) |
| DONE (personal and shared folders; graphical check open) | `accessGroupBookmarkMgr` | `sm.RemoteSvc('accessGroupBookmarkMgr')`, `eve/client/script/ui/shared/bookmarks/bookmarkSvc.py:41`; the first call is `GetMyActiveBookmarks` on the bookmark prime (`bookmarkSvc.py:163`). Served: personal and shared folders (lists from `ownerGroupManager`, `Eve.AccessGroups`), subfolders, bookmarks of stations and systems, shared folder updates/deletes and access-loss revocation notifications. Corporation and alliance membership departure walks verify revocation, including alliance member departure (`test_alliance_member_departure_revokes_an_alliance_shared_folder`). Open: the expiry notice of shared bookmarks, the created-folder limit, the expiry option set against the wiki, `BookmarkLocation` in space, structure and celestial places. Tests: `Eve.Bookmarks.Tests` (`SharedFolderTests`, `SharedFolderNotificationTests`); live shared-folder walks. DEVIATIONS "Bookmarks: shared (access group) folders, built" and "Bookmarks: personal folders". | Bookmarks (shared access revocation and membership walks built) |

### 37b. Advertised by eve.rs, not by EVE.js (49)

Every name below has a V24.01 client call site, except `abyssal_content_manager`, which only the developer tools call. `standingMgr` is
also missing from EVE.js (its advertisement is `standing2`), so the gap is on the reference's side.

| Name | First client call site (eve-logic) | Verdict |
|---|---|---|
| `ProjectDiscovery` | RemoteSvc at `eve/client/script/ui/station/loyaltyPointStore/lpStoreSvc.py:348` | real client service name |
| `abyssalMgr` | RemoteSvc at `eve/client/script/ui/services/menuSvcExtras/movementFunctions.py:569` | real client service name |
| `abyssal_content_manager` | RemoteSvc at `eve/devtools/script/abyssal_npc_spawn_tool.py:78` | devtools only (no player call) |
| `achievementTrackerMgr` | RemoteSvc at `eve/client/script/ui/camera/enterSpaceCamera.py:40` | real client service name |
| `bountyProxy` | ProxySvc at `eve/client/script/ui/shared/bountySvc.py:83` | real client service name |
| `browserLockdownSvc` | RemoteSvc at `carbonui/control/browser/sites.py:327` | real client service name |
| `colonyResourcesMgr` | RemoteSvc at `eve/client/script/ui/services/sovereigntyResourceSvc.py:272` | real client service name |
| `communityTrackMgr` | RemoteSvc at `communitytracks/client/repository.py:84` | real client service name |
| `crimewatch` | Moniker at `eve/common/script/net/eveMoniker.py:136` | real client service name |
| `custom_agency_provider` | RemoteSvc at `agency/client/custom_content.py:10` | real client service name |
| `devIndexManager` | RemoteSvc at `eve/client/script/ui/services/sovSvc.py:182` | real client service name |
| `dungeon` | RemoteSvc at `eve/client/script/ui/inflight/dungeoneditor.py:142` | real client service name |
| `dynamicItemService` | RemoteSvc at `eve/client/script/dogma/dynamicItemSvc.py:24` | real client service name |
| `entity` | Moniker at `eve/common/script/net/eveMoniker.py:60` | real client service name |
| `expertSystemMgr` | RemoteSvc at `expertSystems/client/service.py:34` | real client service name |
| `fighterMgr` | RemoteSvc at `eve/client/script/parklife/fightersSvc.py:95` | real client service name |
| `fleetMgr` | RemoteSvc at `eve/client/script/parklife/fleetSvc.py:367` | real client service name |
| `fleetObjectHandler` | Moniker at `eve/common/script/net/eveMoniker.py:207` | real client service name |
| `fleetProxy` | ProxySvc at `eve/client/script/parklife/fleetSvc.py:1891` | real client service name |
| `fwVictoryPointMgr` | RemoteSvc at `eve/client/script/ui/services/fwVictoryPointSvc.py:36` | real client service name |
| `hackingMgr` | RemoteSvc at `eve/client/script/ui/hacking/hackingUISvc.py:26` | real client service name |
| `home_station` | RemoteSvc at `homestation/client/service.py:46` | real client service name |
| `insurgencySolarsystem` | RemoteSvc at `eve/client/script/ui/services/insurgencyCampaignSvc.py:52` | real client service name |
| `jumpTimers` | RemoteSvc at `eve/client/script/ui/services/crimewatchSvc.py:607` | real client service name |
| `keeper` | RemoteSvc at `eve/client/script/ui/services/menuSvcExtras/movementFunctions.py:553` | real client service name |
| `killRightMgr` | RemoteSvc at `eve/client/script/ui/shared/bountySvc.py:327` | real client service name |
| `mailMgr` | RemoteSvc at `eve/client/script/ui/services/mail/mailSvc.py:79` | real client service name |
| `mailingListsMgr` | RemoteSvc at `eve/client/script/ui/services/mail/mailingListsSvc.py:42` | real client service name |
| `milestoneMgr` | RemoteSvc at `milestones/client/milestoneSvc.py:22` | real client service name |
| `moonExtractions` | RemoteSvc at `eve/client/script/ui/moonmining/schedulingSvc.py:34` | real client service name |
| `node_graph` | RemoteSvc at `nodegraph/client/service.py:144` | real client service name |
| `notificationMgr` | RemoteSvc at `eve/client/script/ui/services/mail/notificationSvc.py:30` | real client service name |
| `overviewPresetMgr` | RemoteSvc at `eve/client/script/ui/control/eveEditPlainText.py:157` | real client service name |
| `paperDollServer` | RemoteSvc at `eve/client/script/ui/shared/preview.py:134` | real client service name |
| `pingService` | ProxySvc at `carbon/client/script/remote/connectionService.py:189` | real client service name |
| `populationCap` | Moniker at `eve/client/script/ui/login/charSelection/characterSelection.py:749` | real client service name |
| `pvpFilamentMgr` | RemoteSvc at `pvpFilaments/client/pvpFilamentSvc.py:30` | real client service name |
| `seasonManager` | RemoteSvc at `seasons/client/seasonservice.py:75` | real client service name |
| `securityMgr` | RemoteSvc at `security/client/securitySvc.py:40` | real client service name |
| `sharedSettingsMgr` | RemoteSvc at `eve/client/script/ui/shared/inventory/filterSvc.py:449` | real client service name |
| `shipCosmeticsMgr` | RemoteSvc at `eve/client/script/ui/shared/skins/devMenuFunctions.py:15` | real client service name |
| `shipKillCounter` | RemoteSvc at `eve/client/script/ui/shared/info/shipInfoPanels/panelOverview.py:276` | real client service name |
| `solarsystemInterferenceMgr` | RemoteSvc at `eve/client/script/ui/services/solarsystemInterferenceSvc.py:41` | real client service name |
| `sovMgr` | RemoteSvc at `eve/client/script/ui/services/sovSvc.py:80` | real client service name |
| `standingMgr` | RemoteSvc at `eve/client/script/ui/shared/neocom/corporation/corp_dlg_edit_member.py:139` | real client service name |
| `structureAssetSafety` | RemoteSvc at `eve/client/script/ui/shared/assets/assetSafetySvc.py:26` | real client service name |
| `structureSettings` | RemoteSvc at `eve/client/script/ui/services/marketsvc.py:239` | real client service name |
| `taleMgr` | RemoteSvc at `taleclient/tale_service.py:129` | real client service name |
| `voidSpaceMgr` | RemoteSvc at `voidspace/client/void_space_service.py:46` | real client service name |

### 37c. Still open (from the table above; the start-up path needs none of them)

Slice B (2026-10-10) found no advertised name on the start-up path that is missing. The names above are needed when a player opens the
feature (the lazy column). Each OPEN row needs its own slice: the owner is the track named in the table.

## 38. Non-strict host: a failing handler (slice A finding, closed in slice B, 2026-10-10)

| Status | Item | Missing work |
|---|---|---|
| CLOSED (B1) | A handler that throws or rejects is answered with the V24.01 server's wrapped error under an error ID (`PacketDispatcher.cs`, `MachoErrors.UnexpectedFailure`); the host logs the exception under the same ID. A deliberate user error is answered as before. Strict mode is unchanged. DEVIATIONS.md, "Slice B: failing handlers", "A failing handler". Tests: `HostServerTests`, `StrictRpcTests`, `DispatchGoldenTests`. | None. |

## 35. Track A8c: the mobile beacon as a jump target, the fleet notices, the inhibitor, the wormhole and Pochven rules (2026-10-10)

Increment 1, done (tests: `CynoJumpHostTests`: A_revelation_jumps_to_an_anchored_beacon..., A_titan_bridge_opens_to_an_anchored_beacon..., A_titan_bridge_is_refused_to_a_covert_beacon..., A_black_ops_conduit_jumps_to_an_anchored_covert_beacon; decisions in DEVIATIONS "Track A8c"):
- A mobile cynosural beacon is a jump, bridge and conduit target while it is anchored and its time is not up (`SpaceContainers.IsLitField`, one rule for the three). A beacon still anchoring is refused "not lit" and the simulation is not asked.
- A Titan bridge goes to a standard field or a beacon; a covert beacon is a covert field for the cyno rule, so only a Black Ops takes it.
- A covert bridge and a Black Ops conduit take a beacon of any kind.

Increment 2, done (tests: `CynoJumpHostTests`: A_beacon_that_anchors_is_told_to_its_fleet..., A_titan_bridge_tells_its_opener...; decisions in DEVIATIONS "Track A8c"):
- A beacon that anchors is told to its lighter's fleet (`OnFleetJumpBeaconDeployableChange`, true), and told gone (false) when its time is up.
- A bridge opener is told the anchor is activated with no passengers (`OnGroupJumpAnchorActivated`, EveJS `notifyGroupJumpAnchor`); the fleet is told the bridge is open (`OnBridgeModeChange`, as before). A bridge passage notifies no one (EveJS sends none); a conduit notifies its anchor and passengers (as before).

Increment 3, done (tests: `BeaconHostTests`: A_standard_beacon_that_anchors_inside_an_inhibitors_range_is_never_lit, A_covert_beacon_is_not_stopped_by_an_inhibitor_and_a_standard_one_outside_its_range_anchors, An_inhibitor_placed_later_does_not_unlight_a_beacon_already_lit; decisions in DEVIATIONS "Track A8c"):
- A standard or highsec authorized beacon whose activation falls inside an anchored Mobile Cynosural Inhibitor's 100 km is not lit: its record keeps an activation time of never (`SpaceContainers.NeverLit`), the simulation is not told it is anchored, and a jump to it is refused as not lit. A covert beacon is not stopped; a lit beacon is not unlit by an inhibitor placed later (the wiki: "do not remove existing cynos").

Increment 4, done (tests: `CynoJumpHostTests`: A_revelation_cannot_jump_into_wormhole_space_or_into_pochven..., A_titan_bridge_is_refused_into_pochven...; `BeaconHostTests`: A_beacon_cannot_be_dropped_in_pochven_but_it_may_be_lit_in_wormhole_space...; decisions in DEVIATIONS "Track A8c"):
- Jump drives (the cynosural jump, a bridge's opening, a conduit) are refused into wormhole space and into Pochven (the wiki: "jump drives cannot jump to systems in wormhole space"; "cannot be used to enter Triglavian space, only to exit"). Leaving is not refused. The refusal texts are ours (no reference text for the jump).
- A Mobile Cynosural Beacon cannot be dropped in Pochven (EveJS text: "Mobile Cynosural Beacons cannot be deployed in Pochven."). A beacon may be dropped and lit in wormhole space (the wiki); EveJS refuses it there, which is a recorded deviation.
- The SDE puts Kuharah (30000021), the null-security system of the beacon tests, in Pochven (region 10000070): those tests now use RU-97T (30000377).

Open, each with its blocker (the remaining increments):
- DONE (track A8c, decisions in DEVIATIONS "Track A8c: the cynosural field in Pochven"): a cynosural field of any kind is not lit in Pochven (the wiki's region-wide jammer): `CYNOSURAL_FIELD_JAMMED` at the simulation's activation, text from EveJS. The region is the SDE's and the simulation already holds it, so no wire change. Tests: `deployable::tests::a_cynosural_field_is_not_lit_in_pochven...`, `TargetingTests.A_cynosural_field_refused_by_a_jammer...`. not exercised: a PostgreSQL host test of the Pochven lighting (the rig cannot place a pilot in Pochven), the client.
- Shortcut: a beacon that is lit before a restart is not told to its fleet again (as the field of track A6; the notice is sent when it anchors, not when a ship returns to the system).
- Shortcut: a bridge or a conduit does not survive a restart of the host (track A9, unchanged), so a beacon jump to a bridge opened before the restart is not possible; the beacon itself comes back.
- not exercised: the client and the MITM capture (`tools/mitm/README.md`): the beacon's menu entry, the fleet's notices, the bridge's notice and the jump's arrival at a beacon are not seen in the client. The host jump to an anchored beacon is tested at the host (`A_revelation_jumps_to_an_anchored_beacon...`), the simulation's arrival at a beacon is not driven by a test.

## 36. Track S2c: module bonuses tested at module level, the Crimewatch timer race (2026-10-10)

Done (tests: `ModuleBonusTests` (PgFact: each module fitted with its skills at level five, started, its numbers compared with the SDE), `SiegeModuleTests`, `SpecialHullBonusTests`; Crimewatch `RuntimeTests.A_timer_that_runs_out...`; fit `RulesTests`; decisions and sources in DEVIATIONS "Track S2c").
- The engine carried every tested bonus to the dogma world without a code change: the triage, bastion and capital industrial core locks and impedances, the Shield Command burst of a Command Destroyer (2059 x skill 37615) and of a Sleipnir (1924 x level five), the Covert Ops cloak of a Black Ops hull, and the covert cyno's duration.
- A module that names both ship groups and ship types fits a hull of either (a command burst names both); it was refused when both lists were required.
- Crimewatch timer test: the race was in the test. It waited for the attacker's session to have three packets and then read the victim's session, but the system flag reaches each in-space session in turn. The test now waits for each session's own packet and for the timers' next wait; a probe that pauses the attacker's third send fails the old test and passes the new one. Ten consecutive runs under a CPU burner are green.
- The Blockade Runner: the wiki states no module restriction; none is built.
- not exercised: the client.

## 39. Static configuration recordsets (slice B3, 2026-10-10)

The `config` service answers the client's owner, ticker, alliance, location and station recordsets from the stores and the
static data; an id it does not have has no row (DEVIATIONS.md, "Static configuration recordsets (B3)").

| Status | Item | Missing work |
|---|---|---|
| OPEN | Structure locations in `GetMultiLocationsEx` | a structure has no row until the structures track the owning implementation answers it: `EveJS configService.js:585-599` reads the structure state. |
| OPEN | Agent owners in `GetMultiOwnersEx` | the agent's owner row (EveJS `buildAgentOwnerRow`) needs the agents track (agent names from the static data). |
| OPEN | `GetMapObjects`, `GetMultiGraphicsEx` | EveJS answers both; no V24.01 caller in eve-logic. Add with the caller that needs them. |
| OPEN | Station and system positions for `positions` (`GetPositionEx`, `GetPositionsEx`) | BOUNDARY: space and ballpark the owning implementation. |

Closed in slice B3 (2026-10-10): `GetAverageMarketPrices` answers each type with a recorded sale, from the market's fills (DEVIATIONS.md, "Static configuration recordsets (B3)"); a type with no sale is absent, so the client's `GetAveragePrice` returns None. Tests: `TradingTests`, `ConfigServiceTests`. An empty market gives an empty payload.


## 40. Character selection and entry (slice C, 2026-10-10)

Done in slice C (DEVIATIONS.md, "Character selection and entry (slice C, 2026-10-10)"):
- `populationCap`: the live per-system load, the nearest alternatives, the saved move, the selection refusal (`PopulationCapTests`).
- `multiLoginBlocker.Login`: the computer hash and clone grade are recorded; the Alpha rule refuses a second account on the same computer (`MultiLoginBlockerTests`).
- Host account admission enforces one active client per account across front connections; a later login disconnects the earlier client (`ConnectedSessionsTests`). The headless live walk with two clients on the same account received the replacement reason, observed the old connection close and confirmed the new session remained responsive.
- `ToggleValidation` by role, `GetCharOmegaDowngradeStatus` answered (`ValidationAndOmegaTests`).
- `OnCharacterSelected` expectation in the live walk corrected to zero arguments; new live step `test_the_population_cap_answers_through_its_moniker`.
- Verified against the client and left as they are: `GetCharacterSelectionData` 4-tuple and row fields, `GetCharacterLockType`, `GetCohortsForUser`, the session attribute set, the reward checks, the entry hooks' order.

Open after slice C:
| Status | Item | Owner / where |
|---|---|---|
| OPEN | `charMgr.GetSettingsInfo`: the client's native `Crypto.Verify` needs CCP's signing key; the failure is logged and `LogSettings` is skipped | DEVIATIONS, slice C; needs a decision on signing |
| DONE | Account session exclusivity: a later same-account session replaces and cleanly disconnects the previous client | Host `ConnectedSessions` and `HostServer`; `ConnectedSessionsTests` |
| OPEN | SSO login: the computer hash and the token are not produced, so `multiLoginBlocker.Login` is not reached | slice A gap G1 (DEVIATIONS "Single sign-on login") |
| OPEN | `SelectCharacterID` population check and the entry are not atomic (two selections into a full system can both pass) | DEVIATIONS, slice C |
| OPEN | `populationCap` alternatives are the nearest systems by distance; the reference's neighbours (stargates) need a stargate table in the static data | static data (Slice B follow-up) |
| OPEN | `nes_intro` service (the skip action) | storylines, later slice |
| CLOSED (slice D) | Live suite isolation: the live script now gives the game store and the corporation, market, fleet, industry and asset-safety tables scratch schemas and drops them at its end; `LiveScriptIsolationTests` checks it (DEVIATIONS.md, "Station entry, account requests and the live suite (slice D)") | `tools/dev/run-eve-live.sh` |
| OPEN | Entry path: the achievement answer `achievementTrackerMgr.GetCompletedAchievementsAndClientEventCount` is a hard-coded fixture payload (`LoginInitializationRpcServices.cs`, B2) and is sent at every character entry | achievements slice |
| CLOSED (slice D) | Station entry: `map.GetStationInfo`, `stationSvc.GetStationItemBits`, `invbroker.GetItemDescriptor`, `station.GetGuests` (init-requirements #19-#21) are answered in the client's shapes and tested; `GetPaperdollState` now follows the stored appearance (sliceD-status.md) | Slice D |
| BOUNDARY | `beyonce.GetFormations` (#23) and the space ballpark load; `dogmaIM` station and ship dogma locations (#22) | the ships / space track the owning implementation |
| BOUNDARY | Docked-ship deferred change and fitting bootstrap: implemented in the entry hooks (`SpaceSessions`, `CharacterDogmaSync`); the ship state belongs to the ships track | ships track the owning implementation |


## 41. Station entry, init services and account requests (slice D, 2026-10-10)

Done in slice D (DEVIATIONS.md, "Station entry, account requests and the live suite (slice D, 2026-10-10)"; the ranked table is in the scratchpad `work/sliceD-status.md`):
- Character logoff: `userSvc.UserLogOffCharacter` returns the character to the selection screen on the same connection
  (it used to close the connection). `CharacterLogoffTests`; live `test_a_character_logs_off_to_the_selection_screen_and_selects_again`.
- `charMgr.GetPaperdollState` follows the stored appearance (2 before a doll, 0 after). `CharacterInfoTests`.
- `userSvc.CastUpvote`, `CastDownvote`, `ReportBot`, `ReportISKSpammer`, `ActivateCharacterReSculpt` and `kiringMgr.ActivateRedeemingCode` answer False (nothing is recorded). Tests updated.
- The live suite isolates every schema the host reads (`LiveScriptIsolationTests`).
- The 41 manifest pairs with no implementation: 3 `lookupSvc`, 2 `map` (incursions), 2 `taleMgr`, 2 `scanMgr`, the `paperDollServer` update, `station.GetGuests` and the `warsInfoMgr` methods are implemented and tested (the earlier static list missed them). The rest are structure and shipcaster methods (the other agent): see Open.

Open after slice D:
| Status | Item | Owner / where |
|---|---|---|
| OPEN | Reports: `ingamereport.*` (eight methods) and `eveguard_report.heartbeat_*` are answered and not kept (the reference keeps them in memory, lost at restart) | a support / report store (later slice) |
| OPEN | Votes and reports: `CastUpvote`, `CastDownvote`, `ReportBot`, `ReportISKSpammer` answer False; nothing is recorded | a feedback and report store (later slice) |
| DONE (CH-8) | Re-sculpt: `userSvc.ActivateCharacterReSculpt` consumes a re-sculpt certificate (SDE 34132 or 89956) of the account's character in the transaction that sets the recustomization state (3 for 34132, 1 for 89956; migration 0011 `characters.paperdoll_state`); a save ends the state; `charMgr.GetPaperdollState` answers it. Tests: `CharacterReSculptHostTests` (passed, eve_p4), `CharacterStore` state test in the Host test, live `tools/headless-client/tests/test_resculpt_live.py` (1 passed). Not checked: the client's own save call with a doll payload (the live test does not save). |
| OPEN | Redeeming codes (`kiringMgr.ActivateRedeemingCode`) answer False: no code store | the store slice (S3) |
| OPEN | EveGuard: `ShouldKickCheater` False, `GetEveGuardServerAddress` None; no EveGuard server | anti-cheat (later) |
| OPEN | `bannedwords`: the reference's regular-expression list is empty here; no list source | owner decision |
| OPEN | `alphaInjectorMgr.GetNextAvailableInjection` answers 0 (available now); the alpha injections are not recorded | skill trading |
| OPEN | `contextualOfferMgr.Track*`, `localizationServer` and `communityTrackMgr` keep their state in memory only | the offers, content and community-track slices |
| OPEN | `LPStoreMgr` answers no offers and refuses every take | the agents and loyalty track |
| OPEN | Map reads with no data source: visits, history, loyalty by constellation, roaming weather, my extra map info, deadspace, factional warfare zones answer the empty shape | each owning track |
| OPEN | `map.GetEdencomFortressSystems`, `GetEdencomMinorVictorySystems`, `GetTriglavianMinorVictorySystems`: the reference's constant lists (no SDE or state source; provenance not established) | content / factional warfare |
| OPEN | `map.GetBeaconCount` answers an empty dictionary while the mobile cynosural beacons exist | the ships track (other agent) |
| OPEN | `repairSvc` (repair quotes and repair in a station) is not registered; the repair shop's first use fails | the ships and modules track (other agent): it repairs item damage |
| OPEN | `reprocessingSvc` (`GetStationEfficiencyForTypeID`, `GetCharRefiningYieldPercentageForType`, `Reprocess`) is not registered; the reprocessing window's first use fails | a reprocessing slice (inventory and SDE materials) |
| OPEN | `structure*` and `shipCaster*`/`shipcasterTravelMgr`/`starterShipcasterTravelMgr` methods answer as stubs (no callbacks) | the other agent (structures and ships) |
| OPEN | `paperDollServer` has a second, unregistered class (`Eve.Services.Core/CompatibilityRpcServices.cs`); the registered one is `Eve.Host/CharacterRpcServices.cs` | cleanup (no behaviour) |

## 42. Market system, slices M1 to M9 (2026-10-10)

Design and decisions: scratchpad `work/market-design.md` (sections 4.3 and 5) and `work/market-decisions.md`; the recorded decisions are in
DEVIATIONS.md ("Market slice M1" and the M2/M3 sections when written).

Done in M1 (DEVIATIONS.md, "Market slice M1"):
- The five missing marketProxy RPCs: BuyMultipleItems, PlacePlexSellOrder, ModifyPlexCharOrder, CharGetTransactions, CorpGetTransactions.
  All 27 client-called methods are registered (`MarketHostTests`).
- The order's division in keyID (G1); the station and system ask summaries as KeyVal rows (Q13, G10).
- Client labels for MktBrokersFeeUnexpected2 and MktInvalidMinVolumeCannotExceedQuantity; other refusals are sentences, not codes (G6).
- The fabricated immediate-fill notification is removed (G8); the cache invalidation that replaces it is M4.
- Tests: `MarketRpcTests` (9), `ProtocolTests` (updated), `MarketHostTests` (the real composition, restart), live `test_market.py`.

Open after M1:
| Status | Item | Owner / where |
|---|---|---|
| DONE (M2) | NPC station sales tax 7.5% base, reduced by Accounting; separate structure broker-rate calculation (default 1%) and 0.5% SCC surcharge; modification delay 300 s with `last_modified_ms` (Q3), duration reset (Q4), price ticks (Q12), matching by price then issue time (Q5), stale-view refusal (Q15), 5 s duplicate refusal (Q14); `RulesTests` replays the decided fixture and a structure-specific rate regression covers the distinction | DEVIATIONS.md, "Market slice M2"; `MarketRules.StructureContext` |
| DONE (M1/M2) | Failed sell placement is transaction-atomic: injected order-write failure restores wallet and inventory; retrying the same operation key charges once. Buy Multiple validates the V24 lines and places them as one serializable transaction; later-line failure rolls back the batch, and retry/replay are covered through service and RPC tests | `MarketTrading.PlaceWithinTransactionAsync`, `PlaceBuyOrdersAsync`; `TradingTests`; `MarketRpcTests`; DEVIATIONS.md, "Market slice M10" |
| OPEN | Client preview of the sales tax: the client reads `market-transaction-tax` from LaunchDarkly (its own SDK key), so eve.rs cannot serve 7.5; the server charges 7.5, the client may preview 8.0 | the client's flag source (Q1) |
| OPEN | Client preview of the highsec security tax (5%, `security/common/const.py:6`): the server charges none (Q11) | confirm in game (Q11) |
| OPEN | Matching priority (price, issue time, id) and the duplicate window are to be confirmed with a live match (Q5, Q14) | the live acceptance (M9) |
| DONE (M3) | Expiry as durable jobs: one job per resting order at its expiry plus 1 s (placed, moved, cancelled in the same transaction), a 60 s safety sweep, a read sweep at most once a second, the restart reschedule, notices to the owner and the corporation members with market rights (Accountant or Trader) | DEVIATIONS.md, "Market slice M3" |
| DECIDED (M4) | M3 notices are not durable: no outbox, a notice goes to a live session only and the client's cache is per run (balances and state stay right) | DEVIATIONS.md, "Market slice M4" |
| DONE (M4): the affected-project gate is green on `2f80397`; the live run passed at `f046112` (70 passed, 8 skipped by the slow-mode gates, 0 failed). Push invalidation and corporate notices (G9, G11); the client's 3-hour price history cache is out of the server's reach | DEVIATIONS.md, "Market slice M4"; `MarketCoherenceTests` (5) |
| DONE (M5) | Price history: one row per UTC day (low, high, volume-weighted average, volume, fills), the day in progress as "new" (the reference's last-row split put a past day there; corrected), `HistoriesAsync` removed (no caller) | DEVIATIONS.md, "Market slice M5"; `MarketHistoryTests` (6) |
| DONE (M6) | Transactions by division: rights (accountant, junior accountant, Account Take, CrpAccessDenied 101), newest first, fromDate and division filters, keyID; the EXPLAIN check is not exercised (no index added) | DEVIATIONS.md, "Market slice M6"; `MarketTransactionReadTests` (3) |
| DONE (M7) | Bounded order-book read: batches of 50 in book order, stop when filled; 10000-order benchmark (before 9999 rows locked per placement, after 50); no index added (EXPLAIN) | DEVIATIONS.md, "Market slice M7"; `OrderBookMatchTests`, opt-in `OrderBookBenchmarkTests` |
| DONE (M8) | Invariants (escrow per wallet, escrowed items per sell, fill legs, order sanity) as a reconciliation the expiry job runs every 10 minutes and logs on mismatch; concurrency tests (modify and cancel racing a fill); documented lock order, with the two entry points that broke it moved; Q14 checked | DEVIATIONS.md, "Market slice M8"; `MarketInvariantTests`, `MarketConcurrencyTests`, `MarketLockOrderTests` |
| DONE (M9) | Live walk: place, match, transactions, modify (refusal, and the change after the delay under EVE_LIVE_SLOW=1), cancel, escrow, corporation orders and refusal; the NPC seed import path has a test | DEVIATIONS.md, "Market slice M9"; `test_market.py`; `MarketSeedImportTests` |
| OPEN | The client label table is partial: `MktCantSellItem2`, `MktCantSellItemOutsideStation` and `MktInvalidRegion` need values the server does not hold (localized jump texts) | the client's localization |
| OPEN | The corporation read rule has a test for a character outside the corporation; a member holding Account Take and not an accountant is covered by M6 | M6 |

## Agents slice AG-1: LP wallet, access rule and bound agentMgr (2026-10-10)

Done (DEVIATIONS.md, "Agents slice AG-1"): LPSvc (balances, notifications, PostgreSQL journal), the client's agent access
rule (`AgentAccessRule`), the bound agentMgr (`MachoBindObject`, release). Design slices of `ess-insurgency-agents-missions-design.md`
section 4.7: A8 done for balances and notifications; A2 done as a rule; A3 done as a binding; A1 partly; A4 to A7 and A9 open.

| Status | Item | Where / blocker |
|---|---|---|
| DONE | LPSvc: `GetAllMyCharacterWalletLPBalances`, `GetAllMyCorporationWalletLPBalances`, `OnLPChange`, `OnCorpLPChange` on commit | `LoyaltyPointsService`, `LoyaltyPointStore` (migration 006), `LoyaltyPointAnnouncer` |
| OPEN | `ExchangeConcordLP`: refused; the CONCORD exchange rate and rule are not in the sources read | design 4.6; EveJS stub `lpService.js:166-168` |
| OPEN | Donations `TransferLPFromMyWalletToOtherCorp`, `TransferLPFromMyCorpWalletToOtherCorp`: refused; need EveJS's destination, Evermark, director and membership rules | `lpWalletState.js:610-740`; client `personalDonateLPPanel.py`, `corpDonateLPPanel.py` |
| OPEN | No credit source: missions (Mi5), factional warfare and insurgency (I5) must call `LoyaltyPointStore.ApplyAsync` in their transaction | design 4.6, 5.5 |
| OPEN | LP store (A9): `LPStoreMgr` still answers no offers and refuses every take | design 4.7 A9; `CoreCompatibilityServiceAdapters.cs` |
| OPEN | Access rule (A2) has no client-visible consumer yet; implants are not modelled in the skill bonus | `AgentAccessRule`; consumers A4, A5, Mi3 |
| SOURCE FIXED (aab0dd10; not full Agent acceptance) | Bound agentMgr resolve and initial nested calls | Current MachoResolveObject and HasMethod-based nested dispatch are present; the old empty/DoAction-only BoundMethods finding is superseded. Remaining 13 unimplemented agentMgr exits (plus 1 client-local alternative and 2 legacy names) are separated in the EveJS response audit backlog; do not reopen the fixed binder. |
| OPEN | Dialogue `DoAction` (A4): greeting, view, request, accept, decline and quit; needs offers (A5) and the dialogue texts of EveJS `agentMgrService.js:906-1120` | design 4.7 A4 |
| OPEN | Journal and offers (A5): offers from the pool, mission state writes, `OnAgentMissionChange`, `OnMissionsUpdated` | design 4.7 A5; `MissionComposition.cs` (read-only today) |
| OPEN | Research agents (A6), locator (A7, fee unknown Q-AG-6) | design 4.7 |
| OPEN | A1 check: the GetAgents rowset against the SDE counts (10966 agents, 360 in space, 1773 locators) is not yet a test that reads the SDE | `AgentComposition.NpcAgents`; the SDE is in PostgreSQL schema `sde` |
| OPEN | Headless walk: the balances and the refused exchange are not yet in `tests/test_login_to_character.py` | the live run |
| CLOSED | `LPSvc` is no longer in the list of names the core registry leaves out (the Host registers it; `ServiceAdvertisementTests`) | `ServiceAdvertisementTests.cs` |
## Agents slice AG-2: research, the rule check and the locator (2026-10-11)

Web research is recorded in DEVIATIONS.md, "Agents slice AG-2". Sources read: ESI OpenAPI (CCP), the EVE University wiki
(Agent, Loyalty_Points, Locator_Agents). CCP's support and developer pages were not reachable (403, 404).

| Status | Item | Where / blocker |
|---|---|---|
| DONE (research) | Sources read and compared with the access rule: thresholds, the -2 floor and the research rule agree; the wiki's skill bonus target is wrong and the client (all three standings) wins | DEVIATIONS "Agents slice AG-2" |
| OPEN | R&D missions: wiki "only two missions, a trade and a courier" vs CCP 2011 research missions (Q-AG-7) | design 4.8 |
| OPEN | LP store offer data: no LP offer rows in the pinned SDE build 3396210 and none in eve-logic (A9 source gap) | DEVIATIONS "Agents slice AG-2" |
| OPEN | Research rates and datacore price: no source read (A6) | DEVIATIONS "Agents slice AG-2" |
| OPEN | CONCORD LP exchange: wiki rates exist but no CCP source; stays refused | DEVIATIONS "Agents slice AG-2" |

Slice A5 (2026-10-11): DONE for the store and the journal: the `agents` schema (`Eve.Agents`, migration 0001), one state
machine with idempotent references, the journal view overlaid on the game store, `OnAgentMissionChange` on commit.
Tests: `Eve.Agents.Tests` (8, PostgreSQL), `Eve.Services.Core.Tests` (AgentMissionStoreTests, 3). Decisions: DEVIATIONS.md,
"Agents slice AG-2, A5".

| Status | Item | Where / blocker |
|---|---|---|
| OPEN | No live path creates an offer: the mission pool is not in the SDE build; `OfferAsync` is tested only | the mission-data track (second SDE pin) |
| OPEN | The expiry sweep is not scheduled (durable job) and `OnMissionsUpdated` is not sent | DEVIATIONS "Agents slice AG-2, A5" |

Slice A4 (2026-10-11): DONE for the dialogue on the bound agent: `DoAction` answers the reference's reply shape; the greeting,
the standing refusal (read from the relations and the skills), request (through the store), accept, decline, quit at the
station; research and the locator refused by name. Tests: `AgentDialogueTests` (Eve.Services.Core.Tests, 6, PostgreSQL).
Decisions: DEVIATIONS.md, "Agents slice AG-2, A4".

| Status | Item | Where / blocker |
|---|---|---|
| OPEN | Decline and quit confirmations (YesNo, deferred response) are not ported; the transition is made on the button | DEVIATIONS "Agents slice AG-2, A4" |
| OPEN | The mission briefing (`GetMissionBriefingInfo`, objectives, offer text) is not bound: no mission content yet | mission-data track |
| OPEN | The headless walk does not yet call DoAction (the live run covers the Services.Core tests only) | tools/headless-client |

Slice A9, part 1 (2026-10-11): DONE for the offers: ESI's official offers cached daily (offline switch `EVE_LP_STORE_SEED=skip`),
`lp_store_offers` (wallet migration 007), `GetAvailableOffersFromCorp` as the reference's rows; the stub is gone. Tests:
`Eve.Wallet.Tests` (LpStoreOffersTests, 4), `Eve.Services.Wallet.Tests` (LpStoreMgrServiceTests, 1). Decisions: DEVIATIONS.md,
"Agents slice AG-2, A9 (part 1)".

| Status | Item | Where / blocker |
|---|---|---|
| DONE (AG-3) | Take an offer for the character and for a corporation (Director): LP, ISK, required items, the granted item, one transaction, journal, notifications. ISK reference type 26 from the client constant | DEVIATIONS "Agents slice AG-3, A9 (part 2)"; `LpStoreMgrServiceTests` |
| OPEN | Standing requirements of LP store offers: ESI returns none, so none is shown | DEVIATIONS "Agents slice AG-2, A9 (part 1)" |

## Agents slice AG-4 (2026-10-11)

A4 client dialog path done: deferred `DoAction` with the client's YesNo (`SendClientCall`, `Provisional`); decline and quit
confirmations use it (DEVIATIONS "Agents slice AG-4, A4").
A6 research buttons 12 to 14 done: field choice, cancel, datacores from the SDE link, the purchase in one transaction (DEVIATIONS
"Agents slice AG-4, A6 ..."). Open: research missions.
A7 locator 15 to 17 done: the name, the offer with the fee, accept (fee and cooldown in one transaction, result notification type 69
after the commit, refund if it cannot be written), reject (DEVIATIONS "Agents slice AG-4, A7"). Open: the client's localized labels for
the dialog, the locator's standing split (faction and corporation). Done in AG-5 (DEVIATIONS "Agents slice AG-5"): the result is an
outbox event (no refund), the live walks for LP store, research, locator and decline, the dialog kwargs and string fixes. Open: the quit
walk, flying the Cockroach, the EVE.js side of the MITM capture, the client's localized labels.

## Agents slice AG-3 (2026-10-11)

Continues AG-2 on the same branch. A9 part 2 is done (DEVIATIONS "Agents slice AG-3, A9 (part 2)").
A1 done: the agent catalog is a pure function (`AgentCatalog.Build`, Eve.Host) that the composition calls; `AgentCatalogSdeTests`
(2, PostgreSQL, the pinned SDE): 10,966 agents by agentTypeID, 1,773 locators, 360 in space, and two concrete agents
(3008416 basic, 3019369 in space). Open after it: the
standing requirements of store offers and the structure stores (gaps), the idempotency key the client does not send.
Offer cross-check (eve.starweb.cc): no rows changed; 1,104 calculator-only offers are an owner question (DEVIATIONS AG-3).
A6 (research): rules and the PostgreSQL project store done (`ResearchRules`, `ResearchProjectStore`, migration 0003); the buttons 12 to 14 and the datacore grant and research missions are open (client dialog path; DEVIATIONS "Agents slice AG-3, A6 and A7").
A7 (locator): fee, cooldown and standing rules done (`LocatorRules`); the locate flow stays refused (target location store and client dialogs; DEVIATIONS AG-3).

## 43. Structures phase ST-E: remaining E wiring (2026-10-10; ST-E2 2026-10-15)
- DONE E-01: StartNewExtraction, FractureChunkWithStructure and CancelExtraction refuse by name (CustomNotify) with no backend, not PyNone (`StructureDataRpcServices.cs`, commit 59e9e89). The backend is ST-6a.
- DONE E-04 proximity: the sim breaks a cloak that has a ship or a non-belt body within 2 km of its surface and sends `OnCloakModuleForceDeactivated` (`CloakDeactivatedByProximityToType`) before the module stops (`crates/eve-space/src/scene/decloak.rs`, polled in `scene/session.rs`). Activation refuses within the same 2 km (`modules/activation.rs`). Tests: `modules::tests` (ship, station, custom); host `SpaceServiceTests.A_forced_decloak_notice_reaches_the_pilot_with_the_message_and_its_arguments`. No protocol bump: the notice is an existing notify.
- DONE E-04 custom (ST-E2, protocol 72): a Mobile Observatory the pilot launched from the hold pings its system every ten minutes once anchored (`space.deployed.observatory_ping`, `SpaceContainers.PingObservatoryAsync`); the sim's `RemoteDecloakPing` decloaks each cloaked ship not in cloak stabilization with a 40% roll and sends `OnCloakModuleForceDeactivated` with `CustomNotify` and reason `mobileObservatory` (`crates/eve-space/src/scene/decloak.rs`, `remote_decloak_ping`). Tests: `modules::tests` (roll under and over 40%, not anchored), Host `MobileObservatoryHostTests` (anchor and ping timing, expiry, restart, skill, Pochven, high-sec, spacing; ST-13: 1 000 km from a station, stargate or Upwell structure, and Abyssal refusal), codec fixtures. ST-13 (2026-10-15): the 1 000 km landmark rule and the Abyssal refusal are done (DEVIATIONS, ST-E2). Open for ST-E2: the observatory's slim-item attributes (component_activate, decloak emitter) are not sent, the client ball is MITM only, and the wiki's 100 km control-tower rule is not implemented (eve.rs has no player starbase positions).
- DONE E-02 assertion: OnTarget lost, otherlost and clear reach the pilot session in order (host test `The_target_lock_notices_and_the_damage_state_action_reach_the_pilot_as_the_simulation_sent_them`). The client reaction is MITM only.
- DONE E-05 assertion: the OnDamageStateChange destiny action reaches the session in DoDestinyUpdate with its arguments (same test). The progression widget is MITM only.
- Owner decision 2026-10-19: E-07 stays held on RC-5 weapon-bank grouped-activation semantics. E-15 is in ST-7. E-06 stays open for the result source (ST-11).
## 44. Structures phase, ST-0: the structure record owner, the timeline and the timers (2026-10-10, closed)

Package ST-0 of `structures_phase.md` (the scratchpad plan); decisions in DEVIATIONS.md "Structure timeline (ST-0)". Handoff: `docs/project/handoff-ST-0.md` (its worktree is removed; the code is on main).

| Status | Item | Evidence |
|---|---|---|
| VERIFIED (tests) | The pure timeline `StructureStateMachine`: anchor, anchoring, onlining, shield, armor and hull stages, reinforcements, Low Power, abandonment, unanchoring, reinforcement time change | `dotnet/tests/Eve.Structures.Tests/StateMachineTests.cs` (47) |
| VERIFIED (tests) | Schema `structures` (migration 0001), `StructureStore` (read, write, legacy view, import), `StructureTimers` (job `structure.timer`; event `structure.state_changed` in the change's transaction; refusal changes nothing; restart fires once; import once) | `StructureTimersTests` (6, PostgreSQL, scratch schemas) |
| VERIFIED (tests) | SDE rules: size from `rigSize` (typeDogma 1547), band from `mapSolarSystems` (Wormhole, Pochven, null, low, high) | `StructureRulesSourceTests` (2, pinned SDE) |
| VERIFIED (tests) | Host start-up: the schema is migrated and the GameStore `structures` table is copied once; the readers use the store's legacy view (`ReadGameStoreTableAsync`); the description and the profile change write the store, and the GameStore `structures` table is no longer written; the timers are registered before the foundations start and the imported deadlines are scheduled | `ArchitectureTests` (`Eve.Structures` listed), `LiveScriptIsolationTests` (`EVE_STRUCTURES_SCHEMA`), `StructureDataRpcPersistenceTests`, `PersistenceStructureHangarViewRpcTests`, Services.Core structure tests (78) |
| VERIFIED (tests) | Through the real composition (`HostComposition.BuildAsync`, scratch schemas, restart): a description set through `structureDirectory.SetStructureDescription` is read back through `GetStructureDescription` after a restart, and the profile change is in the row (profile 7); an imported structure with an elapsed anchoring deadline advances exactly once after start (version 2, `StateEndsMs` = deadline + one day), and a restart replays nothing | `StructureCompositionTests` (2) |
| VERIFIED (tests) | `EVE_PG_URL` form: the URL form (`postgres://postgres@127.0.0.1:5432/eve`, what `tools/dev/pg18.sh url` and the README print) is the form to use. `psql` (the live script's cleanup, `tools/dev/run-eve-live.sh`) accepts only the URL form and rejects the key=value form (`invalid connection option "Host"`); its cleanup error is hidden by `2>/dev/null || true`, so a key=value value would leave the scratch schemas behind. The host and the tests read the URL through `PgConnection.ToConnectionString`, which accepts both. Before this step 13 Host tests passed the URL straight to `NpgsqlDataSource.Create` and failed; they now convert it. The Host suite passes with the URL form (167) | `tools/dev/run-eve-live.sh` line 38; `dotnet/src/Eve.Data/PgConnection.cs`; `Eve.Host.Tests` |
| VERIFIED (tests) | `structureProfiles` writes on a store with no profile table: the cache is loaded by the owner before the write (`PersistenceStructureProfilesRpcBackend.WriteTableAsync`), so the first profile change or Anchor creates the corporation default profile (was `TABLE_NOT_FOUND`). The test seeds no profile table; the default survives a restart and the structure row carries it | `StructureCompositionTests.A_fresh_store_creates_the_corporations_default_profile_on_the_first_profile_change_and_keeps_it_after_a_restart` (red before the fix); 12/12 StructureCompositionTests pass | ST-2 remainder |
| DONE (tests; native client not exercised) | The sim structure ball, the state changes to the simulation and the `structure.state_changed` notices | Host `SpaceStructures` sends the state changes to the simulation (`SpaceStructuresTests`); the `structure.notify` notices (`StructureNotificationsTests`, row 1467). The client view is not exercised. | `structures_phase.md` ST-1, ST-2 |
## 45. Track ST-11: cosmic sites as content, the director and the probe source (2026-10-10)

Plan: scratchpad `specs/structures_phase.md`, ST-11. Handoff: `docs/project/handoff-ST-11.md`. Decisions: DEVIATIONS.md, "Cosmic sites (ST-11)".

| Status | Item | Evidence |
|---|---|---|
| BUILT (tests) | Increment 1, not bound in the host: the site definitions (`anomaly`, `signature`, `site.{id}`) from the static table `Sites/Data/site-templates.json` (419 templates, from `tools/dev/extract-site-templates.py`); `CosmicSiteDirector` (the recurring `site.spawn` job per system, the slots `system:{id}:slot:{n}`, the respawn subscriber on `content.{anomaly,signature}.{completed,expired}`); `ICosmicSiteSource` async, implemented by `ContentCosmicSites` (signatures with their code and level); the scan rows carry the code and the dungeon fields; the warp authority keys results by text. | `CosmicSitesTests.cs` (CosmicSiteRulesTests: codes, placement, template coverage, probe strength; CosmicSiteDirectorTests, PgFact: one site per slot once and idempotent, restart keeps them, expiry replaces each site in its slot elsewhere, the probe source lists the signatures with their codes). Run: `dotnet test Eve.Services.Space.Tests --filter CosmicSite`, 11 passed, 0 skipped, EVE_PG_URL set. Expiry replacement regression passed on `eve_p3`; `Eve.Scheduler.Tests.A_burst_of_due_jobs_finishes_with_bounded_scheduler_latency` passed 512 due jobs below 20 seconds with concurrency 8 and batch size 64. This measures scheduler load separately from multi-system site expiry. |
| BUILT (tests) | Increment 2, the host binding: `SpaceComposition` attaches the director before `foundations.StartAsync`, `SystemEntered` asks for the sites, `SpaceProbeScans` gets `ContentCosmicSites` instead of `NoCosmicSites`, the anomaly read model asks first, `BeyonceService` warps to a result by code. The Eve.Host build is clean. Not tested through the Beyonce rig with a content-backed source. | `CosmicSitesTests` (warp authority by code); the full `Eve.Services.Space.Tests` (642 passed) and `Eve.Host.Tests` runs; handoff |
| BUILT (tests) | Clear and respawn: a site cleared by kills published as `npc.killed { site }` and its containers completes; the respawn replaces it in its slot elsewhere; a restart in the middle keeps the progress. The kills are published by the test; no NPC is spawned yet. | `CosmicSiteDirectorTests.A_site_cleared_by_its_npcs...` (PgFact) |
| BUILT (tests) | Probe results of a signature by its level: a probe of strength 42 covers a level I to III signature in full, a level IV one by 42/72 and a level V one by 42/104; a weaker probe finds it in part (certainty below 25 %, a result that is not perfect); a probe that does not reach it leaves it alone (no row, not absent). | `SignatureScanResultTests` (Theory and Fact, pure) |
| BUILT (tests) | NPC waves of the sites: a wave is made in the scene of its system when a pilot is there and no NPC of the site is in it, with the kills still to come (the target less the progress); a kill of a site's NPC carries `site` in `npc.killed`; the next wave is made at the site when one is done; a scene made again brings back what is left; a system nobody is in gets no site or wave (downtime). Not compared with the reference: placement (5 km and a formation), the identities of survivors. | `SiteNpcTests` (first wave once at the site; a kill counted and the next wave at the site, `site` in the kill; a scene made again brings back the kills still to come; no wave without a pilot; every wave query names an NPC) and `CosmicSiteDirectorTests.A_system_nobody_is_in...`; DEVIATIONS "Cosmic site NPC waves" |
| OPEN | Containers of a data and relic site (groups 306, 1207; the site's containers are the data and relic ones only, combat sites have none): place through `SpaceContainers` and emit `site.container.cleared` when cleared. Blocked by ST-10: a container is opened by the data or relic analyzer (`ExplorationModuleEffects.cs`, body pending) or by hacking, and the contents are not in the static data. | ST-10 (analyzer and hacking), the loot of the containers |
| OPEN | Level from the client difficulty above 5 (ten templates) and the downtime rule of the respawn. | DEVIATIONS "Cosmic sites (ST-11)" |
| OPEN | The reference's anchor set (planets, belts, stations, stargates; no sun, no moons) is not compared with EveJS. | DEVIATIONS "Cosmic sites (ST-11)" |
| OPEN | Client check (MITM): the `ABC-123` codes in the scanner list, the anomaly overlay (`scanMgr`), the dungeon fields and the warp to a result by code. | not provable headless |

## 43. Mission static data, slice Mi1 (2026-10-11)

Done: the official mission build (3405148) is a second pin in `sde/lock.json` (`missionPinned`); `eve-sde ensure --missions`
loads its `missions` and `epicArcs` into schema `sde_mission`; `MissionStaticCatalog` reads them; the EveJS authority
source and its `EVEJS_*` variables are removed from the Host; the agent mission kind and importance come from the eve.rs
rule table (`AgentMissionKindRules`), not from EveJS. DEVIATIONS "Mission static data" has the sources and the decisions.

| Status | Item | Where / blocker |
|---|---|---|
| DONE | Second pin 3405148 (first build with `missions` and `epicArcs`) | `sde/lock.json`; DEVIATIONS "Mission static data" |
| DONE | Loader for the two tables into `sde_mission`, tests with synthetic fixtures and an opt-in real-data test | `crates/eve-sde/tests/missions.rs` (`EVE_SDE_MISSION_DIR`) |
| DONE | Read side replaces `EveJsMissionAuthorityDataSource`; `EVEJS_*` no longer read | `MissionStaticCatalog.cs`, `MissionComposition.cs` |
| DONE | Agent mission kind and importance from the rule table | `AgentMissionKindRules.cs`, `AgentComposition.cs` |
| OPEN | Client check of the mission build against the client's `missions.fsdbinary` (not in this environment) | DEVIATIONS "Mission static data" (build newer than client) |
| OPEN | Epic arc count: 21 official, nine on the wiki, seven in the design file | owner decision; DEVIATIONS |
| OPEN | Basic, storyline, research, FW, heraldry pools: no official agent-to-mission link | `MissionDataGaps.BasicPools`; Mi2 cannot offer missions until a source exists |
| DONE (exception) | Levels: 1,792 tagged missions with provenance (client 1,019, tq-log 13, wiki 346, rule 414) under the owner's approval for four fields; 1,100 official missions have no tag and answer unknown | `MissionStaticCatalog.Level`, `Data/mission-level-tags.v1.json`; DEVIATIONS "Mission static data" | `MissionStaticCatalog.Level`, `Data/mission-level-tags.v1.json`; DEVIATIONS "Mission static data" |
| OPEN | Kill mission dungeon content: `dungeonID` is not a row of `dungeons` (3 of 1,460) | `MissionDataGaps.KillDungeonContent`; dungeon owner |
| OPEN | Epic arc journal text IDs | `MissionDataGaps.EpicJournalText`; the client shows no chapter titles |
| OPEN | LP reward per mission and the LP store standing requirement | `MissionDataGaps.Standings`; the LP store track owns the offers |

### 43a. Owner decisions and the hand-off to Mi2

Recorded:
- **Exception, client and TQ-log level tags committed (owner-approved 2026-10-11).** 1,019 `client` and 13 `tq-log` values are in
  `Data/mission-level-tags.v1.json` with the wiki and rule rows (1,792 rows). Limit: mission ID, level, provenance class, confidence
  only. The exporter that made the client values is not available, so the file is the only record. DEVIATIONS "Mission static data"
  has the full entry. Not decided: whether any other derived data may be committed on the same ground (the owner's decision each time).
- **Owner's heuristic rows.** 23 cargo-volume, 4 default level-1 and 22 related-chain values are committed as `rule` rows with low or
  medium confidence. The owner's document (section 8.2) calls them heuristic; AGENTS.md:18 forbids guessed data. Open: keep or remove.

Open for the repository owner:
- **Epic arc count** (21 official, nine on the wiki, seven in the design file): see the section above.

Hand-off to Mi2 (not ported here; file:line checked against EveJS HEAD 828823a):
- Pool selection: `server/src/services/agent/missionPoolResolver.js:69-116` (`resolveAgentRoute`: heraldry, research, epic arc,
  agent-specific, storyline, else kind route), `:61-67` (`routeForKind`), `:165-184` (`constraintsMatch`: agent type, corporation,
  faction, division), `:186-210` (`routeMatches`), `:216-290` (`resolveMissionPool`: a courier, mining, trade or encounter pool
  requires the mission's authoritative level to equal the agent's level, `:264-275`), `:17-43` (`getAuthoritativeMissionLevel`).
- Pool assembly: `server/src/services/agent/missionAuthority.js:890-909` (`listTemplateBoundMissionIDsForAgent`), `:911-975`
  (`buildMissionPoolCandidate`; sets `clientDungeonAvailable` and `validContent`), `:977-1022` (`listPreferredMissionCandidateIDs`),
  `:1024-1042` (`listMissionIDsForAgent`), `:863-873` (`filterUnsupportedLevelOneAgentMissions`), `:656-681` (`listMinableMissionDungeonIDs`,
  the mining gate, which uses client `populationHints.miningRocks`).
- Dungeon-template selection: `server/src/services/agent/agentMissionRuntime.js:497-509` (`getMissionTemplatePool`, which filters
  `siteFamily === "mission"`, a value that no template carries), `:570-671` (`scoreMissionTemplateCandidate`: name-token overlap,
  +240 for a source dungeon match, +600/+300 for mining rocks), `:673-781` (`resolveMissionTemplateForClientMission`).
- Level rules (for Mi2's own tagging, if the owner's files are used): `missionLevelResolver.js:185-340` (`missionLevel`, the rule order),
  `:40-59` (chain levels), `:63-149` (distribution volume bands), `:151-183` (mining titles). Reward constants for Mi5:
  `agentMissionRuntime.js:207-222` (`REWARD_BY_SCALE_AND_LEVEL`, from captured logs; not committable).

### 43b. Epic arc availability (owner's instruction, 2026-10-11)

The server offers all 21 official arcs: the nine the V24.01 client knows (`in-client-build`) and the twelve Pilot Certification Course arcs (`in-client-build` by owner confirmation; the owner states they exist in the game). `EVE_MISSION_INCLUDE_UNAVAILABLE_ARCS=1` still offers any arc that is not `in-client-build` (none today).
Mission availability follows the arc (a step takes its arc's status); a mission that is no arc step is `unknown`. DEVIATIONS
"Epic arc availability" has the evidence and the method.

| arc ID | availability | official steps | evidence |
|---|---|---|---|
| 29 | in-client-build | 64 | V24.01 client constant (eve-logic epicArcConst.py) |
| 40 | in-client-build | 40 | V24.01 client constant (eve-logic epicArcConst.py) |
| 48 | in-client-build | 24 | V24.01 client constant (eve-logic epicArcConst.py) |
| 52 | in-client-build | 27 | V24.01 client constant (eve-logic epicArcConst.py) |
| 53 | in-client-build | 19 | V24.01 client constant (eve-logic epicArcConst.py) |
| 55 | in-client-build | 20 | V24.01 client constant (eve-logic epicArcConst.py) |
| 56 | in-client-build | 22 | V24.01 client constant (eve-logic epicArcConst.py) |
| 64 | in-client-build | 4 | owner-confirmed (the owner states the arc exists in the game); aura agents (type 11), not in the V24.01 constants |
| 67 | in-client-build | 4 | owner-confirmed (the owner states the arc exists in the game); aura agents (type 11), not in the V24.01 constants |
| 68 | in-client-build | 4 | owner-confirmed (the owner states the arc exists in the game); aura agents (type 11), not in the V24.01 constants |
| 69 | in-client-build | 4 | owner-confirmed (the owner states the arc exists in the game); aura agents (type 11), not in the V24.01 constants |
| 70 | in-client-build | 4 | owner-confirmed (the owner states the arc exists in the game); aura agents (type 11), not in the V24.01 constants |
| 71 | in-client-build | 4 | owner-confirmed (the owner states the arc exists in the game); aura agents (type 11), not in the V24.01 constants |
| 72 | in-client-build | 4 | owner-confirmed (the owner states the arc exists in the game); aura agents (type 11), not in the V24.01 constants |
| 73 | in-client-build | 4 | owner-confirmed (the owner states the arc exists in the game); aura agents (type 11), not in the V24.01 constants |
| 74 | in-client-build | 4 | owner-confirmed (the owner states the arc exists in the game); aura agents (type 11), not in the V24.01 constants |
| 75 | in-client-build | 4 | owner-confirmed (the owner states the arc exists in the game); aura agents (type 11), not in the V24.01 constants |
| 76 | in-client-build | 4 | owner-confirmed (the owner states the arc exists in the game); aura agents (type 11), not in the V24.01 constants |
| 77 | in-client-build | 4 | owner-confirmed (the owner states the arc exists in the game); aura agents (type 11), not in the V24.01 constants |
| 128 | in-client-build | 3 | V24.01 client constant (eve-logic epicArcConst.py) |
| 132 | in-client-build | 8 | V24.01 client constant (eve-logic epicArcConst.py) |

Hand-off to the next slice: offer missions through `MissionStaticCatalog.PlausibleMissionIds` (already filtered), and check
`MissionAvailability(missionId)` for any mission that is not an arc step before using it (its `unknown` answer has the reason).

## 46. Track A-W1: dungeon NPC waves on the content runtime (2026-10-11)

Status: the wave runtime is implemented and tested at the host level (PostgreSQL, a fake clock, and the real space sessions with the simulation's
kill reports). No client route starts a dungeon yet; the entry point is `DungeonWaveDirector.StartAsync` (Eve.Services.Space/Waves), for the
L5 kill mission (A-X1) to call. Not verified with the graphical client.

- Planner and rules: `Eve.Dungeons/Waves/WaveRules.cs` (`WavePlanner`, `WaveRules`) and `Data/waves/v1/trigger-rules.json` (embedded, keyed by
  the official dungeon id, each rule with its source). Triggers implemented: `on_load`, `wave_cleared` (all NPCs of the wave killed), `timer`
  (a delay from a rule or from `countdownSeconds`). Anything else (room, gate, hack, entity kill, object, visible countdown) refuses the dungeon
  with the reason. Breeding Facility (2158) is the first rule: wave 1 after 240 s (wiki).
- Composition: each wave's spawn query must be an exact pool or profile name of the NPC authority (aliases and derived standard pools count);
  anything else refuses the start with the name. Only `parity_drifter_dungeon_family` (109 mission waves) is unresolved in the owner data.
- Runtime: one content definition per plannable dungeon (`dungeon.{id}`), one sequential objective per wave (target = NPC count). A wave's
  objective schedules its spawn job when it becomes the one to do. Kills come from `npc.killed` (ship id to wave in `dungeon_wave_npcs`), each
  counted once. Tables: `dungeon_wave_spawns`, `dungeon_wave_npcs` (migration 001 in the space schema).
- Sweep (every 10 s): ends a dungeon whose pilot is no longer in space in its system, and takes out the NPCs of every ended dungeon
  (completed, failed, abandoned, expired).
- Coverage (owner data, 1,460 kill-mission dungeons, planner checks re-implemented, not exercised): 1,404 plan; 56 are refused (on_room_active 6,
  timer without delay 3, no waves 38, numbering 2, no template 7). The composition is not yet checked against the authority for all of them.
- Open after A-W1: object_destroyed and proximity triggers (A-W2), entity_killed, room and hack triggers, reinforcements, the bonus window,
  the L4/L5 composition table (D-D1), and a real-client run.
- Known gaps: a crash between a wave's simulation spawn and its commit can spawn that wave twice (at-least-once); NPCs alive in the simulation
  across a host restart are not re-attached (the dungeon's lifetime bounds the stall); the timer of a wave starts when the wave before it is
  cleared.
- Tests: Eve.Dungeons.Tests `WavePlannerTests` (17); Eve.Services.Space.Tests `DungeonWaveTests` (9 scenarios), `DungeonWaveSpaceTests` (1),
  `WaveCompositionTests` (7).
## 46. Structures phase, ST-7: service fuel, compression plant, cyno jammer, cyno beacon (2026-10-11)

Package ST-7 of `structures_phase.md`; handoff `docs/project/handoff-ST-7.md`; decisions in DEVIATIONS.md "Structure services fuel (ST-7, increment a)".

Done (verified):
| Status | Item | Evidence |
|---|---|---|
| VERIFIED (tests) | Service fuel: online against the SDE's 72-hour charge from the fuel bay, hourly burn of each online service at the full hour (job `structure.fuel_hour`), a short hour takes every service offline into Low Power | `dotnet/tests/Eve.Structures.Tests/StructureFuelTests.cs` (7 pass on PostgreSQL); Host wiring builds and `dotnet/tests/Eve.Host.Tests` passes 164 of 164 with the composition; `Eve.Inventory.Tests` 55 of 55 (the new `PgItemStore.InContainerAsync` overload) |

Open (next steps, in order):
| Status | Item |
|---|---|
| VERIFIED (tests) | (b) `structureCompressionMgr` CompressItemInStructure / DecompressGasInStructure / GetMyGasDecompressionEfficiency on the real stores and SDE: the EveJS gate (docked at a structure with Reprocessing online), the item in the hangar or ship there, the six-value reply, the join with a stack of the output, gas decompression with the SDE efficiencies (structure 0.8 plus hull bonus, pilot 1 % a level), the removal of a stack that gives nothing, named refusals. Host route tests: `StructureCompressionTests` (6 pass) with the Host project 170 of 170. |
| OPEN | (b) gaps: the corporation-office take path (EveJS `itemIsInDockableCorpOfficeWithTakeAccess`) waits for ST-5; no client run (docking is ST-3) |
| VERIFIED (tests) | (c, part 1) E-03 jammer state: `structure_services.online_since_ms` (migration 0002), `StructureJammer.StateAt` (null, 0, or the warm-up FILETIME), `GetCynoJammerState` through `BeyonceService.CynoJammerOnlineSimTimeForSystem` (wired in `AgentRpc`/`StructureComposition`). E-15 `OnCynoJammerChanged` to the pilots in the system, from the subscriber `structure.cyno_jammer_notice` on `structure.service_changed`. Tests: `StructureJammerTests` (2), Structures 63 of 63, Host 173 of 173. The Host wiring has no RPC-level test yet (build and the Host suite only) |
| VERIFIED (tests) | (c, part 2) the standard cynosural field refused in a jammed system (`CYNO_JAMMED`, host side, after the fleet rule); the covert field not stopped by a jammer. Tests: `CynoFieldHostTests` (4 of 4, two new). Still OPEN: the sov Cynosural Suppression upgrade check for onlining the jammer; the refusal's text is ours (DEVIATIONS) |
| VERIFIED (tests) | (d) the structure cyno beacon checks (`StructureBeaconTravel`, EveJS `resolveUsableBeacon` order and texts): the beacon exists and is a Pharolux, the requested system, in space, not already there, the service online, not jammed. Tests: `StructureBeaconTravelTests` (5 new), Space test project and Host 173 of 173 |
| OPEN | (d) the jump itself: the access check and the jump drive's fuel and range, and the sim cynosural beacon at the structure's position (the field the jump lands on). Until then a beacon that passes the checks is refused by name |
| OPEN | (d) `structureCynoBeaconMgr` backend in place of the user error (`StructureTravelRpcService.cs`): Pharolux beacon service 16 online, jump to it |
| OPEN | Fuel burns send no `OnItemChange` to the owner's sessions (no session at burn time); rig and skill reductions of the online charge |

## 41. Omega state (slice D, 2026-10-11)

Done: the Omega state is the account's stored subscription end time (DEVIATIONS.md, "Omega state is the account's stored subscription
end time"). New accounts are written with 2100-01-01; existing rows are back-filled once; every answer reads the same value
(`OmegaStateTests`, `SubscriptionTransitionsTests`, `ValidationAndOmegaTests`, live `test_a_new_account_is_omega_until_2100_on_every_answer`).

Open:
- A lapse that happens while a session is logged in is announced only on that session's next subscription query, not pushed (needs a timer
  that walks the open sessions).
- No operator command or admin route sets an account's end time yet; `SubscriptionTransitions.SetSubscriptionEnd` is the path, tested.
- The Omega readers are synchronous ADO.NET calls (`PgAccountDirectory.SubscriptionEndsAt`), one primary-key read each; fine at this size,
  a cache with invalidation would be the next step.
- Not verified against the native V24.01 client: whether the fanfare on `OnSubscriptionChangedServer` is shown for a lapse (the client's
  `clone_grade_fanfare_ui_controller.py:59-85`).

## 46. Structures phase ST-1: the structure ball in the simulation (protocol 59, 2026-10-11)

| status | item | where | note |
|---|---|---|---|
| DONE | `BodyKind::Structure`, `StructureStatus`, `PlaceStructure` / `RemoveStructure` / `SetStructureState` (protocol 59) | `crates/eve-space/src/scene/structures.rs`, `link/*`, `sim/dispatch.rs` | Sim-side only; the host sends nothing yet (increment d). |
| DONE | Slim item of a structure (state, upkeep, timer, unanchoring, repairing, category 65) | `crates/eve-space/src/slim.rs` `structure_fields` | Client field names read from eve-logic `structure.py`, `structureBracket.py`. |
| DONE | C# codec, records, `ISimulation`, `SimClient`, fakes; fixtures regenerated | `dotnet/src/Eve.Space/*`, `oracle/fixtures/simlink/messages.json` | `Eve.Space.Tests` 276 pass; `Eve.Services.Space.Tests` 648 pass with PG. |
| DONE (sim) | Health layers, per-second damage cap, `StructureDamaged` (protocol 60) | `crates/eve-space/src/health/structure.rs`, `scene/structures.rs` `damage_structure` | Slice 4: turrets, missiles, smartbombs and damage bombs hit a structure's layers (`weapons/fire.rs`, `missile_entity.rs`, `deployable.rs`); a hit that empties several layers is told per layer, in order. Open: fighters and drones, spool and disruption against a structure. |
| DONE | Dockable body generalisation in `scene/docking.rs` | ST-1 (c) | Built: row 1374 (four scene tests). |
| DONE | Host `SpaceStructures` (restore on first entry, `structure.state_changed` subscriber, damage to the ST-0 machine) | ST-1 (d) | Built: row 1375 (`dotnet/src/Eve.Services.Space/SpaceStructures.cs`). |
| VERIFIED (live, ST-20) | Headless test `tools/headless-client/tests/test_structures.py`: 11 scenarios pass in the live run on eve_p4 (the ST-14 docking and ballpark, the ST-20 refusals); the module seeds the Astrahus | ST-1 (e) |
| NOT CONFIRMED | The client's ball kind and slim rendering of an Upwell structure | MITM | See DEVIATIONS "Structure ball (ST-1)". |

## 47. Structures phase ST-6a: moon extraction record and reads (2026-10-11)

| status | item | where | note |
|---|---|---|---|
| DONE | `structure_moon_extractions` and `structure_moon_resources` (migration 0003), the one-time import of GameStore `moonExtractions` | `dotnet/src/Eve.Structures/MoonExtractionStore.cs`, `Migrations/0003_moon_extractions.sql`, `Composition/StructureComposition.cs` | Import keeps rows already there. Completed history is not read. |
| DONE | Moon reads (`GetExtractionAndEventIDFromStructureID`, `GetExtractionsForCorp`, `GetMoonResourcesForStructure`) from the store | `Rpc/StructureRpc.cs`, `StructureDataRpcServices.cs` | Read shapes unchanged; tests `MoonExtractionStoreTests` (2 PostgreSQL tests), Host 180 of 180. |
| VERIFIED (tests) | (b) `StartNewExtraction`: drill online, moon resources, duration clamped to 6 to 56 days, 30 000 m3/h chunk split by the moon, running extraction answered, calendar id, the named refusals, the caller's `OnMoonExtractionUpdate`. Tests: `MoonExtractionStartTests` (11), `MoonExtractionStartHostTests` (13). Controller/profile gate: DEVIATIONS "Moon extraction (ST-6a)" | ST-6a | Controller/session/profile checks are now wired; 13 moon Host tests verify the RPC gate and actor events. |
| VERIFIED (tests) | (c) jobs `moon.chunk_arrival`, `moon.auto_fracture`, `moon.field_decay` (48 h), the drill-offline and destroyed reactions (`offlined`), `OnMoonExtractionUpdate` broadcast on every completion, re-arm after import. Tests: `MoonExtractionJobTests` (6). The fracture start is (d) | ST-6a | |
| VERIFIED (tests) | (d) `FractureChunkWithStructure` (fire on a ready chunk, `fractured` after 30 s, field decays after 48 h) and `CancelExtraction` (`cancelled`, Station Manager role); `OnMoonExtractionUpdate` to the corporation and the structure's sessions on every completion. Tests: `MoonExtractionFractureTests` (7), `MoonExtractionStartHostTests` | ST-6a | |
| VERIFIED (PG tests) | Owner-corporation notifications 202-206/group 7, offline recipients, actor links, transactional inbox/cursor and post-commit publication | ST-6a | Host wiring and 3 PG tests cover payload, concurrent retry, rollback and restart. |
| VERIFIED (PG tests) | Fitted SDE rig modifiers for fracture delay, yield and field lifetime; start-time snapshots | ST-6a | 7 Host rig tests; full Structures 98 PASS. Functional decay multiplier is applied once; DEVIATIONS records the display-hours correction. |
| VERIFIED (PG/Rust tests) | Persisted mineable moon fields, multi-ore volume conservation, observer ledger, depletion/restart and durable field removal | ST-6b | 7 PG field tests including expiration/conservation/rig radius; native graphical acceptance open. |

### ST-1 status after increments c and d (2026-10-11)

| status | item | where | note |
|---|---|---|---|
| DONE | Dockable body: `dock`, `accept_docking`, `undock`, `undock_place` take a structure; `STRUCTURE_DISABLED` and `SHIP_TOO_LARGE` refusals | `crates/eve-space/src/scene/docking.rs`, `scene/tests.rs` | Four scene tests; the eve-space unit suite passes. |
| DONE | Host `SpaceStructures`: restore on system entry, `structure.state_changed` to `SetStructureState` and `RemoveStructure`, `StructureDamaged` to the state machine in one transaction | `dotnet/src/Eve.Services.Space/SpaceStructures.cs`, `dotnet/src/Eve.Host/Composition/StructureComposition.cs` | Three PG tests in `SpaceStructuresTests.cs`. |
| DONE (tests) | Structure placement on anchoring (ST-2) and the slim `repairing`/`unanchoring` fields | ST-2 | The anchor completion calls `SpaceStructures.PlaceAsync` (`StructureComposition.cs`); the slim fields come from `structure_fields` (row 1349). |
| VERIFIED (live, ST-14) | (e) headless scenario `tools/headless-client/tests/test_structures.py`: a pilot undocked in a system with an imported Astrahus sees it in the ballpark with its state and timer, docks at it and undocks again. Both tests pass in the default live run plus this file (73 passed, 0 failed, 8 skipped, 2026-10-15). The structure is seeded by the run (`handoff-ST-14.md`), not anchored by the scenario | ST-1, ST-3 |
| NOT CONFIRMED | Client behaviour of a structure's dock (`OnDockingAccepted` and the docked session), the damage the ball shows, the state label | MITM | Not verified on the graphical client. |

## 49. Track D: CONCORD Rogue Analysis Beacon (CRAB), first cut (2026-10-11)

Status: deploy, 20-second activation, link, scan-after-link, four mutaplasmid rewards, 120-second linking-pilot access, self-destruction into a public wreck, expiry, restart and durable CEQ/system interference are implemented in the host. Focused Host tests cover the reward hold and timed wreck transition. The simulation blocks tether eligibility and new cloak activation through the link status tail and applies 10% less incoming damage to resisted and resolved hits. A focused simulation test checks the shield result before and after the tail. The graphical client has not been exercised. Figures CCP does not publish remain eve.rs rule constants (owner decision D-C1, DEVIATIONS "CRAB").

| Status | Item | Where / evidence |
|---|---|---|
| VERIFIED (tests) | D-T1: types 60244 (standard) and 92183 (Carrier), group 4137, category 22; hp, shield and armor 50,000 each; Anchoring 3; link lists 300 and 946 read from the pinned SDE 3396210 (no other build loaded) | `Beacons/AnalysisBeaconRules.cs`; `AnalysisBeaconRulesTests` (9 non-PG, 3 SDE-backed with PostgreSQL) |
| VERIFIED (tests) | D-L1 deploy: `ship.LaunchFromShip` of a CRAB in the hold, 2,500 m ahead, active 20 s later, record for 1 h, low and null security only, skill 11584 at 3 | `AnalysisBeaconDirector.LaunchAsync`; `AnalysisBeaconHostTests` |
| VERIFIED (tests) | D-L1 link: `CallComponentFromClient(itemID, 'linkWithShip', 'InitiateLink' or 'InitiateLinkQuick')` through `BeyonceService`; IDLE to RUNNING to COMPLETED after 4 min; the linked ship in the slim item; a ship outside the link list refused; a second link refused | `BeyonceService.Components.cs`; `AnalysisBeaconHostTests` |
| VERIFIED (tests) | D-L1 slim state: `component_activate`, `activate_comp_durationSeconds` and `component_linkWithShip` (FILETIME times) in `AddBalls2` and `OnSlimItemChange`; protocol 61 (`PlaceAnalysisBeacon` 78, `SetAnalysisBeaconState` 79); 44 new fixture frames | `crates/eve-space/src/deployable.rs`; `oracle/fixtures/simlink/messages.json`; `tools/dev/gen-simlink-fixtures.py`; `SimCodecTests` |
| VERIFIED (tests) | D-S1 scan: begins when the 4-minute link completes; 10 min standard, 16 min Carrier; progress moves only while no drone lives (10 s tick); wave 1 (4 standard or 6 Carrier, owner figures) spawned through `IDungeonNpcSpawner.SpawnAsync`; each drone killed (`npc.killed`) is forgotten | `AnalysisBeaconDirector.LinkCompleteAsync`, `ScanTickAsync`; `AnalysisBeaconHostTests` |
| VERIFIED (tests) | D-S1 completion: the configured reward items go into the beacon inventory in the scan transaction; only the linking pilot can open and take them for 120 s; a durable job moves all remaining contents into a public wreck | `AnalysisBeaconDirector.ScanTickAsync`, `CreateRewardWreckAsync`; `SpaceContainers`; `AnalysisBeaconHostTests` |
| VERIFIED (tests) | Character energy and per-system signal interference regenerate continuously and are consumed atomically on link; standard and Carrier costs follow the report's approximate daily-use rates, with concurrent exhaustion covered | `AnalysisBeaconRules`; `AnalysisBeaconStore.TryConsumeLinkResourcesAsync`; `AnalysisBeaconHostTests`; `AnalysisBeaconRulesTests` |
| VERIFIED (tests) | Expiry at 1 h: the simulation removes the beacon, its drones are taken out, its item is deleted, no reward. Restart: a new director activates the beacon from its durable job; restore puts it back with its state | same |

Open (cut line, or not built):

| Status | Item |
|---|---|
| VERIFIED (tests) | D-S2 wave 2 at scan progress 0.5 (EveJS `crabWaveAuthority.js`, same pool and amount; CCP guide: waves block the scan until cleared). The rule file holds both waves; the second is made when the scan reaches half (`AnalysisBeaconDirector.ScanTickAsync`, `waves_made` in migration `002_analysis_beacon_waves`) and the scan waits for its drones. Host test `A_scan_makes_its_second_wave_when_it_reaches_half...` |
| PARTLY (tests, live) | D-L2 link lock (live walk `tools/headless-client/tests/test_crab_walk.py`, eve.rs, 2026-10-12, passed: launch from the hold, first wave of NPCs in space, the link taken (the beacon row linked to the ship), a warp refused during the link, free after four minutes and a minute). Found and fixed by that walk: `BeyonceService.CallComponentFromClient` accepted only `PyStr` names, but a client's Python `str` arrives as `PyBytes`, so every live link was silently refused (the handler answers None). EVE.js side not exercised: its character store is not seeded through the database. The lock: from the link start the ship cannot move (GotoDirection, AlignTo, FollowBall, Orbit) until the link completes, and cannot warp, jump, cyno-jump or dock until a minute later (EveJS status clear delay). Refused in the simulation as `LINK_HOLD` (`Scene::link_holds`, `crates/eve-space/src/deployable.rs`; test `a_linked_ship_is_held_until_...`). Open: the live walk did not exercise cloak or tether; simulation regressions cover both restrictions and the status tail, while each command retains its generic refusal text |
| VERIFIED (tests) | Link ship resistance bonus and 60 s post-link tail: tether and new cloak activation are blocked through the tail; resisted and already-resolved incoming damage is reduced by 10%; movement/warp hold durations remain unchanged | `crates/eve-space/src/deployable.rs`, `health/scene_api.rs`, `scene/tether.rs`, `modules/activation.rs`; focused test `a_crab_link_reduces_damage_by_ten_percent_until_the_status_tail_clears` |
| VERIFIED (tests) | Deployment checks the candidate position against every `Asteroid`-category space item within 10,000 km, which includes ice types in the pinned SDE; host regression refuses the beacon before removing it from cargo |
| VERIFIED (tests) | Reward inventory is reserved for the linking pilot for 120 s; after the durable timer, the beacon is removed and all remaining contents enter a public wreck | `AnalysisBeaconDirector.CreateRewardWreckAsync`; `SpaceContainers.OpenAsync`, `MayTakeFromAsync`; `InvBrokerService.ContentsAsync`; `AnalysisBeaconHostTests` |
| VERIFIED (SDE test) | Standard blueprint 60514 and Carrier blueprint 92182 both make one beacon in 12,000 s (3 h 20 min). Per run: Tritanium 1,400,000; Pyerite 400,000; Isogen 6,000; Zydrine 10,000; Megacyte 5,000; Broadcast Node 2; Self-Harmonizing Power Core 1; Sterile Conduits 1; Wetware Mainframe 1. These pinned SDE 3396210 values override report 1's conflicting material quantities. CCP Version 20.03 changes the five-run LP offer from 20,000 to 9,000 LP; LP store offers are not in this SDE. | `AnalysisBeaconRulesTests.Both_beacon_blueprints_match_the_pinned_sde_manufacturing_recipe`; `blueprints` keys 60514 and 92182 |
| OPEN (D-R1) | Bounties from the NPC bounty store: not built. Reward quantities are the owner's figures |
| OPEN | Damage to a beacon (its 50,000 HP layers are in the SDE) and drone attacks on a beacon: no damage path |
| OPEN | Graphical client: the link UI, the activation timer and the scan UI (no caller of the client's scan UI in the V24.01 snapshot), the ball kind of a beacon (Container) |
| LIMITATION | Drones alive in the simulation across a host restart are not re-attached: a scan that waits for them stays blocked until they die or the beacon's hour is up |
| VERIFIED (tests) | Rewards enter the deployed beacon inventory and are owned by the linking pilot. The inventory broker and loot moves enforce the 120-second exclusive window; the durable self-destruct job moves remaining stacks into a public wreck; a one-hour expiry inside the exclusive window extends the beacon through its same deadline. The launching ship is not used as the reward destination |

Data: `dotnet/src/Eve.Services.Space/Beacons/Data/analysis-beacon-rules.json` (owner decision D-C1; EveJS v0.12.9 values; not CCP data).

Runs (database eve_p3, merged head): Eve.Services.Space.Tests prior run: 737 of 737 passed (173 + 564, PostgreSQL included); Eve.Space.Tests 279 of 279; the Rust workspace 1,395 of 1,395 against a baseline of 1,394 (the one added test is the CRAB state test); the live headless suite 70 passed, 8 skipped by its slow-mode gates, 0 failed. The slow-gated headless CRAB walk covers launch, linking, the first NPC wave and warp restrictions, but not scan completion or reward access. No graphical client walk is recorded.
## 48. Track B: Incursions, lifecycle, staging sites and the map (B-M1, B-L1, B-S1, 2026-10-11)

Status: implemented and tested at the host level on PostgreSQL (`IncursionDirectorTests`, 11 scenarios; `IncursionRulesTests`, 23 cases). not exercised with the graphical client; no live run of the incursion path.

- Lifecycle (`Eve.Services.Space/Incursions/IncursionDirector.cs`): capacity highsec 1, lowsec 1, nullsec 3; Mobilized 48 h, Established 1 day (fixed), Withdrawing 24 h; a band's next incursion spawns 12 to 36 h after the one it replaces ended. Durable jobs `incursion.spawn`, `incursion.phase`, `incursion.sites`; state in `incursions` and `incursion_sites` (migration `001_incursions` in the space schema). Restart keeps the phases and the respawn gaps.
- Map: `map.GetIncursionGlobalReport` and `map.GetSystemsInIncursions` now read the live incursions (`PersistenceIncursionAnomalyReadModel` through `IncursionDirector.ReadActiveAsync`), replacing the read of content kind `incursion`, which nothing produced.
- Rewards: `rewardMgr.GetDelayedRewardsByGroupIDs` answers the loyalty points pending on the groups of the live incursions. The pending value is 0 until the final-boss pipeline exists (B-R1).
- Staging sites (B-S1): five sites per incursion, dungeons 5188, 5190, 5191 and 5193 (archetype 60), started with `DungeonWaveDirector.StartForSystemAsync` (owner 0, lifetime of the incursion, ended with it). Their waves are the owner data's (`sanshas_deadspace_pve`). All four plan on the owner data's `on_load` and `wave_cleared` triggers, so `Data/waves/v1/trigger-rules.json` needed no entry and is unchanged.
- Sites by role (B-S2, 2026-10-12): the staging system's five sites, one headquarters (the system farthest from staging), one vanguard site and one assault site per nearer system (the layout of EVE.js `buildIncursionSystemLayout`, by stargate jumps from the staging system: `StargateGraph`, `mapStargates`). Each role site is a dungeon of the owner catalog (vanguard 5164 to 5166, archetype 61; assault 5167, 5168, 5172, archetype 62; headquarters 5114, 5171, 5175, archetype 63) with its owner waves. The site rows carry role, system and completion (migration `002_incursion_roles`). Constellations under three systems take no incursion.
- Influence and mothership (B-F1, 2026-10-12): the influence is 1 minus the completed share of the non-mothership sites (EVE.js: every seeded site an equal share), updated by the subscriber `incursion.sites.completed` on `content.dungeon.completed`. When the influence is 0 and the headquarters is done, the mothership (5158; 5201 in lowsec) starts in the headquarters system, after the band's protection (three days highsec, one lowsec, none nullsec). Its completion ends the incursion (`mothership_defeated`) and the band's next one is scheduled. Not completed before the withdrawal ends: `withdrawn`, as before.
- Not built: final-boss loyalty points (B-R1: CCP pays the fleet with the most damage on the site and publishes no amount, so the pending value stays 0), standings effects, the Sansha influence penalties on resistances and damage (10%, 25%, 50% at full influence; a dogma path), influence decay (no published rate), site counts tied to influence (no source), the stargate jump cache is capped at 64 sources (shortcut). Decisions and sources: DEVIATIONS.md, "Incursions (B-S2, B-F1)".
- Not verified: the simulation spawns site NPCs with no pilot in the system (the path is covered with the fake simulation only); templateNameID is 0 because the owner catalog gives no name id for these dungeons.
- Slice 2 (2026-10-12): the stargate layout (`StargateGraph`, `mapStargates`) replaces straight-line distance. Live walk `tools/headless-client/tests/test_incursion_walk.py` (eve.rs debug host through `tools/mitm`, lane eve ports 26110/26210) against EVE.js (scratch copy, port 26010): the report and the systems-in-incursions answers now have the same key sets and value types. Fixed on the way: `lastUpdated` is sent as a long (it was a real). Not walked: the site entry in space (the pilot in a staging system).
## 48. Track ST-10: hacking minigame, analyzer, site containers and loot (package closed 2026-10-11; runtime follow-up 2026-10-14; Cockroach regression recorded PASS, native not confirmed)

Built on main: the minigame engine (`dotnet/src/Eve.Hacking`), the `hackingMgr` service with its four calls and `OnHackingTurnComplete` (`Eve.Services.Space/HackingMgrService.cs`), the data and relic analyzer body and its activation checks, the hacking window (warp and reach), the site containers as inventory items (placement in the site fill, persistent failures, destruction at two failures), and the loot of a win (EveJS `generic_data_loot` / `generic_relic_loot`). Tests: `Eve.Hacking.Tests` (25), `HackingMgrServiceTests` (9), `SiteContainerTests` (4), `SiteLootTests` (3), `ContainerSiteEndToEndTests` (1), `HackingCompositionTests` (Host, 1). The full `Eve.Services.Space.Tests` passed on the final rebased tree (698) before the runtime follow-up; parent reported SiteContainerRestartTests: 1 PASS, 0 FAIL, 0 SKIP (3s). Native client and MITM remain not confirmed. This section supersedes the open rows for `doHacking` in section 7 and for containers in section 45. Decisions: DEVIATIONS "Hacking minigame (ST-10)", "Site loot", "Container items", "Site container types", "Site completion" and "Cleared container ball".

| Status | Item | Next step (exact) | Source |
|---|---|---|---|
| IMPLEMENTED; recorded Cockroach PASS (1/0/0, 3s) | Site completion when the hacked containers are empty. | A win keeps the container looted. The outer site item stays owner 0; a matching looted site row opens in SpaceContainers and the pilot owning remaining cargo may take it. InvBrokerService.MultiAdd reaches CharacterShips; PgItemStore writes inventory.item_moved in that SQL transaction. The Host site.container.emptied subscriber calls OnItemLeftAsync, which clears the container and emits site.container.cleared for CosmicSiteDirector. SiteContainerRestartTests exercises actual MultiAdd and a concurrent final CharacterShips move; ContainerSiteEndToEndTests verifies active-site completion. Recorded SiteContainerRestartTests: 1 PASS, 0 FAIL, 0 SKIP (3s); client/MITM not exercised. | EveJS dungeonUniverseSiteService.js 5729-5800 (maybeCompleteMaterializedDataRelicSite) |
| IMPLEMENTED; recorded Cockroach PASS (1/0/0, 3s) | Remove emptied ball and restore looted ball after restart. | InSystemAsync returns secure and looted rows so SpaceContainers.RestoreSiteContainersAsync restores a looted ball after restart. A committed inventory.item_moved fact drives OnItemLeftAsync; when cargo count reaches zero it marks cleared and calls RemoveSiteContainer. SiteContainerRestartTests covers Cockroach, rollback, MultiAdd, final-stack concurrency, bus restart, restored ball, clear topic and removal. Recorded SiteContainerRestartTests: 1 PASS, 0 FAIL, 0 SKIP (3s); native scene not confirmed. | EveJS dungeonUniverseSiteService.js 5726-5746 (removedEmptyContainers) |

## 48. Structures phase ST-2: deployment, vulnerability and notices (2026-10-11, closed 2026-10-15 in the remainder pass: see `docs/project/structures-phase/handoff-ST-2-remainder.md`)

| status | item | where | note |
|---|---|---|---|
| VERIFIED (tests) | (a) `structureVulnerability.UpdateReinforceTimesFromClient`: pending day and hour, effective after 30 days; the current time withdraws the pending change; refusals for another corporation, a pilot without a role, an unknown structure (nothing changes) | `dotnet/src/Eve.Host/Persistence/Structures/StructureDeploymentAuthority.cs`, `Composition/StructureComposition.cs`, `Rpc/StructureRpc.cs` | Host tests `StructureCompositionTests` (5 with the two ST-0 tests), real Host registration. The pending time is not yet asserted across a restart (the rename is). |
| VERIFIED (tests) | (a) `structureDeployment.RenameStructure`: normalised name shown by the directory, `CharNameTooShort`, `CrpAccessDenied`, survives a restart | same | The sim ball keeps the name it was placed with until the next placement (ST-2 d). |
| VERIFIED (tests) | (a) reinforcement-change mailbox notice (`STRUCTURES_REINFORCEMENT_CHANGED`, type 209, group 7): one notice per corporation member, written in the outbox subscriber through `PersistenceTransactionMailbox.WriteNotificationsAsync` keyed by the event id; written only after the state commit; a forced redelivery (cursor rewound, restart) writes no second notice | `ReinforcementChangedNotifications.cs`, `StructureCompositionTests.Reinforcement_change_notice_is_durable_formatted_and_scoped_to_the_owner_corporation` (Host, real registration, restart) | ST-2 remainder |
| VERIFIED (tests) | (b) `structureDeployment.Anchor`: named refusals (SDE per-system anchor restrictions for Thera, shattered, starter and career systems; trade hubs by name; corporation age; the medium ADM gate above 4.0; extra-large in high-security; 1000 km; an item id already taken); the item consumed and the structure created in one PostgreSQL transaction with `structure.anchored`; the default profile on a fresh store; the sovereignty delay persisted as `anchoring_ms` and kept through the 15-minute transition, a restart and a stale TimerDue replay | `StructureAnchorTests`, `StructureAnchorDelayTests` (Host), `StructureTimerAnchoringDelayTests` (Structures); 13/13 and 5/5 pass. Open: the registered route refuses "not in space" without the simulation (its ship position comes from the sim), so the pilot route is covered at the authority with real PG and SDE. | ST-2 remainder |
| VERIFIED (tests) | (c) `Unanchor` and `CancelUnanchor` through the registered Host (Director or CEO; 7-day timer, 45 minutes for Flex; refusal in a cancelling state). The drop at completion: hull, fittings container (type 10167, group 306), the attached assets, the rigs destroyed, the Quantum Core from SDE attribute 3101; created in the timer's transaction; placed in space after commit; scoopable through bound `Ship.Scoop` with the cargo-hold checks | `StructureCompositionTests` (Unanchor, Cancel), `StructureUnanchorDropTests` (Host), `StructureUnanchorCompletionTests` (Structures), `SpaceContainersUnanchorDropsTests` (Space, 2/2 incl. scoop); Rust wreck group 306 classification exists. Native client open. | ST-2 remainder |
| VERIFIED (tests) | (d) `structure.notify` subscriber: `OnStructureStateChanged`, `OnCorporationStructuresUpdated`, `OnCurrentStructureStateChanged` from one Event Bus subscriber | `StructureNotificationsTests` (Host, in the full run of 334/334). Native client open. | ST-2 |

## 48. Structures phase ST-3: structure docking, undock and hangars (2026-10-11)

Plan: scratchpad `specs/structures_phase.md`, ST-3. Handoff: `docs/project/handoff-ST-3.md`. Decisions: DEVIATIONS.md "Structure docking and hangars (ST-3)".

| Status | Item | Where | Notes |
|---|---|---|---|
| VERIFIED (tests) | (a) `structureDocking.Dock`: unknown structure, another ship, docking access, module and criminal timer refused by name; out of range the ship is taken to the structure and the client is told the distance; in range the simulation is asked to accept | `SpaceSessions.Structures.cs`, `StructureComposition.cs` | `StructureDockingTests` (3, PG) |
| VERIFIED (tests) | (a) the finished docking puts the ship in the structure's hangar (`locationID` = structure, hangar flag), the character at the structure, the session with `structureid` and `solarsystemid`, and the reference's notification order | `SpaceSessions.Movement.cs` `FinishDockingAsync` | `StructureDockingTests` |
| VERIFIED (tests) | (b) `structureDocking.Undock`: another structure or ship refused (DeniedShipChanged), the structure put in the simulation first, the ship in space in the system, the session out of the structure, `UndockingStructure:` item change | `SpaceSessions.Ships.cs` `UndockFromAsync`, `SpaceSessions.Structures.cs` `UndockStructureAsync`, `StructureComposition.cs` | `StructureUndockingTests` (2, PG); station undock tests (SpaceServiceTests, 132 with the new tests) |
| VERIFIED (tests) | (c) the structure hangar lists the docked ship and the items kept at the structure (`GetInventory` / `List` through the station-items path, `DockedLocationId` includes `structureid`); the items persist; a login while docked gives `structureid` and the solar system | `StructureHangarTests` (2, PG) |
| PG VERIFIED | (c) `OnHangarViewStateUpdated(structureID, state)` uses the existing ST-2 subscriber and shared persisted hangar view read model; current-structure notice precedes hangar view, removal skips the view, and control-seat sessions are excluded | ST-2 / ST-3 (c) | PG transition/replay and wire coverage passes; no view read without a docked audience |
| VERIFIED (live, ST-14, ST-20) | (d) headless `test_a_pilot_docks_at_the_structure_and_undocks_from_it` in `tools/headless-client/tests/test_structures.py`: docks, OnDockingFinished, undocks (live on eve_p4 with the default run, ST-20) | `tools/headless-client/tests/test_structures.py` | live |
| PG VERIFIED | Undock confirmation (`ShipContrabandWarningUndock`) for station and structure uses official `contrabandTypes`, system faction/security and current owned ship cargo. The boolean flag reaches the shared preflight; no ship/session/simulation mutation occurs before confirmation | ST-3 (b) | Space 10/10; RPC 32/32. Direct cargo preflight only; fines/confiscation remain a separate customs flow |
| PG VERIFIED | Structure/station guest notifications on successful dock and undock, with existing `GuestLoginHook` / `StationGuestLedger`; station payload is one tuple, structure join is four arguments. Current-location disconnect handlers support retries and character rollback | ST-3 (c) | Station 28/28; Space transition coverage passes. Graphical acceptance remains open |
| ENVIRONMENT | `Eve.Services.Core.Tests` `MissionSdeDataTests` (2) fail: no mission build in schema `sde_mission`. Load with `cargo run -p eve-sde -- ensure --missions --pg $EVE_PG_URL` | local DB | Not a code failure; not counted as passed |
| VERIFIED (tests) | (a) `structureVulnerability.UpdateReinforceTimesFromClient`: pending day and hour, effective after 30 days; the current time withdraws it; refusals (another corporation, a pilot without a role, an unknown structure) change nothing | `dotnet/src/Eve.Host/Persistence/Structures/StructureDeploymentAuthority.cs`, `Composition/StructureComposition.cs`, `Rpc/StructureRpc.cs` | `StructureCompositionTests` (real Host registration). The pending time is not yet asserted across a restart. |
| VERIFIED (tests) | (a) `structureDeployment.RenameStructure`: normalised name, the directory shows it, `CharNameTooShort`, `CrpAccessDenied`, survives a restart | same | The sim ball keeps the name it was placed with until the next placement. |
| VERIFIED (tests) | (a) reinforcement-change notice (`STRUCTURES_REINFORCEMENT_CHANGED`, type 209) | ST-2 / notifications | `ReinforcementChangedNotifications.cs`: the corporation members, through the mailbox in the caller's transaction. Test: `StructureCompositionTests` (registered Host route). |
| VERIFIED (tests) | (b) `structureDeployment.Anchor`: the session gates (in space, not in warp, role, NPC corporation), the item in the ship's cargo, fleet or infrastructure hold, the size rules, the placement, 1000 km from stations, structures and belts, trade hubs, corporation age, the item id taken; the item consumed and the structure created with `structure.anchored` in one transaction; the simulation shows it when a pilot is in the system | `Persistence/Structures/StructureAnchorAuthority.cs`, `SpaceStructures.PlaceAsync` (`Eve.Services.Space`) | `StructureAnchorTests` (4), `StructureCompositionTests` (registry refusal), `SpaceStructuresPlacementTests` (pilot undocks, placed). |
| VERIFIED (tests) | (b) `structureDeployment.Anchor` refusals and sovereignty delay, re-checked in ST-15 against the code: the pinned SDE refuses Structure anchoring in shattered (class 13, J000895 = 31002580) and Thera systems through `disallowedAnchorCategories` 65 (`StructureAnchorTests.Pinned_SDE_anchor_restrictions_refuse_before_consumption`, which now names J000895; J015092 = 31002505 is class 1 and also carries category 65); a foreign-IHub deployment adds 24 h per Strategic Index level in the authority and persists `anchoring_ms` (`StructureAnchorDelayTests`, `StructureTimerAnchoringDelayTests`); the corporation default profile is stored when none is named. Fail-closed on missing sovereignty or index data. | ST-2, ST-15 |
| VERIFIED (tests) | (c) `Unanchor` (Director or CEO, 7 days, 45 minutes for Flex) and `CancelUnanchor`: the refusal names of EveJS, repeated requests answered without a change, the timeline refuses the EveJS cancel states | `StructureDeploymentAuthority.cs`, `StateMachineTests` (existing) | `StructureCompositionTests` (2). |
| VERIFIED (tests) | (c) the drop at the end of an unanchoring: hull item, container and attached items, the quantum core (SDE attribute 3101) | ST-2 | `StructureUnanchorDropAuthority.cs`, run by the unanchor completion (`StructureTimers`). Tests: `StructureUnanchorDropTests`, `StructureUnanchorCompletionTests` (Structures), `SpaceContainersUnanchorDropsTests` (Space). |
| VERIFIED (tests) | (d) `structure.notify`: `OnStructureStateChanged` and `OnCorporationStructuresUpdated` to the owner corporation, `OnCurrentStructureStateChanged` to the pilots docked in the structure, nothing to a stranger, nothing for a change the client does not show | `Persistence/Structures/StructureNotifications.cs`, `Composition/StructureComposition.cs` | `StructureNotificationsTests` (1). Sessions whose ship id is the structure: ST-5. |
| NOT CONFIRMED | Anchor and notices on the graphical client (placement shading, the anchoring bracket, the reinforcement window) | MITM | Not verified on the client. |

## 49. Pirate insurgency campaigns, C-M1 and C-S1 (2026-10-11)

Plan: scratchpad `work/missions-hard-first-plan.md` track C; decisions in DEVIATIONS.md "Insurgency campaigns (C-M1, C-S1)". Gateway feasibility: scratchpad `work/insurgency-gateway-spike.md`.

| status | item | where | note |
|---|---|---|---|
| DONE (tests) | Campaign state machine with the 24.01 numbers: forecast 48 h, active to 7 days from the start, no winner at the maximum, wind-down 48 h after any end, ambition 5 to 10 systems, FOB reinforcement 2 to 4 h (rule only), spread level 3 and contribution LP constants (rule only) | `dotnet/src/Eve.Insurgency/InsurgencyRules.cs`, `InsurgencyCampaign.cs` | `CampaignMachineTests` (10 pure tests, 6 PostgreSQL tests in `InsurgencyCampaignTests`) |
| DONE (tests) | Store, deadline jobs (one durable job per campaign), restart keeps the expiry, points refused unless active and covered, win updates the ambition in the same transaction | `Eve.Insurgency/InsurgencyStore.cs`, `InsurgencyCampaigns.cs`, `Migrations/0001_insurgency.sql` (schema `insurgency`) | Scheduler and events are the Host's |
| DONE (tests) | `GetAllVisibleCampaigns` and `GetLocalCampaignClientSnapshot` read the store through the real Host composition (structure of the snapshot, `structureID` None, `[systemID, None]` outside covered systems) | `dotnet/src/Eve.Host/Composition/InsurgencyComposition.cs`, `CoreRpc.cs`; `Eve.Host.Tests/InsurgencyCompositionTests` | |
| DONE (code, tests pending live) | Campaign notices after commit: started, updated, ending for covered systems; state changed and system fully corrupted or suppressed for every pilot | `InsurgencyComposition.cs` (`NotifyCampaignChanged`, `NotifyPointScored`) | Client text and handling not checked in the graphical client |
| PARTLY (tests, 2026-10-12) | Start of campaigns: `InsurgencyDirector` (`Eve.Services.Space/Insurgency/InsurgencyDirector.cs`, job `insurgency.start`, at once then hourly) starts a campaign in each warzone with none open: hub = a low-security system of the warzone's map (the client's Faction Warfare warzone map: warzone 1 Angel, 2 Guristas; `InsurgencyWarzones`, `Data/warzone-systems.json`), covered = the hub and the nearest low-security systems by stargate jumps, as many as the pirates' victory needs. The CCP trigger is the FOB spawn of the structures owner (Q-INS-4 answered by the wiki and the Havoc notes; the interval and the hub choice are shortcuts, DEVIATIONS) |
| DONE (tests, slice 3) | FOB structure and its signals: the warzone's FOB type (78260, 79172) is a structure record in the hub system, deployed invulnerable by the spawn that starts the campaign (`InsurgencyDirector.StartAsync`); exposed at the empire side's count; one reinforcement of 2 to 4 h after the shield; destruction ends an active campaign in the empire's win; pirate win and tie despawn it. Open: damage by players (no weapon path to a structure body, ST-8a), the FOB position (origin), respawn | `Eve.Structures` (`StructureStateMachine`, `Migrations/0006_fob_ids.sql`), `StructureRulesSource`, `InsurgencyDirector`; DEVIATIONS "Pirate FOB (slice 3)" | `StateMachineTests` (Fob), `InsurgencyDirectorTests` (FOB exposed and destroyed), `CampaignMachineTests`, `InsurgencyCampaignTests`. Live (eve.rs, 2026-10-12): `tools/headless-client/tests/test_fob_walk.py` passed: two campaigns at boot, their FOB ids in the map's answer (the warzones were found empty before the client's warzone map replaced the pirate regions). Slice 4: the same walk through `tools/mitm` on eve.rs passed; the captured `GetAllVisibleCampaigns` answer names both FOBs (structure ids 1100000000000 and 1100000000001, state 2, forecasting). `tools/dev/run-eve-live.sh` takes `EVE_LIVE_START_TENTHS` for a slow start (default 600 = 60 s) | Live: the in-system walk (`test_fob_insystem_walk.py`) passed on 2026-10-12, state 111 after 1,804 s (slice 5).
| PARTLY (tests) | Point producers (C-P1): a pirate campaign site (archetypes 72 to 79 of the owner catalog, the warzone's pool) starts in each covered system when the campaign is active; its completion scores the system once for the pirates (`insurgency_points`, `ScorePointAsync`). Kills in a covered system are contributions (`insurgency_contributors`). Slice 4: the empire (Suppression) sites are the owner catalog's archetypes 73 and 75 to 79, one per covered system, and their completion scores the empire side's point (DEVIATIONS "Insurgency sites"). Open: the spread at corruption level 3 (wiki), the per-stage contribution percentages, the Ice Refinery Heist and the Counter-Insurgency Outposts |
| BLOCKED | Public gateway for corruption and suppression (C-G1) | owner decision 2026-10-19: deferred until the client call path and truthful progress/threshold rules exist | Transport exists; insurgency handlers, truthful progress aggregation and threshold sources remain absent; client messenger path has a recorded failure |
| DONE (tests) | Victory payouts (C-V1): a pirate win credits each contributor 1,500,000 LP from the warzone's pirate corporation (1000127 Guristas, 1000138 Angel), in the win's transaction, keyed by campaign and character (the wiki's Insurgency page). Not paid: the losing side (the wiki and the Havoc notes publish no amount; EVE.js's 0.4 factor is not CCP data) and the ISK bonus (no amount published) |

## 49. Host start-up: shared container type names (2026-10-11)
| DONE (tests, live run) | Host start-up no longer throws on a container type name that several SDE types share (Secure Depot and about 40 more in groups 306, 314, 1207): the published type, then the lowest type id, is kept and each skipped type is logged. Tests: `SiteContainerTypesTests`. |

## 50. Mission execution X1a: epic arc offers (2026-10-11)

Done: `PickOffer` is wired to the official arc sequence. The offer is the agent's next step: entries first, then the steps that
follow a completed step, with forks, the 90-day restart and the held-back sites (DEVIATIONS "Mission execution X1: epic arc offers").

| Status | Item | Where / blocker |
|---|---|---|
| DONE (tests) | Offer of the current arc step (entry, nextMissions progression, fork exclusion, one step at a time, lowest ID among offerable steps) | `Eve.Services.Core/EpicArcOffers.cs`; `EpicArcOffersTests` (9), `EpicArcDialogueTests` (PostgreSQL, the dialogue on the real store) |
| DONE (tests) | Completed history from the journal with the game time (journal column `transition_at`, migration 0002) | `Eve.Agents/AgentMissionStore.cs` `ReadCompletedAsync`; `AgentMissionStoreTests` |
| DONE (data) | Site status of the 182 arc kill steps: 178 generic, 4 missing, 0 specific; held back unless `EVE_MISSION_INCLUDE_GENERIC_ARCS=1` | `Data/mission-site-status.v1.json`; `MissionSiteStatus.cs`; `MissionStaticCatalog.IsHeldBack` |
| DONE | The restart interval is minutes (`RestartIntervalMinutes`; was misnamed `RestartIntervalSeconds`) | `MissionStaticCatalog.cs` |
| DONE | Mission content of an offer: the official English name and `nameID` | `MissionStatic.NameId`; `MissionComposition.cs` |
| DONE (tests) | An accepted kill mission starts its dungeon through `DungeonWaveDirector.StartAsync` when the pilot is in space in the agent's system (durable sweep, 10 s); the origin is the pilot's position plus 20 km along x | `Eve.Services.Core/MissionDungeonRuntime.cs`; `Eve.Host/Composition/MissionComposition.cs` (`AttachMissionDungeonsAsync`); `MissionDungeonRuntimeTests` (PostgreSQL) |
| DONE (tests) | The dungeon's outcome decides the mission: completed credits the SDE base ISK once (wallet journal type 33, key per instance) and completes; failed, abandoned or expired fails and pays nothing; a replayed outcome pays nothing | `MissionDungeonRuntime.OnDungeonOutcomeAsync`; `content.dungeon.{completed,failed,abandoned,expired}` subscribers; `AgentMissionStore.FailAsync` |
| OPEN | Live check of the dungeon start on the client (no client route tested) and the dungeon of arc 29 14117 (generic; held back by default) | live walk after the owner decides the switch |
| OPEN | Bonus window and LP for kill missions | DEVIATIONS "Mission execution X1"; LP base not in official data |
| OPEN | Talk steps (`isTalkToAgent`) complete when the pilot talks to the next agent | next slice (X1b) |
| DONE (tests) | Basic L4 and L5 kill missions of the security agents: the lowest level-matching kill mission not completed by the pilot, whose dungeon can start (the wave director's check); 191 level-4 and 133 level-5 candidates | `Eve.Services.Core/BasicKillOffers.cs`; `BasicKillOffersTests` (5); `StartRefusalOf` in `DungeonWaveDirector` |
| OPEN | Basic kill missions: replay after the official replay delay (missions are offered once per pilot), and the bonus and LP | DEVIATIONS "Mission execution X2" |
| DONE (tests) | `GetAgentLocationWrap` on the bound agent (station, in space with the agentsInSpace spawn data, solar system); unknown agentMgr methods answer a named error instead of None | `AgentMissionService.cs` (`AgentLocationWrap`); `AgentLocationWrapTests` (3); the 631 Services.Core tests pass on eve_p3 |
| OPEN | Journal text (`GetMissionBriefingInfo`, objectives), `GetMissionObjectiveInfo`, `GetReplayTimestamp`, the warp calls | next slice; they answer the named error until built |
| OPEN | In-space agents: placement rule, the agent as a space entity, warp to the agent, talk steps (arc 29 step 2) | next slice (owner decision 1: the rule position) |

## 51. Mission execution X3 step 1: in-space agents and arc 29 steps 1-2 (2026-10-11)

| Status | Item | Where / blocker |
|---|---|---|
| DONE (tests) | In-space agents are put into the scene when a pilot enters their system: ball = agent ID, ship type and group from agentsInSpace, owner corporation; position 20 km from the system centre at the agent-ID angle (owner rule, not CCP data) | `Eve.Services.Core/InSpaceAgentPlacement.cs`; `Eve.Services.Space/SpaceContainers.Agents.cs`; `MissionComposition.cs` (`PutInSpaceAgents`); `InSpaceAgentPlacementTests` |
| DONE (tests) | A talk step completes when the pilot talks to the next step's agent, in the station or in space in the agent's system; the reward is paid once under the step's key | `MissionDungeonRuntime.OnConversationAsync`; `AgentDialogueSources.OnConversation`; `Arc29StepsTests` (PostgreSQL) |
| DONE (tests) | Arc 29 steps 1-2 through the store, the dialogue and the runtime: offer 14117, accept, dungeon in Arnon, completion (100,000 ISK once), talk 14118 with Tevis Jak in Tar, completion, offer 14119 | `Arc29StepsTests` (with the generic switch on) |
| OPEN | Warp and approach to the in-space agent in the live client (the client uses its own ballpark commands; the server-side warp calls `WarpToLocation` and `WarpToAgentInSpace` still answer the named error) | next slice |
| OPEN | Mission objective window (`GetMissionObjectiveInfo`, `GetMissionBriefingInfo`) | next slice (X3 step 2) |
| HELD | Arc 29 on by default | kept held back: step 1's site is the family-derived template 3509 and its Damsel content is missing (owner decision 2); `EVE_MISSION_INCLUDE_GENERIC_ARCS=1` runs it |

## 52. Mission execution X3 step 3: failure branches and basic re-offer (2026-10-11)

| Status | Item | Where / blocker |
|---|---|---|
| DONE (tests) | A failure (failed or quit) whose failMissionID names another step restarts the arc run there; a self-naming failure only retries the step (arc 29) | `EpicArcOffers.cs`; `EpicArcOffersTests.A_failure_whose...`; `AgentMissionStore.ReadFailedAsync` (journal, PostgreSQL test) |
| DONE (tests) | A basic kill mission completed is offered again after the replay delay (4 h, EveJS value, shortcut) | `BasicKillOffers.cs`; `BasicKillOffersTests` |

## 53. Mission execution X3 step 4: rewards (2026-10-11)

| Status | Item | Where / blocker |
|---|---|---|
| CHECKED | LP: the SDE holds no LP reward for missions (`fixedLpRewardAlpha` and `fixedLpRewardOmega` are 0 on all 2,892 records; no other field carries LP). No LP is paid. AG-3 LP wallet is not wired to missions | nothing to wire until a source exists (DEVIATIONS "Mission execution X3 step 4") |
| DONE (tests) | Standings: the SDE `extraStandings` of a completion change the character's standing toward the faction (tenths: 7.0 = +0.7), once per completion | `MissionDungeonRuntime.CompleteWithRewardAsync`; `MissionComposition.ApplyMissionStandingAsync` (relations `AdjustAsync`); `MissionDungeonRuntimeTests.A_completion_moves_the_standing_once...` |
| OPEN | Bonus time window (`bonusReward`, `bonusTimeInterval`): unit not established | DEVIATIONS |

## 54. Mission execution X3 step 2: client texts and the held-mission views (2026-10-11)

| Status | Item | Where / blocker |
|---|---|---|
| DONE (tests) | Loader of the client's mission text IDs (embedded copy `client-missions.jsonl.gz`, build 3396210, IDs only) into schema `client_sde` (replaced at each start); `EVE_CLIENT_SDE_DIR` overrides | `Eve.Services.Core/ClientMissionMessages.cs`; `MissionComposition.LoadClientMissionMessagesAsync`; `MissionViewsTests` (synthetic, and the real export when configured: 2,886 rows with messages, 6 without) |
| DONE (tests) | `GetMissionBriefingInfo` and `GetMissionObjectiveInfo` on the bound agent for a held (offered, accepted or completed) mission: the briefing and title IDs; the kill dungeon objective and the ISK reward; None when no mission is held | `Eve.Services.Core/MissionViews.cs`; `AgentMissionService.ReadHeldMissionAsync`; host `ReadHeldMissionAsync` |
| OPEN | Fetch objectives (14119: item in cargo and turn-in at the agent) and courier objectives (`objectives` and `locations` lists are empty) | next slice; the EveJS builder is `buildMissionObjectivePayload` (agentMissionRuntime.js:5885) |
| OPEN | The dungeon's enemy owner (`ownerID` is the mission's faction, a shortcut) and the dungeon location (0 placeholders) | next slice |
| NOT FOUND | LP and text are the client's: the server sends IDs, and the client resolves the text in its own localization | none needed |

## 55. Mission execution X4: live walk, completion transaction (2026-10-11)

| Status | Item | Where / blocker |
|---|---|---|
| DONE (live, passed) | Basic level 4 kill mission, live: a pilot warps to the Manifest gate, jumps to 30005204, the level 4 security agent 3015920 offers mission 891 (request, accept), the accepted mission is in the journal, and the dungeon brings its NPCs into space | `tools/headless-client/tests/test_mission_walk.py` (run: `tools/dev/run-eve-live.sh` with `EVE_RUN`, see the file) |
| FIXED (live finding) | The agent service's `DoAction` was refused at dispatch (`HasMethod` did not list it): no agent conversation worked on a live host. Unit tests call `CallAsync` directly and missed it | `AgentMissionService.HasMethod` |
| FIXED (live finding) | Offers had an empty mission type label, so the journal dropped every offered or accepted mission | `MissionComposition` (offers carry the agent's kind) |
| OPEN (live) | Killing the dungeon's NPCs and the completion and ISK in the live walk: after the first shot the dogma and ballpark calls answer `UnMachoDestination`; cause not found (possibly the pilot's ship lost to the site NPCs of the system). The completion is unit-tested only | next slice |
| OPEN (live) | The arc 29 walk (Arnon is five gate jumps from Manifest, Tar four): not exercised | next slice; see the decision on arc 29 |
| DONE (tests) | Standing and completion in one transaction: `AgentMissionStore.CompleteInAsync` joins the caller's transaction, the host's `CompleteMissionAsync` commits the completion and the relations standings together and announces after the commit | `AgentMissionStore.cs`, `MissionComposition.CompleteMissionAsync`, `MissionDungeonRuntime` |
| OPEN | Atomicity is not tested (no test rolls the transaction back) | next slice |
| CUT | Fetch and courier objectives (item 2): not built this slice | see DEVIATIONS "Mission execution X4" |
| HELD | Arc 29 on by default (item 3): not flipped; the live walk through step 2 has not exercised | owner decision |
| FOUND | `SiteLoot` divides by a candidate count that can be zero: "the sites of system X could not be made: Attempted to divide by zero" on pilots' entry to 30100032 and 30005204 (the sites track, not the mission path) | not fixed here |

## 12. Zarzakh rules and client-only system data (2026-10-12)

Evidence and decisions: DEVIATIONS "Zarzakh (30100000) and the client's system flags".

| Status | Item | Missing work |
|---|---|---|
| DONE (tests) | Client flags `disallowCyno` and `disallowScanning` read from the embedded copy `client-solarsystemcontent.jsonl.gz` at start-up; cyno lighting refused (`CannotLightCynoInSystem`) and probe launch refused in those systems | `ClientSystemFlags`, `SolarSystemCatalog.LoadAsync`, `SpaceSessions.Modules.cs`, `SpaceSessions.LaunchAsync`; tests `ClientSystemFlagsTests`, `ScanProbeHostTests`. Cyno refusal has no host test yet; no graphical-client run |
| DONE (rule) | No sites in Zarzakh (`CosmicSiteDirector.NoSiteSystems`) | Owner decision: the real game may have sites there (see DEVIATIONS) |
| DONE (tests) | Capital-ship gate lists (the eight gates of list 579, from client data and the SDE type list) | `GateRules`, `CheckGateRulesAsync`; `GateRulesTests`, host jump test. Refusal text is ours |
| PARTIAL (tests) | Emanation Lock: server-side refusal of exits through another gate for 6 h after entry, reset on re-entry, ended by leaving Zarzakh (the client text) | `EmanationLocks`; `EmanationLocksTests`. Not built: the client notices (the public gateway, owner decision 1); the lock is in memory |
| DONE (tests) | Alignment toll: charged at the jump as a sink (journal ref 168, no counterparty), refused when unpaid, refunded when the simulation refuses | `SpaceSessions.ChargeTollAsync`, `RefundTollAsync`, composition `ChargeGateToll`; jump test. OPEN: a payee if one is published (owner decision); mass from dogma attribute 4, not the client ball |
| DONE (tests) | D-Scan and bookmarks disabled in Zarzakh | `ScanningAllowedFor`, `BookmarkRules.DisabledSystems`; `ScanProbeHostTests`, `AccessGroupBookmarkMgrTests` |
| CUT (no published numbers) | Gate guns (retribution timer), gravity hull damage | DEVIATIONS "Zarzakh gate, station and scanning rules (slice 2)" |
| CUT (needs Insurgency FOB data) | Deathless Shipcaster jump | DEVIATIONS, same section |
| DONE (headless rules) | Client `deployBlocker` on the Zarzakh gate types: warp bubbles blocked within 100000000 m | Field generators, mobile warp disruptors, and interdiction sphere launchers apply the client rule; structures remain subject to their separate service and anchoring checks. |
| OPEN | Sovereignty refusal in Zarzakh | No claim path in eve.rs yet (structures track) |
| OPEN | Warp disruption bubbles and probes in Zarzakh | No CCP or wiki source; EveJS refuses them. Owner decision |
| CUT | Heat, temperature, tidal forces, Point of No Return effect | No mechanic in the SDE, the client or the wiki |
| KEPT | Pirate-faction market modifier (0.5, factions 500018 and 500019) | Already in eve.rs (`MarketRules`); EveJS-derived, not in the wiki. Owner decision |
| CLIENT-LOCAL | Sun type and flare, system-wide cloud, warp tunnel overwrite, dscan range, anchor categories | Read by the client only; no server loader |
| CUT | Landmarks placed by the server and the Black Monolith look-at challenge (2848) | EveJS and eve.rs both advertise `client_lookAtObject` 0; the client look-at path is not proven |
| OPEN | Live check of the Zarzakh flags and the lane run on the embedded client data (no client directory) | Needs a live run |

| PARTIAL (live) | Turnur to Zarzakh jump, eve.rs only: `tools/headless-client/tests/test_zarzakh_jump.py` passes through `tools/mitm` on the eve_p2 lane (1 passed, 96.9 s); the toll journal row (ref 168) is counted | DEVIATIONS "Live check: Turnur to Zarzakh (slice 3)". OPEN: the EVE.js comparison (no reference data here); the Cockroach seeded through the database |


## Pochven, Triglavian and EDENCOM space (2026-10-12)

Research and what is cut: `docs/project/POCHVEN_DESIGN.md` (sources, SDE ids, the wiki's rules, what is not published).

| Status | Item | Note |
|---|---|---|
| DONE (unit) | Region, faction and corporation ids of Pochven, capital group ids | `dotnet/src/Eve.Services.Space/Pochven/PochvenRules.cs`; `PochvenRulesTests` (4) |
| DONE (unit; host path not exercised) | Capital ships refused a stargate in Pochven | `SpaceSessions.Movement.cs` `CheckGateRulesAsync` before the client gate list; the pilot cannot be placed in Pochven by the rig (see DEVIATIONS) |
| DONE (pure rule, consumer pending) | Faction hostility between the four Pochven NPC factions; NPC standing rule (Triglavian at 0.00 and below, EDENCOM below 0.00, Rogue Drones and Drifters hostile to all pilots) | `PochvenRules.FactionsAreHostile`, `NpcHostileToPilot`. The simulation does not call them yet |
| OPEN | NPC-vs-NPC targeting in the simulation (the four factions fight each other) | Needs target selection among NPCs in `crates/eve-space` brains; the rule input is above |
| CUT (no published rule) | Gate sentries that shoot (EDENCOM GunStar 54579, Heavy GunStar 54580, Entropic Disintegrator Werpost 54577) | SDE gives the numbers only; no firing rule, targeting order or retribution timer published. EveJS places cosmetic sentries only |
| CUT (no published spawn table) | Pochven skirmish spawning and per-site NPC composition | No SDE link from a dungeon to a system; no spawn tables; `npc_authority.json` has no Triglavian, EDENCOM or Drifter profile |
| CUT (no published state data) | Invasion states (Redoubt, Bulwark, Fortress, Liminality, Minor Victory) and their system lists | Thresholds (75%, 76%, 96%) are on the wiki; per-system state is not. `map.GetEdencomFortressSystems` stays open (see the line above in this file) |
| OPEN | Filament entry (Pochven filaments 56065 to 56081, 1/5/15 ships), the 6.0 standing for Home systems | Needs the filament item and jump rules; not in the SDE beyond the counts |
| OPEN | Pochven standings for docking (-0.05), the LP store (-0.01), manufacturing and offices (3.00), local chat in delayed mode | Wiki rules; not built |

## 13. Wormhole space: research and the type catalog (2026-10-12)

Evidence and decisions: `docs/project/WORMHOLE_DESIGN.md` (sources, SDE numbers, the SDE against the wiki, what is not published, owner decisions).

| Status | Item | Missing work |
|---|---|---|
| DONE (tests) | The 129 wormhole types from the pinned SDE: lifetime, mass, per-jump mass, target class, distribution (`WormholeTypeCatalog`) | `WormholeTypeCatalogTests` (PgFact, real SDE). K162 is not a type of the catalog (no mass attribute) |
| DONE (tests) | Rules on the numbers: J-space ids, the per-ship limit, the mass after a transit, the mass and lifetime states (wiki thresholds), the 5-minute polarization time (`WormholeRules`) | `WormholeRulesTests` (synthetic numbers, the state boundaries) |
| SUPERSEDED (see "Wormhole space: final status")| Jump through a wormhole (`wormholeMgr.WormholeJump`) | Needs a wormhole entity in the simulation and a jump callback in the composition (`SpaceRpc.cs` line 72 passes none; the call returns None). Owner decision 1 (the class of each J-space system) comes first |
| OPEN | Connection generator (statics, k-space connections, spawn) and the lifetime and mass jobs | Statics per class and the spawn chances are not published (design section 4); owner decision 2 |
| SUPERSEDED (see "Wormhole space: final status")| Signatures, system effects, Sleeper and Drifter sites, Thera and Turnur connections | Design sections 4, 5 and 7. Effect magnitudes per class are not published (owner decision 3) |
| SUPERSEDED (see "Wormhole space: final status")| Local chat in J-space | The brief said there is none; the wiki and the client say there is one (owner decision 4) |

### Slice 2 (2026-10-12)

| Status | Item | Missing work |
|---|---|---|
| DONE (tests) | J-space system classes from the V24.01 client data (`WormholeSystemClasses`, embedded copy `client-systems.jsonl.gz`): 2,604 systems | `WormholeClientDataTests`. Fuzzwork `mapLocationWormholeClasses` cross-check not done |
| DONE (tests) | Per-class effect beacons from the SDE (group 920, 36 types) with their dogma modifiers (`WormholeEffects`) | `WormholeEffectsTests`. The wiki effects page is not reachable |
| SUPERSEDED (see "Wormhole space: final status")| Which effect each J-space system has | Design 9.2 (owner decision 6) |
| CUT | Connection generator, statics, spawn | No source for statics per class or probabilities (design 9.3, owner decision 2) |
| SUPERSEDED (see "Wormhole space: final status")| Jump, lifetime/mass jobs, collapse, signatures, effects on ships, Sleeper/Drifter sites, Thera/Turnur, MITM run | Design 9.6 |
| SUPERSEDED (see "Wormhole space: final status")| Local chat in J-space | Design 9.4 (owner decision 4) |

### Slice 3 (2026-10-12)

| Status | Item | Missing work |
|---|---|---|
| DONE (tests) | Committed J-space data (2,604 systems, 393 KB; shattered effects from the wiki): class, effect beacon, statics, sources and cross-checks (`Data/jspace-systems.json`, `JSpaceSystems`) | `JSpaceSystemsTests` (PgFact checks statics against the SDE) |
| PARTIAL | Local chat in J-space: delayed membership and Local message delivery are implemented in the Host gateway | V24.01 request and notice contract; Q-13 |
| OPEN | Class 4 statics: EveJS one static, wiki "usually two" | Design 10.5 (owner decision 7) |
| CUT | Connection generator and static pairing | No published pairing (design 10.4, owner decision 8) |
| SUPERSEDED (see "Wormhole space: final status")| Setup loader into a PostgreSQL schema; jump; entities; sim jump (Rust build needs 9 to 11 GB free); signatures; effects on ships; sites; Thera/Turnur; MITM run | Design 10.4 |

### Slice 4 (2026-10-12)

| Status | Item | Missing work |
|---|---|---|
| DONE (tests) | Connection generator for the statics (seeded, documented mechanic) and durable lifecycle jobs on Eve.Scheduler (expiry, static replacement, transit collapse, restart) | `dotnet/tests/Eve.Wormholes.Tests` (14). Not wired into the Host composition yet |
| CUT | Wandering K-space connections (K162) | No published spawn rate (design 11) |
| CUT | Region-neighbour exclusion for statics | The pages read do not state it (design 11) |
| SUPERSEDED (see "Wormhole space: final status")| Wormhole entities in space, sim jump, WormholeMgr.WormholeJump callback, signatures, effects on ships, sites, Thera/Turnur, delayed Local gateway (partial), MITM run | Design 11 |

### Slice 5 (2026-10-12)

| Status | Item | Missing work |
|---|---|---|
| SUPERSEDED (see "Wormhole space: final status")| Wormhole jump path: simulation body kind, placement (protocol 60), jump and arrival in `crates/eve-space`, WormholeMgr callback in the composition | Design 12.3 (not started in slice 5; investigated) |
| OPEN | Effects as dogma modifiers | No system-wide effect mechanism exists in eve.rs (design 12.4) |
| SUPERSEDED (see "Wormhole space: final status")| Signatures, sites, Thera/Turnur, headless MITM run | Design 12.4 |
| PARTIAL | Delayed Local | Host gateway lists active speakers in wormhole systems; the wider chat channel set and live walk remain open | Q-13; WORMHOLE_DESIGN 10.3 |

### Slice 6 (2026-10-12)

| Status | Item | Missing work |
|---|---|---|
| DONE (tests) | Protocol 63: wormhole body kind, `PlaceWormhole`/`RemoveWormhole` (Rust sim, codec on both sides, goldens), jump to a wormhole end and arrival by its far end | `eve-space` 747 + codec 299 tests. Design 13 |
| OPEN | The far end as a body of its own scene (pilots must be able to jump back) | Design 13 |
| SUPERSEDED (see "Wormhole space: final status")| Host placement of the ends, `WormholeMgr.WormholeJump` through the lifecycle, composition wiring | Design 13 |
| SUPERSEDED (see "Wormhole space: final status")| Effects, signatures, sites, Thera exits, MITM run | Design 13 |


## Pochven population (2026-10-12, after step 1)

| Status | Item | Note |
|---|---|---|
| DONE (tests) | NPC-vs-NPC attack between the Pochven factions | `brains::pochven_tests` (attack, own faction, outside Pochven) |
| DONE (tests) | Gate sentries: a werpost at each gate of a Pochven system, 20 km out, stationary; locks a rogue drone, never a fellow triglavian | `pochven_population.rs`; rules in DEVIATIONS |
| DONE (tests) | Camps: two Damavik at the first two gates of a Triglavian system | `pochven_population.rs` |
| DONE (tests) | Skirmish spawner: every 900 s, four factions in turn, deterministic, capped at 40 NPCs per scene | `pochven_population.rs` (`populate_pochven`) |
| DONE (data) | System states: 27 Pochven systems held by the Triglavians; EDENCOM list empty | `data/pochven_states.json` (source and date in the file) |
| DONE (unit rule) | NPC bounty payout in Pochven | Host payout returns before recording a payout when the victim system is in Pochven; the owner rule leaves loot and drops unchanged. Regression: PochvenRulesTests.NPC_bounty_payout_is_disabled_only_in_Pochven. |
| CUT | Fortress and Heavy GunStar placements in Pochven | No Pochven system is an EDENCOM fortress in the wiki; the path is tested through the data file |
| DONE (live, eve.rs only) | Live walk: a Cockroach logs into Pochven 30000021 in space; the Werpost (54577) and a Liminal Damavik (52182) come into its scene and fight | `tools/headless-client/tests/test_pochven_live.py`; the pilot is seeded (no station); no EVE.js reference run, so no mitm diff |
| OPEN | Skirmish spawns and the other gates' sentries in the live walk | Only 25 s observed; the skirmish cadence is 900 s |
| DONE (tests) | Step 3, filament rules and data (17 SDE types, caps, spool-ups, families, trace numbers) | `crates/eve-space/src/filament.rs`; jumps not built |
| OPEN | Filament destinations (jump targets) | Not in the client extract (location list 4510 to 4569 absent); no invented destination |
| OPEN | Home-system 6.0 standing | Skipped per owner |


## Slice 3, step 1: zones outside Pochven

| Status | Item | Note |
|---|---|---|
| DONE (tests) | Arshat (30002253) as an EDENCOM zone: GunStars at its gates, Sentinel camps at the first two | `pochven_population.rs`, `data/pochven_states.json` (source and date in the file) |
| OPEN | More EDENCOM fortress, stronghold and minor-victory systems | No dated source names them (the wiki's list page 404s; the forum names are not states) |

### Slice 7 (2026-10-12)

| Status | Item | Missing work |
|---|---|---|
| DONE (tests) | 7b: placement of the ends with the system's scene, WormholeMgr.WormholeJump (mass, collapse, publication), polarization, world at start-up | Eve.Wormholes.Tests 17; the service jump has no test yet (DEVIATIONS) |
| DONE (tests) | 7c: system environment as dogma modifiers on the ship and its modules (C# dogma) | Eve.Dogma.Tests `SystemEnvironmentTests` (3). Not in the Rust simulation |
| SUPERSEDED (see "Wormhole space: final status")| 7d signatures, 7e Sleeper/Drifter sites, 7f Thera exits | Not started |

| SUPERSEDED (see "Wormhole space: final status")| 7d signatures in probe scanning | Design 14: no wormhole rows in the probe path; scan-strength value not read from the wiki |
| SUPERSEDED (see "Wormhole space: final status")| 7e Sleeper and Drifter sites | Design 14: site content not published (owner decision needed) |
| SUPERSEDED (see "Wormhole space: final status")| 7f Thera exits | Design 14: Thera's connections are wandering (K-space), cut by owner decision |

### Slice 8 (2026-10-12)

| Status | Item | Missing work |
|---|---|---|
| DONE (tests) | Thera's seeded connections (12, documented types and endpoints), respawn after collapse | `TheraConnectionsTests`. Design 15 |
| SUPERSEDED (see "Wormhole space: final status")| Signature scan strength per type | No per-type value in the wiki pages or EveJS (design 15) |
| SUPERSEDED (see "Wormhole space: final status")| Sleeper/Drifter sites | Combat site names per class from the wiki; per-system counts and shares not published (design 15) |
| NOT DONE | Environment effects in the Rust sim | Design 15 |
| SUPERSEDED (see "Wormhole space: final status")| Service-layer wormhole jump tests; headless live check | Design 15 |

### Slice 9 and slice 10 (2026-10-12)

| Status | Item | Missing work |
|---|---|---|
| DONE (tests) | Signatures per system (4 to 9 from the seed), shares by category, Drifter five combat sites (slice 9) | `WormholeSitesTests` |
| DONE (tests) | Real site names and client dungeon keys per class, zones of gas and ore, C13 rule (slice 10) | `WormholeSitesTests`, DEVIATIONS |
| DONE (tests) | Plan connected to `CosmicSiteDirector`: a J-system's planned combat, data and relic sites are placed in their slots, with containers and Sleeper waves (48 templates) | `A_wormhole_system_gets_the_sites_of_its_plan...` (PgFact), `WormholeSiteTemplatesTests` |
| DONE (tests) | Probe scan of a J signature: strength 0.0 (SDE 1908); a probe in range finds it by its geometry | `ProbeScanMathTests` |
| SUPERSEDED (see "Wormhole space: final status")| Gas and ore signatures placed as sites (no site kind) | Mining site generation |
| NOT DONE | Rust sim environment effects (the attributes the sim models) | Design 15 |
| DONE (tests) | Service-layer `WormholeJumpAsync` tests: mass refusal, jump and destination end, polarization, collapse (own world in the space rig) | `WormholeJumpServiceTests` (PgFact). Polarization expiry and RPC path not covered |
| NOT DONE | Headless MITM live check on 26110/26210 | Design 15 |

### Slice 11 to 12 (2026-10-12)

| Status | Item | Missing work |
|---|---|---|
| DONE (tests) | Live jump in and back through a static wormhole (headless, through the MITM proxy); the cause of the drift: a wormhole end inside a star; the scene dropped with its ends | `test_wormhole_live.py` (passed), `a_wormhole_end_inside_a_star_is_placed_outside_it_on_its_bearing`, `a_wormhole_end_placed_in_a_system_nobody_is_in_is_still_there_after_a_poll` |
| DONE (tests) | Polarization expiry; the wormhole jump through the RPC service | `WormholeJumpServiceTests` (2 new) |
| DONE (tests) | Gas and ore sites: 9 reservoirs (signatures, clouds), 11 deposits (anomalies, rocks), placed and restored | `WormholeSiteTemplatesTests`, `SiteResourceTests`, `CosmicSitesTests` |
| SUPERSEDED (see "Wormhole space: final status")| Live check of a gas or ore site | No headless scenario |
| SUPERSEDED (see "Wormhole space: final status")| Removal of a site's resources when the site ends | Shortcut in DEVIATIONS |

## Wormhole space: final status (2026-10-12)

The design is in `docs/project/WORMHOLE_DESIGN.md`; the deviations and project rules are in `docs/reference/compat/DEVIATIONS.md` (the sections named "Wormhole ..."). Earlier wormhole rows above are superseded by this section.

| Status | Area | What is there | Evidence |
|---|---|---|---|
| DONE (tests) | Wormhole types, rules, J-space classes, effect beacons | 129 types from the SDE; mass and lifetime rules; 2,604 systems with their class; 36 beacons | `WormholeTypeCatalogTests`, `WormholeRulesTests`, `WormholeClientDataTests`, `WormholeEffectsTests` |
| DONE (tests) | Statics, lifecycle, collapse, respawn | seeded connections; expiry, static replacement, transit collapse, restart; durable jobs | `Eve.Wormholes.Tests` (PostgreSQL where marked) |
| DONE (tests) | Thera connections | 12 connections, 8 types, respawn after collapse | `TheraConnectionsTests` |
| DONE (tests) | Protocol 63 wormhole ends in the simulation | PlaceWormhole and RemoveWormhole (Rust, codec, goldens); jump to the far end; arrival clear of the end | `eve-space` tests, `SimCodecTests` |
| DONE (tests) | Ends in space and the jump | host places both ends with the scene; mass pre-check; jump; collapse removes both ends; polarization 5 min; RPC answers | `WormholeJumpServiceTests`, `WormholeMgr` RPC tests |
| DONE (tests) | System environment | beacon modifiers as dogma on the ship (C#); the ship's spec to the simulation after each system change; resistance bonuses on the resonance attributes | `SystemEnvironmentTests`; DEVIATIONS "Wormhole environment in the simulation" |
| DONE (tests) | Signatures, sites and the probe scan | plan of 4 to 9 signatures per J-system; 68 site templates (combat, data, relic from the EveJS wormhole pack; gas and ore from the EveJS gas and ore packs); probe targets with strength 0.0 | `WormholeSitesTests`, `WormholeSiteTemplatesTests`, `CosmicSitesTests`, `ProbeScanMathTests` |
| DONE (tests) | Gas and ore resources | a reservoir's cloud and a deposit's rocks placed at the site, put back on entry, removed when the site ends | `SiteResourceTests`, `CosmicSitesTests` (expiry) |
| DONE (live) | Jump in and back through a static wormhole | headless, through the MITM proxy: undock, warp to the home end, jump, fly back to the junction end, jump | `tools/headless-client/tests/test_wormhole_live.py` (passed) |
| RULE (project) | Signature counts and shares | 4 to 9 per system; combat 40%, relic 15%, data 15%, gas 20%, ore 10%; Drifter systems have 5 combat sites | DEVIATIONS "Wormhole signatures, sites and scan strength" |
| RULE (project) | Plan seed | the system id (not the world seed) | DEVIATIONS "Wormhole sites in the director" |
| RULE (project) | End position and star clearance | ends 50 km from the system centre, pushed out of any star or planet | DEVIATIONS "The live drift and the star under a wormhole end" |
| RULE (project) | Minimum warp, arrival, return | 150 km minimum warp (game rule); arrival 12 km from the far end; the return flies in | DEVIATIONS "Warp to a wormhole end ..." |
| RULE (project) | Resource stock | a cloud holds 1,000 units; rock quantities from the client | DEVIATIONS "Gas and ore sites of wormhole space" |
| CUT (owner) | K-space wandering connections | not built | owner decision, slice 5 |
| CUT (owner) | Region-adjacency exclusion of statics | not applied | owner decision, slice 5 |
| NOT DONE | Shattered Ice Field (ice belt) | in the data, not planned (no ice category) | DEVIATIONS "Wormhole sites ..." |
| PARTIAL | J-space Local membership and delivery | Q-13 decides DELAYED membership; Host gateway roster and message delivery are implemented | owner decision Q-13 (decided 2026-10-19); live two-character walk remains open |
| NOT DONE | Live check of a gas or ore site | no headless scenario scans or mines one | — |
| NOT DONE | Capture comparison with a stargate jump | not compared | — |
| NOT DONE | Client attribute refresh after a system change | the client reads its ship attributes at its next change | DEVIATIONS "Wormhole environment in the simulation" |
| NOT DONE | Trace logging behind an environment variable | not built (the destiny data sufficed) | — |
| NOT DONE | Mining of wormhole ore and gas | the belt mining path is not tested on these sites | DEVIATIONS "Gas and ore sites of wormhole space" |


## Client static data embedded (2026-10-12)

The server runs without `EVE_CLIENT_SDE_DIR`: the rows and fields it reads from the V24.01 client export are committed as embedded copies, built by `tools/dev/extract-client-sde.py` (build 3396210).

| Status | Item | Where / blocker |
|---|---|---|
| DONE (tests) | Consumers inventoried: `solarsystemcontent` (flags, stargate lists), `spacecomponentsbytype` (tolls, deploy blockers), `systems` (wormhole classes), `missions` (text IDs, templates) | `ClientStaticData`; DEVIATIONS "Client-side data read by the server" |
| DONE (tests) | Embedded copies: 5, 9, 8,113 and 2,892 rows (94 KB in all); each header records the build, the source and the generator | `dotnet/src/Eve.Universe/Data/`, `dotnet/src/Eve.Services.Core/Data/` |
| DONE (tests) | Loaders read the embedded copies by default; `EVE_CLIENT_SDE_DIR` overrides them where the export holds the file; no warning when it is unset | `ClientStaticData.Open`; `CharacterStores`, `SpaceComposition`, `MissionComposition` |
| DONE (tests) | Wormholes are on without the variable (their classes are embedded); `EVE_WORMHOLES=off` turns them off | `SpaceComposition`; `ClientStaticDataHostTests` |
| DONE (tests) | Not consumers of the client directory: the epic arcs, agents in space and mission dungeons come from the pinned SDE in the database; the warzone map is embedded already (`warzone-systems.json`) | DEVIATIONS "Client-side data read by the server" |
| OPEN | Live run of `tools/dev/run-eve-live.sh` without the variable | Needs a live run |


## EveJS response audit backlog (2026-10-13)

### Snapshot, review result and status gates

This is the method-level entrance for the received [Japanese summary](../../docs/project/service-response-audit/evejs-only-summary.md), [672-row detailed ledger](../../docs/project/service-response-audit/evejs-only-responses.md) and [Host/protobuf audit](../../docs/project/service-response-audit/host-protobuf.md). It extends the existing native-audit/feature TODO rows; it is **not a second set of additive work items**. Match by service + method/operation before assigning or closing work. Existing feature owner rows, NOT NEEDED decisions and documented policy differences are retained.

- Canonical root/branch: `repository root` / `main`. Source-audit start HEAD `52b8421f34e635900a18459da4403f26f2dfaec0`; final source-audit and documentation review HEAD `dda03ca0464043822e68c8348604ff34eadedcac`. The received source snapshot includes parent dirty RPC declarations (870 attributed declarations); it is not a clean-tree/runtime acceptance claim. Reference snapshots: EveJS `828823a75f211fdecb945b087a26c225f111491f`, V24.01 eve-logic `1dbb5711a072a146e1d228365668ac05655eefe1`.
- Summary review: counts and evidence tiers agree with the ledger. 672 rows = 476 physical named-export differences (448 unimplemented named exits + 3 client-local covered + 25 legacy/not confirmed) + 182 gateway selector candidates + 14 response/notification/session/provider groups. There are 63 service groups in the 448-exit index below. These are not counts of wholly unimplemented business domains or reproduced broken features.
- Host coverage: 181 registered keys / 1280 paths in the full source profile (1212 primary + 68 alias paths), 8 detailed common Host RPC source comparisons and 1272 other response paths not fully compared in that slice. Runtime/differential/client acceptance remains not confirmed; no tests/builds were run for this documentation update.
- **UNIMPLEMENTED NAMED EXIT** means the exact named exit is absent from the inspected partial type/Method/HasMethod/Host or bound registration. It does **not** mean the whole service/business operation is unimplemented. **Alternative API/alias equivalence remains not confirmed** unless explicitly recorded; verify the V24 caller, meaningful state, full arguments/kwargs, result and failure contract before adding another endpoint.
- **UNIMPLEMENTED PROVIDER/NOTIFICATION/RESPONSE BRANCH** means a route can exist while real reads/writes/persistence or a response/notice producer is absent. Connect existing owners; do not close it with empty defaults, guessed values, captured packets or fake success.
- **FIX EXISTING RESPONSE / VERIFY FIELD IMPACT** is separate from new implementation. **VERIFY CANDIDATE/LEGACY/DTO** is not a blanket OPEN gameplay defect. Preserve CCP-required improvements and policy decisions rather than copying permissive or partial-commit EveJS behavior.
- Categories overlap. Service differences (11 advertised / 29 source candidates), 39 bound rows and 6 provider groups are **not** added to 448. In the 14 groups, category tags occur 7 result / 3 notification / 2 session / 6 provider times, with shared rows; 13 groups have concrete source differences and 1 has a current-sequence verification gap. In the named ledger's V24 column, 40 rows have caller/forwarding-source evidence and 436 are not confirmed; this is not an exhaustive caller count across the older native traces.

### Service boundary candidates (category 1; no additive service implementation count)

The 143-name EveJS advertisement has **11 names absent from the full RS source profile**: `objectCaching, structure, loadService, planetOrbitalRegistryBroker, LSC, certificateMgr, tutorialSvc, bookmarkMgr, ownerGroupManager, contractMgr, publicGatewaySvc`. These are a source/advertisement difference, not 11 proven failed player operations.

The wider 210-name constructor/re-export/bootstrap source inventory has **29 absent candidates**: `activityMgr, autoMoonMining, bookmarkMgr, certificateMgr, clientStatLogger, contractMgr, dungeonUniverseSite, eventLog, externalQueueMgr, facWarMgr, inventoryAssetHoldingMgr, loadService, LSC, market, miningCommand, miningResourceSite, monolithTaskTrackerMgr, objectCaching, ownerGroupManager, photoUploadSvc, planetOrbitalRegistryBroker, publicGateway, publicGatewaySvc, search, sessionMgr, structure, tcpRawProxyService, tutorialSvc, XmppChatMgr`. The 11 are included in these 29; there are 18 additional candidates. Constructor success, runtime activation, caller reachability and semantic replacement are not confirmed. Services with no finite named-handler list stay candidates; do not fabricate method names for them.

Use the [service boundary ledger](../../docs/project/service-response-audit/evejs-only-responses.md:27) and the named index below. It covers 144 named rows on absent services: 143 absent-exit rows plus the single legacy publicGateway row. Do not duplicate these as another 144 tasks. Section 37a NOT NEEDED decisions for loadService/tutorialSvc/bookmarkMgr/certificateMgr/LSC/publicGatewaySvc are preserved. In particular publicGatewaySvc is a client-local adapter; registering the name is not a public protobuf gateway implementation. Newer native startup evidence and the old gateway owner decision must be reconciled explicitly, not silently discarded.

### Named exits: complete 448-method index (category 2)

**Status for every method in this index: UNIMPLEMENTED NAMED EXIT; VERIFY REMAINING ALTERNATIVE API / V24 FLOW.** Exact-name absence is not an unimplemented whole domain. Source-proven client-local alternatives and legacy candidates are separated below; dynamic response/notification acceptance remains not confirmed. These are method-level supplements to existing owner TODOs, not additive duplicate tasks.

#### Named group: account (10 exits)

[GetAurumBalance (EJS-0001)](../../docs/project/service-response-audit/evejs-only-responses.md:252); [GetDefaultContactCost (EJS-0002)](../../docs/project/service-response-audit/evejs-only-responses.md:253); [GetDefaultWalletDivision (EJS-0003)](../../docs/project/service-response-audit/evejs-only-responses.md:254); [GetEntryTypes (EJS-0004)](../../docs/project/service-response-audit/evejs-only-responses.md:255); [GetJournal (EJS-0005)](../../docs/project/service-response-audit/evejs-only-responses.md:256); [GetJournalForAccounts (EJS-0006)](../../docs/project/service-response-audit/evejs-only-responses.md:257); [GetKeyMap (EJS-0007)](../../docs/project/service-response-audit/evejs-only-responses.md:258); [GetTransactions (EJS-0008)](../../docs/project/service-response-audit/evejs-only-responses.md:259); [GetWalletDivisionsInfo (EJS-0009)](../../docs/project/service-response-audit/evejs-only-responses.md:260); [SetContactCost (EJS-0010)](../../docs/project/service-response-audit/evejs-only-responses.md:261).

#### Named group: activityMgr (4 exits)

[ActivitySeen (EJS-0011)](../../docs/project/service-response-audit/evejs-only-responses.md:262); [GetActivitiesPopState (EJS-0012)](../../docs/project/service-response-audit/evejs-only-responses.md:263); [GetActivityBadgeStatuses (EJS-0013)](../../docs/project/service-response-audit/evejs-only-responses.md:264); [SetActivitiesPopState (EJS-0014)](../../docs/project/service-response-audit/evejs-only-responses.md:265).

#### Named group: agentMgr (13 exits)

[GetDungeonShipRestrictions (EJS-0017)](../../docs/project/service-response-audit/evejs-only-responses.md:268); [GetEntryPoint (EJS-0018)](../../docs/project/service-response-audit/evejs-only-responses.md:269); [GetInfoServiceDetails (EJS-0019)](../../docs/project/service-response-audit/evejs-only-responses.md:270); [GetMissionJournalInfo (EJS-0020)](../../docs/project/service-response-audit/evejs-only-responses.md:271); [GetMissionKeywords (EJS-0021)](../../docs/project/service-response-audit/evejs-only-responses.md:272); [GetReplayTimestamp (EJS-0022)](../../docs/project/service-response-audit/evejs-only-responses.md:273); [GetStandingGainsForMission (EJS-0023)](../../docs/project/service-response-audit/evejs-only-responses.md:274); [GotoLocation (EJS-0025)](../../docs/project/service-response-audit/evejs-only-responses.md:276); [IsCheatingWithAgent (EJS-0026)](../../docs/project/service-response-audit/evejs-only-responses.md:277); [RemoveOfferFromJournal (EJS-0027)](../../docs/project/service-response-audit/evejs-only-responses.md:278); [ShouldAlwaysAllowReplay (EJS-0028)](../../docs/project/service-response-audit/evejs-only-responses.md:279); [WarpToAgentInSpace (EJS-0029)](../../docs/project/service-response-audit/evejs-only-responses.md:280); [WarpToLocation (EJS-0030)](../../docs/project/service-response-audit/evejs-only-responses.md:281).

#### Named group: alert (3 exits)

[BeanDelivery (EJS-0031)](../../docs/project/service-response-audit/evejs-only-responses.md:282); [GetLogModeForError (EJS-0032)](../../docs/project/service-response-audit/evejs-only-responses.md:283); [GroupBeanDelivery (EJS-0033)](../../docs/project/service-response-audit/evejs-only-responses.md:284).

#### Named group: autoMoonMining (2 exits)

[RequestMiningCycleOutput (EJS-0036)](../../docs/project/service-response-audit/evejs-only-responses.md:287); [RequestMiningDetails (EJS-0037)](../../docs/project/service-response-audit/evejs-only-responses.md:288).

#### Named group: beyonce (7 exits)

[BookmarkLocation (EJS-0038)](../../docs/project/service-response-audit/evejs-only-responses.md:289); [BookmarkScanResult (EJS-0039)](../../docs/project/service-response-audit/evejs-only-responses.md:290); [CmdFleetTagTarget (EJS-0040)](../../docs/project/service-response-audit/evejs-only-responses.md:291); [CmdGotoBookmark (EJS-0041)](../../docs/project/service-response-audit/evejs-only-responses.md:292); [CmdGotoPoint (EJS-0042)](../../docs/project/service-response-audit/evejs-only-responses.md:293); [CmdSteerDirection (EJS-0043)](../../docs/project/service-response-audit/evejs-only-responses.md:294); [GetCapacityOfSiphon (EJS-0044)](../../docs/project/service-response-audit/evejs-only-responses.md:295).

#### Named group: bookmarkMgr (4 exits)

[CreateFolder (EJS-0045)](../../docs/project/service-response-audit/evejs-only-responses.md:296); [GetBookmarks (EJS-0046)](../../docs/project/service-response-audit/evejs-only-responses.md:297); [GetBookmarksInFolder (EJS-0047)](../../docs/project/service-response-audit/evejs-only-responses.md:298); [GetFolders (EJS-0048)](../../docs/project/service-response-audit/evejs-only-responses.md:299).

#### Named group: certificateMgr (9 exits)

[BatchCertificateGrant (EJS-0049)](../../docs/project/service-response-audit/evejs-only-responses.md:300); [BatchCertificateUpdate (EJS-0050)](../../docs/project/service-response-audit/evejs-only-responses.md:301); [GetAllShipCertificateRecommendations (EJS-0051)](../../docs/project/service-response-audit/evejs-only-responses.md:302); [GetCertificateCategories (EJS-0052)](../../docs/project/service-response-audit/evejs-only-responses.md:303); [GetCertificateClasses (EJS-0053)](../../docs/project/service-response-audit/evejs-only-responses.md:304); [GetCertificatesByCharacter (EJS-0054)](../../docs/project/service-response-audit/evejs-only-responses.md:305); [GetMyCertificates (EJS-0055)](../../docs/project/service-response-audit/evejs-only-responses.md:306); [GrantCertificate (EJS-0056)](../../docs/project/service-response-audit/evejs-only-responses.md:307); [UpdateCertificateFlags (EJS-0057)](../../docs/project/service-response-audit/evejs-only-responses.md:308).

#### Named group: charMgr (13 exits)

`GetPrivateInfo` is implemented as the V24.01 corporation-member editor row, using the character record, wallet balance and security-status provider; `CharMgrTests.Private_info_returns_the_authoritative_member_editor_row` checks its fields and values. Existing exact-name TODO evidence (same owner/work): [existing row](../../docs/project/TODO.md:50). Retain its status/policy; use the detailed row for the contract and classification.

Personal contacts and labels are implemented on the bound `charMgr`: `AddContact` and the client-called `EditContact` update contact relationship and watchlist state; `CreateLabel`, `DeleteLabel`, `EditLabel`, `AssignLabels`, and `RemoveLabels` update character-scoped state and persist through the Host read model. The RPC tests cover contact editing, label editing, bitmask assignment/removal, deletion, legacy allocation hints, and continued allocation; the Host test covers persisted label state after restart; `test_personal_contact_labels_can_be_created_edited_and_deleted_live` checks the bound label RPC flow with the headless client.

[get_home_station_row (EJS-0063)](../../docs/project/service-response-audit/evejs-only-responses.md:314); [GetAssetWorth (EJS-0064)](../../docs/project/service-response-audit/evejs-only-responses.md:315); [GetCloneInfo (EJS-0065)](../../docs/project/service-response-audit/evejs-only-responses.md:316); [getHomeStationRow (EJS-0066)](../../docs/project/service-response-audit/evejs-only-responses.md:317); [GetPrivateInfo (EJS-0067)](../../docs/project/service-response-audit/evejs-only-responses.md:318); [GetTopBounties (EJS-0068)](../../docs/project/service-response-audit/evejs-only-responses.md:319); [List (EJS-0069)](../../docs/project/service-response-audit/evejs-only-responses.md:320); [ListIncludingContainers (EJS-0070)](../../docs/project/service-response-audit/evejs-only-responses.md:321); [ListStationItems (EJS-0071)](../../docs/project/service-response-audit/evejs-only-responses.md:322); [ListStations (EJS-0072)](../../docs/project/service-response-audit/evejs-only-responses.md:323); [LogStartOfCharacterCreation (EJS-0073)](../../docs/project/service-response-audit/evejs-only-responses.md:324); [MachoBindObject (EJS-0074)](../../docs/project/service-response-audit/evejs-only-responses.md:325).

#### Named group: charUnboundMgr (5 legacy exports)

`PrepareCharacterForDelete` and `DeleteCharacter` are implemented on `charUnboundMgr`. Character selection client methods call both endpoints. `Eve.Services.CharacterSelect.Tests` passed 61 tests, including preparation, cancellation, persisted queue state after restart, and deletion.

The remaining exports are not selected for implementation: source search found no V24.01 client references to `GetCharacterToSelect`, `GetCharCreationInfo`, `GetCharNewExtraCreationInfo`, or `IsUserReceivingCharacter`. `GetTopBounties` is used through `bountySvc.GetTopBounties`, a separate service call. See [EJS-0077 through EJS-0081](../../docs/project/service-response-audit/evejs-only-responses.md:328).

#### Named group: clientStatLogger (1 exits)

Existing exact-name TODO evidence (same owner/work): [existing row](../../docs/project/TODO.md:55). Retain its status/policy; use the detailed row for the contract and classification.

[LogString (EJS-0083)](../../docs/project/service-response-audit/evejs-only-responses.md:334).

#### Named group: config (2 exits)

[GetMapObjects (EJS-0084)](../../docs/project/service-response-audit/evejs-only-responses.md:335); [GetMultiGraphicsEx (EJS-0085)](../../docs/project/service-response-audit/evejs-only-responses.md:336).

#### Named group: contractMgr (12 exits)

[AcceptContract (EJS-0086)](../../docs/project/service-response-audit/evejs-only-responses.md:337); [CollectMyPageInfo (EJS-0087)](../../docs/project/service-response-audit/evejs-only-responses.md:338); [CompleteContract (EJS-0088)](../../docs/project/service-response-audit/evejs-only-responses.md:339); [CreateContract (EJS-0089)](../../docs/project/service-response-audit/evejs-only-responses.md:340); [DeleteContract (EJS-0090)](../../docs/project/service-response-audit/evejs-only-responses.md:341); [GetContract (EJS-0091)](../../docs/project/service-response-audit/evejs-only-responses.md:342); [GetContractListForOwner (EJS-0092)](../../docs/project/service-response-audit/evejs-only-responses.md:343); [GetItemsInStation (EJS-0093)](../../docs/project/service-response-audit/evejs-only-responses.md:344); [GetLoginInfo (EJS-0094)](../../docs/project/service-response-audit/evejs-only-responses.md:345); [GetMyExpiredContractList (EJS-0095)](../../docs/project/service-response-audit/evejs-only-responses.md:346); [NumOutstandingContracts (EJS-0096)](../../docs/project/service-response-audit/evejs-only-responses.md:347); [SearchContracts (EJS-0097)](../../docs/project/service-response-audit/evejs-only-responses.md:348).

#### Named group: contractProxy (30 exits)

Existing exact-name TODO evidence (same owner/work): [existing row](../../docs/project/TODO.md:52). Retain its status/policy; use the detailed row for the contract and classification.

[AcceptContract (EJS-0098)](../../docs/project/service-response-audit/evejs-only-responses.md:349); [CollectMyPageInfo (EJS-0099)](../../docs/project/service-response-audit/evejs-only-responses.md:350); [CompleteContract (EJS-0100)](../../docs/project/service-response-audit/evejs-only-responses.md:351); [CreateContract (EJS-0101)](../../docs/project/service-response-audit/evejs-only-responses.md:352); [DeleteContract (EJS-0102)](../../docs/project/service-response-audit/evejs-only-responses.md:353); [DeleteContractNotification (EJS-0103)](../../docs/project/service-response-audit/evejs-only-responses.md:354); [DeleteMultipleContracts (EJS-0104)](../../docs/project/service-response-audit/evejs-only-responses.md:355); [DeleteNotification (EJS-0105)](../../docs/project/service-response-audit/evejs-only-responses.md:356); [FinishAuction (EJS-0106)](../../docs/project/service-response-audit/evejs-only-responses.md:357); [GetAvailableContractList (EJS-0107)](../../docs/project/service-response-audit/evejs-only-responses.md:358); [GetContract (EJS-0108)](../../docs/project/service-response-audit/evejs-only-responses.md:359); [GetContractBids (EJS-0109)](../../docs/project/service-response-audit/evejs-only-responses.md:360); [GetContractListForOwner (EJS-0110)](../../docs/project/service-response-audit/evejs-only-responses.md:361); [GetContractListForStatus (EJS-0111)](../../docs/project/service-response-audit/evejs-only-responses.md:362); [GetContractsForOwner (EJS-0112)](../../docs/project/service-response-audit/evejs-only-responses.md:363); [GetContractTypes (EJS-0113)](../../docs/project/service-response-audit/evejs-only-responses.md:364); [GetCourierContractFromItemID (EJS-0114)](../../docs/project/service-response-audit/evejs-only-responses.md:365); [GetItemsInContainer (EJS-0115)](../../docs/project/service-response-audit/evejs-only-responses.md:366); [GetItemsInDockableLocation (EJS-0116)](../../docs/project/service-response-audit/evejs-only-responses.md:367); [GetLoginInfo (EJS-0117)](../../docs/project/service-response-audit/evejs-only-responses.md:368); [GetMyBids (EJS-0118)](../../docs/project/service-response-audit/evejs-only-responses.md:369); [GetMyCurrentContractList (EJS-0119)](../../docs/project/service-response-audit/evejs-only-responses.md:370); [GetMyExpiredContractList (EJS-0120)](../../docs/project/service-response-audit/evejs-only-responses.md:371); [GetNumItemsInContainers (EJS-0121)](../../docs/project/service-response-audit/evejs-only-responses.md:372); [GM_ExpireContract (EJS-0122)](../../docs/project/service-response-audit/evejs-only-responses.md:373); [NumOutstandingContracts (EJS-0123)](../../docs/project/service-response-audit/evejs-only-responses.md:374); [PlaceBid (EJS-0124)](../../docs/project/service-response-audit/evejs-only-responses.md:375); [SearchContracts (EJS-0125)](../../docs/project/service-response-audit/evejs-only-responses.md:376); [SetContractExpired (EJS-0126)](../../docs/project/service-response-audit/evejs-only-responses.md:377); [SplitStack (EJS-0127)](../../docs/project/service-response-audit/evejs-only-responses.md:378).

#### Named group: corporationSvc (3 exits)

Existing exact-name TODO evidence (same owner/work): [existing row](../../docs/project/TODO.md:71). Retain its status/policy; use the detailed row for the contract and classification.

[GetInfoWindowDataForChar (EJS-0128)](../../docs/project/service-response-audit/evejs-only-responses.md:379); [GetNPCDivisions (EJS-0129)](../../docs/project/service-response-audit/evejs-only-responses.md:380); [GetRecruitmentAdsByCriteria (EJS-0130)](../../docs/project/service-response-audit/evejs-only-responses.md:381).

#### Named group: corpStationMgr (14 exits)

Existing exact-name TODO evidence (same owner/work): [existing row](../../docs/project/TODO.md:70). Retain its status/policy; use the detailed row for the contract and classification.

[GetCorporateStationOffice (EJS-0131)](../../docs/project/service-response-audit/evejs-only-responses.md:382); [GetImprovementStaticData (EJS-0132)](../../docs/project/service-response-audit/evejs-only-responses.md:383); [GetNumberOfUnrentedOffices (EJS-0133)](../../docs/project/service-response-audit/evejs-only-responses.md:384); [GetOwnerIDsOfClonesAtStation (EJS-0134)](../../docs/project/service-response-audit/evejs-only-responses.md:385); [GetPotentialHomeStations (EJS-0135)](../../docs/project/service-response-audit/evejs-only-responses.md:386); [GetQuoteForRentingAnOffice (EJS-0136)](../../docs/project/service-response-audit/evejs-only-responses.md:387); [GetRentableItems (EJS-0137)](../../docs/project/service-response-audit/evejs-only-responses.md:388); [GetStationDetails (EJS-0138)](../../docs/project/service-response-audit/evejs-only-responses.md:389); [GetStationImprovements (EJS-0139)](../../docs/project/service-response-audit/evejs-only-responses.md:390); [GetStationManagementServiceCostModifiers (EJS-0140)](../../docs/project/service-response-audit/evejs-only-responses.md:391); [GetStationOffices (EJS-0141)](../../docs/project/service-response-audit/evejs-only-responses.md:392); [GetStationServiceAccessRule (EJS-0142)](../../docs/project/service-response-audit/evejs-only-responses.md:393); [GetStationServiceIdentifiers (EJS-0143)](../../docs/project/service-response-audit/evejs-only-responses.md:394); [GetStationServiceStates (EJS-0144)](../../docs/project/service-response-audit/evejs-only-responses.md:395).

#### Named group: devIndexManager (1 exits)

[GetDevelopmentIndicesForSystem (EJS-0145)](../../docs/project/service-response-audit/evejs-only-responses.md:396).

#### Named group: dogmaIM (1 exits)

[GetLocationInfo (EJS-0146)](../../docs/project/service-response-audit/evejs-only-responses.md:397).

#### Named group: dungeonExplorationMgr (1 exits)

[GetInstancesForSolarsystem (EJS-0147)](../../docs/project/service-response-audit/evejs-only-responses.md:398).

#### Named group: eventLog (3 exits)

Existing exact-name TODO evidence (same owner/work): [existing row](../../docs/project/TODO.md:55). Retain its status/policy; use the detailed row for the contract and classification.

[LogClientEvent (EJS-0148)](../../docs/project/service-response-audit/evejs-only-responses.md:399); [LogClientStats (EJS-0149)](../../docs/project/service-response-audit/evejs-only-responses.md:400); [LogPlayerRequestedDisconnect (EJS-0150)](../../docs/project/service-response-audit/evejs-only-responses.md:401).

#### Named group: expertSystemMgr (2 exits)

[ConsumeExpertSystem (EJS-0151)](../../docs/project/service-response-audit/evejs-only-responses.md:402); [RemoveMyExpertSystem (EJS-0152)](../../docs/project/service-response-audit/evejs-only-responses.md:403).

#### Named group: externalQueueMgr (3 exits)

[PublishEvent (EJS-0153)](../../docs/project/service-response-audit/evejs-only-responses.md:404); [PublishEventPayload (EJS-0154)](../../docs/project/service-response-audit/evejs-only-responses.md:405); [PublishProxyEvent (EJS-0155)](../../docs/project/service-response-audit/evejs-only-responses.md:406).

#### Named group: facWarMgr (4 exits)

[GetCharacterRankInfo (EJS-0156)](../../docs/project/service-response-audit/evejs-only-responses.md:407); [GetCharacterRankOverview (EJS-0157)](../../docs/project/service-response-audit/evejs-only-responses.md:408); [GetMyCharacterRankInfo (EJS-0158)](../../docs/project/service-response-audit/evejs-only-responses.md:409); [GetMyCharacterRankOverview (EJS-0159)](../../docs/project/service-response-audit/evejs-only-responses.md:410).

#### Named group: hackingMgr (1 exits)

[StartNewGameInstance (EJS-0160)](../../docs/project/service-response-audit/evejs-only-responses.md:411).

#### Named group: insuranceSvc (3 exits)

[GetItemsToInsure (EJS-0161)](../../docs/project/service-response-audit/evejs-only-responses.md:412); [MachoBindObject (EJS-0162)](../../docs/project/service-response-audit/evejs-only-responses.md:413); [MachoResolveObject (EJS-0163)](../../docs/project/service-response-audit/evejs-only-responses.md:414).

#### Named group: invbroker (3 remaining exits)

[GetItemByID (EJS-0165)](../../docs/project/service-response-audit/evejs-only-responses.md:416); [ListFighterBay (EJS-0167)](../../docs/project/service-response-audit/evejs-only-responses.md:418); [ListFuelBay (EJS-0168)](../../docs/project/service-response-audit/evejs-only-responses.md:419). No direct callsite for these names appears in the V24.01 client source. The client's `invCacheContainer.ListDroneBay` wrapper calls the already implemented `List(flagDroneBay)` route, so a separate `ListDroneBay` RPC is unnecessary. `GetDamageForCrystals` is backed by owned inventory, the SDE `crystalsGetDamaged` attribute, and persisted module condition state. `ValidateItemCanBeOpened` checks owned item state, station hangar ownership, corporation division access, and in-space container access. `ValidateItemListCanBeOpened` returns inaccessible item IDs so V24 `ValidateLocationCacheForItems` invalidates their location cache; this intentionally supplies the transition that EveJS omits for ordinary inaccessible items while its handler still returns an empty list.

#### Named group: inventoryAssetHoldingMgr (4 exits)

[is_item_failure_enabled (EJS-0171)](../../docs/project/service-response-audit/evejs-only-responses.md:422); [is_item_validation_enabled (EJS-0172)](../../docs/project/service-response-audit/evejs-only-responses.md:423); [set_item_failure_enabled (EJS-0173)](../../docs/project/service-response-audit/evejs-only-responses.md:424); [set_item_validation_enabled (EJS-0174)](../../docs/project/service-response-audit/evejs-only-responses.md:425). These are only called from the `assetholding` Insider QA panel in the client source; ordinary V24 EVE inventory flows do not call them.

#### Named group: jumpCloneSvc (16 exits)

[AcceptShipCloneInstallation (EJS-0175)](../../docs/project/service-response-audit/evejs-only-responses.md:426); [CancelShipCloneInstallation (EJS-0176)](../../docs/project/service-response-audit/evejs-only-responses.md:427); [CloneJump (EJS-0177)](../../docs/project/service-response-audit/evejs-only-responses.md:428); [DestroyInstalledClone (EJS-0178)](../../docs/project/service-response-audit/evejs-only-responses.md:429); [GetCloneState (EJS-0179)](../../docs/project/service-response-audit/evejs-only-responses.md:430); [GetNumClonesInPilotsStructure (EJS-0180)](../../docs/project/service-response-audit/evejs-only-responses.md:431); [GetPriceForClone (EJS-0181)](../../docs/project/service-response-audit/evejs-only-responses.md:432); [GetShipCloneState (EJS-0182)](../../docs/project/service-response-audit/evejs-only-responses.md:433); [GetStationCloneState (EJS-0183)](../../docs/project/service-response-audit/evejs-only-responses.md:434); [InstallCloneInStation (EJS-0184)](../../docs/project/service-response-audit/evejs-only-responses.md:435); [InstallCloneInStructure (EJS-0185)](../../docs/project/service-response-audit/evejs-only-responses.md:436); [MachoBindObject (EJS-0186)](../../docs/project/service-response-audit/evejs-only-responses.md:437); [OfferShipCloneInstallation (EJS-0187)](../../docs/project/service-response-audit/evejs-only-responses.md:438); [ResetLastCloneJumpTime (EJS-0188)](../../docs/project/service-response-audit/evejs-only-responses.md:439); [SetJumpCloneName (EJS-0189)](../../docs/project/service-response-audit/evejs-only-responses.md:440); [ValidateInstallJumpClone (EJS-0190)](../../docs/project/service-response-audit/evejs-only-responses.md:441).

**CH-12 (character phase, S1):** the clone state is real for the reads `GetCloneState`, `GetStationCloneState`, `GetShipCloneState`, `GetNumClonesInPilotsStructure`, `ValidateInstallJumpClone` (read), and for `SetJumpCloneName`, `DestroyInstalledClone`, `ResetLastCloneJumpTime` and the bind and resolve. Store: migration `0006_jump_clones.sql` (Eve.Characters). Tests: `JumpCloneStoreTests` (5 PostgreSQL facts), `JumpCloneHostTests` (Host registry, restart, notifications), live `tools/headless-client/tests/test_jump_clone_live.py`. `DestroyInstalledClone` writes a durable `notificationMgr` row and sends `OnNotificationReceived` after the clone is removed: type 56 for the character's own deletion, group 4, with the character as `locationOwnerID` and `destroyerID`, and the removed implant type IDs. The Host test checks the wire fields and notification persistence across restart. `CloneJump` is implemented under CH-16, with a PostgreSQL-backed activation settlement and live cooldown walk.

**CH-1 (character phase, S1, S5):** `PrepareCharacterForDelete` queues an offline character for 600 minutes (EveJS `characterDeletionDelayMinutes`; the EVE University wiki pages tried were not found, HTTP 404), a second prepare returns the same time as the reference does, `DeleteCharacter` refuses an unqueued or not-yet-due character with the reference's CustomInfo text and otherwise sets `deleted_at` (migration `0007_character_deletion.sql`), and deleted characters are hidden from their account and refused at selection. Cancel was already present. Tests: `CharacterDeletionStoreTests` (2 facts), `CharacterHostTests` (Host registry, restart, refusals, the successful delete, the NPC-corporation destroy path), live `tools/headless-client/tests/test_character_delete.py` (prepare, refusal while waiting, cancel, refusal). Deletion moves the character into the graveyard corporation (1000001), moves its asset trees to the corporation deliveries (flag 62; destroyed with contents when its corporation is an NPC corporation), and clears its skill data and saved fittings in one transaction (`HostCharacterDeletionPurge`). S5 adds `CharacterPrivateStatePurge` (the EveJS `purgePrivateCharacterState` set): PostgreSQL rows of agents' missions, journal, storyline counters, research projects and locate requests, scanned probes, the bookmark folders and calendar events the character owns, the remaining skill tables; and, through the GameStore cache (flushed), mail mailboxes, notification boxes, LP character wallets, character notes and settings, expert systems, mission, research and probe runtime state. Tests: `CharacterPrivateStatePurgeTests` (removal, flush, restart), plus `CharacterHostTests.Character_deletion_queues_waits_cancels_and_deletes_and_the_deleted_character_is_refused` asserting a queued PostgreSQL `skill_queue` row is removed. Character deletion now removes PostgreSQL skill plans in the same transaction as skills and queue rows; requirements, milestones and active selection cascade from the plan rows. GameStore skill plans, queue and skill-trading state (`skillPlans`, `skillQueues` and `skillTradingState`) are removed through their character-keyed paths and flushed; regression coverage checks that another character's rows remain. Deletion clears the stored doll and portrait payloads (`doll_info`, `portrait_info`, and `paperdoll_state`) in the same PostgreSQL transaction; these are the appearance fields held by the character store. It also deletes owned jump clones and their cascading implant rows, earned and equipped titles, achievement tracker headers and children, and resets the tombstoned character's last-clone-jump timestamp in the same transaction. Regression coverage verifies removal for the deleted character and preservation for another character. The deletion transaction also removes the character's NES intro state, Project Discovery tutorial state and jump-clone settlement rows (including rows where the character was the offering pilot); account-scoped login milestones remain available to other characters on that account. No separate image-file store is wired into this character path.

**CH-15 (character phase, S1, S6), partial:** `InstallCloneInStation` and `InstallCloneInStructure` install a clone at the docked place after the validation (the first failed label is refused as CustomNotify), charge the fee once in the same PostgreSQL transaction as the clone row and its settlement row (`character_jump_clone_settlements`, migration 0012; wallet journal, reference 55), and refuse too little ISK with NotEnoughMoney. A station fee is 900,000 ISK. A structure needs its Medical service online, or its Jump Clone service online and the docking right; its fee is the cloning bay setting (profile setting 23, default 900,000) and it is paid into the owner corporation's master wallet. `GetPriceForClone` answers the fee. `OfferShipCloneInstallation`, `AcceptShipCloneInstallation` and `CancelShipCloneInstallation`: the offering ship needs a Clone Vat Bay I fitted (its online state is not read), canReceiveCloneJumps, and the target within its maxOperationalDistance (centre to centre); the clone bay capacity is maxJumpClones; the offer waits in memory (EveJS); the completion is Done to the target, then to the offering pilot, then the clone caches. The fee has the offering pilot as its journal counterparty and credits no wallet (EveJS). Tests: `CharacterHostTests` (station and structure install, refusal, charge, restart, settlement rows), `JumpCloneShipInstallTests` (offer, accept, cancel, refusals, notification order; Services.Core), live `test_clone_install_live.py` (station refusal; offer refused InviteClone1). Structure Medical access now uses the profile's cloning-bay access groups (setting 23), covered by `Structure_settings_host_read_model_uses_persisted_owner_profile_and_access_group_rows` and the Host installation test. Open: a live offer accepted between two pilots (needs a fitted Clone Vat Bay in space; not built). No implants are copied (the reference).

**CH-6 (character phase, S1, S5):** `userSvc.ActivateMultiTraining(itemID, trainingID)` consumes one Multiple Character Training certificate (type 34133 or 63188) held by a character of the account from its stack (the rest of the stack stays), adds 30 days to the queue (the named slot, else the free slot or the one that ends first), and refuses other items, unknown items and non-Omega accounts with the reference's CustomInfo text (`PersistenceNewEdenStoreRuntime.MultipleTraining`). Every granted slot schedules a durable job at its end (`MultipleTrainingSlotExpiry`, job kind `skills.mctSlotExpiry`, scheduled in the grant's transaction): at the end, the running queues beyond the remaining slots are paused, the character with the fewest skill points first (wiki "Multiple Character Training"), and the account's clients are told (`OnSkillQueuePausedServer`, `OnMultipleCharactersTrainingUpdated`). Tests: `CharacterHostTests` (consume, 30 days, refusal, restart, the notification to the registered session, a stack of two, the expiry job scheduled at the slot end and run by the scheduler), `TrainingServiceTests` (the pause decision and the notification order), live `test_multi_training_live.py` (refusal; a stack of two certificates gives up one per activation, the slots read). Startup now reads persisted account slot expiries and recreates missing future expiry jobs; expiries that passed while the host was stopped immediately reconcile over-limit queues. Host regression: `Host_startup_recreates_expiry_jobs_for_existing_training_slots`; new certificate grants still schedule their jobs in the grant transaction.

**CH-3 (character phase, S1, S5), partial:** `skillHandler.CheckAndSendNotifications` answers an empty list and sends nothing, as the reference does (EveJS `skillMgrService.js` 594; the client calls it only from a development provider). Test: `CharacterHostTests` (Host registry). S6: `GetSkillBundleInfo(skillBundleID)` answers the client's own shape, the pair (name label, icon) with an empty label and no icon (eve-logic `crate/lootItem.py` 87 unpacks it and falls back to the default icon). No bundle name or icon exists in any source (S5 searched them all), so the answer carries no bundle data and does not check the id. Tests: `CharacterHostTests.Skill_bundle_info_answers_the_client_empty_shape_through_the_registry`, live `test_skill_bundle_info_live.py`. Open: the name and icon source, which the owner decides (DEVIATIONS, S6).

**CH-10 (character phase, S1):** GATE-C decided by the owner: `CastUpvote`, `CastDownvote`, `ReportBot` and `ReportISKSpammer` answer `true` and keep nothing (DEVIATIONS, "Character phase: votes, reports and playtime"). `GetSessionPlaytimeMinutes` answers 0 as the reference does; no V24.01 caller and no stored source were found. Test: `UserSvcServiceTests` (Core tests).

#### Named group: keeper (1 exits)

[GetOperationSpawnpointPosition (EJS-0191)](../../docs/project/service-response-audit/evejs-only-responses.md:442).

#### Named group: loadService (7 exits)

[GetConfig (EJS-0192)](../../docs/project/service-response-audit/evejs-only-responses.md:443); [GetTotalStats (EJS-0193)](../../docs/project/service-response-audit/evejs-only-responses.md:444); [IsRunning (EJS-0194)](../../docs/project/service-response-audit/evejs-only-responses.md:445); [Ping (EJS-0195)](../../docs/project/service-response-audit/evejs-only-responses.md:446); [SetConfig (EJS-0196)](../../docs/project/service-response-audit/evejs-only-responses.md:447); [StartLoad (EJS-0197)](../../docs/project/service-response-audit/evejs-only-responses.md:448); [StopLoad (EJS-0198)](../../docs/project/service-response-audit/evejs-only-responses.md:449).

#### Named group: lookupSvc (4 exits)

[LookupAgents (EJS-0199)](../../docs/project/service-response-audit/evejs-only-responses.md:450); [LookupKnownLocations (EJS-0200)](../../docs/project/service-response-audit/evejs-only-responses.md:451); [LookupNPCCharacters (EJS-0201)](../../docs/project/service-response-audit/evejs-only-responses.md:452); [LookupStations (EJS-0202)](../../docs/project/service-response-audit/evejs-only-responses.md:453).

#### Named group: LPStoreMgr (2 exits)

[MachoBindObject (EJS-0203)](../../docs/project/service-response-audit/evejs-only-responses.md:454); [MachoResolveObject (EJS-0204)](../../docs/project/service-response-audit/evejs-only-responses.md:455).

#### Named group: LPSvc (7 exits)

[GetAvailableOffersFromCorp (EJS-0205)](../../docs/project/service-response-audit/evejs-only-responses.md:456); [GetLPExchangeRates (EJS-0206)](../../docs/project/service-response-audit/evejs-only-responses.md:457); [GetLPForCharacterCorp (EJS-0207)](../../docs/project/service-response-audit/evejs-only-responses.md:458); [GetLPsForCharacter (EJS-0208)](../../docs/project/service-response-audit/evejs-only-responses.md:459); [MachoBindObject (EJS-0209)](../../docs/project/service-response-audit/evejs-only-responses.md:460); [MachoResolveObject (EJS-0210)](../../docs/project/service-response-audit/evejs-only-responses.md:461); [TakeOffer (EJS-0211)](../../docs/project/service-response-audit/evejs-only-responses.md:462).

#### Named group: LSC (7 exits)

[GetChannels (EJS-0212)](../../docs/project/service-response-audit/evejs-only-responses.md:463); [GetMyMessages (EJS-0213)](../../docs/project/service-response-audit/evejs-only-responses.md:464); [JoinChannel (EJS-0214)](../../docs/project/service-response-audit/evejs-only-responses.md:465); [JoinChannels (EJS-0215)](../../docs/project/service-response-audit/evejs-only-responses.md:466); [LeaveChannel (EJS-0216)](../../docs/project/service-response-audit/evejs-only-responses.md:467); [LeaveChannels (EJS-0217)](../../docs/project/service-response-audit/evejs-only-responses.md:468); [SendMessage (EJS-0218)](../../docs/project/service-response-audit/evejs-only-responses.md:469).

#### Named group: machoNet (4 exits)

[ForwardCharacterNotification (EJS-0219)](../../docs/project/service-response-audit/evejs-only-responses.md:470); [GetClusterGameStatisticsForClient (EJS-0220)](../../docs/project/service-response-audit/evejs-only-responses.md:471); [GetNodeFromAddress (EJS-0221)](../../docs/project/service-response-audit/evejs-only-responses.md:472); [ReloadClientCodeHash (EJS-0222)](../../docs/project/service-response-audit/evejs-only-responses.md:473).

#### Named group: mailMgr (21 exits)

[AssignLabels (EJS-0223)](../../docs/project/service-response-audit/evejs-only-responses.md:474); [CreateLabel (EJS-0224)](../../docs/project/service-response-audit/evejs-only-responses.md:475); [DeleteLabel (EJS-0225)](../../docs/project/service-response-audit/evejs-only-responses.md:476); [DeleteMail (EJS-0226)](../../docs/project/service-response-audit/evejs-only-responses.md:477); [EditLabel (EJS-0227)](../../docs/project/service-response-audit/evejs-only-responses.md:478); [EmptyTrash (EJS-0228)](../../docs/project/service-response-audit/evejs-only-responses.md:479); [MarkAllAsRead (EJS-0229)](../../docs/project/service-response-audit/evejs-only-responses.md:480); [MarkAllAsUnread (EJS-0230)](../../docs/project/service-response-audit/evejs-only-responses.md:481); [MarkAsReadByLabel (EJS-0232)](../../docs/project/service-response-audit/evejs-only-responses.md:483); [MarkAsReadByList (EJS-0233)](../../docs/project/service-response-audit/evejs-only-responses.md:484); [MarkAsUnread (EJS-0234)](../../docs/project/service-response-audit/evejs-only-responses.md:485); [MarkAsUnreadByLabel (EJS-0235)](../../docs/project/service-response-audit/evejs-only-responses.md:486); [MarkAsUnreadByList (EJS-0236)](../../docs/project/service-response-audit/evejs-only-responses.md:487); [MoveAllFromTrash (EJS-0237)](../../docs/project/service-response-audit/evejs-only-responses.md:488); [MoveAllToTrash (EJS-0238)](../../docs/project/service-response-audit/evejs-only-responses.md:489); [MoveFromTrash (EJS-0239)](../../docs/project/service-response-audit/evejs-only-responses.md:490); [MoveToTrash (EJS-0240)](../../docs/project/service-response-audit/evejs-only-responses.md:491); [MoveToTrashByLabel (EJS-0241)](../../docs/project/service-response-audit/evejs-only-responses.md:492); [MoveToTrashByList (EJS-0242)](../../docs/project/service-response-audit/evejs-only-responses.md:493); [RemoveLabels (EJS-0243)](../../docs/project/service-response-audit/evejs-only-responses.md:494); [ReplaceLabels (EJS-0244)](../../docs/project/service-response-audit/evejs-only-responses.md:495).

V24.01 `mailSvc.MarkMessagesAsRead` calls `mailMgr.MarkAsRead` (`eve/client/script/ui/services/mail/mailSvc.py:336-348`). eve.rs now exposes this RPC and routes unique message IDs through the durable mail status callback; `MailServiceTests.Mark_as_read_forwards_each_distinct_mailbox_message_to_durable_host_callback` covers the service contract. The other listed mailMgr methods remain gaps for separate client-call and Host-state review.

#### Named group: map (1 exits)

[GetSolarsystemItems (EJS-0245)](../../docs/project/service-response-audit/evejs-only-responses.md:496).

#### Named group: market (10 exits)

[GetCharOrders (EJS-0246)](../../docs/project/service-response-audit/evejs-only-responses.md:497); [GetCorporationOrders (EJS-0247)](../../docs/project/service-response-audit/evejs-only-responses.md:498); [GetMarketGroups (EJS-0248)](../../docs/project/service-response-audit/evejs-only-responses.md:499); [GetNewPriceHistory (EJS-0249)](../../docs/project/service-response-audit/evejs-only-responses.md:500); [GetOldPriceHistory (EJS-0250)](../../docs/project/service-response-audit/evejs-only-responses.md:501); [GetOrders (EJS-0251)](../../docs/project/service-response-audit/evejs-only-responses.md:502); [GetRegionBest (EJS-0252)](../../docs/project/service-response-audit/evejs-only-responses.md:503); [GetStationAsks (EJS-0253)](../../docs/project/service-response-audit/evejs-only-responses.md:504); [GetSystemAsks (EJS-0254)](../../docs/project/service-response-audit/evejs-only-responses.md:505); [StartupCheck (EJS-0255)](../../docs/project/service-response-audit/evejs-only-responses.md:506).

#### Named group: notificationMgr (6 exits)

[DeleteAllNotifications (EJS-0256)](../../docs/project/service-response-audit/evejs-only-responses.md:507); [DeleteGroupNotifications (EJS-0257)](../../docs/project/service-response-audit/evejs-only-responses.md:508); [DeleteNotifications (EJS-0258)](../../docs/project/service-response-audit/evejs-only-responses.md:509); [MarkAllAsProcessed (EJS-0259)](../../docs/project/service-response-audit/evejs-only-responses.md:510); [MarkAsProcessed (EJS-0260)](../../docs/project/service-response-audit/evejs-only-responses.md:511); [MarkGroupAsProcessed (EJS-0261)](../../docs/project/service-response-audit/evejs-only-responses.md:512).

2026-10-18 service comparison: V24.01 `notificationSvc.MarkAllRead` and `MarkAsRead` call `notificationMgr.MarkAllAsProcessed` and `MarkAsProcessed`; `notificationSvc.DeleteNotifications` calls `DeleteNotifications` (`eve/client/script/ui/services/mail/notificationSvc.py:229-260,312-321`). All six methods are connected to the existing `notifications` game-store mailbox; group mutations filter by stored group ID and delete updates are sent to another active character session after persistence. V24.01 also calls `MarkGroupAsProcessed` from `notificationSvc.MarkAllReadInGroup`, `DeleteGroupNotifications` from `DeleteAllFromGroup`, and `DeleteAllNotifications` from `DeleteAll` (`eve/client/script/ui/services/mail/notificationSvc.py:213-239,287-321); all three are now advertised and connected to the existing `notifications` game-store mailbox. Host persistence, mutation state reload, and the post-commit delete notification are covered by `Notification_manager_mutations_persist_and_notify_after_the_commit` in `dotnet/tests/Eve.Host.Tests/CharacterHostTests.cs`.

#### Named group: objectCaching (8 exits)

[GetCachableObject (EJS-0262)](../../docs/project/service-response-audit/evejs-only-responses.md:513); [GetCachedMethodCallVersion (EJS-0263)](../../docs/project/service-response-audit/evejs-only-responses.md:514); [GetCachedObject (EJS-0264)](../../docs/project/service-response-audit/evejs-only-responses.md:515); [GetCachedObjectVersion (EJS-0265)](../../docs/project/service-response-audit/evejs-only-responses.md:516); [InvalidateCachedMethodCall (EJS-0266)](../../docs/project/service-response-audit/evejs-only-responses.md:517); [InvalidateCachedMethodCalls (EJS-0267)](../../docs/project/service-response-audit/evejs-only-responses.md:518); [InvalidateCachedObjects (EJS-0268)](../../docs/project/service-response-audit/evejs-only-responses.md:519); [UpdateCache (EJS-0269)](../../docs/project/service-response-audit/evejs-only-responses.md:520).

#### Named group: ownerGroupManager (21 exits)

[AddMembers (EJS-0270)](../../docs/project/service-response-audit/evejs-only-responses.md:521); [Create (EJS-0271)](../../docs/project/service-response-audit/evejs-only-responses.md:522); [CreateGroup (EJS-0272)](../../docs/project/service-response-audit/evejs-only-responses.md:523); [Delete (EJS-0273)](../../docs/project/service-response-audit/evejs-only-responses.md:524); [DeleteGroup (EJS-0274)](../../docs/project/service-response-audit/evejs-only-responses.md:525); [DeleteMembers (EJS-0275)](../../docs/project/service-response-audit/evejs-only-responses.md:526); [GetGroup (EJS-0276)](../../docs/project/service-response-audit/evejs-only-responses.md:527); [GetGroupLogs (EJS-0277)](../../docs/project/service-response-audit/evejs-only-responses.md:528); [GetGroupsMany (EJS-0278)](../../docs/project/service-response-audit/evejs-only-responses.md:529); [GetMembers (EJS-0279)](../../docs/project/service-response-audit/evejs-only-responses.md:530); [GetMembersForMultipleGroups (EJS-0280)](../../docs/project/service-response-audit/evejs-only-responses.md:531); [GetMyGroups (EJS-0281)](../../docs/project/service-response-audit/evejs-only-responses.md:532); [GetMyGroupsAndMembers (EJS-0282)](../../docs/project/service-response-audit/evejs-only-responses.md:533); [GetMyGroupsToUseForBookmarks (EJS-0283)](../../docs/project/service-response-audit/evejs-only-responses.md:534); [GetPublicGroupInfo (EJS-0284)](../../docs/project/service-response-audit/evejs-only-responses.md:535); [RemoveMembers (EJS-0285)](../../docs/project/service-response-audit/evejs-only-responses.md:536); [SearchGroups (EJS-0286)](../../docs/project/service-response-audit/evejs-only-responses.md:537); [UpdateDescription (EJS-0287)](../../docs/project/service-response-audit/evejs-only-responses.md:538); [UpdateMemberships (EJS-0288)](../../docs/project/service-response-audit/evejs-only-responses.md:539); [UpdateMembershipTypes (EJS-0289)](../../docs/project/service-response-audit/evejs-only-responses.md:540); [UpdateName (EJS-0290)](../../docs/project/service-response-audit/evejs-only-responses.md:541).

#### Named group: photoUploadSvc (1 exits)

[Upload (EJS-0292)](../../docs/project/service-response-audit/evejs-only-responses.md:543).

#### Named group: planetMgr (17 exits)

[GetCommandPinsForPlanet (EJS-0293)](../../docs/project/service-response-audit/evejs-only-responses.md:544); [GetExtractorsForPlanet (EJS-0294)](../../docs/project/service-response-audit/evejs-only-responses.md:545); [GetFullNetworkForOwner (EJS-0295)](../../docs/project/service-response-audit/evejs-only-responses.md:546); [GetPlanetInfo (EJS-0296)](../../docs/project/service-response-audit/evejs-only-responses.md:547); [GetPlanetResourceInfo (EJS-0297)](../../docs/project/service-response-audit/evejs-only-responses.md:548); [GetProgramResultInfo (EJS-0298)](../../docs/project/service-response-audit/evejs-only-responses.md:549); [GetResourceData (EJS-0299)](../../docs/project/service-response-audit/evejs-only-responses.md:550); [GMAddCommodity (EJS-0300)](../../docs/project/service-response-audit/evejs-only-responses.md:551); [GMGetCompleteResource (EJS-0302)](../../docs/project/service-response-audit/evejs-only-responses.md:553); [GMGetLocalDistributionReport (EJS-0303)](../../docs/project/service-response-audit/evejs-only-responses.md:554); [GMGetSynchedServerState (EJS-0305)](../../docs/project/service-response-audit/evejs-only-responses.md:556); [MachoBindObject (EJS-0306)](../../docs/project/service-response-audit/evejs-only-responses.md:557); [MachoResolveObject (EJS-0307)](../../docs/project/service-response-audit/evejs-only-responses.md:558); [UserAbandonPlanet (EJS-0308)](../../docs/project/service-response-audit/evejs-only-responses.md:559); [UserLaunchCommodities (EJS-0309)](../../docs/project/service-response-audit/evejs-only-responses.md:560); [UserTransferCommodities (EJS-0310)](../../docs/project/service-response-audit/evejs-only-responses.md:561); [UserUpdateNetwork (EJS-0311)](../../docs/project/service-response-audit/evejs-only-responses.md:562).

#### Named group: planetOrbitalRegistryBroker (6 exits)

[GetSettingsInfo (EJS-0312)](../../docs/project/service-response-audit/evejs-only-responses.md:563); [GetTaxRate (EJS-0313)](../../docs/project/service-response-audit/evejs-only-responses.md:564); [MachoBindObject (EJS-0314)](../../docs/project/service-response-audit/evejs-only-responses.md:565); [MachoResolveObject (EJS-0315)](../../docs/project/service-response-audit/evejs-only-responses.md:566); [RevertOrbitalsToInterBus (EJS-0316)](../../docs/project/service-response-audit/evejs-only-responses.md:567); [UpdateSettings (EJS-0317)](../../docs/project/service-response-audit/evejs-only-responses.md:568).

#### Named group: posMgr (22 exits)

[AnchorOrbital (EJS-0318)](../../docs/project/service-response-audit/evejs-only-responses.md:569); [AnchorStructure (EJS-0319)](../../docs/project/service-response-audit/evejs-only-responses.md:570); [AssumeStructureControl (EJS-0320)](../../docs/project/service-response-audit/evejs-only-responses.md:571); [ChangeStructureProvisionType (EJS-0321)](../../docs/project/service-response-audit/evejs-only-responses.md:572); [GetConsumptionFactor (EJS-0322)](../../docs/project/service-response-audit/evejs-only-responses.md:573); [GetMoonForTower (EJS-0323)](../../docs/project/service-response-audit/evejs-only-responses.md:574); [GetMoonProcessInfoForTower (EJS-0324)](../../docs/project/service-response-audit/evejs-only-responses.md:575); [GetStarbasePermissions (EJS-0325)](../../docs/project/service-response-audit/evejs-only-responses.md:576); [GetTowerNotificationSettings (EJS-0326)](../../docs/project/service-response-audit/evejs-only-responses.md:577); [GetTowerSentrySettings (EJS-0327)](../../docs/project/service-response-audit/evejs-only-responses.md:578); [LinkResourceForTower (EJS-0328)](../../docs/project/service-response-audit/evejs-only-responses.md:579); [MachoBindObject (EJS-0329)](../../docs/project/service-response-audit/evejs-only-responses.md:580); [MachoResolveObject (EJS-0330)](../../docs/project/service-response-audit/evejs-only-responses.md:581); [OnlineOrbital (EJS-0331)](../../docs/project/service-response-audit/evejs-only-responses.md:582); [RelinquishStructureControl (EJS-0332)](../../docs/project/service-response-audit/evejs-only-responses.md:583); [RunMoonProcessCycleforTower (EJS-0333)](../../docs/project/service-response-audit/evejs-only-responses.md:584); [SetStarbasePermissions (EJS-0334)](../../docs/project/service-response-audit/evejs-only-responses.md:585); [SetTowerNotifications (EJS-0335)](../../docs/project/service-response-audit/evejs-only-responses.md:586); [SetTowerPassword (EJS-0336)](../../docs/project/service-response-audit/evejs-only-responses.md:587); [SetTowerSentrySettings (EJS-0337)](../../docs/project/service-response-audit/evejs-only-responses.md:588); [UnanchorOrbital (EJS-0338)](../../docs/project/service-response-audit/evejs-only-responses.md:589); [UnanchorStructure (EJS-0339)](../../docs/project/service-response-audit/evejs-only-responses.md:590).

#### Named group: raffleProxy (15 exits)

[AwardItem (EJS-0356)](../../docs/project/service-response-audit/evejs-only-responses.md:607); [BuyRandomTickets (EJS-0357)](../../docs/project/service-response-audit/evejs-only-responses.md:608); [BuyTicket (EJS-0358)](../../docs/project/service-response-audit/evejs-only-responses.md:609); [CreateRaffle (EJS-0359)](../../docs/project/service-response-audit/evejs-only-responses.md:610); [FilteredGrab (EJS-0360)](../../docs/project/service-response-audit/evejs-only-responses.md:611); [GetActiveHistoricPrices (EJS-0361)](../../docs/project/service-response-audit/evejs-only-responses.md:612); [GetCreatedParticipated (EJS-0362)](../../docs/project/service-response-audit/evejs-only-responses.md:613); [GetMyActiveTickets (EJS-0363)](../../docs/project/service-response-audit/evejs-only-responses.md:614); [GetMyRaffleHistory (EJS-0364)](../../docs/project/service-response-audit/evejs-only-responses.md:615); [GetRaffle (EJS-0365)](../../docs/project/service-response-audit/evejs-only-responses.md:616); [Grab (EJS-0366)](../../docs/project/service-response-audit/evejs-only-responses.md:617); [SubscribeToRaffle (EJS-0367)](../../docs/project/service-response-audit/evejs-only-responses.md:618); [SubscribeToTickets (EJS-0368)](../../docs/project/service-response-audit/evejs-only-responses.md:619); [UnsubscribeFromRaffle (EJS-0369)](../../docs/project/service-response-audit/evejs-only-responses.md:620); [UnSubscribeFromTickets (EJS-0370)](../../docs/project/service-response-audit/evejs-only-responses.md:621).

#### Named group: repairSvc (5 exits)

[DamageModules (EJS-0371)](../../docs/project/service-response-audit/evejs-only-responses.md:622); [GetDamageReports (EJS-0372)](../../docs/project/service-response-audit/evejs-only-responses.md:623); [MachoResolveObject (EJS-0373)](../../docs/project/service-response-audit/evejs-only-responses.md:624); [RepairItems (EJS-0374)](../../docs/project/service-response-audit/evejs-only-responses.md:625); [UnasembleItems (EJS-0375)](../../docs/project/service-response-audit/evejs-only-responses.md:626).

#### Named group: reprocessingSvc (4 exits)

[GetOptionsForItemTypes (EJS-0376)](../../docs/project/service-response-audit/evejs-only-responses.md:627); [GetQuote (EJS-0377)](../../docs/project/service-response-audit/evejs-only-responses.md:628); [GetReprocessingInfo (EJS-0378)](../../docs/project/service-response-audit/evejs-only-responses.md:629); [MachoResolveObject (EJS-0379)](../../docs/project/service-response-audit/evejs-only-responses.md:630).

#### Named group: scanMgr (7 exits)

[GetScanTargetID (EJS-0380)](../../docs/project/service-response-audit/evejs-only-responses.md:631); [MachoBindObject (EJS-0381)](../../docs/project/service-response-audit/evejs-only-responses.md:632); [MachoResolveObject (EJS-0382)](../../docs/project/service-response-audit/evejs-only-responses.md:633); [QAOverrideProbeExpiry (EJS-0383)](../../docs/project/service-response-audit/evejs-only-responses.md:634); [QAScanSites (EJS-0384)](../../docs/project/service-response-audit/evejs-only-responses.md:635); [SetProbeDestination (EJS-0385)](../../docs/project/service-response-audit/evejs-only-responses.md:636); [SetProbeRangeStep (EJS-0386)](../../docs/project/service-response-audit/evejs-only-responses.md:637).

#### Named group: search (2 exits)

[Query (EJS-0387)](../../docs/project/service-response-audit/evejs-only-responses.md:638); [QuickQuery (EJS-0388)](../../docs/project/service-response-audit/evejs-only-responses.md:639).

#### Named group: seasonManager (13 exits)

Existing exact-name TODO evidence (same owner/work): [existing row](../../docs/project/TODO.md:53). Retain its status/policy; use the detailed row for the contract and classification.

[challenge_is_dormant (EJS-0389)](../../docs/project/service-response-audit/evejs-only-responses.md:640); [claim_challenge_rewards (EJS-0390)](../../docs/project/service-response-audit/evejs-only-responses.md:641); [claim_goal_reward (EJS-0391)](../../docs/project/service-response-audit/evejs-only-responses.md:642); [fill_up_and_return_challenges_for_character (EJS-0392)](../../docs/project/service-response-audit/evejs-only-responses.md:643); [get_challenge (EJS-0393)](../../docs/project/service-response-audit/evejs-only-responses.md:644); [get_challenge_expiration_date (EJS-0394)](../../docs/project/service-response-audit/evejs-only-responses.md:645); [get_character_progress_by_character_id (EJS-0395)](../../docs/project/service-response-audit/evejs-only-responses.md:646); [get_loot_table_by_typeID (EJS-0396)](../../docs/project/service-response-audit/evejs-only-responses.md:647); [get_max_active_challenges (EJS-0397)](../../docs/project/service-response-audit/evejs-only-responses.md:648); [get_season_data_for_character (EJS-0398)](../../docs/project/service-response-audit/evejs-only-responses.md:649); [get_season_end_time (EJS-0399)](../../docs/project/service-response-audit/evejs-only-responses.md:650); [reload_fsd_data (EJS-0400)](../../docs/project/service-response-audit/evejs-only-responses.md:651); [select_season (EJS-0401)](../../docs/project/service-response-audit/evejs-only-responses.md:652).

#### Named group: sessionMgr (2 exits)

[GetSessionStatistics (EJS-0402)](../../docs/project/service-response-audit/evejs-only-responses.md:653); [RemoveSessionsFromServer (EJS-0403)](../../docs/project/service-response-audit/evejs-only-responses.md:654).

#### Named group: ship (20 exits)

[ActivateShip (EJS-0404)](../../docs/project/service-response-audit/evejs-only-responses.md:655); [BoardStoredShip (EJS-0405)](../../docs/project/service-response-audit/evejs-only-responses.md:656); [ConfigureShip (EJS-0406)](../../docs/project/service-response-audit/evejs-only-responses.md:657); [Drop (EJS-0407)](../../docs/project/service-response-audit/evejs-only-responses.md:658); [FitShips (EJS-0408)](../../docs/project/service-response-audit/evejs-only-responses.md:659); [GetDisplayKillCounterValue (EJS-0409)](../../docs/project/service-response-audit/evejs-only-responses.md:660); [GetFittedItems (EJS-0410)](../../docs/project/service-response-audit/evejs-only-responses.md:661); [GetKillCounter (EJS-0411)](../../docs/project/service-response-audit/evejs-only-responses.md:662); [GetModules (EJS-0412)](../../docs/project/service-response-audit/evejs-only-responses.md:663); [GetShipConfiguration (EJS-0413)](../../docs/project/service-response-audit/evejs-only-responses.md:664); [GetShipKillCounter (EJS-0414)](../../docs/project/service-response-audit/evejs-only-responses.md:665); [GetTurretModules (EJS-0415)](../../docs/project/service-response-audit/evejs-only-responses.md:666); [LaunchFromContainer (EJS-0416)](../../docs/project/service-response-audit/evejs-only-responses.md:667); [ResetDirtTimestamp (EJS-0417)](../../docs/project/service-response-audit/evejs-only-responses.md:668); [Scoop (EJS-0418)](../../docs/project/service-response-audit/evejs-only-responses.md:669); [ScoopToFleetHangar (EJS-0419)](../../docs/project/service-response-audit/evejs-only-responses.md:670); [ScoopToMobileDepotHold (EJS-0420)](../../docs/project/service-response-audit/evejs-only-responses.md:671); [SetDirtTimestamp (EJS-0421)](../../docs/project/service-response-audit/evejs-only-responses.md:672); [SetShipPassword (EJS-0422)](../../docs/project/service-response-audit/evejs-only-responses.md:673); [StoreVessel (EJS-0423)](../../docs/project/service-response-audit/evejs-only-responses.md:674).

#### Named group: storeManager (1 exits)

[BuyOffer (EJS-0428)](../../docs/project/service-response-audit/evejs-only-responses.md:679).

#### Named group: structure (3 exits)

[JettisonStructureFuel (EJS-0429)](../../docs/project/service-response-audit/evejs-only-responses.md:680); [MachoBindObject (EJS-0430)](../../docs/project/service-response-audit/evejs-only-responses.md:681); [MachoResolveObject (EJS-0431)](../../docs/project/service-response-audit/evejs-only-responses.md:682).

#### Named group: structureDirectory (5 exits)

[GetCorporationStructures (EJS-0432)](../../docs/project/service-response-audit/evejs-only-responses.md:683); [GetSolarsystemStructures (EJS-0433)](../../docs/project/service-response-audit/evejs-only-responses.md:684); [GetStructureInfo_ (EJS-0434)](../../docs/project/service-response-audit/evejs-only-responses.md:685); [GetStructures (EJS-0435)](../../docs/project/service-response-audit/evejs-only-responses.md:686); [GetStructuresInSystem (EJS-0436)](../../docs/project/service-response-audit/evejs-only-responses.md:687).

#### Named group: tcpRawProxyService (2 exits)

[GetESPTunnelingAddressByNodeID (EJS-0437)](../../docs/project/service-response-audit/evejs-only-responses.md:688); [GetTunnelsByType (EJS-0438)](../../docs/project/service-response-audit/evejs-only-responses.md:689).

#### Named group: tourneyMgr (2 exits)

[BanShip (EJS-0439)](../../docs/project/service-response-audit/evejs-only-responses.md:690); [ExecuteMemberChange (EJS-0440)](../../docs/project/service-response-audit/evejs-only-responses.md:691).

#### Named group: tutorialSvc (14 exits)

[GetActions (EJS-0441)](../../docs/project/service-response-audit/evejs-only-responses.md:692); [GetCareerAgents (EJS-0442)](../../docs/project/service-response-audit/evejs-only-responses.md:693); [GetCategories (EJS-0443)](../../docs/project/service-response-audit/evejs-only-responses.md:694); [GetCharacterTutorialState (EJS-0444)](../../docs/project/service-response-audit/evejs-only-responses.md:695); [GetCriterias (EJS-0445)](../../docs/project/service-response-audit/evejs-only-responses.md:696); [GetTutorialAgents (EJS-0446)](../../docs/project/service-response-audit/evejs-only-responses.md:697); [GetTutorialInfo (EJS-0447)](../../docs/project/service-response-audit/evejs-only-responses.md:698); [GetTutorials (EJS-0448)](../../docs/project/service-response-audit/evejs-only-responses.md:699); [GetTutorialsAndConnections (EJS-0449)](../../docs/project/service-response-audit/evejs-only-responses.md:700); [LogAborted (EJS-0450)](../../docs/project/service-response-audit/evejs-only-responses.md:701); [LogAppClosed (EJS-0451)](../../docs/project/service-response-audit/evejs-only-responses.md:702); [LogClosed (EJS-0452)](../../docs/project/service-response-audit/evejs-only-responses.md:703); [LogCompleted (EJS-0453)](../../docs/project/service-response-audit/evejs-only-responses.md:704); [LogStarted (EJS-0454)](../../docs/project/service-response-audit/evejs-only-responses.md:705).

#### Named group: vaultManager (1 exits)

[GetAccountBalance (EJS-0455)](../../docs/project/service-response-audit/evejs-only-responses.md:706).

#### Named group: voteManager (2 exits)

Existing exact-name TODO evidence (same owner/work): [existing row](../../docs/project/TODO.md:71). Retain its status/policy; use the detailed row for the contract and classification.

[ActOnSanctionedAction (EJS-0456)](../../docs/project/service-response-audit/evejs-only-responses.md:707); [CanVote (EJS-0457)](../../docs/project/service-response-audit/evejs-only-responses.md:708).

#### Named group: warStatisticMgr (5 exits)

[GetBaseInfo (EJS-0458)](../../docs/project/service-response-audit/evejs-only-responses.md:709); [GetKills (EJS-0459)](../../docs/project/service-response-audit/evejs-only-responses.md:710); [GetKillsByGroup (EJS-0460)](../../docs/project/service-response-audit/evejs-only-responses.md:711); [MachoBindObject (EJS-0461)](../../docs/project/service-response-audit/evejs-only-responses.md:712); [MachoResolveObject (EJS-0462)](../../docs/project/service-response-audit/evejs-only-responses.md:713).

#### Named group: XmppChatMgr (14 exits)

Existing exact-name TODO evidence (same owner/work): [existing row](../../docs/project/TODO.md:54). Retain its status/policy; use the detailed row for the contract and classification.

[AddCharacterToChannel (EJS-0463)](../../docs/project/service-response-audit/evejs-only-responses.md:714); [CreatePlayerOwnedChannel (EJS-0464)](../../docs/project/service-response-audit/evejs-only-responses.md:715); [CreatePrivateChannel (EJS-0465)](../../docs/project/service-response-audit/evejs-only-responses.md:716); [EnsureResourceWarsChannelExists (EJS-0466)](../../docs/project/service-response-audit/evejs-only-responses.md:717); [GetDeprecatedPrefsFallback (EJS-0467)](../../docs/project/service-response-audit/evejs-only-responses.md:718); [GetHelpChannel (EJS-0468)](../../docs/project/service-response-audit/evejs-only-responses.md:719); [GetRookieChannel (EJS-0469)](../../docs/project/service-response-audit/evejs-only-responses.md:720); [GetRookieHelpChannel (EJS-0470)](../../docs/project/service-response-audit/evejs-only-responses.md:721); [GMMute (EJS-0471)](../../docs/project/service-response-audit/evejs-only-responses.md:722); [GMUnmute (EJS-0472)](../../docs/project/service-response-audit/evejs-only-responses.md:723); [GrantChannelOwnership (EJS-0473)](../../docs/project/service-response-audit/evejs-only-responses.md:724); [Hostname (EJS-0474)](../../docs/project/service-response-audit/evejs-only-responses.md:725); [ResyncSystemChannelAccess (EJS-0475)](../../docs/project/service-response-audit/evejs-only-responses.md:726); [SetMotd (EJS-0476)](../../docs/project/service-response-audit/evejs-only-responses.md:727).

### Bound registration and successor verification (category 3; 39 overlapping exact-name rows)

These 39 physical-exit rows are a subset of the 476 named differences: 37 unimplemented rows in the 448 backlog plus 2 legacy candidates below, grouped into 13 services. They add **zero** missing-method count. Verify resolve → bind(parameter, nested call) → OID registration/parameter ownership → every successor → stale/current release → retry/reconnect; a service route alone is not a working moniker. The V24 column has caller-source evidence for 17 rows and leaves 22 not confirmed. Each group's complete tagged method set is:

- **charMgr (1, same named entries):** [MachoBindObject (EJS-0074)](../../docs/project/service-response-audit/evejs-only-responses.md:325).
- **insuranceSvc (2, same named entries):** [MachoBindObject (EJS-0162)](../../docs/project/service-response-audit/evejs-only-responses.md:413); [MachoResolveObject (EJS-0163)](../../docs/project/service-response-audit/evejs-only-responses.md:414).
- **jumpCloneSvc (1, same named entries):** [MachoBindObject (EJS-0186)](../../docs/project/service-response-audit/evejs-only-responses.md:437).
- **LPStoreMgr (2, same named entries):** [MachoBindObject (EJS-0203)](../../docs/project/service-response-audit/evejs-only-responses.md:454); [MachoResolveObject (EJS-0204)](../../docs/project/service-response-audit/evejs-only-responses.md:455).
- **LPSvc (2, same named entries):** [MachoBindObject (EJS-0209)](../../docs/project/service-response-audit/evejs-only-responses.md:460); [MachoResolveObject (EJS-0210)](../../docs/project/service-response-audit/evejs-only-responses.md:461).
- **planetMgr (19, same named entries):** [GetCommandPinsForPlanet (EJS-0293)](../../docs/project/service-response-audit/evejs-only-responses.md:544); [GetExtractorsForPlanet (EJS-0294)](../../docs/project/service-response-audit/evejs-only-responses.md:545); [GetFullNetworkForOwner (EJS-0295)](../../docs/project/service-response-audit/evejs-only-responses.md:546); [GetPlanetInfo (EJS-0296)](../../docs/project/service-response-audit/evejs-only-responses.md:547); [GetPlanetResourceInfo (EJS-0297)](../../docs/project/service-response-audit/evejs-only-responses.md:548); [GetProgramResultInfo (EJS-0298)](../../docs/project/service-response-audit/evejs-only-responses.md:549); [GetResourceData (EJS-0299)](../../docs/project/service-response-audit/evejs-only-responses.md:550); [GMAddCommodity (EJS-0300)](../../docs/project/service-response-audit/evejs-only-responses.md:551); [GMCleanupExpiredLaunches (EJS-0301)](../../docs/project/service-response-audit/evejs-only-responses.md:552); [GMGetCompleteResource (EJS-0302)](../../docs/project/service-response-audit/evejs-only-responses.md:553); [GMGetLocalDistributionReport (EJS-0303)](../../docs/project/service-response-audit/evejs-only-responses.md:554); [GMGetPlanetDiagnostics (EJS-0304)](../../docs/project/service-response-audit/evejs-only-responses.md:555); [GMGetSynchedServerState (EJS-0305)](../../docs/project/service-response-audit/evejs-only-responses.md:556); [MachoBindObject (EJS-0306)](../../docs/project/service-response-audit/evejs-only-responses.md:557); [MachoResolveObject (EJS-0307)](../../docs/project/service-response-audit/evejs-only-responses.md:558); [UserAbandonPlanet (EJS-0308)](../../docs/project/service-response-audit/evejs-only-responses.md:559); [UserLaunchCommodities (EJS-0309)](../../docs/project/service-response-audit/evejs-only-responses.md:560); [UserTransferCommodities (EJS-0310)](../../docs/project/service-response-audit/evejs-only-responses.md:561); [UserUpdateNetwork (EJS-0311)](../../docs/project/service-response-audit/evejs-only-responses.md:562).
- **planetOrbitalRegistryBroker (2, same named entries):** [MachoBindObject (EJS-0314)](../../docs/project/service-response-audit/evejs-only-responses.md:565); [MachoResolveObject (EJS-0315)](../../docs/project/service-response-audit/evejs-only-responses.md:566).
- **posMgr (2, same named entries):** [MachoBindObject (EJS-0329)](../../docs/project/service-response-audit/evejs-only-responses.md:580); [MachoResolveObject (EJS-0330)](../../docs/project/service-response-audit/evejs-only-responses.md:581).
- **repairSvc (1, same named entries):** [MachoResolveObject (EJS-0373)](../../docs/project/service-response-audit/evejs-only-responses.md:624).
- **reprocessingSvc (1, same named entries):** [MachoResolveObject (EJS-0379)](../../docs/project/service-response-audit/evejs-only-responses.md:630).
- **scanMgr (2, same named entries):** [MachoBindObject (EJS-0381)](../../docs/project/service-response-audit/evejs-only-responses.md:632); [MachoResolveObject (EJS-0382)](../../docs/project/service-response-audit/evejs-only-responses.md:633).
- **structure (2, same named entries):** [MachoBindObject (EJS-0430)](../../docs/project/service-response-audit/evejs-only-responses.md:681); [MachoResolveObject (EJS-0431)](../../docs/project/service-response-audit/evejs-only-responses.md:682).
- **warStatisticMgr (2, same named entries):** [MachoBindObject (EJS-0461)](../../docs/project/service-response-audit/evejs-only-responses.md:712); [MachoResolveObject (EJS-0462)](../../docs/project/service-response-audit/evejs-only-responses.md:713).

Agent resolve/initial nested dispatch is already fixed by aab0dd10 and is not in this bound-gap list. Its 13 unimplemented exits are in the agentMgr group; one client-local lookup and two legacy names are separated below. Office resolve/bind/remote-station exports and CorpStationMgr release cleanup are also not new OPEN binder work.

### Connected-route provider backlog (category 7; 6 overlapping groups)

These routes already exist. The work is an authoritative provider/transaction/notification connection, not another service registration. All methods attributed to each provider group are listed. Categories 4 and 7 share some of these ledger rows; do not create a separate field task for the same missing owner without keeping that relationship explicit.

- **UNIMPLEMENTED PROVIDER: facilityManager.** All methods: `GetFacilities`, `GetMaxActivityModifiers`, `GetFacilityTaxes`, `GetFacilityLocations`, `SetFacilityTaxes`. Detailed group: [EJS-0661](../../docs/project/service-response-audit/evejs-only-responses.md:947). Connect real facility lists/activity modifiers/taxes/locations and tax persistence. GetFacility/GetFacilitiesByID are already routed; do not replace them with new stubs. Existing Industry/facility owner; this is one group also tagged as a result-field gap.
- **PARTIAL ACL PROVIDER / RESULT: accessGroupBookmarkMgr.** The shared ACL owners, folder writes, access checks, and `SearchFoldersWithAdminAccess` are implemented; folder update/delete and access-loss notifications are emitted, with corporation and alliance departure walks covering revocation. Remaining items are the specific response-audit rows not covered by those paths; compare section 37a and `SHARED_BOOKMARKS_DESIGN.md`. Detailed group: [EJS-0665](../../docs/project/service-response-audit/evejs-only-responses.md:951).
- **UNIMPLEMENTED PROVIDER: billMgr.** All methods: `CharPayBill`. Detailed group: [EJS-0667](../../docs/project/service-response-audit/evejs-only-responses.md:953). Connect personal bill ownership, wallet debit, paid state and idempotency to an authoritative transaction. Current refusal is not fake success; corporation payment is separate. Existing Corporation audit CharPayBill row (same work item).
- **UNIMPLEMENTED NEGOTIATION PROVIDER: warRegistry.** All methods: `GetNegotiations`, `GetWarNegotiation`, `CreateWarAllyOffer`, `RetractWarAllyOffer`, `CreateSurrenderNegotiation`, `AcceptAllyNegotiation`, `DeclineAllyOffer`, `AcceptSurrender`, `DeclineSurrender`. Detailed group: [EJS-0668](../../docs/project/service-response-audit/evejs-only-responses.md:954). Implement negotiation reads/writes, permissions, state and target notification through the existing war/relation owner. Existing SetOpenForAllies/RetractMutualWar are excluded. Existing Corporation negotiation row and section 37a 'DONE WITH CUTS' negotiation remainder (same work).
- **UNIMPLEMENTED INVITE PROVIDER; REFUSAL FIXED: mutualWarInviteMgr.** All methods: `GetPendingInvitesForSession`, `IsCorpInvitesBlockedPlayer`, `SendInviteByPlayer`, `WithdrawInviteByPlayer`, `RespondToInviteByPlayer`, `SetInvitesBlockedByPlayer`. Detailed group: [EJS-0669](../../docs/project/service-response-audit/evejs-only-responses.md:955). Connect invite/blocked-state reads and four writes. Owner decision 2026-10-19: retain the explicit unavailable refusal until the authoritative war owner is connected; do not restore None success. Existing Corporation mutual-war row (same work); V24 caller/complete state flow still needs verification.
- **UNIMPLEMENTED COMMUNITY CATALOG PROVIDER: corpFittingMgr.** All methods: `GetCommunityFittings`. Detailed group: [EJS-0670](../../docs/project/service-response-audit/evejs-only-responses.md:956). Connect real community fitting records to the existing cached dictionary callback. The dict/cache wire shape is already fixed. Existing native-audit community fitting catalog row (same work).

### Other response, notification and session work (categories 4–6)

- **FIX EXISTING RESPONSE — machoNet.GetServerStatus:** live multi-session count is always forced to 1 in the inspected Host source. Reconnect the actual count owner; branding/time/ID differences are not the same issue. [EJS-0659](../../docs/project/service-response-audit/evejs-only-responses.md:945). Client display acceptance not replayed.
- **UNIMPLEMENTED RESPONSE BRANCH / VERIFY CLIENT CACHE CONTRACT — CachedMethodCallResult / CacheOK:** kwargs.machoVersion checksum validation and CacheOK producer are absent, although fresh result wrappers exist. Trace consumer behavior before deciding whether the branch is required; do not describe the whole cache domain as broken. [EJS-0660](../../docs/project/service-response-audit/evejs-only-responses.md:946). objectCaching's 8 named exits are already in the named index.
- **PARTIAL EXISTING RESULT — facilityManager.GetFacility:** activities/serviceAccess/rigModifiers are empty in the current provider. Fill them from the same facility authority, alongside [EJS-0661](../../docs/project/service-response-audit/evejs-only-responses.md:947) provider work; full nested payload/client eligibility not verified. [EJS-0662](../../docs/project/service-response-audit/evejs-only-responses.md:948).
- **UNIMPLEMENTED NOTIFICATION PRODUCER — calendarMgr:** event names `OnNewCalendarEvent, OnEditCalendarEvent, OnRemoveCalendarEvent, OnEventResponseByExternal`; mutation coverage `CreatePersonalEvent, CreateCorporationEvent, CreateAllianceEvent, EditPersonalEvent, EditCorporationEvent, EditAllianceEvent, DeleteEvent, UpdateEventParticipants, SendEventResponse`. Trace recipients/payload/state-before-reply order for each mutation; do not add notification calls blindly to reads. [EJS-0663](../../docs/project/service-response-audit/evejs-only-responses.md:949).
- **PARTIAL NOTIFICATION COVERAGE — accessGroupBookmarkMgr:** folder update/delete notices and access-loss revocation are implemented. The remaining event names and write paths in [EJS-0664](../../docs/project/service-response-audit/evejs-only-responses.md:950) need event-by-event comparison; do not count the already covered notifications as an unimplemented producer. Shared ACL work is tracked in [EJS-0665](../../docs/project/service-response-audit/evejs-only-responses.md:951).
- **UNIMPLEMENTED NOTIFICATION PRODUCER — officeManager:** `OnOfficeRentalChange` after rental-state changes. This is the only Office gap reintroduced here; 009291c6's existing exports/remote-station operations remain source-fixed, not OPEN. [EJS-0666](../../docs/project/service-response-audit/evejs-only-responses.md:952).
- **VERIFY CURRENT SESSION FLOW, NOT CONFIRMED MISSING — Skills mutations and corporation kick/leave:** trace current feature writers, committed attributes, recipients and order. The ledger group does not establish a finite named-method deficit; do not manufacture a method list or count it as missing implementation. This is a current-sequence verification item, not 448+another group of missing RPCs. [EJS-0671](../../docs/project/service-response-audit/evejs-only-responses.md:957).
- **DONE — Skill injector tiers and sequential awards:** The handler calculates each consumed item from allocated plus unallocated SP at that point in the sequence. Large awards are 500k/400k/300k/150k SP and Small awards are 100k/80k/60k/30k SP at the 5M, 50M, and 80M boundaries; each allowed non-diminishing use grants its corresponding full-tier award. The award returned to the client, free-SP balance, item consumption, and ordered inventory/free-SP notifications are covered by `Injector_preview_uses_per_item_large_and_small_bands_and_non_diminishing_uses`, `Injector_notifies_committed_inventory_then_free_balance_and_failed_retries_are_silent`, and `Skill_injector_applies_sequential_awards_and_persists_points_and_inventory_through_host`. Evidence: [CCP Skill Extractors and Skill Injectors](https://support.eveonline.com/hc/en-us/articles/207605005-Skill-Extractors-and-Skill-Injectors) and [SkillHandlerService.Injection.cs](../../dotnet/src/Eve.Services.Skills/SkillHandlerService.Injection.cs).
- **VERIFY FIELD IMPACT — Placebo ack.session_init.maxSessionTime:** EveJS sends None and the inspected Rust ack omits the key; no required V24 consumer was established. Keep separate from functional login failure, and preserve the additional access_token/computer_hash keys required by LoginSso. [EJS-0672](../../docs/project/service-response-audit/evejs-only-responses.md:958).

The provider entries [EJS-0661](../../docs/project/service-response-audit/evejs-only-responses.md:947), [EJS-0665](../../docs/project/service-response-audit/evejs-only-responses.md:951), [EJS-0668](../../docs/project/service-response-audit/evejs-only-responses.md:954) are also result-field entries; they are not repeated as separate work above. Thus this provider+other index covers all **14 unique** group IDs EJS-0659 through EJS-0672. The three notification groups and both session groups still need real delivery/order acceptance; no live break reproduction was performed in this worker.

### Public protobuf gateway: complete candidate selector index (182; category VERIFY)

**Status for every selector below: VERIFY V24 NEED / DTO / OWNER / POLICY, not “182 confirmed missing features.”** Current production public transport/producer absence is a source finding, but the 182 selectors include legacy defaults, empty compatibility payloads, admin requests and intentional 404 cases. Full nested DTO comparisons: **0 complete / 182 incomplete**. The index has 59 namespace groups. It extends the existing native protobuf startup row; it is not a second gateway project or 182 additional OPEN services.

For each candidate, establish actual caller/request fields, response field numbers/types/oneof/repeated/optional status, application status versus gRPC trailers, identity/permission scope, real state owner, notifications, retry and next client action. Retain the older “gateway not built” owner decision until it is explicitly reconciled with the current startup TODO. The actual public service paths `Requests/Ping, Notices/Ping, Events/Ping, Requests/Send, Notices/Consume, Events/Publish` are separate from MachoNet and internal HostLink; no name alias substitutes for them.

#### Gateway candidate namespace: eve_public.achievement.api (1)

[GetRequest (EJS-0477)](../../docs/project/service-response-audit/evejs-only-responses.md:754).

#### Gateway candidate namespace: eve_public.achievement.category.api (1)

[GetRequest (EJS-0478)](../../docs/project/service-response-audit/evejs-only-responses.md:755).

#### Gateway candidate namespace: eve_public.achievement.definition.api.cdn (1)

[GetLatestRequest (EJS-0479)](../../docs/project/service-response-audit/evejs-only-responses.md:756).

#### Gateway candidate namespace: eve_public.achievement.reward.api (2)

[ClaimRequest (EJS-0480)](../../docs/project/service-response-audit/evejs-only-responses.md:757); [GetUnclaimedRequest (EJS-0481)](../../docs/project/service-response-audit/evejs-only-responses.md:758).

#### Gateway candidate namespace: eve_public.achievement.showcase.api (1)

[GetCharacterScoreRequest (EJS-0482)](../../docs/project/service-response-audit/evejs-only-responses.md:759).

#### Gateway candidate namespace: eve_public.campaign.api (6)

[GetAllRequest (EJS-0483)](../../docs/project/service-response-audit/evejs-only-responses.md:760); [GetAllUnredeemedRewardsRequest (EJS-0484)](../../docs/project/service-response-audit/evejs-only-responses.md:761); [GetLimitsRequest (EJS-0485)](../../docs/project/service-response-audit/evejs-only-responses.md:762); [GetParticipantStatsRequest (EJS-0486)](../../docs/project/service-response-audit/evejs-only-responses.md:763); [GetRequest (EJS-0487)](../../docs/project/service-response-audit/evejs-only-responses.md:764); [GetStatsRequest (EJS-0488)](../../docs/project/service-response-audit/evejs-only-responses.md:765).

#### Gateway candidate namespace: eve_public.campaign.contributionmethod.definition.api (1)

[GetAllRequest (EJS-0489)](../../docs/project/service-response-audit/evejs-only-responses.md:766).

#### Gateway candidate namespace: eve_public.campaign.objective.api (5)

[CommitRequest (EJS-0490)](../../docs/project/service-response-audit/evejs-only-responses.md:767); [GetParticipantStatsRequest (EJS-0491)](../../docs/project/service-response-audit/evejs-only-responses.md:768); [GetStatsRequest (EJS-0492)](../../docs/project/service-response-audit/evejs-only-responses.md:769); [RedeemRewardsRequest (EJS-0493)](../../docs/project/service-response-audit/evejs-only-responses.md:770); [ResignRequest (EJS-0494)](../../docs/project/service-response-audit/evejs-only-responses.md:771).

#### Gateway candidate namespace: eve_public.career.goal.api (2)

[GetAllRequest (EJS-0495)](../../docs/project/service-response-audit/evejs-only-responses.md:772); [GetDefinitionsRequest (EJS-0496)](../../docs/project/service-response-audit/evejs-only-responses.md:773).

#### Gateway candidate namespace: eve_public.character.skill.plan (10)

[CreateRequest (EJS-0497)](../../docs/project/service-response-audit/evejs-only-responses.md:774); [DeleteRequest (EJS-0498)](../../docs/project/service-response-audit/evejs-only-responses.md:775); [GetActiveRequest (EJS-0499)](../../docs/project/service-response-audit/evejs-only-responses.md:776); [GetAllRequest (EJS-0500)](../../docs/project/service-response-audit/evejs-only-responses.md:777); [GetRequest (EJS-0501)](../../docs/project/service-response-audit/evejs-only-responses.md:778); [GetSharedRequest (EJS-0502)](../../docs/project/service-response-audit/evejs-only-responses.md:779); [SetActiveRequest (EJS-0507)](../../docs/project/service-response-audit/evejs-only-responses.md:784); [SetDescriptionRequest (EJS-0508)](../../docs/project/service-response-audit/evejs-only-responses.md:785); [SetNameRequest (EJS-0509)](../../docs/project/service-response-audit/evejs-only-responses.md:786); [SetSkillRequirementsRequest (EJS-0510)](../../docs/project/service-response-audit/evejs-only-responses.md:787).

#### Gateway candidate namespace: eve_public.character.skill.plan.milestone (4)

[CreateRequest (EJS-0503)](../../docs/project/service-response-audit/evejs-only-responses.md:780); [DeleteRequest (EJS-0504)](../../docs/project/service-response-audit/evejs-only-responses.md:781); [GetAllRequest (EJS-0505)](../../docs/project/service-response-audit/evejs-only-responses.md:782); [SetDescriptionRequest (EJS-0506)](../../docs/project/service-response-audit/evejs-only-responses.md:783).

#### Gateway candidate namespace: eve_public.chat.local.api (2)

[BroadcastMessageRequest (EJS-0512)](../../docs/project/service-response-audit/evejs-only-responses.md:789); [GetMembershipListRequest (EJS-0513)](../../docs/project/service-response-audit/evejs-only-responses.md:790).

#### Gateway candidate namespace: eve_public.chat.local.api.admin (1)

[MuteRequest (EJS-0511)](../../docs/project/service-response-audit/evejs-only-responses.md:788).

#### Gateway candidate namespace: eve_public.corporation.skill.plan (9)

[CreateRequest (EJS-0514)](../../docs/project/service-response-audit/evejs-only-responses.md:791); [DeleteRequest (EJS-0515)](../../docs/project/service-response-audit/evejs-only-responses.md:792); [GetAllRequest (EJS-0516)](../../docs/project/service-response-audit/evejs-only-responses.md:793); [GetRequest (EJS-0517)](../../docs/project/service-response-audit/evejs-only-responses.md:794); [GetSharedRequest (EJS-0518)](../../docs/project/service-response-audit/evejs-only-responses.md:795); [SetCategoryRequest (EJS-0523)](../../docs/project/service-response-audit/evejs-only-responses.md:800); [SetDescriptionRequest (EJS-0524)](../../docs/project/service-response-audit/evejs-only-responses.md:801); [SetNameRequest (EJS-0525)](../../docs/project/service-response-audit/evejs-only-responses.md:802); [SetSkillRequirementsRequest (EJS-0526)](../../docs/project/service-response-audit/evejs-only-responses.md:803).

#### Gateway candidate namespace: eve_public.corporation.skill.plan.milestone (4)

[CreateRequest (EJS-0519)](../../docs/project/service-response-audit/evejs-only-responses.md:796); [DeleteRequest (EJS-0520)](../../docs/project/service-response-audit/evejs-only-responses.md:797); [GetAllRequest (EJS-0521)](../../docs/project/service-response-audit/evejs-only-responses.md:798); [SetDescriptionRequest (EJS-0522)](../../docs/project/service-response-audit/evejs-only-responses.md:799).

#### Gateway candidate namespace: eve_public.corporationgoal.api (9)

[GetActiveRequest (EJS-0527)](../../docs/project/service-response-audit/evejs-only-responses.md:804); [GetAllRequest (EJS-0528)](../../docs/project/service-response-audit/evejs-only-responses.md:805); [GetContributorSummariesForGoalRequest (EJS-0529)](../../docs/project/service-response-audit/evejs-only-responses.md:806); [GetInactiveRequest (EJS-0530)](../../docs/project/service-response-audit/evejs-only-responses.md:807); [GetMineWithRewardsRequest (EJS-0531)](../../docs/project/service-response-audit/evejs-only-responses.md:808); [GetMyContributorSummariesRequest (EJS-0532)](../../docs/project/service-response-audit/evejs-only-responses.md:809); [GetMyContributorSummaryForGoalRequest (EJS-0533)](../../docs/project/service-response-audit/evejs-only-responses.md:810); [RedeemAllMyRewardsRequest (EJS-0534)](../../docs/project/service-response-audit/evejs-only-responses.md:811); [RedeemMyRewardsRequest (EJS-0535)](../../docs/project/service-response-audit/evejs-only-responses.md:812).

#### Gateway candidate namespace: eve_public.cosmetic.character.title.api (4)

[EquipRequest (EJS-0536)](../../docs/project/service-response-audit/evejs-only-responses.md:813); [GetEquippedForCharacterRequest (EJS-0537)](../../docs/project/service-response-audit/evejs-only-responses.md:814); [GetOwnedRequest (EJS-0538)](../../docs/project/service-response-audit/evejs-only-responses.md:815); [UnequipRequest (EJS-0539)](../../docs/project/service-response-audit/evejs-only-responses.md:816).

#### Gateway candidate namespace: eve_public.cosmetic.character.title.definition.api (1)

[GetLatestRequest (EJS-0540)](../../docs/project/service-response-audit/evejs-only-responses.md:817).

#### Gateway candidate namespace: eve_public.cosmetic.corporation.palette.api (4)

[CanEditRequest (EJS-0541)](../../docs/project/service-response-audit/evejs-only-responses.md:818); [GetOwnRequest (EJS-0542)](../../docs/project/service-response-audit/evejs-only-responses.md:819); [GetRequest (EJS-0543)](../../docs/project/service-response-audit/evejs-only-responses.md:820); [SetRequest (EJS-0544)](../../docs/project/service-response-audit/evejs-only-responses.md:821).

#### Gateway candidate namespace: eve_public.cosmetic.market.skin.listing.api (2)

[GetAllOwnedRequest (EJS-0545)](../../docs/project/service-response-audit/evejs-only-responses.md:822); [GetAllRequest (EJS-0546)](../../docs/project/service-response-audit/evejs-only-responses.md:823).

#### Gateway candidate namespace: eve_public.cosmetic.ship.api (2)

[GetAllInBubbleRequest (EJS-0547)](../../docs/project/service-response-audit/evejs-only-responses.md:824); [GetRequest (EJS-0548)](../../docs/project/service-response-audit/evejs-only-responses.md:825).

#### Gateway candidate namespace: eve_public.cosmetic.ship.logo (2)

[ClearRequest (EJS-0549)](../../docs/project/service-response-audit/evejs-only-responses.md:826); [DisplayRequest (EJS-0550)](../../docs/project/service-response-audit/evejs-only-responses.md:827).

#### Gateway candidate namespace: eve_public.cosmetic.ship.skin.thirdparty.component.license.api (1)

[GetOwnedRequest (EJS-0551)](../../docs/project/service-response-audit/evejs-only-responses.md:828).

#### Gateway candidate namespace: eve_public.cosmetic.ship.skin.thirdparty.draft.api (2)

[GetAllSavedRequest (EJS-0552)](../../docs/project/service-response-audit/evejs-only-responses.md:829); [GetSaveCapacityRequest (EJS-0553)](../../docs/project/service-response-audit/evejs-only-responses.md:830).

#### Gateway candidate namespace: eve_public.cosmetic.ship.skin.thirdparty.license.api (4)

[ActivateRequest (EJS-0554)](../../docs/project/service-response-audit/evejs-only-responses.md:831); [ApplyRequest (EJS-0555)](../../docs/project/service-response-audit/evejs-only-responses.md:832); [GetOwnedRequest (EJS-0556)](../../docs/project/service-response-audit/evejs-only-responses.md:833); [UnapplyRequest (EJS-0557)](../../docs/project/service-response-audit/evejs-only-responses.md:834).

#### Gateway candidate namespace: eve_public.cosmetic.ship.skin.thirdparty.sequencing.job.api (1)

[GetAllActiveRequest (EJS-0558)](../../docs/project/service-response-audit/evejs-only-responses.md:835).

#### Gateway candidate namespace: eve_public.cosmetic.structure.paintwork.api (2)

[GetAllInSolarSystemRequest (EJS-0559)](../../docs/project/service-response-audit/evejs-only-responses.md:836); [GetRequest (EJS-0560)](../../docs/project/service-response-audit/evejs-only-responses.md:837).

#### Gateway candidate namespace: eve_public.cosmetic.structure.paintwork.license.api (5)

[GetAllOwnedByCorporationRequest (EJS-0563)](../../docs/project/service-response-audit/evejs-only-responses.md:840); [GetCatalogueRequest (EJS-0564)](../../docs/project/service-response-audit/evejs-only-responses.md:841); [GetRequest (EJS-0565)](../../docs/project/service-response-audit/evejs-only-responses.md:842); [IssueRequest (EJS-0566)](../../docs/project/service-response-audit/evejs-only-responses.md:843); [RevokeRequest (EJS-0567)](../../docs/project/service-response-audit/evejs-only-responses.md:844).

#### Gateway candidate namespace: eve_public.cosmetic.structure.paintwork.license.api.admin (2)

[IssueRequest (EJS-0561)](../../docs/project/service-response-audit/evejs-only-responses.md:838); [RevokeRequest (EJS-0562)](../../docs/project/service-response-audit/evejs-only-responses.md:839).

#### Gateway candidate namespace: eve_public.dailygoal.api (7)

[GetAllCompletedWithEntitlementsRequest (EJS-0568)](../../docs/project/service-response-audit/evejs-only-responses.md:845); [GetAllCurrentRequest (EJS-0569)](../../docs/project/service-response-audit/evejs-only-responses.md:846); [GetAllWithRewardsRequest (EJS-0570)](../../docs/project/service-response-audit/evejs-only-responses.md:847); [GetRequest (EJS-0571)](../../docs/project/service-response-audit/evejs-only-responses.md:848); [PayForCompletionRequest (EJS-0572)](../../docs/project/service-response-audit/evejs-only-responses.md:849); [RedeemAllRequest (EJS-0573)](../../docs/project/service-response-audit/evejs-only-responses.md:850); [RedeemRequest (EJS-0574)](../../docs/project/service-response-audit/evejs-only-responses.md:851).

#### Gateway candidate namespace: eve_public.entitlement.character (1)

[GetAllRequest (EJS-0575)](../../docs/project/service-response-audit/evejs-only-responses.md:852).

#### Gateway candidate namespace: eve_public.entitlement.character.ship.admin.alliancelogo (2)

[GrantRequest (EJS-0576)](../../docs/project/service-response-audit/evejs-only-responses.md:853); [RevokeRequest (EJS-0577)](../../docs/project/service-response-audit/evejs-only-responses.md:854).

#### Gateway candidate namespace: eve_public.entitlement.character.ship.admin.corplogo (2)

[GrantRequest (EJS-0578)](../../docs/project/service-response-audit/evejs-only-responses.md:855); [RevokeRequest (EJS-0579)](../../docs/project/service-response-audit/evejs-only-responses.md:856).

#### Gateway candidate namespace: eve_public.faction.activity (1)

[GetSolarSystemScoresRequest (EJS-0580)](../../docs/project/service-response-audit/evejs-only-responses.md:857).

#### Gateway candidate namespace: eve_public.freelance.contributionmethod.definition.api (2)

[GetAllLatestRequest (EJS-0581)](../../docs/project/service-response-audit/evejs-only-responses.md:858); [GetAllLatestWithinMajorRequest (EJS-0582)](../../docs/project/service-response-audit/evejs-only-responses.md:859).

#### Gateway candidate namespace: eve_public.freelance.contributionmethod.itemdelivery.api (1)

[GetStatusRequest (EJS-0583)](../../docs/project/service-response-audit/evejs-only-responses.md:860).

#### Gateway candidate namespace: eve_public.freelance.project.api (19)

[CloseRequest (EJS-0584)](../../docs/project/service-response-audit/evejs-only-responses.md:861); [CommitRequest (EJS-0585)](../../docs/project/service-response-audit/evejs-only-responses.md:862); [CreateRequest (EJS-0586)](../../docs/project/service-response-audit/evejs-only-responses.md:863); [GetAllActiveForCorporationRequest (EJS-0587)](../../docs/project/service-response-audit/evejs-only-responses.md:864); [GetAllActiveRequest (EJS-0588)](../../docs/project/service-response-audit/evejs-only-responses.md:865); [GetAllBroadcastedRequest (EJS-0589)](../../docs/project/service-response-audit/evejs-only-responses.md:866); [GetAllCommittedRequest (EJS-0590)](../../docs/project/service-response-audit/evejs-only-responses.md:867); [GetAllInactiveRequest (EJS-0591)](../../docs/project/service-response-audit/evejs-only-responses.md:868); [GetAllUnredeemedRequest (EJS-0592)](../../docs/project/service-response-audit/evejs-only-responses.md:869); [GetCommitLimitsRequest (EJS-0593)](../../docs/project/service-response-audit/evejs-only-responses.md:870); [GetCreationLimitsRequest (EJS-0594)](../../docs/project/service-response-audit/evejs-only-responses.md:871); [GetCreationPricesRequest (EJS-0595)](../../docs/project/service-response-audit/evejs-only-responses.md:872); [GetForManagerRequest (EJS-0596)](../../docs/project/service-response-audit/evejs-only-responses.md:873); [GetParticipationDetailsRequest (EJS-0597)](../../docs/project/service-response-audit/evejs-only-responses.md:874); [GetRequest (EJS-0598)](../../docs/project/service-response-audit/evejs-only-responses.md:875); [GetStatsRequest (EJS-0599)](../../docs/project/service-response-audit/evejs-only-responses.md:876); [KickRequest (EJS-0600)](../../docs/project/service-response-audit/evejs-only-responses.md:877); [RedeemRewardRequest (EJS-0601)](../../docs/project/service-response-audit/evejs-only-responses.md:878); [ResignRequest (EJS-0602)](../../docs/project/service-response-audit/evejs-only-responses.md:879).

#### Gateway candidate namespace: eve_public.goal.api (7)

[CloseRequest (EJS-0603)](../../docs/project/service-response-audit/evejs-only-responses.md:880); [CreateRequest (EJS-0604)](../../docs/project/service-response-audit/evejs-only-responses.md:881); [DeleteRequest (EJS-0605)](../../docs/project/service-response-audit/evejs-only-responses.md:882); [GetAllRequest (EJS-0606)](../../docs/project/service-response-audit/evejs-only-responses.md:883); [GetCapacityRequest (EJS-0607)](../../docs/project/service-response-audit/evejs-only-responses.md:884); [GetRequest (EJS-0608)](../../docs/project/service-response-audit/evejs-only-responses.md:885); [SetCurrentProgressRequest (EJS-0609)](../../docs/project/service-response-audit/evejs-only-responses.md:886).

#### Gateway candidate namespace: eve_public.payment.purchase.api (2)

[CostRequest (EJS-0610)](../../docs/project/service-response-audit/evejs-only-responses.md:887); [TokenRequest (EJS-0611)](../../docs/project/service-response-audit/evejs-only-responses.md:888).

#### Gateway candidate namespace: eve_public.payment.schedule.api (1)

[GetActiveByTypeRequest (EJS-0612)](../../docs/project/service-response-audit/evejs-only-responses.md:889).

#### Gateway candidate namespace: eve_public.payment.token.api (3)

[DisableAllRequest (EJS-0613)](../../docs/project/service-response-audit/evejs-only-responses.md:890); [GetQuickPayRequest (EJS-0614)](../../docs/project/service-response-audit/evejs-only-responses.md:891); [GetRequest (EJS-0615)](../../docs/project/service-response-audit/evejs-only-responses.md:892).

#### Gateway candidate namespace: eve_public.pirate.corruption.api (2)

[GetStageThresholdsRequest (EJS-0616)](../../docs/project/service-response-audit/evejs-only-responses.md:893); [GetSystemInfoRequest (EJS-0617)](../../docs/project/service-response-audit/evejs-only-responses.md:894).

#### Gateway candidate namespace: eve_public.pirate.suppression.api (2)

[GetStageThresholdsRequest (EJS-0618)](../../docs/project/service-response-audit/evejs-only-responses.md:895); [GetSystemInfoRequest (EJS-0619)](../../docs/project/service-response-audit/evejs-only-responses.md:896).

#### Gateway candidate namespace: eve_public.plex.vault.api (2)

[BalanceRequest (EJS-0620)](../../docs/project/service-response-audit/evejs-only-responses.md:897); [PurchaseRequest (EJS-0621)](../../docs/project/service-response-audit/evejs-only-responses.md:898).

#### Gateway candidate namespace: eve_public.plex.vault.transaction.api (3)

[GetAllLoggedForUserRequest (EJS-0622)](../../docs/project/service-response-audit/evejs-only-responses.md:899); [GetLogRequest (EJS-0623)](../../docs/project/service-response-audit/evejs-only-responses.md:900); [GetStatisticsRequest (EJS-0624)](../../docs/project/service-response-audit/evejs-only-responses.md:901).

#### Gateway candidate namespace: eve_public.sovereignty.hub.api (1)

[GetResourcesRequest (EJS-0625)](../../docs/project/service-response-audit/evejs-only-responses.md:902).

#### Gateway candidate namespace: eve_public.sovereignty.hub.fuel.api (2)

[AddRequest (EJS-0626)](../../docs/project/service-response-audit/evejs-only-responses.md:903); [GetRequest (EJS-0627)](../../docs/project/service-response-audit/evejs-only-responses.md:904).

#### Gateway candidate namespace: eve_public.sovereignty.hub.upgrade.api (4)

[GetDefinitionsRequest (EJS-0628)](../../docs/project/service-response-audit/evejs-only-responses.md:905); [GetHubUpgradesRequest (EJS-0629)](../../docs/project/service-response-audit/evejs-only-responses.md:906); [ProcessConfigurationRequest (EJS-0630)](../../docs/project/service-response-audit/evejs-only-responses.md:907); [UninstallRequest (EJS-0631)](../../docs/project/service-response-audit/evejs-only-responses.md:908).

#### Gateway candidate namespace: eve_public.sovereignty.hub.workforce.api (4)

[ConfigureRequest (EJS-0632)](../../docs/project/service-response-audit/evejs-only-responses.md:909); [GetConfigurationRequest (EJS-0633)](../../docs/project/service-response-audit/evejs-only-responses.md:910); [GetNetworkableHubsRequest (EJS-0634)](../../docs/project/service-response-audit/evejs-only-responses.md:911); [GetStateRequest (EJS-0635)](../../docs/project/service-response-audit/evejs-only-responses.md:912).

#### Gateway candidate namespace: eve_public.sovereignty.mercenaryden.activity.api (4)

[GetAllRequest (EJS-0636)](../../docs/project/service-response-audit/evejs-only-responses.md:913); [GetCapacityRequest (EJS-0637)](../../docs/project/service-response-audit/evejs-only-responses.md:914); [GetForMercenaryDenRequest (EJS-0638)](../../docs/project/service-response-audit/evejs-only-responses.md:915); [StartRequest (EJS-0639)](../../docs/project/service-response-audit/evejs-only-responses.md:916).

#### Gateway candidate namespace: eve_public.sovereignty.mercenaryden.api (3)

[GetAllOwnedRequest (EJS-0640)](../../docs/project/service-response-audit/evejs-only-responses.md:917); [GetAsOwnerRequest (EJS-0641)](../../docs/project/service-response-audit/evejs-only-responses.md:918); [GetMaximumForCharacterRequest (EJS-0642)](../../docs/project/service-response-audit/evejs-only-responses.md:919).

#### Gateway candidate namespace: eve_public.sovereignty.resource.planet.api (2)

[GetAllDefinitionsRequest (EJS-0643)](../../docs/project/service-response-audit/evejs-only-responses.md:920); [GetDefinitionsVersionRequest (EJS-0644)](../../docs/project/service-response-audit/evejs-only-responses.md:921).

#### Gateway candidate namespace: eve_public.sovereignty.resource.star.api (1)

[GetAllConfigurationsRequest (EJS-0645)](../../docs/project/service-response-audit/evejs-only-responses.md:922).

#### Gateway candidate namespace: eve_public.sovereignty.skyhook.api (7)

[ActivateRequest (EJS-0646)](../../docs/project/service-response-audit/evejs-only-responses.md:923); [DeactivateRequest (EJS-0648)](../../docs/project/service-response-audit/evejs-only-responses.md:925); [GetAllByCorporationRequest (EJS-0649)](../../docs/project/service-response-audit/evejs-only-responses.md:926); [GetAllLocalRequest (EJS-0650)](../../docs/project/service-response-audit/evejs-only-responses.md:927); [GetRequest (EJS-0651)](../../docs/project/service-response-audit/evejs-only-responses.md:928); [GetSolarSystemsWithTheftVulnerableSkyhooksRequest (EJS-0652)](../../docs/project/service-response-audit/evejs-only-responses.md:929); [GetTheftVulnerableSkyhooksInSolarSystemRequest (EJS-0653)](../../docs/project/service-response-audit/evejs-only-responses.md:930).

#### Gateway candidate namespace: eve_public.sovereignty.skyhook.api.admin (1)

[ModifyReagentsRequest (EJS-0647)](../../docs/project/service-response-audit/evejs-only-responses.md:924).

#### Gateway candidate namespace: eve_public.space.api.cdn (1)

[GetUpdateRequest (EJS-0654)](../../docs/project/service-response-audit/evejs-only-responses.md:931).

#### Gateway candidate namespace: eve_public.stargate.lock.api (1)

[GetRestrictedSystemsRequest (EJS-0655)](../../docs/project/service-response-audit/evejs-only-responses.md:932).

#### Gateway candidate namespace: eve_public.store.offer.api (2)

[GetAllByTagRequest (EJS-0656)](../../docs/project/service-response-audit/evejs-only-responses.md:933); [GetRequest (EJS-0657)](../../docs/project/service-response-audit/evejs-only-responses.md:934).

#### Gateway candidate namespace: eve_public.user.license.api (1)

[GetRequest (EJS-0658)](../../docs/project/service-response-audit/evejs-only-responses.md:935).

### Client-local alternatives and legacy (3 + 25; excluded from the 448 named backlog)

**CLIENT-LOCAL COVERED (3)** means the V24 wrapper/local lookup uses an existing operation. The literal old RPC name remains absent; this is not proof of dynamic materialization or full value/notification parity. Two spelling aliases are a subset of these three, not a fourth/fifth work item.

- **CLIENT-LOCAL COVERED:** [agentMgr.GetAgentByID (EJS-0015)](../../docs/project/service-response-audit/evejs-only-responses.md:266). svc.agents.GetAgentByID → GetAgents().Clone/Index; existing agentMgr.GetAgents (materialization/OnAgentAdded dynamic parity not confirmed).
- **CLIENT-LOCAL COVERED:** [shipCosmeticsMgr.GetLicensedSkins (EJS-0424)](../../docs/project/service-response-audit/evejs-only-responses.md:675). cosmeticsSvc local wrapper → GetLicencedSkins; existing same-service wire exit; dynamic parity not confirmed.
- **CLIENT-LOCAL COVERED:** [shipCosmeticsMgr.GetLicensedSkinsForShipType (EJS-0425)](../../docs/project/service-response-audit/evejs-only-responses.md:676). cosmeticsSvc local wrapper → GetLicencedSkinsForShipType; existing same-service wire exit; dynamic parity not confirmed.

**VERIFY LEGACY (25):** caller/need remains not confirmed; legacy is not a NOT NEEDED decision or an implemented RPC. Retain existing Native trace OPENs for ProjectDiscovery.initialize_tutorial_status and skillHandler.CheckAndSendNotifications; skillHandler/skillMgr inherited alias is one issue.

- **agentMgr (2):** [GetAgentStaticInfo (EJS-0016)](../../docs/project/service-response-audit/evejs-only-responses.md:267); [GetStaticBlob (EJS-0024)](../../docs/project/service-response-audit/evejs-only-responses.md:275).
- **authentication (2):** [Login (EJS-0034)](../../docs/project/service-response-audit/evejs-only-responses.md:285); [Logout (EJS-0035)](../../docs/project/service-response-audit/evejs-only-responses.md:286).
- **paperDollServer (1):** [ConvertAndSavePaperDoll (EJS-0291)](../../docs/project/service-response-audit/evejs-only-responses.md:542).
- **planetMgr (2):** [GMCleanupExpiredLaunches (EJS-0301)](../../docs/project/service-response-audit/evejs-only-responses.md:552); [GMGetPlanetDiagnostics (EJS-0304)](../../docs/project/service-response-audit/evejs-only-responses.md:555).
- **ProjectDiscovery (15):** [get_is_player_entitled_to_tutorial_reward (EJS-0340)](../../docs/project/service-response-audit/evejs-only-responses.md:591); [get_maximum_bonus_samples (EJS-0341)](../../docs/project/service-response-audit/evejs-only-responses.md:592); [get_new_task (EJS-0342)](../../docs/project/service-response-audit/evejs-only-responses.md:593); [get_project_id (EJS-0343)](../../docs/project/service-response-audit/evejs-only-responses.md:594); [get_remaining_bonus_samples (EJS-0344)](../../docs/project/service-response-audit/evejs-only-responses.md:595); [get_total_needed_xp (EJS-0345)](../../docs/project/service-response-audit/evejs-only-responses.md:596); [get_tutorial_level (EJS-0346)](../../docs/project/service-response-audit/evejs-only-responses.md:597); [get_tutorial_xp_reward (EJS-0347)](../../docs/project/service-response-audit/evejs-only-responses.md:598); [give_tutorial_rewards (EJS-0348)](../../docs/project/service-response-audit/evejs-only-responses.md:599); [increase_tutorial_level (EJS-0349)](../../docs/project/service-response-audit/evejs-only-responses.md:600); [initialize_tutorial_status (EJS-0350)](../../docs/project/service-response-audit/evejs-only-responses.md:601); [new_training_task (EJS-0351)](../../docs/project/service-response-audit/evejs-only-responses.md:602); [post_classification (EJS-0352)](../../docs/project/service-response-audit/evejs-only-responses.md:603); [reset_tutorial (EJS-0353)](../../docs/project/service-response-audit/evejs-only-responses.md:604); [skip_tutorial (EJS-0354)](../../docs/project/service-response-audit/evejs-only-responses.md:605).
- **publicGateway (1):** [send_request (EJS-0355)](../../docs/project/service-response-audit/evejs-only-responses.md:606).
- **shipKillCounter (1):** [GetItemKillCountNPC (EJS-0426)](../../docs/project/service-response-audit/evejs-only-responses.md:677).
- **skillMgr (1):** [CheckAndSendNotifications (EJS-0427)](../../docs/project/service-response-audit/evejs-only-responses.md:678).

Core A-F reconciliation: old 44 = 33 unimplemented + 3 client-local + 6 legacy + 2 same-service implemented; current missing names 42. agentMgr.MachoResolveObject and dungeonInstanceCacheMgr.GetTriglavianSiteInstances were already excluded from the old 455 index. No double decrement and no new OPEN. [Revised handoff](../../docs/project/service-response-audit/core-a-f.md:345).

### Corrected / policy / existing-implementation exclusions

- **SOURCE FIXED, no new OPEN:** station/skillMgr/tradeMgr/standing2 aliases (cf450dc9); correct CorpRegistryService partial ownership of CreateAlliance/GetAllianceApplications/GetSuggestedAllianceShortNames (c98dffeb regression); Agent resolve and initial nested dispatch (aab0dd10); Office exports/remote-station operations (009291c6); Crate shared fitting-store target and claim retry/restoration path (c7288f1c/dda03ca0); subsequent beyonce.CmdAbandonLoot delegation; dungeonInstanceCacheMgr.GetTriglavianSiteInstances; CorpStationMgr bound release cleanup. Source fixes do not mean full client acceptance. Remaining distinct fields/providers/notices keep their own entries above.
- **CONTAINER MISDETECTION WITHDRAWN:** EveJS native arrays marshal as Python tuples. PaperDoll portrait and IndustryMonitor.ConnectJob native-array/Py.Tuple comparisons are not list/tuple deficits. Other DTO/provider/authorization differences remain separate.
- **CCP-required improvements, not omissions:** ROLE_ADMIN/ROLE_SERVICE global-config write gate; LoginSso acknowledgement access_token/computer_hash keys. Preserve transaction atomicity for SKIN activation/dynamic-item mutation instead of copying partial-commit reference behavior.
- **POLICY/INTENT DIFFERENCE, no blanket missing-route work:** protected config-key errors; explicit unavailable refusal for unconnected mutual invites; wrapped errors instead of unknown-service/method None success; older public-gateway omission decision. Preserve existing NOT NEEDED/cut decisions and verify conflicts using the current client flow. No fake field values or response packets are authorized by this backlog.
- **EXISTING RESPONSE DIFFERENCE / IMPACT NOT CONFIRMED:** multi-session status count and partial facility/ACL DTOs are repair/connect work, not whole services to implement. maxSessionTime and cache-validation differences need consumer/impact verification. EJS-0671's session-flow group remains not confirmed, not a confirmed missing producer.

### Closure and non-additive accounting

A named-exit item can close only after the actual partial type/attribute/HasMethod and Host/bound registration are connected, or an existing API is shown to satisfy the same meaningful V24 flow and the documented caller is routed there. Confirm arguments/kwargs, complete result type/fields/rowsets, authoritative reads/writes, permissions/errors, persistence/idempotency, required notification/session order and next operation. Do not infer closure from a route name or a stub response. A retained legacy/policy difference closes as an explicit justified decision, not fabricated success.

This documentation review did not rerun source acceptance, builds, tests, runtime comparisons, graphical-client scenarios, concurrency/retry/restart or all nested DTO checks. Snapshot and parent dirty changes are stated above. Implementation/test work belongs to a separately authorized owner; this update changed documentation only and made no Git mutation.

Accounting checks for this appendix: **448 unique unimplemented named IDs across 63 service groups; 39 bound IDs are an overlapping 37-unimplemented/2-legacy subset; 6 provider IDs overlap the 14 response groups; 182 unique gateway selector IDs across 59 namespaces; 25 legacy + 3 client-local IDs (including 2 spelling aliases) are separate; all 672 ledger IDs are addressable.** Do not add 11/29 services, 39 bound, 182 candidates or provider group sizes to 448 as a total of unimplemented gameplay features.

### Core A-F handoff applied (2026-10-13; document reconciliation)

All 42 current literal-name differences in [core-a-f.md](../../docs/project/service-response-audit/core-a-f.md:345) match existing EJS IDs. Seven former unimplemented rows changed class: EJS-0015 is client-local covered; EJS-0016/EJS-0024/EJS-0426/EJS-0291/EJS-0301/EJS-0304 are legacy/not confirmed. EJS-0424/EJS-0425 are included in client-local 3. The already implemented Resolve/Triglavian names were absent from the old missing index and remain source-fixed. The 33 core-a-f unimplemented entries stay in the 448 index.

Total remains **672 = 476 physical named differences (448 + 3 + 25) + 182 candidate selectors + 14 groups**. No dynamic acceptance, service coverage, caller necessity outside the new handoff, complete nested DTO parity or source functionality was newly executed. CBM MCP Transport closed was checked again; documented CLI index_status verified the canonical main root ready. Serena remained canonical. This update changes the five authorized documents only, no source/test/build or Git mutation.

Document readback observed parent main HEAD `c114dc0d8e292c056e8d71b37f496084f484676f`. The classification/source snapshot above remains `dda03ca0464043822e68c8348604ff34eadedcac`; the later parent source commit was not re-audited by this documentation task.

## Structure ST-8a skill inputs (2026-10-13)
- VERIFIED (PG regression only): controlled structure DogmaWorld uses only Structure Doomsday Operation, Structure Electronic Systems, Structure Engineering Systems and Structure Missile Systems; mutable skill item values are independent from the retained personal snapshot. Character skill metadata and total SP are preserved. Host StructureControl subset: 12 PASS, 0 FAIL/SKIP. Evidence and source priority: docs/project/structures-phase/handoff-ST-8a-skills.md.
- OPEN: ordinary structure module execution and full Host-to-Sim/EveJS comparison; ST-8b doomsdays and native client acceptance. This input-layer correction does not close those feature rows.

## Structure ST-8 controlled-structure modules and superweapons (2026-10-15)

- DONE (Sim and Host tests, not native): the Standup burst projectors 47069-47074 and 35944 (the structure's doomsdayAOE families: ECM, damp, web, paint, bubble, track, neutralization) on a controlled structure. Each cycle asks the Host, per pulse, which ships in reach the owner may affect (Kind 85/86); only the granted ships are reached (`modules/burst.rs`, `StructureBurstEffects.cs`). Tests: `modules::tests::a_controlled_structure_burst_*`, `StructureBurstAuthorizationTests`, simlink fixture `activate a structure ecm burst projector`.
- DONE (Sim and Host tests, not native): Standup guided bomb launchers (35923, 47325) release their bombs as before; each blast is authorized per detonation, the blast reaching only the ships and structures the Host grants (a bomb not answered within 30 s goes off with no effect). Tests: `modules::tests::a_controlled_structure_bomb_*`, `a_structure_bomb_*`, `StructureBurstAuthorizationTests.A_standup_guided_bomb_launcher_*`.
- DONE: the structure Vorton (35928, lightning, SDE hop attributes) authorizes each hop by the owner's facts (`A_standup_vorton_projector_*`); point defense authorizes each cycle (existing).
- DONE: the Standup HOG 47068 is refused as `MODULE_NOT_PUBLISHED` (SDE publication bit kept on `TypeDef`), with nothing sent to the Sim.
- FIXED: `FitsStructureDamageModule` refused any structure module with `disallowRepeatingActivation` when the client asked for a repeat above 1; the built effect carries the one-cycle budget, so the check is removed.
- OPEN: the ship-fitted burst projectors (27678, 40635, 40696-40699, 40636, 41350) stay refused (no ship body; the manifest lists their effect names).
- OPEN: native client acceptance of the structure bursts, bombs and Vorton (slim rendering, module HUD, FX), and a live Host-to-Sim walk through `run-eve-live.sh`.
- OPEN: ST-8 not closed for the guided bomb after the controller releases the structure or the launcher is deactivated before the blast (fail closed: the blast reaches no one). See DEVIATIONS "Structure ST-8".


## 56. ST-7 structure services, closing pass: jammer gate, fuel owner notices, beacon and jammer Host tests (2026-10-15)

Continues section 46 (the earlier ST-7 rows); the closure record is docs/project/structures-phase/handoff-ST-7-closure.md. It supersedes the E-03 and beacon rows of sections 22, 23, 25 and 35 that say the jammer callback is unassigned or the structure beacon is refused: both are built now.

Done (tests in Eve.Structures.Tests, Eve.Host.Tests, Eve.Services.Space.Tests; decisions in DEVIATIONS "ST-7 structure services"):
- Service fuel: online against the SDE charge, hourly burn from the fuel bay (StructureFuel, StructureFuelTests).
- Structure compression in a personal hangar or ship (StructureCompressionAuthority, StructureCompressionTests).
- Cynosural System Jammer (E-03): the jammer state of a system is answered by GetCynoJammerState only when the jammer service is online and the hub Cynosural Suppression upgrade is online (StructureComposition.JammerStateAsync, StructureCynoSuppressionTests, StructureCompositionTests).
- A standard cynosural field is refused in a jammed system; the covert field is immune (CynoFieldHostTests).
- E-15 notice (OnCynoJammerChanged) on a jammer service change.
- Structure cyno beacon: the checks of EveJS resolveUsableBeacon, the jump through the capital jump drive, and the Pharolux beacon placed in the simulation when service 16 comes online and removed when it goes offline or the structure is gone (SpaceStructureCynoBeacons, StructureBeaconTravelTests, StructureCompositionTests).
- Fuel burn: the owner corporation gets OnItemChange when a member is online outside the structure; otherwise the fuel alert (type 181) goes to the notifications mailbox in the burn transaction (StructureFuelOwnerNotifications).

NOT BUILT (open rows):
- Structure corporation offices (rental at a structure, EveJS officeManagerService structure branch): built in section 57 (ST-12); the compression source reads the office rental rows.
- E-15 on a sov change of the Cynosural Suppression upgrade alone (the jammer state changes but no notice is sent).
- Group jumps and bridges to a structure beacon (refused by name).
- The power and workforce budget of the sov upgrades (EveJS computeHubUpgradeSnapshot); the stored online flag is read instead.
- Native client acceptance of the jammer notice, the beacon jump and the fuel notices (not exercised; no graphical client here).
## 57. Structures phase ST-12: corporation offices in Upwell structures (2026-10-15)

Handoff: `docs/project/structures-phase/handoff-ST-12.md`. Decisions and sources: DEVIATIONS "Structure corporation offices (ST-12, 2026-10-15)". Tests: `dotnet/tests/Eve.Host.Tests/StructureOfficeTests.cs` (Host registration, PostgreSQL, restart).

| Status | Row | Notes |
|---|---|---|
| VERIFIED (Host registration, PG, restart) | (a) the Corporation Offices service is reported online at every structure in the legacy shape with no module and no fuel (the wiki's "always available"; SDE type 35893 has no fuel attribute); its access is profile setting 20; the structure is an office site for `officeManager`; the quote is the profile rent for the pilot | `The_office_service_is_online...`. The brief's "fuel via ST-7" does not apply (DEVIATIONS ST-12). |
| VERIFIED (Host registration, PG, restart) | (b) rent at a structure: the rent is charged to the renting corporation and credited to the owner in one transaction; a second rent is not charged again; an unaffordable rent is refused with nothing written; the due job (`office.rent_due`) renews a paid office by auto-pay, or gives an unpaid one up at due (its items to corporate asset safety, its bills cancelled), durable across a restart; a Director un-rents by hand, a member cannot | `Renting_an_office...`, `A_rent_paid_before_a_restart...`, `A_renter_who_cannot_pay...`, `A_due_rent_is_paid_by_auto_pay...`, `An_unpaid_office...`, `A_director_gives_an_office_up...` |
| VERIFIED (Host registration, PG) | (c) the office hangar at the structure is the compression source: with the rented office and the plant online, the corporation's ore in its hangar is compressed through `structureCompressionMgr`; without the office it is refused | `A_rented_office_hangar_at_a_structure...` |
| VERIFIED (Host registration, PG) | (f) renewal reprices the rent from the structure profile: the next bill takes the Corporation Office Rent group 0 value at renewal (auto-pay; the manual payment shares the store path, not separately tested); no such group keeps the paid rent | `A_renewal_reprices_the_office_rent_from_the_structure_profile...`; DEVIATIONS "Structure corporation offices (ST-13, 2026-10-15)" |
| OPEN | Owner credit for station offices: the station rent settlement debits the renter and does not credit the station owner (existing behaviour, outside ST-12) | DEVIATIONS ST-12 |
| OPEN | Station offices have no due or impound job (EveJS impounds at a default; eve.rs keeps the bill open) | DEVIATIONS ST-12 |
| OPEN | Notices of an office rent or give-up to the corporation (EveJS notifyOfficeRentItemChange, notifyOfficeRentalChange, bill refresh); not built | DEVIATIONS ST-12 |
| VERIFIED (Host registration, PG) | (g) a structure destroyed or unanchored while rented: the state change makes each office due at once; the office closes (its bills cancelled, its corporation's items to asset safety, the office row removed); a refused wrap is retried hourly | `A_destroyed_structure_closes_its_rented_office...`; DEVIATIONS ST-13. No destruction loot (eve.rs has none) |
| OPEN | Client acceptance: the office window, the hangar listing of a structure's corporate division, the rent dialogue (no graphical client run) | DEVIATIONS ST-12 |

## 50. Structures phase ST-9: Entosis link and Sovereignty Hub capture (2026-10-15)
| Status | Row | Notes |
|---|---|---|
| VERIFIED (PG, Host registration, Sim fake) | (a) a Sovereignty Hub is a structure ball from the sovereignty records on system entry (`SpaceSovHubs`, `SpaceSovHubsTests` 6 of 6, 0 skipped) | DEVIATIONS "Sovereignty Hub ball (ST-9 a)". Position at the origin, client state not confirmed. |
| VERIFIED (Rust and C# unit, fixtures, Host build) | (b) `EntosisLinkEffect` body: protocol 70, tag 31, command 14; sov-hub target and alliance refused by name; lock and reach refused by name in the sim; package 300 000 ms, 20 000 m, fuel per cycle | DEVIATIONS "Entosis link: the body and its refusals (ST-9 b)". The command 14 cycle is refused `ENTOSIS_CAPTURE_NOT_SETTLED` until (c). |
| VERIFIED (pure, PostgreSQL, container with the fake simulation) | (c) capture state per hub in `entosis.hubs` / `entosis.links`: warm-up on the first cycle, one attacker link scores, a defender pauses, a defender alone regenerates, the completion is a durable job, a link end moves the capture to now, restart reads the stored progress | DEVIATIONS "Entosis capture state (ST-9 c)". Tests: `EntosisCaptureTests` 6, `EntosisCaptureServiceTests` 4, `EntosisCycleHostTests` 2. Window and notices: see the ST-9 e rows below. |
| VERIFIED (Host containers with the real rig and the fake simulation; PostgreSQL) | (e) the entosis cycle through `SpaceContainers` (`EntosisCycleHostTests`, 2), the module end report, the capture and the handover tests, and the gap manifest in `ModuleEffectsTests` (`entosisLink` leaves `currentSdeGaps`) | Native client acceptance is not exercised: see the open rows below and the handoff. |
| VERIFIED (pure, PostgreSQL, Host registration with the event bus and the mailbox) | (e) the sovereignty vulnerability window: a cycle outside the owner's window (18 h / ADM, centred on the prime hour) is refused `SOVEREIGNTY_TARGET_NOT_VULNERABLE` and stores nothing; a partly captured hub stays vulnerable; a neutral hub has no window | DEVIATIONS "Sovereignty vulnerability window, reinforcement and owner notices (ST-9 e)". Tests: `EntosisCaptureTests` (+3), `EntosisCaptureServiceTests` (outside the window). Open: the wiki's midpoint lock of a day's length; custom windows. |
| VERIFIED (PostgreSQL, the event bus and the mailbox, Host) | (e) the owner's notices: capture started (V24.01 147, on the first attacker cycle of an idle owned hub) and structures reinforced (160, decloakTime, campaignEventType 2) to every character of the defending alliance; written in the event's transaction, redelivery writes nothing twice; neutral hubs notify nobody | DEVIATIONS (ST-9 e). Tests: `EntosisOwnerNotificationTests` (1), `EntosisCaptureServiceTests` (notices). Client text of 147/160 not verified (MITM open). |
| PARTIAL | ST-9 d/e: the owned hub's reinforcement is recorded `reinforced` (48 h, then a random point of the next window: `hubs.reinforcement_exit_ms`); the command-node event is OPEN (out of scope, not built) | Nothing consumes the exit yet; a reinforced hub refuses further cycles (`SOVEREIGNTY_HUB_ALREADY_REINFORCED`) and keeps its owner. |
| VERIFIED (PostgreSQL, Host) | ST-9 e: the claim notice of a neutral hub's new owner (V24.01 43 to the capturing alliance's members, 44 to the claiming corporation's members; payload `corpID`, `allianceID`), written in the handover after the claim is persisted; a retry writes nothing twice | `SovereigntyClaimNotificationTests` (1), `SovHubClaimTests` (3) and `EntosisOwnerNotificationTests` (1) pass on eve_p3. Recipient rule and client text: DEVIATIONS "Sovereignty claim notices"; client not exercised. |
| NOT IN V24.01 | ST-9 e: a notice of a capture's progress | eve-logic `notificationconst.py` names no progress type; none is written. |
| VERIFIED (pure, in-memory sovereignty tables, PostgreSQL, Host registration with the fake simulation) | (d) a neutral hub captured by an entosis link is claimed by the capturing alliance's executor corporation through the sovereignty write path; the ball is replaced with the new owner; a failed write is retried by a job | DEVIATIONS "Entosis handover of a captured Sovereignty Hub (ST-9 d)". Tests: `SovHubClaimTests` 3, `EntosisCaptureServiceTests` 8 in total, `SpaceSovHubsTests` 6. Not verified: the owner's notice, the client's view of the new owner. |

## 58. Structures phase ST-16: station rent, observatory control towers, destruction loot, customs (2026-10-16)

Handoff: `docs/project/structures-phase/handoff-ST-16.md`. Decisions and sources: DEVIATIONS "Structure ST-16 (2026-10-16)". Tests: `dotnet/tests/Eve.Corporations.Tests/CorporationTests.cs` (station rent credit, impound helper, impound release credit), `dotnet/tests/Eve.Host.Tests/StructureUnanchorDropTests.cs` (destruction loot, atomic), `dotnet/tests/Eve.Services.Space.Tests/MobileObservatoryHostTests.cs` (control tower 100 km).

| Status | Row | Notes |
|---|---|---|
| VERIFIED (PG, Corporations suite 128/128) | (3a) station rent and impound release credit the station owner; a bill payment credits the resolved creditor; the owner balance is opened when missing | DEVIATIONS ST-16 |
| VERIFIED (PG, Corporations suite) | (3b) an unpaid station office is impounded at due time (items to the Impounded location, folder deleted, office ended); the helper is shared with the Director un-rent | `Unpaid_station_office_is_impounded...`; the due job's Host wiring is not covered by a Host test |
| OPEN | (3c) station bills rented before this change have no creditor: their due job does nothing (no eviction, no notice) | migration of the creditor is open |
| BUILT, NOT TESTED | (3d) rent-due and eviction notices (type 10, group Bills) through `PersistenceTransactionMailbox.WriteNotificationsAsync` after the commit | no test reads the notification back; pre-due reminder not built |
| VERIFIED (PG, Space MobileObservatory 9/9) | (1a) Mobile Observatory refused within 100 km of a control tower (SDE group 365 structure records) and placed beyond it | `A_control_tower_within_100_km...`; no control tower deployment exists (gap) |
| OPEN | (1b) control tower deployment (the minimal deployable kind the brief asks for): needs a sim ball kind, protocol change and deploy RPC | not built |
| VERIFIED (PG, Space MobileObservatory 12/12 filter) | (1c) deadspace: no observatory anchor within 1e9 m of an ST-11 site (EveJS DEADSPACE_SITE_RADIUS_METERS); mission pockets not positioned in the sim | `An_observatory_is_not_anchored_in_a_deadspace_pocket...`; DEVIATIONS ST-16 |
| VERIFIED (PG, Host 2/2) | (2a) destruction loot: fitted modules, fuel, cargo and the Quantum Core into one Abandoned Container at the structure's position; rigs destroyed; the container placed in space after commit | `Destruction_loot_moves...`, `Destruction_loot_is_atomic...`; the Space placement of the container is not tested |
| VERIFIED (PG, Host 4/4 with the wrap fake; real wrap not tested) | (2b) asset safety first: each owner offered to the wrap in the destruction transaction; the loot takes what the wrap leaves (FLEX, wormhole, Pochven, refused) | `Destruction_wraps_each_owner_first...`; DEVIATIONS ST-16 |
| VERIFIED (PG, Space UndockContraband 1/1 new test, undock and jump filters 137 and 82 passed) | (4) customs fine (SDE fineByValue x base price, charged up to the balance) and confiscation of the matching cargo at undock and stargate jump | `Penalty_charges_the_fine...`; DEVIATIONS ST-16. OPEN inside it: smuggling chance (attribute 445) not read, so no inspection roll; standing loss has no standing system; durable penalty job not used; client presentation not verified |


## 51. Structures phase ST-17: sovereignty windows, command nodes, power budget, ship burst projectors (2026-10-16)
| Status | Row | Notes |
|---|---|---|
| VERIFIED (pure, PostgreSQL, Host registration) | (b) custom windows: a hub's centre hour and length, a change takes effect after the next window; the alliance default inherited by a captured hub; the midpoint lock of a day's length; the reinforcement exit under the custom window; a refused cycle stores nothing | DEVIATIONS "Sovereignty vulnerability window: custom windows, inheritance, the midpoint lock (ST-17 b)". Tests: `EntosisWindowTests` 7, `EntosisCaptureServiceTests` (window rows) 3. Open: no client RPC for an owner's window (none in eve-logic); client view not verified. |
| FIXED (ST-9 c regression) | (b) attacker scoring is bounded to the in-progress cycle of a link; a module that stopped without an end report no longer scores the gap | `A_link_without_an_end_report...` (`EntosisWindowTests`). |
| VERIFIED (PostgreSQL, Host, pure) | (a) command node stage: the stage starts at the reinforcement exit (4 nodes in the constellation, further spawns), node links and capture with the entosis engine (signed progress), the tally (nine attacker or six defender captures), the defended outcome (invulnerable until the next window), the destroyed outcome: the hub leaves the sovereignty records (durable job, retried), the hub ball and the nodes leave space, the notices 161 and 162 reach the defending members through the mailbox | DEVIATIONS "Command node stage ... (ST-17 a, continued)". Tests: `EntosisCaptureServiceTests` (stage, tally, destruction job retry), `EntosisCommandNodeTests` 5, `SovHubClaimTests` (loss writer), `EntosisOwnerNotificationTests` (161/162). OPEN: a new neutral hub after a loss (the anchor is not built), the drop roll of the lost hub upgrades and fuel, the regeneration time (wiki 2015 maximum), client view and live Host with the simulation (MITM). |
| PARTIAL (decision needed) | (c) sovereignty upgrade power budget: `IsCynoSuppressionOnlineAsync` is derived from the EVE.js allocation (`SovUpgradeBudget`), star power 500 (EVE.js fallback) and the active skyhooks' workforce; the upgrade's numbers are the wiki's (250 power, 4 500 workforce); other upgrades use EVE.js placeholders | DEVIATIONS "Sovereignty upgrade power budget ... (ST-17 c)". Tests: `StructureCynoSuppressionTests`, `StructureCompositionTests`. The consequence: the jammer is off in a system without an active skyhook of at least 4 500 workforce (EVE.js's rule with the wiki numbers), which conflicts with the "not off everywhere" requirement; decision needed. Transfers between hubs and the planet power of skyhooks not ported. |
| VERIFIED (Rust, C# build and burst/module tests; Host ship branch not end-to-end) | (d) ship-fitted burst projectors (27678, 40635, 40636, 40696-40699) on the structure burst engine: the pilot's ship is the source, the per-pulse Host grant reaches the ships in reach except its own, the ECM family follows the ECM burst's high-security rule, crimewatch as for smartbombs; 41350 stays refused by type policy | DEVIATIONS "Ship-fitted burst projectors (ST-17 d)". Tests: `a_ship_fitted_burst_projector_asks_the_host_for_each_pulse_and_spares_its_own_ship` (Rust), `ModuleEffectsTests` manifest. Open: the Host ship branch end to end, the high-security refusal test, the client FX. |

## 59. Structures phase ST-18: customs inspection roll, standing loss, durable penalty job, fine ref type (2026-10-16)

Handoff: `docs/project/structures-phase/handoff-ST-18.md`. Decisions and sources: DEVIATIONS "Structure ST-18 (2026-10-16)". Closes the four ST-16 "Not done" items of section 58 (1-4).

| Status | Row | Notes |
|---|---|---|
| VERIFIED (PG, Space UndockContraband 6/6) | (ST-16 1) inspection roll: the smuggling chance is attribute 445 of the pilot's implants and skills; a roll below it evades (no penalty, cargo kept); the roll is seeded by the inspection | `The_inspection_roll_is_seeded...`, `An_inspection_that_rolls_below...`; DEVIATIONS ST-18 |
| VERIFIED (PG, Skills 2/2 filter) | (ST-16 1) the attribute 445 reader: an implant's modifier reaches the pilot; zero without an implant | `Smuggling_chance_is_the_attribute_445...` |
| VERIFIED (PG, Space 6/6) | (ST-16 2) standing loss: SDE standingLoss through EveJS calculateStandingsByRawChange into the existing `standing` relation, with a standing-history entry (event 126) | `Penalty_is_settled_by_the_durable_job...`, `The_standing_moves_by_the_raw_change...` |
| VERIFIED (PG, Space; Host 1/1) | (ST-16 3) the penalty settles through `contraband.penalty-settle` on DurableScheduler in its own transaction; a waiting job survives a restart and settles once; the Host's own runner settles a scheduled penalty once | `A_scheduled_penalty_survives_a_restart...`, `A_penalty_scheduled_on_the_host_scheduler...` |
| VERIFIED (PG, Space) | (ST-16 4) the fine's journal ref type is 51 (eve-logic refContrabandFine), counterparty and reference the faction | `Penalty_is_settled_by_the_durable_job...` (journal line) |
| BUILT, SHORTCUT | the penalty runs inline after its scheduling; if that run fails the undock goes on and the runner settles later, with no client removal notice until the next inventory sync | DEVIATIONS ST-18 |
| NOT CHECKED | graphical client: the standing window, the fine's journal line and the removal notice | needs the graphical client |

## 59. Structures phase ST-19: the ST-16 open items (mission pockets, control towers, destruction wrap, creditor migration) (2026-10-16)

Handoff: `docs/project/structures-phase/handoff-ST-19.md`. Decisions and sources: DEVIATIONS "Structure ST-19 (2026-10-16)". Protocol 73 (kind 92), migration 0015, ADR 0013 addendum.

| Status | Row | Notes |
|---|---|---|
| VERIFIED (PostgreSQL, Host) | (1) the destruction wrap of the composition is driven over the real asset safety read model; the test found and fixed K-space read as wormhole (`IsWormholeClass`, 7/8/9 are K-space) | `Destruction_wrap_of_the_composition_is_the_real_asset_safety_read_model...` (`StructureUnanchorDropTests`) |
| VERIFIED (PostgreSQL, Space) | (2) a mission pocket (dungeon instance origin) is a deadspace anchor for the Mobile Observatory; the pocket is not a simulation body | `A_started_dungeon_is_a_deadspace_anchor_at_its_origin_in_its_system` (`DungeonWaveTests`) |
| VERIFIED (PostgreSQL, Space, Rust, codec) | (3) control towers (group 365) launched from a ship: placed unanchored, anchored after the SDE anchoring delay (30 min), never end by time, a Mobile Observatory is refused within 100 km, restart keeps the anchored state; Rust `a_control_tower_is_in_space...`, simlink fixtures 204 valid / 82 invalid | `ControlTowerHostTests` (3 tests) |
| PARTIAL | (3) no client RPC removes a deployed tower (removal is `RemoveControlTowerAsync`, the item is deleted, not returned to the hold); fuel, modules and the tower's own interface are not built | DEVIATIONS "Structure ST-19" |
| VERIFIED (PostgreSQL, Corporations) | (4) one-time creditor migration: a station rent bill taken before ST-16 gets the station owner as creditor once; a bill with a creditor is untouched | `StationBillCreditorMigrationTests` |
| PARTIAL | (4) the rent due job running after the migration is not exercised by a test | DEVIATIONS "Structure ST-19" |
| FIXED (ST-19) | the live script isolates `EVE_CONTRABAND_SCHEMA` (the ST-16 customs schema was not scratch or dropped) | `LiveScriptIsolationTests` |

## 60. Structures phase ST-20: live refusals of the structure services, the slow and skill-speed runs, the full Host suite (2026-10-16)

Handoff: `docs/project/structures-phase/handoff-ST-20.md`. Lane eve_p4 (live port 26103). The module `tools/headless-client/tests/test_structures.py` seeds its own Astrahus (fixture `seeded_structure`) and the default live run in `tools/dev/run-eve-live.sh` includes the file.

| Status | Row | Notes |
|---|---|---|
| VERIFIED (live) | (1) `test_structures.py` runs nine scenarios against the stack, each with the answer the reference documents: `structureDeployment.Anchor` from an NPC-corporation pilot is `DropNeedsPlayerCorp` (EVE.js `sovPlayerDeployment.js`); from a member of a player corporation without the role, `CrpAccessDenied` with `Insufficient roles`; `structureControl.GetStructurePilot` of an uncontrolled structure is None; `TakeControl` from space is the docked notice; `BoardStructure` of an unknown structure is `TargetingAttemptCancelled`; `hackingMgr.QuitHackingAttempt` and `ClickedOnTile` with no attempt are None, the tile click sends the ACK; `structureDeliveries.Deliver` from space is the inside notice; `moonExtractions.StartNewExtraction` with duration 0 is `MoonminingEventTooShort` (DEVIATIONS, ST-6a) and from space `StructureDefenseDenied`. Result on eve_p4: 11 passed (the two ST-14 tests and the nine). | `tests/test_structures.py`; `handoff-ST-20.md` |
| FIXED (server) | (1) `StructureControlBackend`: the refusals follow the reference. The docked check comes before the structure's setting (EVE.js `TakeControl`), the system check before the setting, and the reasons `STRUCTURE_CONTROL_DENIED`, `STRUCTURE_NOT_FOUND` and `NOT_DOCKED_IN_STRUCTURE` are sent as `StructureDefenseDenied`, `TargetingAttemptCancelled` and the reference's notice. By code reading, the old order answered `STRUCTURE_CONTROL_DENIED` first, so the docked scenario could not pass; that was not exercised before the change. | `StructureControlBackend.Denied`, `Eve.Host.Tests` 373 of 373 after the change |
| VERIFIED (live, ST-21) | (1) `test_structures.py` on eve_p4 (19 scenarios, port 26103): `TakeControl` by a docked pilot is None and names the pilot (`GetStructurePilot`), with `OnShipCosmeticsChanged`; `ReleaseControl`; `BoardStructure` from space is None, and `EjectFromStructure` to a capsule ends the control; a second pilot's `TakeControl` displaces the first (EVE.js `assumeStructureControl` relinquishes the previous controller with reason `override`; the reference refuses only a session that is not the recorded controller); `structureDeliveries.Deliver` moves the sender's hangar items to the recipient's deliveries bay (flag 173) and the recipient gets `OnNotificationReceived` of type 199 (StructureItemsDelivered); `structureCargoDelivery.DropOffItems` answers the moved ids; `moonExtractions.StartNewExtraction` by the controlling pilot answers the ten-field extraction and sends `OnMoonExtractionUpdate`, and a second start answers the same extraction; `structureDeployment.Anchor` by a Director of a corporation eight days old in a legal system (30000903) consumes the item, creates the structure in `AnchorVulnerable` with the corporation's default profile, places it (the ballpark shows it) and sends `OnStructureStateChanged`; `Unanchor` and `CancelUnanchor` are None once the structure is online. | `test_structures.py`, `docs/project/structures-phase/handoff-ST-21.md` |
| FIXED (server) | (1) moon service access: `SettingIdForService` had no entry for service 14 (MOON_MINING), so every moon start was refused `StructureDefenseDenied`. Service 14 is admitted through setting 17 (EVE.js `structureServiceAuthority.js` SERVICE_ACCESS_SETTING_BY_ID). | `PersistenceStructureDirectoryReadModel.Part2.cs`; `HostServerTests.Moon_mining_service_is_admitted_by_the_control_setting_of_the_owner_corporation` |
| FIXED (server) | (1) moon rig rules read the structure record (type, owner, system) instead of its inventory item: an anchored structure has no item row (the anchor consumes the item it came from), so every moon start of an anchored refinery threw `Moon rig rules require the structure item`. | `MoonRigReadModel.cs`; `MoonRigReadModelTests` (the fixture creates the structure record) |
| FIXED (server) | (1) boarding from space refused every medium structure: the gate `rigSize >= 2` treated the Astrahus (attribute 1547 = 2) as flex. EVE.js `boardStructureControlFromSpace` has no size gate; the gate is removed, and the in-space, not-in-structure and system checks keep their reasons (`NOT_BOARDABLE_FROM_SPACE`). | `StructureControlBackend.cs`; `StructureControlBackendTests.A_medium_upwell_structure_is_boarded_from_space_as_the_reference_allows` |
| OPEN | (1) live: `EjectFromStructure` back to the pilot's own ship (restoreShipID > 0) after a boarding from space; a successful anchor in a second system needs a pilot placed by the seed (done here by relocating the recorded character to a station of system 30000903); the 300 s wait for a warp to end uses `CmdStop`. | `handoff-ST-21.md` |
| VERIFIED (live) | (2) the live suite with `EVE_LIVE_SLOW=1` (the default list plus `test_structures.py`, eve_p4, port 26103): exit 0, one skip, the skill-speed test, which needs its own host run. No failure to fix. | `tools/dev/run-eve-live.sh` |
| VERIFIED (live) | (2) the skill-speed test `test_login_to_character.py::test_a_queue_that_finishes_while_the_client_watches_is_told_level_by_level` with the host at `EVE_SKILL_TRAINING_SPEED=60000`: 1 passed (not skipped). | `test_login_to_character.py` |
| VERIFIED (tests) | (3) full `Eve.Host.Tests` on eve_p4: 373 passed, 0 failed, 0 skipped, before the control change and again after it. | `dotnet test tests/Eve.Host.Tests` |

## 61. Structures phase ST-23: control tower fuel and unanchoring (2026-10-16)

Built and tested headless on eve_p4 (PostgreSQL): the tower's fuel per hour by SDE size and race fuel block, the hourly job that pays or takes it offline, the `posMgr.UnanchorStructure` RPC (offline and corporation rules, the SDE's one-hour delay), and the unanchored item kept and dropped in space instead of deleted. Rules and differences: DEVIATIONS "Structure ST-23". Tests: `ControlTowerHostTests` 4/4, `PosMgrServiceTests` 5/5 (Core); full Space 928/928.

Open:

- Manual online (the pilot's online command and its online fuel charge), and any client surfacing of the tower's online state.
- The client's fuel-bay access to a tower's container and the owner's fuel-bay notifications (no `OnItemChange` on a fuel change).
- The scoop of an unanchored tower from space: built on the unanchor-drop marker, not verified on the graphical client or live.
- The anchoring charter (SDE purpose 1) and the reinforcement fuel (purpose 4); the tower's modules.
- Mission pockets: the body is in the scene (protocol 74) and the observatory check reads it; the pocket's warp rules (EveJS `deadspaceWarpPolicy` A and B) are not in the simulation (DEVIATIONS, ST-23).
- Live: the own-ship eject, the moon fracture, the completion and the owners' rows are live (`test_structures.py`, ST-23). The tower's online state and fuel-bay notices are not on the client yet.

## Character phase S2 (2026-10-16)

Packages CH-11 (partial), CH-2, CH-5, CH-7, CH-9 of `docs/project/character-phase/plan.md`. Differences and sources: DEVIATIONS "Character phase S2".

- **CH-2 (done, lane eve_p3).** Daily Alpha Injector: the 11:00 UTC downtime rule and `alphaInjectorMgr.GetNextAvailableInjection` (`Eve.Services.Skills/AlphaInjectorMgrService.cs`). Confirms the injector tier calculation (`DiminishedInjectorSp`, tests `Injector_preview_uses_per_item_large_and_small_bands_and_non_diminishing_uses`, `Daily_alpha_injector_is_limited_to_alpha_characters_and_once_per_day`, `Daily_alpha_injector_is_spent_until_the_next_11_utc_downtime`, `Daily_alpha_clock_answers_zero_then_the_next_downtime_after_a_use_and_keeps_it_across_a_restart`). Live: `tools/headless-client/tests/test_alpha_injector_live.py`.
- **CH-5 (done, lane eve_p3).** Login milestone: `MilestoneMgrService`, `LoginMilestoneStore` (migration 0008). Tests: `LoginMilestoneStoreTests`, `MilestoneMgrServiceTests`. Live: `tools/headless-client/tests/test_login_milestone_live.py`.
- **CH-7 (done, lane eve_p3).** Atomic population seat on `SelectCharacterID`. Tests: `PopulationCapTests` (`Simultaneous_entries_into_the_last_room_admit_exactly_one`, `A_seat_is_taken_in_the_same_step_as_the_room_check_and_is_given_back_when_disposed`). Live: `tools/headless-client/tests/test_population_cap_live.py`.
- **CH-9 (done, lane eve_p3).** Achievement tracker writes and reads, no golden fixture: `AchievementTrackerStore` (migration 0009). Tests: `AchievementTrackerStoreTests`, `AchievementTrackerWriteTests`. Live: `tools/headless-client/tests/test_achievement_tracker_live.py`.
- **CH-11 (implemented; live walk covers the implant lifecycle).** `PilotLearningBonus` refreshes SDE dogma modifiers from installed implants and active boosters, and `CharacterTraining` adds those bonuses to the learning attributes used for skill rates. Implant mutations publish item, implant-cache, attribute and brain notifications. Coverage: `PilotAugmentationTests`, `PilotLearningBonusTests`, `CharacterTrainingLearningTests`, and live `tools/headless-client/tests/test_implant_live.py` (booster training-rate change, implant install/refusal/removal and notification order). The live test does not isolate the implant's contribution to the training rate or verify it across a host restart.
- **CH-17 (not started in S2).** Expert systems: the catalogue is EveJS's static table (`tools/DatabaseCreator/staticTables/expertSystems/data.json`, from the client SDE 3365090; the pinned SDE copy has no expert system data).

## Character phase S4 (2026-10-16)

Packages CH-11 (done, eve_p3), CH-17 (done, eve_p3), CH-16 (partial). Differences and sources: DEVIATIONS "Character phase S4".

- **CH-11 (done, lane eve_p3).** Implants and boosters act on the training rate and are walked live: `dogmaIM.InjectImplant` (Plug In), `DestroyImplant` (Unplug) and `UseBooster` (Consume) are served by `Eve.Services.Dogma/DogmaIMService.Augmentations.cs` and `PilotAugmentationRpc.cs`, with `Eve.Ships/PilotImplants.cs` and `PilotLearningBonus.cs`. Tests: `CharacterTrainingLearningTests` (rate, rebase, refresh), `PilotLearningBonusTests` (Eve.Ships.Tests), live `test_implant_live.py` (booster rate 1.5 on Gunnery; plug-in, refusal of a second implant for the slot, unplug; notification order). Not done: the Omega rule for certain implants (wiki, unnamed); the booster expiry changes the rate at the next read, not at the lapse (DEVIATIONS S4). The implant and booster outcome after a host restart is read from PostgreSQL but no restart test covers it.
- **CH-17 (done, lane eve_p3).** Expert systems: `expertSystemMgr` (`Eve.Services.Skills/ExpertSystemMgrService.cs`): `GetMyExpertSystems`, `ConsumeExpertSystem`, `RemoveMyExpertSystem`; the catalogue `Eve.Skills/Data/expert-systems.v1.json` (EveJS data, generated by `tools/dev/gen-expert-system-catalog.py`); table `character_expert_systems` (skills migration 0008); the granted levels in `GetSkills` and in the pilot's dogma; the expiry job `skills.expert-systems.expire` (10 s). Tests: `ExpertSystemTests`, `ExpertSystemStoreTests` (Eve.Skills.Tests), live `test_expert_system_live.py` (consume, list, granted skill, remove, lapse with `OnExpertSystemExpired`). Not done: the persistent expiry notice for an offline pilot (type 253), the fitting checks on the granted level (not checked).
- **CH-16 (station-clone jump implemented; other destinations remain open).** The station-clone jump relocates the session, charges the activation fee, starts the cooldown, applies replacement-clone rules and ends normal boosters while preserving cerebral accelerators. Host restart and live station-clone coverage are recorded below. Structure clone destinations are not supported by this path; pod-death respawn remains at the home station. Sources and remaining destination limits are recorded in DEVIATIONS, "Clone jumps (CH-16)".
- **Not this package (found, not fixed).** `dotnet/tests/Eve.Services.Core.Tests` does not compile on `origin/main`: `UserSvcServiceTests.cs` `InMemoryMultipleTraining` implements `ActivateCertificatesAsync` and not the `IMultipleTrainingCallbacks.ActivateItemAsync` of CH-6 (`MultipleTrainingCallbacks.cs`). The Core test project therefore runs nothing until that double is updated.


## Character phase S8 (2026-10-16): CH-16 closure, restart and fitting checks

Lane eve_p3, branch ch-s8. Results (nothing skipped unless named):

- **CH-16 clone jump (done for the jump).** Live `tools/headless-client/tests/test_clone_jump_live.py` passed on eve_p3 (exit 0) with the booster rule; `test_implant_live.py` passed in the same run. Host: `CharacterHostTests.Clone_jump_moves_the_body_charges_the_fee_starts_the_cooldown_and_keeps_all_of_it_through_a_restart` and `A_clone_jump_ends_the_normal_boosters_and_the_cerebral_accelerator_goes_on_with_the_pilot` pass. The clone-jump live helper `dbseed.isk_of` now reads the main wallet division (account 1000).
- **CH-16 pod death (checked 2026-10-19).** The recorded session mismatch and watcher notification differences no longer reproduce: `tools/headless-client/tests/test_death.py` passed with `EVE_LIVE_SLOW=1` on eve_p3/port 26112, comparing the victim and watcher flows with `oracle/fixtures/scenarios/death.json`. `CharacterSessionRelocation.Docked` sends `stationid2` with the station transition. The existing PostgreSQL unit coverage remains `Eve.Ships.Tests` `DeathTests` and `Eve.Services.Space.Tests` `PlayerDeathTests` (13 each).
- **Booster lapse (partial; status checked 2026-10-19).** `CharacterHostTests.A_skill_booster_lapses_at_its_expiry_through_the_durable_job_and_an_online_pilot_is_told` passes on eve_p3 (1/1): the durable job removes the expired booster row and item and notifies the online pilot. Open: training progress across expiry is not settled at the old rate before the booster is removed (DEVIATIONS, "Booster expiry"). No headless-client expiry transition is recorded.
- **Implant and expert system restart (done, A3).** `CharacterHostTests.An_implant_and_an_expert_system_are_kept_through_a_host_restart_and_the_expert_grant_still_applies` passes.
- **Fitting with expert grants (done, A4).** `Eve.Ships.Tests` `ExpertGrantFitTests` passes: Damage Control II is refused for Hull Upgrades, fits while Core Ship Operations (57208, level 5) is active, and is refused again after the lapse.
- **CH-20, CH-21, CH-22 (not started in S8 as of this entry).** See the S8 handoff.

- **CH-20 public gateway (done for Requests/Send, Ping and Events/Publish).** Host: `dotnet/src/Eve.Host/PublicGateway/` (`PublicGatewayServer.cs` Kestrel h2c with the routes of EveJS, `GatewayEnvelope.cs` envelopes and gRPC framing, `ProtoWire.cs` protobuf wire codec, `SkillPlanWire.cs` message codec). Enabled by `EVE_PUBLIC_GATEWAY=host:port`. Tests: `PublicGatewayTests` (Eve.Host.Tests, 2 passed on eve_p3: real HTTP/2 client, status and trailers, persistence across a Host restart, the limit, no identity, unknown namespace). Open: TLS, Notices/Consume (no notices yet), identity check (see DEVIATIONS S8).
- **CH-21 skill plans (done).** `PgSkillPlanStore` (`dotnet/src/Eve.Skills/PgSkillPlanStore.cs`, migration `0009_skill_plans` in `PgSkillStore`). Live: `tools/headless-client/tests/test_skill_plan_live.py` passed on eve_p3 (grpcio over the live gateway, character from the client protocol, rows checked in PostgreSQL). Open: prerequisite canonicalization (DEVIATIONS S8).
- **CH-22 character titles (not started in S8 as of this entry).**
- **Open failures from S8:** `A_skill_booster_lapses` (job stays running: `DurableScheduler.ExecuteAsync` finishes without a state change, not diagnosed); `test_death.py` live walk (see the pod-death note above). Operational: a Host killed by SIGTERM can outlive the live script (it held the gateway port); a re-bind of the same gateway port within about a minute fails with address in use (TIME_WAIT), so each live run uses a fresh port.

- **CH-22 character titles (done for owned, worn and wearing).** `CharacterTitleStore` (`dotnet/src/Eve.Characters/CharacterTitleStore.cs`, migration `0013_character_titles`), `TitleGateway` (`dotnet/src/Eve.Host/PublicGateway/TitleGateway.cs`). Tests: `PublicGatewayTests.Character_titles_are_listed_worn_and_taken_off_through_the_gateway_and_survive_a_restart` passed (eve_p3); live `tools/headless-client/tests/test_title_live.py` passed on eve_p3 (exit 0) with the skill plan walk. Open: the definition CDN checkpoint, title notices, the reward path that grants titles (CH-9 claims), and the showcase namespace (DEVIATIONS S8).

## Character phase S9 (2026-10-17): booster lapse, pod death, gateway identity and notices

Lane eve_p3, branch ch-s9 (merged with origin/main). Results (nothing skipped unless named):

- **Booster lapse (done).** `CharacterHostTests.A_skill_booster_lapses_at_its_expiry_through_the_durable_job_and_an_online_pilot_is_told` failed (2 of 2 runs): the composed host starts the scheduler runner, which claims a job when it is scheduled, so `RunDueAsync` claimed nothing and the test read the job while the handler still ran. A diagnostic log in `ExecuteAsync` made the same run pass, which confirms the race. The test now waits, bounded, for the job to be removed, as the multiple-training expiry test does. `CharacterHostTests` 15 passed (eve_p3, one serial class run).
- **Pod death: the suicide walk (done).** Live `tools/headless-client/tests/test_death.py::test_a_pilot_who_destroys_its_own_ship_is_left_in_a_capsule_as_in_the_reference` passes (eve_p3, EVE_LIVE_SLOW=1). Root causes: `UndockFromAsync` cleared `stationid` but not `stationid2`, so the death relocation announced nothing (EveJS clears it silently at undock; now the same, and the death announces it from nothing as the recording does); the survivor and the wreck entered the simulation before the session change and the cosmetics (now after, as the recording). The pod step compares the session change in full. `Eve.Services.Space.Tests` `Death` 15 passed.
- **Pod death: the hull walk (open).** Live `test_a_hull_that_blows_up_beside_two_pilots_is_seen_as_it_was_in_the_reference` FAILS. Diagnosed, not fixed: (1) the two pilots undock from the same undock point (`scene/docking.rs` `undock_place`), and `collision.rs` `bump_ships` bumps them on every tick, so the victim and the watcher get `SetBallPosition/SetBallVelocity/SetSpeedFraction/Stop` at about 4 Hz where the recording has none in that step (the cause of the repeat is not isolated); (2) the hull's arrival is sent 0.25 s after the command, the recording's 3.36 s after it.
- **Gateway identity (done; signature open).** `GatewayAuthority`: a Requests or Notices request names a character only when the bearer token's account (`USER:EVE:<id>`) owns it; no token 401, another account's character 403. Tests: `PublicGatewayTests.A_request_without_a_token_or_for_a_character_of_another_account_is_refused`; live `test_skill_plan_live.py` and `test_title_live.py` pass with the account token. Open: the token signature is not checked (no identity key in the repository).
- **Notices/Consume (done for characters and accounts).** The stream stays open; routed by character and by account (`NoticeHub`). Test: `PublicGatewayTests.A_title_the_character_puts_on_is_announced_on_the_notice_stream_of_its_account`. Open: the solar system, corporation, alliance and application-instance target groups.
- **Character title notices (SetNotice done; GrantedNotice and RevokedNotice open).** `SetNotice` on equip and unequip (EveJS `title-set`). Open: `GrantedNotice` and `RevokedNotice` need a reward grant.
- **Reward grants (open).** EveJS `achievementState.js` `claimReward` grants titles (and items) through the achievement reward claim; eve.rs has no reward claim through the gateway and `CharacterTitleStore.GrantAsync` has no caller. The achievement claim is a feature of its own (CH-9 claim path), not built here.
- **Skill plan prerequisite canonicalization (done).** `SkillPlanCanonical` (EveJS `canonicalizeSkillRequirements`, applied on create and on the requirement update). `Eve.Skills.Tests` `SkillPlanCanonicalTests` 2 passed; live `test_skill_plan_live.py` passed (eve_p3) with the canonical levels.
- **TLS (not served, DEVIATIONS S9).** The client uses TLS only for the discovered production address; eve.rs serves h2c on `EVE_PUBLIC_GATEWAY`. A TLS proxy in front is not in this repository.
- **Full .NET suite (S9 run, merged tree):** {SUITE}


## Acceleration gates (ACCEL-1, open)

- Live headless walk through a multi-room site with a MITM capture (eve_p2 lane).
- Gate lock state in the slim item (the client's gate display).

## GM tools (2026-10-18): state

Done: every command the Insider window and its helper service send is served (`docs/project/GM_QA_DESIGN.md`): `/givestarterskills`, `/randomizeskills`, `/skillscopy` (G20), `/dogmaupdate` (G21), `/killright` (G22), `/asteroid rewards` (G23), `/skyhook state` (G24), `/entity deploy` (G25). Each has database-backed or unit tests, recorded in DEVIATIONS G20 to G25. The Insider walk ran on the eve_p2 lane against eve.rs and EVE.js through the MITM; the call sequences match, and the answers that differ are in DEVIATIONS G26 (MITM_COMPARE.md, "Insider walk").

Open:
- `/missioncomplete` (no placeholder mission in eve.rs, DEVIATIONS G9) and `/pi program` (the forced install needs the client's own inputs, DEVIATIONS G10): not built.
- DONE (G4 follow-up): migrated old standing rows and their history to owner → character direction (`Eve.Relations` migration `0002_standing_direction`); direct database tests cover owner-to-pilot and corporation standing reads plus the reprocessing tax quote. See `docs/reference/compat/DEVIATIONS.md`, G4.

## Simulation and link performance (2026-10-19)

The simulation writer now appends encoded frames directly to its batch buffer. The link frame golden tests pass; same-run median 64-frame batch time changed from 11.29 µs to 4.45 µs with the same 704 output bytes. See [PERFORMANCE.md](PERFORMANCE.md) for the command and benchmark. A stationary-pilot `Sim::poll` baseline now covers 1, 16 and 64 pilots. Phase attribution and measurements of spatial queries, marshal, SDE loading and process memory remain open.

## Owner decisions recorded 2026-10-19 — open implementation work

- **Pochven bounties:** No bounty payout for Triglavian NPCs in Pochven; leave loot and drops unchanged. See DEVIATIONS.md, “Owner decisions (2026-10-19): Pochven bounties and wormhole Local.”
- **Wormhole Local:** Delayed mode; only characters who have spoken appear in the list. See the same DEVIATIONS entry.
- **Planetary customs offices (Q-24):** Implement and test owner-set rates by standings tier, standing discounts, import/export charging, and corporation wallet payment according to the Wiki sources recorded in DEVIATIONS.md. PI_DESIGN.md and the existing partial tax path need alignment.
- **ESS:** Extend tools/headless-client/tests/test_ess.py through a successful main-bank link, reserve-bank unlock/payout, and key-consumption path. /ess grant now selects a hull from SDE type list 231; no active link-module type exists in the official SDE.


## Chat transport inventory (2026-10-19)

The reviewed V24.01 contract and coverage matrix are recorded in `docs/reference/compat/DEVIATIONS.md`, “V24.01 chat transport inventory”. The client uses XMPP for room discovery, membership, private conversations, presence, group messages, invites, and MOTD subjects; it has no Macho `LSC` callsite or `OnLSC` consumer. eve.rs currently serves the Local protobuf requests/notices and Local/group channel behavior only. Open work: connect the existing `chatAuthenticationService.GetAuthenticationToken` to an XMPP room transport, then implement room discovery, private conversations, ignore/block enforcement, channel MOTD, XMPP staff presence, channel administration RPCs, native LSC method shape compatibility as a separate surface, and a two-character headless live walk covering K-space Local, corporation chat, private conversation, and delayed J-space Local.

## Research tasks from 2026-10-19 reports

- **Security agent:** add regression cases for zero-value donation response projection, idempotent duplicate requests, pre-SessionInit calls, admin-only service authorization, and bounded nested substreams.
- **Performance agent:** add proposal-labeled load scenarios and link them from [PERFORMANCE.md](PERFORMANCE.md); retain measured baselines separately from target SLOs.
- **Protocol/security agent:** specify parser state progression and configurable frame/string/list/nesting bounds; include partial and coalesced TCP frames.
- **FW agent:** reconcile Battlefield LP against the June 16 checkpoint, blockers, pods, capture points and respawn.
- **CRAB agent:** compare reported phases, timers, resistances, CEQ/interference, reward window, HP, deployment and BPC data to code and release sources.
- **Insurgency agent:** map stage effects to 24.01 implementation and record thresholds/gateway protocol gaps.
- **Weapon-bank agent:** inspect client grouped-weapon RPC contracts and state transitions.
- **Static-data agent:** check June 12/16 NPC, capacitor recharge, Pyroxeres grade-0 yield and NPC mass changes against pinned SDE.

## 62. Scheduler, war and inventory scale, structure identities, customs client contract (2026-10-19)

| Status | Row | Notes |
|---|---|---|
| VERIFIED (PostgreSQL measurements) | Item 9: due-job scheduler throughput and site expiry storm | `Eve.Scheduler.Tests.A_burst_of_due_jobs_finishes_with_bounded_scheduler_latency`: 512 due jobs completed below 20 seconds with concurrency 8 and batch size 64. `CosmicSiteDirectorTests.A_burst_of_expired_sites_across_thirty_two_systems_finishes_within_twenty_seconds`: 128 site expiries across 32 systems completed below 20 seconds and each system regained four sites. Dungeon wave-progression load remains open. |
| VERIFIED (PostgreSQL measurements) | Item 10: war lookup and item moves | `RuntimeTests.A_war_check_with_one_thousand_unrelated_wars_has_bounded_latency` checks the indexed owner-pair query against 1,000 unrelated wars and an active match. `ItemMutationStoreTests.Moving_items_uses_bounded_row_lookups_with_ten_thousand_items_present` measures 100 row-locked moves with 10,000 rows present. Ordinary moves lock by item primary key; merges acquire both item locks in ascending id order. Mixed-operation contention remains open. |
| VERIFIED (PostgreSQL Host regression) | Item 11: structure item identity | `StructureAnchorAuthority` consumes the inventory item and inserts the structure under the same id in one transaction. `StructureAnchorTests.An_item_id_already_taken_by_a_structure_is_refused_and_the_item_stays` verifies duplicate live-structure ids are refused and the source item remains. Concurrent sovereignty save/restart stress coverage remains open. |
| BUILT (target SDE, Host tests; graphical presentation open) | Item 14: customs inspection | V24 `eve/client/script/ui/shared/info/infosvc.py` exposes faction contraband legality and `eve/client/script/ui/shared/item.py` displays the contraband marker. EveJS checks undock and destination-system gate arrival; eve.rs uses the matching SDE `contrabandTypes` rules for fines, standing loss and confiscation. EVE University `New Slaves` documents a fine and faction-standing loss. `UndockContraband` regression tests cover the penalty settlement. `SpaceServiceTests.A_gate_arrival_inspects_the_ship_in_the_destination_system_only_after_the_jump_succeeds` verifies the destination-system scan runs once after an accepted jump and not after a refusal; the graphical notice, wallet line and item removal view remain open. |

The war and move audit found no global in-process inventory or war lock. Direct war checks use the relation store's `(kind, subject, object)` uniqueness index and object index; inventory moves are keyed by item id. No runtime behavior change was required by these scale checks. Rechecked on 2026-10-19 against eve_p3: the 512-due-job scheduler burst and 32-system/128-site expiry storm each passed their 20-second assertions; the 1,000-war lookup, 10,000-item move benchmark, structure collision rollback, contraband penalty settlement, and shared Host penalty runner each passed (1/1). Mixed-operation contention, concurrent sovereignty save/restart, dungeon wave progression, and graphical-client presentation remain open.
