# Structure integration verification - 2026-10-14

Canonical checkout: repository root, main. Native client not exercised.

Observed parent checks:
- Host Debug build: 0 warnings, 0 errors.
- Rust eve-space library: 814 passed, 0 failed.
- Rust simlink fixtures: 8 passed, 0 failed.
- C# Eve.Space: 337 passed, 0 failed.
- C# Space structure/superweapon/unanchor selection before Cockroach fixture adjustment: 78 passed, 0 failed.
- C# Cockroach Unanchor Drop/Scoop selection: 2 passed, 0 failed.
- C# Structures after restoring optional inventory-owner extension compatibility: 105 passed, 0 failed.
- Host Anchor/Drop/owner-context selection: 11 passed, 0 failed.

The Host-wide structure selection initially had two failures: deployment validation ordering, and generic timer use incorrectly requiring an inventory handoff. Both production paths were corrected; the complete selection then passed all 81 tests.

Current work:
- Cockroach (SDE3396210 type11019) is the default ordinary test vessel. Existing tests must not be changed further per the latest human instruction. Dedicated hull-rule tests retain their own required types.
- Structure-controlled Field success remains under implementation. It is not complete merely because its wire contract is registered.
- Existing unrelated Corporation and service-response-audit changes are preserved.
- Protocol69 wire and dependent Host/Sim implementation are under integration review before commit/push.
