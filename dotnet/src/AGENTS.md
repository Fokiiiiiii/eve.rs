# C# Development Rules

## Responsibilities
- `Eve.Host`: composition, registration and startup.
- `Eve.Services*`: RPC handlers, wire conversion and notifications.
- `Eve.Macho` / `Eve.Marshal`: protocol representation.
- `Eve.Space`: Rust simulation IPC.
- `Eve.GameStore`: persistence.
- Other `Eve.*` projects: gameplay domains and shared foundations.

## Requirements
1. Use CCP official material and the matching SDE for game rules; supplement with EVE University Wiki.
2. Verify RPC arguments, return shapes, exceptions and sequencing against V24.01 eve-logic and real traces.
3. Strongly reference the **working EveJS v0.12.9 code and execution path** for gameplay flows. Preserve proven client-observable behavior by default. Validate any deliberate difference.
4. Confirm concrete Host registration and provider integration rather than checking only that a handler exists.
5. Use existing authoritative stores and appropriate single database transactions for coupled state changes.
6. Publish notifications after commit, preserving ordering and follow-up behavior.
7. Reuse Scheduler, Events, Relations, Content, Dogma and other shared foundations.
8. Never claim completion for a stub, missing provider, fabricated payload or untested full flow.
