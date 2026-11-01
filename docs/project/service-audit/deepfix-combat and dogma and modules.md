# Combat, Dogma, and Modules Deep Fix Handoff

## Scope and verified client path

This change covers the turret hit roll used when an activated weapon fires. In V24.01, `shipmodulebutton.py:932-961,1309-1345` handles the player click, obtains the active target for a targeted effect, and calls `def_effect.Activate(targetID, repeats)`. `eve/client/script/environment/godma.py:975-988,2048-2072` forwards `Activate(itemID, effectName, target, repeat)` through the bound dogma location. eve.rs registers `DogmaIMService.Activate` (`dotnet/src/Eve.Services.Dogma/DogmaIMService.Modules.cs:15-39`), validates the bound context, and asks the space provider to activate the module; its successful reply is integer `1`. The space provider sends the fitted turret snapshot and target to the sim (`dotnet/tests/Eve.Services.Space.Tests/WeaponHostTests.cs:20-40`). The sim requires a target in scene and locked (`crates/eve-space/src/weapons/fire.rs:4-17`), rolls the shot, applies damage, then builds the existing `OnDamageMessage`.

V24.01 batches `OnDamageMessage` into `OnDamageMessages` (`godma.py:219-243`). `godma.py:501-549` consumes `hitQuality` for turret state and the combat log; `eve/client/script/ui/shared/uilog.py:314-350` resolves the combat message's object names; `eve/client/script/parklife/bracketMgr.py:1169-1184` consumes applied damage for attacker display; and `eve/client/script/ui/inflight/bracketsAndTargets/targetInBar.py:1009-1030` blinks the target's health bar when the pilot deals damage. The sim emits the notification from the landed shot path in `crates/eve-space/src/weapons/fire.rs`; the hit tier and shot damage come from `crates/eve-space/src/weapons/math.rs`.

The client-facing payload and notification order are unchanged. A wrecking result keeps the existing `OnDamageMessage` shape, reports quality tier 6, and applies three times the weapon's raw shot damage before the existing resistance and layer handling.

## Defect and repair

EVE University [Turret mechanics — Damage](https://wiki.eveuniversity.org/Falloff#Damage) specifies that a random roll below 0.01 is a wrecking shot, including when hit chance is below one percent; under that hit chance, shots are misses or wrecking hits. `resolve_shot` previously returned a miss for every roll greater than or equal to hit chance before checking the wrecking threshold. With a positive chance of 0.002122 and roll 0.005, it therefore missed instead of applying the wrecking shot.

`crates/eve-space/src/weapons/math.rs` now recognizes a wrecking roll before the ordinary hit cutoff when the computed chance is positive. Zero hit chance remains a miss, preserving the existing zero-chance regression. This intentionally differs from EveJS 261028.1 `server/src/space/combat/laserTurrets.js:92-192`, which applies the ordinary cutoff before the wrecking check; the EVE University mechanic takes priority.

## Regression evidence

`crates/eve-space/src/weapons/tests.rs:203-214` adds `a_wrecking_shot_lands_when_the_positive_hit_chance_is_below_one_percent`, which checks a low positive chance, the 3.0 quality, and three-times damage. It failed before the fix with `chance_to_hit: 0.002122`, `roll: 0.005`, `hit: false`. After the fix, `cargo +1.94.1 test -p eve-space weapons::tests::` passed 84 tests. The existing zero-chance and exact-boundary assertions remain strict. The railgun's low-probability ordinary miss now uses the exact 0.01 wrecking boundary so that it continues to test a non-wrecking shot. The PostgreSQL-backed `WeaponHostTests` class ran against `eve_p5`: 35 passed, none skipped. Rust checks also passed: `cargo +1.94.1 fmt --all -- --check`, `cargo +1.94.1 check -p eve-space`, and workspace Clippy with `-D warnings`.

## Coverage boundary

This handoff records the hit-roll slice of the combat/module path. It does not certify every weapon family, dogma modifier, module activation refusal, or reload transition. No graphical-client run was performed; the check is a sim regression test, not graphical-client verification.
