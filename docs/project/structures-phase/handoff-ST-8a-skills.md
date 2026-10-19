# ST-8a Skills Handoff

## Repository and scope

Target root: eve.rs. The assigning parent supplied branch main at baseline b328b58a; this agent ran no Git commands.

This is a narrow Structure Dogma skill-filter correction. It does not complete ST-8 execution.

## Changed files in this slice

- dotnet/src/Eve.Host/Persistence/Structures/StructureControlReadModel.cs — StructureControlReadModel.DogmaAsync adds only the four structure-specific skill items to the structure DogmaWorld.
- dotnet/tests/Eve.Host.Tests/StructureControlReadModelTests.cs — the PG regression test asserts personal skill metadata and total skill points are preserved while retaining the structure World allowlist and copy checks.
- docs/project/structures-phase/handoff-ST-8a-skills.md — this handoff.

## Rationale and evidence

CCP's Upwell Structures - Quick Facts says structure modules and fighters are unaffected by standard skills, except Structure Doomsday Operation, Structure Electronic Systems, Structure Engineering Systems, and Structure Missile Systems: https://support.eveonline.com/hc/en-us/articles/209985225-Upwell-Structures-Quick-Facts

DogmaAsync resolves each skill's name through the existing SDE-backed DogmaStatic.Type lookup. It adds only those four names and does not hardcode SDE type IDs. Each allowed DogmaItem is copied into the structure world: OwnValues (including skill level) is copied by the existing DogmaItem constructor, ActiveEffectIds is copied to a separate set, and the structure world no longer shares the mutable DogmaItem instance with the personal world.

Character and skill metadata remain authoritative: the returned snapshot retains personal.Skills, so ShipSnapshot.TotalSkillPoints stays equal to the personal snapshot. DogmaInfo.ShipState in dotnet/src/Eve.Services.Dogma/DogmaInfo.cs takes wire skillPoints from s.TotalSkillPoints, while only the structure DogmaWorld modifier inputs are restricted to the four structure-specific skills. The PG test asserts both metadata/total preservation and the independent World item behavior. Existing pilot/structure authorization, read transaction, fitted-item state, and module condition handling are unchanged. No inventory writer or persistence mutation was added.

## not performed

This agent did not execute tests, builds, formatters, EveJS comparisons, or the graphical EVE client. No result from the parent's PG-backed regression test is recorded here.

## Remaining work

ST-8 end-to-end execution and client-visible validation remain incomplete and must not be treated as finished based on this source correction alone.

## Parent verification (2026-10-13)
- After preserving full character skill metadata in the test contract, the parent temporarily restored the original production loop. The focused PG regression failed with Assert.NotSame: personal and structure worlds shared the same skill object. The corrected loop was restored immediately.
- With the corrected loop, the Host StructureControl subset passed 12 tests, 0 failed/skipped, against PostgreSQL. The test checks four structure-only modifier items, excludes Gunnery/Anchoring from the structure world, preserves skill levels, isolates mutable item values and retains personal/DB skill metadata and total SP.
- Upstream 6119b18d was integrated as 3d6a5722 without conflicts. Post-merge Inventory tests passed 63, 0 failed/skipped, and Rust simlink fixtures passed 8, 0 failed/ignored.
- This verifies a structure Dogma input/isolation correction, not module execution, an executable EveJS end-to-end comparison or native client acceptance.

## EveJS reference comparison
The parent checked EveJS 828823a server/src/services/fitting/liveFittingState.js:55-60 and :886-895. Its filterStructureDogmaSkillMap selects precisely type IDs 37796-37799, matching the four CCP/SDE names used here. The parent executed the exact helper body and declared allowlist in isolation with a toInt utility substitute and valid integer SDE inputs: a six-skill map selected the same four without changing the input; null and empty maps returned empty maps (3 checks passed). This is a helper-level functional comparison, not an EveJS server/client walkthrough. Copying mutable C# DogmaItem values is independently required by the PG alias regression; the EveJS filtered Map retains record references and is not an implementation template.

## Final upstream verification (2026-10-13)
After integrating upstream c5782d80, the selected Host StructureControl plus changed Industry persistence suites passed 21 tests with 0 failures/skips. This is a post-merge regression checkpoint; graphical acceptance and full structure module execution remain open.
