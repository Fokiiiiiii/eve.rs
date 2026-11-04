# Customs handover encounter handoff

Updated: 2026-10-23

## Current state

The requested handoff file did not exist in this worktree at the start of this pass. No encounter code was added.

The existing path is SDE-backed automatic settlement:

- `dotnet/src/Eve.Services.Space/UndockContraband.cs` inspects owned-ship cargo against `contrabandTypes`, snapshots item ids and amounts, and settles fine, standing loss, and custody removal through the durable `contraband.penalty-settle` job.
- `SpaceSessions.Ships.ApplyContrabandAsync` uses that settlement for undock. `SpaceSessions.Movement.JumpAsync` calls it for the destination system only after an accepted jump.
- `dotnet/src/Eve.Host/Composition/SpaceComposition.cs` migrates the contraband schema, registers the scheduler handler through `UndockContraband.LoadAsync`, and wires inspection and settlement into `SpaceSessions`.
- Existing regression coverage is in `dotnet/tests/Eve.Services.Space.Tests/UndockContrabandTests.cs`; the settlement tests require PostgreSQL.

## V24.01 prompt evidence

The V24.01 source confirms a generic client prompt method, but not the customs-specific message contract:

- `/home/user/eve-logic/xmppchatclient/xmppchatsvc.py:1745-1750` defines exported `XmppChat.AskYesNoQuestion(question, props, defaultChoice=1)`; it displays a yes/no FSD dialog and returns a boolean.
- `/home/user/eve-logic/player_messaging/client/dialog_handler.py:48-93` requires `question` to resolve through `cfg.GetMessage(..., onNotFound='raise')`.
- `/home/user/eve-logic/eve/common/script/util/notificationUtil.py:269-293` formats the post-resolution confiscation notice. The formatter registry at line 1175 maps `notificationTypeCustomsMsg` to that formatter; `notificationconst.py:37` assigns type 33. This is an outcome notice, not a surrender request.
- Searches of the V24.01 Python source found no reference to `ChtCustomsConfiscationConfirmation2` and no customs-specific `AskYesNoQuestion` callsite or response handler. The generic method alone does not establish that the later-build message key or its typed properties resolve in V24.01.

EVE University’s [Static Data Export](https://wiki.eveuniversity.org/Static_Data_Export) page (lines 330-336) identifies faction/type contraband rows and lists `standingLoss`, `fineByValue`, `confiscateMinSec`, and `attackMinSec`; its `invTypes` entry lists `basePrice` (lines 402-413). The runtime continues to read the pinned SDE tables `contrabandTypes` and `types` in `UndockContraband.LoadAsync` / `InspectDetectedAsync`.

EVE University’s [New Slaves](https://wiki.eveuniversity.org/New_Slaves) page describes a gate encounter, a surrender fine and standing loss, and ship destruction after refusal; its displayed prompt also describes officers targeting, scrambling, and webbing the ship while awaiting a response. It does not define the client RPC or response payload.

EveJS 261018.1 uses `XmppChat.AskYesNoQuestion`, message key `ChtCustomsConfiscationConfirmation2`, typed `contraband` and `empire` properties, and a configured 60-second prompt deadline (`customsInspectionPresentation.js:25-43,106-175`; `customsInspectionRuntime.js:93-101`). Those details remain later-build reference behavior; the matching V24 message resource and customs-specific response flow were not found in the inspected V24 source.

## Next step

Do not add a customs prompt packet from the later-build key until a V24.01 message-resource record or client trace establishes the key and property contract. Once established, keep the existing SDE rules and contraband custody settlement authoritative, persist the case and deadline before sending the prompt, and test the reply through Host dispatch. No PostgreSQL database was used in this pass. No Host-dispatch or headless walk, graphical-client check, build, or test was executed.
