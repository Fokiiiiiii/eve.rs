# Rust Development Rules

## Responsibilities
- `eve-net`: TCP transport, framing and Host communication.
- `eve-marshal`: client marshal wire compatibility.
- `eve-destiny`: Destiny encoding and motion calculations.
- `eve-space`: space, combat, drones and NPC behavior.
- `eve-sim`: simulation process, ticks and IPC.
- `eve-behaviors`: NPC behavior logic.
- `eve-sde`: official static data.
- `eve-oracle` / `eve-bench`: differential verification and benchmarks.

## Requirements
1. Prefer CCP official gameplay specifications and the build-matched SDE; use the functioning EveJS v0.12.9 implementation as a primary executable behavioral reference.
2. Verify client-visible numbers, transport encodings and notification order against V24.01 traces and running EveJS scenarios.
3. Update the corresponding C# implementation, protocol version and golden fixtures together whenever IPC changes.
4. Avoid redundant calculations, blocking operations and unnecessary per-tick work.
5. Measure behavior and performance under equivalent conditions before and after optimizations.
6. Never embed captured responses, arbitrary constants or invented gameplay results in production logic.
