# Fleet and combat status

## Fleet

Fleet creation, invitation and acceptance, hierarchy and role changes, boosters, fleet broadcasts, and command bursts have focused Host coverage. The two-pilot headless command-burst walk passed on 2026-10-23: the receiver received the fleet invite, joined, followed a fleet warp, received the burst Dogma changes before the buff-state update, and returned to baseline when the buff expired. Graphical-client verification remains open.

## Drones

The Host suite covers launch limits and bandwidth, explicit and target-seeking engagement, idle aggression, assist and guard, return and scoop, reconnect, control range, utility effects, mining and salvage. The live headless drone walk passed both cases on 2026-10-23, comparing launch, combat, bay recovery and targetless search against the recorded client flow. Launch item notifications follow the simulation launch actions; the drone item update is sent twice, matching the reference flow. The walk allows 1.5 seconds for the simulation whole-second update boundary. Hybrid turret and utility combinations, mining/salvage client checks, and graphical-client checks remain open. Abandoning a drone removes its ball and item; the reference leaves an idle scene ball that another pilot can scoop.

## Electronic warfare and area effects

Host tests cover module and drone ECM, sensor dampening, weapon disruption, target painting, webs, scramblers, neutralizers and energy transfer, alongside projected repairs. Command bursts use the SDE buff collection, update receiver Dogma attributes and restore them at expiry. Rust simulation tests cover smartbombs, bombs, bubbles, interdiction and their scene effects.

## Death and killmails

Host tests cover ship and capsule death, wreck and item outcomes, pilot notifications, and killmail persistence. These flows have focused headless coverage; graphical-client checks remain open.

## Recorded checks

- eve_p4 Host filters for fleet, drone, command-burst and death tests: 89 passed.
- eve_p4 Host filters for module effects, repairs, weapons, mobile warp disruptors and interdiction nullifiers: 77 passed.
- Rust `eve-space` tests: 884 unit tests passed with their integration and reference fixtures.
- Rust smartbomb filter: 10 passed.
- Two-pilot command-burst headless walk: 1 passed.
- Drone flight and targetless-aggression headless walk: 2 passed.
- Graphical-client checks: open.
