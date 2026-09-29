# Deviations from the reference server

The reference (EVE.js) is matched by default (`docs/adr/0001-external-compatibility-only.md`).
A deliberate difference goes here, with the evidence that the client cannot tell or that it
is better off, and how it is tested. A behaviour is either:

- **contract**: the client can observe it; eve.rs matches the reference;
- **tolerated**: the client can observe it but accepts a better behaviour; eve.rs may differ,
  and the difference is listed below;
- **internal**: the client cannot observe it; eve.rs is free.

## Register

| # | Behaviour | Reference does | eve.rs does | Evidence | Status |
|---|---|---|---|---|---|
| 1 | Password check at login | Accepts any password by default (`devSkipPasswordValidation` is true) and creates an account on first use | Checks the password hash by default; an unknown user name still creates the account with the hash it presented; `EVE_DEV_SKIP_PASSWORD=1` restores the reference | The client always sends the hash of the password typed, so only a wrong password can tell the two apart (it is refused, as a real server does) | tolerated (S1); `tools/dev/run-eve-live.sh` runs the wrong-password test |
| 2 | Session ids | `ms * 15 + clientID % 15`, which can repeat within a millisecond | The same value, moved up past the last one issued | The client reads `sessionID` as an opaque long; a repeat replaced a live session in the front (found by a test) | internal (S1) |
| 3 | Character slots | The screen says 3 (`maxCharacterSlots`) but nothing stops an account making more: the recording has four on one account | Refuses a character beyond the three, with a `CustomInfo` message; two creations racing for the last slot cannot both win | The limit is stated to the client in the selection data, so a server that keeps it is what the screen promises | tolerated (S2); marked `DEVIATION (slots)` in `tests/test_login_to_character.py` |
| 4 | Reserved names | Reserves the staff prefixes (`CCP `, `GM `, `ISD `) and also "Elysian", the name of its own system character | Reserves the staff prefixes only | Nothing in the client refers to "Elysian"; a player may want it | tolerated (S2); the `Elysian` case of the name test |
| 5 | When two names are one | Names that differ by case or spacing are one name (`recorded pilot` is taken by `Recorded Pilot`) | The same, and names that differ only in Unicode compatibility form (full-width letters, combining marks) are one too (NFKC) | The client shows both spellings alike, so allowing both makes look-alike names | tolerated (S2); `dotnet/tests/Eve.Characters.Tests/NameTests.cs` |
| 6 | Account creation date on the selection screen | A constant (the year 2019) | The time the account was created | The client displays it as the account's age; a real date is what the field means | tolerated (S2) |
| 8 | A character's description | New characters have the text "Character created via EveJS Elysian", the reference's own branding | An empty description | The client shows the description in the character sheet; text that names another project is not something a player wrote | tolerated (S3); the `description` of the public record and `GetCharacterDescription` in `tests/test_login_to_character.py` |
| 9 | Asking about a character or station that does not exist | A made-up id gets a made-up record: `charMgr.GetPublicInfo(1)` returns the name of the last character chosen, `stationSvc.GetStation(1)` returns Jita IV | `None`, as for any call the server cannot answer | The made-up record is a side effect of the reference filling every field with defaults; a real server has no such answer, and the client asks about ids it has been given | tolerated (S3); `dotnet/tests/Eve.Services.CharacterInfo.Tests`, `Eve.Universe.Tests` |
| 10 | What is in a ship | The ship's inventory answers `None` to `List`, `ListByFlags` and `GetCapacity`, and its contents (`GetContainerContents`) list the ship itself | An empty list, and an empty rowset | The client iterates the answer to `List` (`for item in inventory.List(flag)`), which a `None` breaks; a ship does not contain itself. A ship has no cargo or fitting yet, so empty is the true answer | tolerated (S3); marked `DEVIATION (ship contents)` in `tests/test_login_to_character.py` |
| 7 | A session's `role` | Every account gets the staff roles and a chat colour by default (a development setting), on top of a base mask | The account's role from the identity module (0 for an ordinary player) with the same base mask `0x6000000080000000`; the initial state carries the account's role, the session change on choosing a character carries the combination | The client uses the bits to show staff tools; a normal account should not have them | tolerated (S2); `role` is compared by type only in the differential test |

## Candidates to examine

Each is matched to the reference today. None is decided; the evidence needed is in the last column.

| Behaviour | Why it may not need to match | Evidence to collect |
|---|---|---|
| A handler that throws is answered with a `None` result, not an error | A `None` where the client expects data is probably worse than an error the client can report | What the client does on each service's `None` and on an error response |
| A service call may reach any method of the JS class by name | Only the methods the client calls matter | `docs/spec/services.tsv` against the reference's method set |
| A received `str` is echoed as `buffer` (`JsSemantics.AsReceived`) | The Python 2 client's `str` is bytes, so it may not tell the two apart | The client's marshal reader for opcodes `str` and `buffer` |
| JavaScript number and truthiness rules in ported handlers (`parseInt`, `\|\|`) | They are accidents of the language unless the client feeds the value | Which inputs the client can send |
| `charMgr.GetPublicInfo`'s `empireID` is taken from a table of four corporations (a Caldari school's pupils are shown under the Gallente empire, 500004) and is the faction for every other corporation | The table looks like a mistake; the client reads `empireID` only to pick an empire icon (`RACE_ICONS[empireID]`) | Whether the character sheet gets `empireID` from this record, and which icon a Caldari character should have |
| A station's `serviceMask` and `reprocessingEfficiency` are the same for every station on the reference | The data has the services and the efficiency of each operation; a constant may show services a station does not have | Which of the two the client's station window and reprocessing window rely on |
| Session and cache objects are sent in the reference's order | The client may not depend on the order | A run of the client with the order changed |
| One JSON document per legacy table, with a synchronous cache-first API | It is storage and internal structure | Nothing observable; decided by the design of each domain module |

