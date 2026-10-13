# Skill injector award correction (2026-10-13)

## Scope and status

The award correction changed only `dotnet/src/Eve.Services.Skills/SkillHandlerService.Injection.cs` (`DiminishedInjectorSp`), related cases in `dotnet/tests/Eve.Services.Skills.Tests/SkillServiceTests.cs`, and this handoff. This latest update changes only this document. The pre-correction parent run was 62 PASS, 0 FAIL, 0 SKIP, but those tests encoded the wrong formula. After the correction, the parent reported the Skills Debug full suite at 63 PASS, 0 FAIL, 0 SKIP. The counts are from separate runs and are not interchangeable.

## Authority comparison

CCP's current [Skill Extractors and Skill Injectors support article](https://support.eveonline.com/hc/en-us/articles/207605005-Skill-Extractors-and-Skill-Injectors) specifies the Large/current injector award bands as 500,000 / 400,000 / 300,000 / 150,000 SP at the 5M / 50M / 80M thresholds. It says the calculation uses allocated plus unallocated SP and gives the 4,501,000-SP, two-injector example: 500,000 followed by 400,000, total 900,000. CCP's [May 2017 injector update](https://www.eveonline.com/news/view/increased-skill-injector-flexibility-coming-on-may-23rd) says the previous injector became Large, one Large breaks into five Small, and Small awards 100,000 / 80,000 / 60,000 / 30,000 SP across the corresponding bands.

The official CCP build `3396210` archive was fetched from the URL in `sde/lock.json`. Its SHA-256 (`17ae0c7f00fec2c4b79f785a9c35debdc5ae9edde558d18dc26d51cf87279b3e`) matches the pinned lock hash, and the read-only database reports build `3396210`. The official JSONL rows and database rows agree for the relevant types:

- `40520` is published Large Skill Injector; `45635` is published Small Skill Injector. Both are in group `1739` and neither has a `typeDogma` row, so their award bands do not come from those type attributes.
- `43630` is Mini Skill Injector. Its `attributeID 2459 = 1,500,000` is the maximum total SP limit before that item is unusable; `attributeID 2461 = 25,000` is the SP amount contained in that item. The 1.5M value is not a Large Injector award. `46375` Daily Alpha Injector has `attributeID 2461 = 50,000`.

EveJS v0.12.9 at commit `828823a75f211fdecb945b087a26c225f111491f` independently matches the CCP behavior. `server/src/services/skills/trading/skillTradingAuthority.js` defines Large full SP as 500,000 and the Large bands above; Small is 100,000 and its bands are derived by dividing each Large band by five. `buildDiminishingInjectionPreview` advances total SP once per requested item. Its `nonDiminishing` count is also consumed once per item. `getDiminishedSpFromInjectors` passes `quantity` and `nonDiminishingInjectionsRemaining` to that same preview logic (`skillTradingRuntime.js:837-855`, `skillMgrService.js:553-561`).

The former C# formula was wrong: it multiplied Large quantity by three, treated Small as a full Large award, and consumed each non-diminishing use per fabricated unit. The shared helper now processes one award per item, scales Small awards by five, and consumes one non-diminishing use per item. `GetDiminishedSpFromInjectors` forwards its optional third argument; `InjectSkillpoints` passes zero. No other caller was found. The Daily Alpha fixed award and the quantity limit remain unchanged.

## Regression coverage

`Skill_injectors_grant_diminishing_free_points_and_consume_the_stack` now starts at 4,950,000 SP and checks two Small injectors award 180,000 total (100,000 crosses 5M, then 80,000). The inventory-notification test checks each Large item returns 500,000, with balances 500,123 and 1,000,123, while preserving stack changes and silent failed retry. The concurrent injector test checks one committed 500,000 award, one consumed item, and the still-running queue.

`Injector_preview_uses_per_item_large_and_small_bands_and_non_diminishing_uses` checks both item type IDs, all four award tiers, 5M/50M/80M transitions, the official 4,501,000-SP quantity-two example, quantity-two previews, and one non-diminishing use at 5M. At exactly 80M the test follows current CCP Support's “80 million or more” band and EveJS's exclusive 80M upper bound. The 2017 Small article says “more than 80M”; the exact Small endpoint is inferred from the newer Large cutoff, the one-fifth rule, and EveJS behavior.

No concurrency, retry, queue, wallet, notification-order, or failure assertion was weakened. Production edits are confined to `DiminishedInjectorSp`.

## Handoff

The parent reviewed the current `DiminishedInjectorSp`, `PurchaseSkills`, and `AnnounceInjectedPoints` implementations and reported the corrected Skills Debug full suite as 63 PASS, 0 FAIL, 0 SKIP. The source fix and its regression expectations are accepted by that Skills run.

Overall build/test acceptance remains incomplete: the Host composition checks stopped at the Inventory worker's `PyFloat` compile error `CS0246`. The parent will rerun the Host composition/build checks after that blocker is resolved. This document-only update ran no tests, build, or Git operation. Restart and graphical-client acceptance remain outside this fixture correction.

## Parent Host baseline follow-up (2026-10-13)
After the fixture fixes and upstream 9db7d354, the parent observed a successful Host Debug build (0 warnings/errors) and 103 passing Host structure/moon/industry/PaperDoll/SKIN checks (0 failed/skipped). The Skills service suite was separately 63 PASS, 0 FAIL, 0 SKIP. The earlier Inventory PyFloat build blocker is resolved. These are build/PG regression results, not a native client or full Skills Host-to-Sim acceptance claim.
