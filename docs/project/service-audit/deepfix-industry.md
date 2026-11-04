# Industry deep-fix handoff

## Personal NPC station inventory

Client eve-logic/eve/client/script/industry/industrySvc.py:316-320 loads facility and owner locations; eve-logic/industry/job.py:569-584 validates blueprint, input when materials are required, and output. eve-logic/eve/common/script/util/industryCommon.py:430-449 matches item and flag. The NPC station location response in IndustryComposition.cs exposes the personal station hangar.

InstallOnceAsync previously accepted personal jobs using inventories outside the facility. ConsumeMaterialsAsync uses the submitted input; output creation uses the stored output. The fix checks the actual blueprint and submitted input/output before consumption, debit or lock, while preserving remote installer distance skills.

PostgreSQL eve_terra: dotnet test dotnet/tests/Eve.Host.Tests --filter FullyQualifiedName~Personal_station_jobs_reject_locations_outside_the_facility_without_side_effects. Before fix: 1 failed because remote output was accepted. After fix: 1 passed, 0 skipped. The passing test covers remote output, stocked remote input, remote blueprint, unchanged balance, inventory and locks, zero jobs, and successful local install. It invokes real persistence operations, not a registered Host wire route or graphical client.

## Scoped flow matrix

| Core flow | Inspection and evidence | Validation and remaining limits |
| --- | --- | --- |
| Personal station installation | Client `eve-logic/eve/client/script/industry/industrySvc.py:316-320` and `eve-logic/industry/job.py:569-584`; Host `PersistenceIndustryJobOperations.Part2.cs` `InstallOnceAsync` and station location rendering | Focused real-PostgreSQL fail-before/pass-after test above; the registered wire path and GUI were not run. |
| Industry owner, office and facility selection | `InstallOnceAsync`, facility location responses and relevant corporation inventory flags | The confirmed personal NPC station defect is fixed. Corporation office and structure permissions and their fixture flag discrepancy need further client-backed verification. |
| Job materials, cost, output and lifecycle | Installation's material consumption, cost and blueprint lock paths; `PersistenceIndustryJobOperations.cs` output creation | The regression checks no partial install side effects and a valid local control. Delivery, cancellation, concurrency and restart were not independently verified in this work. |
| Reprocessing quote and execution | Client `eve-logic/reprocessing/ui/itemReprocessor.py:17-70` and `quotes.py:18-25`; `Eve.Services.Station/ReprocessingService.cs` `PlanAsync` and `Reprocess` | Reviewed yield and scrapmetal skill/formula against the EVE University guide. No concrete defect confirmed, and capacity/conservation, concurrent changes and registered wire were not independently tested. |
| Planetary production and routing | `Eve.Planets/Colony.cs` `EvaluatePin`, `RouteOutput` and `StimulateIdle`; pinned SDE schematic outputs | A potential mixed-product allocation issue was not patched: pinned SDE has no multi-output planetary schematic, and the ECU path produces one type. Reachability and other PI production/routing behavior remain unverified. |

No full test suite or graphical client was run. These are scoped traces, not an exhaustive audit or a claim that the remaining flows are correct.

## Coverage accounting boundary

The 2026-11-02 coverage snapshot records extracted client callsites and verified RPC tests. Across the five scoped task areas it contains 306 remote-callsite rows, of which 279 are marked tested (91.2%); 27 rows remain in TourneyMgr and ProjectDiscovery. This is callsite coverage, not proof that every task requirement or every state transition is complete. In the industry/PI slice, 25 of 25 remote-callsite rows are tested; the two `planetMgr.GetPin` rows are client-local and excluded.

`reprocessingSvc` has no rows in `EVE_RS_COVERAGE.csv`, so reprocessing is outside that denominator. The reviewed multi-location result-flattening path remains a candidate for follow-up, not a confirmed defect; verify UI selection constraints and the registered wire path before drawing that conclusion. Office/structure permission behavior, industry delivery/cancellation/concurrency/restart, and remaining PI production/routing correctness are separate from callsite coverage and are still unverified.
