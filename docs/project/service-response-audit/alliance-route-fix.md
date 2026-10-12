# Alliance RPC route audit

## Result

At `main` HEAD `1556ee81ab5dfab285eccb5c0e5da837d9757c37`, the reported `corpRegistry` route mismatch is not present. `CreateAlliance`, `GetAllianceApplications`, and `GetSuggestedAllianceShortNames` are methods of the `CorpRegistryService` partial type, even though this part of that type is declared in `AllianceRegistryService.cs`. The type is named `corpRegistry`, and Host includes its instance in the corporation RPC services.

No production forwarding methods or duplicate handlers were added. Moving or forwarding these methods would not correct a route defect in this checkout and would add an unnecessary dispatch path.

## Contract and state flow

- eve-logic `eve/client/script/ui/services/corporation/bco_alliance.py:23-32` sends the three calls through `GetCorpRegistry()`. `CreateAlliance` passes four strings; `GetAllianceApplications` takes no caller-supplied corporation; `GetSuggestedAllianceShortNames` passes the alliance name.
- EveJS v0.12.9 commit `828823a75f211fdecb945b087a26c225f111491f`, `server/src/services/corporation/corpRegistryRuntime.js:3192-3276,3409-3411`, registers the same handlers on its corporation registry runtime.
- In eve.rs, `CorpRegistryService.cs:12-13` declares `[Service("corpRegistry")]` on the partial type. `AllianceRegistryService.cs:1347-1428` declares the three `[Method]` handlers on that same type. C# partial declarations form one type; the source filename does not change the service route.
- `CorporationRpc.cs:41-68` constructs and returns `CorpRegistryService`. `CorporationComposition.cs:151-183` continues constructing the separate `AllianceRegistryService`, preserving its existing route and methods.
- `CreateAlliance` resolves the actor's corporation from persisted character state, requires its CEO, and delegates creation to `CorporationStore.CreateAllianceAsync` with the session/call idempotency key and the existing affiliation writer. It schedules the existing maintenance-bill writer and queues alliance, member, and corporation notifications after the creation result. `GetAllianceApplications` resolves the actor's persisted current corporation and requires CEO or director authority before querying that corporation's applications. Neither method trusts a caller-supplied corporation id.
- Existing `AllianceTests.cs` already calls these three handlers through an `IService` created from `CorpRegistryService`; the create/application test also repeats the same call and checks balance and notification replay behavior.

## Change and verification handoff

- `dotnet/tests/Eve.Corporations.Tests/AllianceTests.cs`: made the route contract explicit by asserting the service name and all three `HasMethod` results on `corpRegistry`; existing `allianceRegistry` assertions remain.
- Tests and builds were not run because the parent is running the .NET suite serially, as instructed. No Git mutation was performed.
- Recommended focused check after the parent finishes: `dotnet test dotnet/tests/Eve.Corporations.Tests/Eve.Corporations.Tests.csproj --filter FullyQualifiedName~Alliance_and_corporation_registry_expose_application_methods`.
- No graphical-client verification was performed. This audit confirms the current source-level service/Host route and adds a regression assertion; it does not claim a live client session was exercised.
