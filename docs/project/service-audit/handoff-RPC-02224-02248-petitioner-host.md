# Host coverage: petitioner RPC-02224–RPC-02248

## Result

The 25 audit rows cover 12 unique methods; the repeated row IDs represent the same V24.01 callsites. The petitioner service was already registered by `CoreServices.Create` as `new PetitionerService()` and its methods were dispatched by the real service registry. The missing evidence was a Host socket regression. `PetitionerHostTests.cs` now sends the observed petition calls through Host startup, session open, Macho packet decoding, service dispatch, and reply decoding.

The regression covers both `CreatePetition` signatures, `DeletePetition`, `EscalatePetition`, and all nine list/hierarchy reads. It supplies the transport-added `machoVersion` kwarg and the caller's chat/combat log kwargs. Return values match EveJS v0.12.9: petition creation is `false`, deletion/escalation are `None`, list calls return empty lists, and hierarchical info returns four empty dictionaries. These are the reference service's deliberate no-ticket-backend responses; the client handles the creation refusal, and mutation RPC replies are ignored. There is no petition persistence or service notification in this reference flow.

## V24.01 caller and consumer evidence

The complete-tree search found the target calls in these two Neocom files:

- `petitionWindow.py:203-204` uses category properties as property IDs to construct the form; `:256-264` uses picker rows as (display name, ID) pairs, chooses the first row, and displays a narrow-search prompt at 25 or more rows.
- `petitionWindow.py:280-315` submits the eight positional values and then scatters `OnPetitionCreated`; the RPC reply is ignored. `:316-344` unpacks the four hierarchy dictionaries, filters labels by language, and builds parent/child category and description UI data.
- `petition.py:243-275` caches `GetMyPetitionsEx` and displays petition records; `:287-316` displays claimed records; `:470-490` renders queue rows. These records feed petition list fields and use petition IDs, petitioner IDs, category, subject, status, and update data.
- `petition.py:577-592` consumes category rows as category IDs/names. `:707-725` calls CreatePetition with five positional values and chat/combat log kwargs, and treats `false` as rejection.
- `petition.py:824-866` consumes log rows with `logDateTime`, `characterID`, `userName`, and `eventID`; `:868-894` consumes message rows with `senderID` and message data.
- `petition.py:993-1003` assigns DeletePetition's result to an unused local; `:1036-1049` ignores EscalatePetition's result.

The broad caller search also found unrelated methods named `GetCategories` and `GetLog`; none are calls to `petitioner`. The service-specific caller set is the two files above.

## EveJS v0.12.9 comparison

`server/src/services/support/petitionerService.js` returns:
- `GetMyPetitionsEx`: empty list, lines 139-142; `GetClaimedPetitions`: empty list, 144-147; `GetPetitionQueue`: empty list, 149-154.
- `GetCategories`: empty list, 161-164; `GetCategoryHierarchicalInfo`: four empty dictionaries, 166-169; `GetCategoryProperties`: empty list, 171-176.
- `GetClientPickerInfo`: empty list, 185-191; `CreatePetition`: false, 206-213.
- `GetLog`: empty list, 215-220; `GetPetitionMessages`: empty list, 222-227; `DeletePetition`: None, 245-250; `EscalatePetition`: None, 273-279.

The eve.rs Host response defaults in `dotnet/src/Eve.Services.Core/PetitionerService.cs` match those shapes. No intentional compatibility difference was introduced.

## Checks

- PASS: `MSBUILDDISABLENODEREUSE=1 dotnet build dotnet/tests/Eve.Host.Tests/Eve.Host.Tests.csproj -p:UseSharedCompilation=false` (0 warnings, 0 errors).
- PASS: focused `Eve.Host.Tests` filter for `Petitioner_client_calls_return_reference_shapes_through_host_registration` (1 passed, 0 skipped).
- GitHub Actions verification is pending the push.
- The real graphical client flow was not exercised.