## Not yet matched

Behaviour of the reference that eve.rs does not have yet, and where it will come from. These are gaps, not
decisions.

| Behaviour | Comes with |
|---|---|
| The selection screen's unread-mail count is the reference's constant for a new character (`unreadMailCount` 1); it will come from the mail module | S5 (mail) |
| The skill handler only reads: `GetSkills`, `GetAllSkills`, `GetSkillPoints`, `GetFreeSkillPoints`, the queue calls (always empty), `GetAttributes` (all five at 20, no implants or boosters), `GetRespecInfo` (3 free respecs), `GetMySkillInfo`, `IsSkillInjected`, `IsSkill`. Not there: training and its queue (`CharStartTrainingSkill*`, `CharStopTrainingSkill`, `SaveNewQueue`, `AbortTraining`), buying and injecting skills (`PurchaseSkills`, `InjectSkillIntoBrain`, `IsSkillAvailableForPurchase`, `GetDirectPurchasePrice`, injectors and extraction), free skill points, `GetSkillChangesForISIS`; each is answered with `None`, so skill points never change and nothing trains | S3 (skills: training) |
| `charMgr` notes, contacts, blocked senders, settings and titles are those of a player that has none (empty lists, `GetOwnerNote`/`GetNote`/`GetPrivateInfoOnCorpChange` not answered); security status is 0 and there are no bounties | contacts, notes and corporation modules (S5) |
| The inventory broker only reads: `GetInventory` for the hangar, `GetInventoryFromId` for the station and a ship of the character in the hangar, and `List`, `ListByFlags`, `GetItems`, `GetItem`, `GetSelfInvItem`, `GetContainerContents` on them. Not there: `GetCapacity` (answered with `None`), moving and stacking items (`Add`, `MultiAdd`, `MultiMerge`, `StackAll`), the character's and corporations' inventories, containers, fitting, the drone, fighter and fuel bays, `SetLabel`, `GetAvailableTurretSlots`, item ownership rules | S3 (inventory, fitting, dogma) |
| The station's own inventory shows the character's hangar, as on the reference; other characters' items in the same hangar (a corporation hangar) are not listed | S5 (corporations) |
| `stationSvc.GetStationsForOwner` (the stations an owner has; the reference lists them with a system security rounded to three places, unlike `GetStation`'s six) | S3 (station) |
| The station's services and reprocessing figures: `serviceMask` is the reference's constant (every service on) and `reprocessingEfficiency` its constant 0.5, whatever the operation or the station's own value in the data; the take and the hangar flag come from the data and match | with the services of each station operation (S3/S5) |
| `account` other than `GetCashBalance` (`GiveCash`, corporation wallets, the journal) | S3 (wallet journal) and S5 (corporations) |
| The character's ISK and PLEX are the reference's development values (100000 and 2222) unless `EVE_START_ISK` / `EVE_START_PLEX` say otherwise; `balanceChange` is always 0 (no journal yet) | S3 (wallet journal) |
| `charUnboundMgr` methods other than the ones the screen needs: `PrepareCharacterForDelete`, `CancelCharacterDeletePrepare`, `DeleteCharacter`, `GetCharacterInfo`, `GetQAStarterSystemIDs`, `UpdateCharacterGender`, `UpdateCharacterBloodline`, `GetCharCreationInfo`, `GetCharNewExtraCreationInfo`, `GetTopBounties`, `IsUserReceivingCharacter`, `GetCharOmegaDowngradeStatus`, `GetCharacterToSelect`; a call to one is answered with a `None` result | as the client calls them (`docs/spec/services.tsv`) |
| The reference's login takeover (`loginTakeoverEnabled`, evicting the earlier session) and browser-pilot control checks on choosing a character; eve.rs always refuses a character that is already online | with the session model of S3 |
| The login function also patches portrait upload (`photoUploadSvc`) and seeds the skill-extractor token | S2 (portraits), S3 (skills) |
| `machoNet.SetGlobalConfigValue`, `ForwardCharacterNotification`, `ReloadClientCodeHash`, `GetNodeFromAddress`, `GetClusterGameStatisticsForClient` | as the client calls them |
| The service table lists about 150 service names; eve.rs lists the services it implements | each service group |
| Store entries in the global config (`newEdenStore`) | S3 |
| Cluster user count from all sessions; a real login queue (`QC`) | S6 |
| The reference's optional AES branch of the crypto request (its own comment says it never worked) | not planned |
