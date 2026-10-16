# Handoff S8 (CH-16 closure, CH-20..22 gateway; database lane eve_p3, live port 26102)

Worktree /home/user/wt-ch-s8, branch ch-s8. Push: git fetch origin main && git merge origin/main, then git push origin HEAD:main.

## Status

| Item | State | Verified (eve_p3) |
|---|---|---|
| A1 CH-16 live clone jump | DONE | test_clone_jump_live.py and test_implant_live.py: exit 0 after the booster rule |
| A2 death | PARTLY | Ships DeathTests 13 and Space PlayerDeathTests 13 pass; live test_death.py FAILS (open) |
| A3 restart, implants and expert systems | DONE | CharacterHostTests An_implant_and_an_expert_system passes |
| A4 fitting with expert grants | DONE | Eve.Ships.Tests ExpertGrantFitTests passes |
| A5 normal boosters end on a jump | DONE | CharacterHostTests A_clone_jump_ends_the_normal_boosters passes; live CH-16 passes |
| CH-20 gateway | DONE (Requests/Send, Ping, Events/Publish; TLS and Notices open) | PublicGatewayTests 2 passed (eve_p3); live gateway via test_skill_plan_live.py |
| CH-21 skill plans | DONE (prerequisite canonicalization open) | test_skill_plan_live.py passed (eve_p3, exit 0) |
| CH-22 titles | NOT STARTED | |

## Live run notes

- Gateway port per run: EVE_PUBLIC_GATEWAY=127.0.0.1:26125 style, a new port each run (TIME_WAIT blocks a re-bind).
- An orphaned host from a run can hold the port: check with ps before a run.

## Open failures (not fixed)

- CharacterHostTests A_skill_booster_lapses (job stays running; see TODO S8).
- test_death.py live walk (session change keys and watcher messages; see TODO S8).

## Plan for the gateway (CH-20..22)

- Transport (eve-logic publicGateway and eveProto/__init__.py): the client native gRPC module connects to the tier public gateway over TLS, or to EVE_PUBLIC_GATEWAY_ADDRESS in plain text. Methods: /eve_public.gateway.Requests/Ping and Send, /eve_public.gateway.Notices/Ping and Consume, /eve_public.gateway.Events/Ping and Publish (EveJS publicGatewayLocal.js 2564-2591).
- Envelope: eve_public.Request (issued 1, correlation_uuid 2, payload Any 3, external_origin 4, application_instance_uuid 5, authoritative_context 6; identity 10, character 1, sequential 1). Response: dispatched 1, correlation_uuid 2, status_code 3, status_message 4, payload Any 5. Status 200 is success; the client maps 400, 404, 412 and 500 to SkillPlansError labels (requestsMessenger.py).
- Host: Kestrel on Microsoft.AspNetCore.App (shared framework 10.0.12 installed, no NuGet), HTTP/2 h2c listener on EVE_PUBLIC_GATEWAY (unset = off). gRPC framing by hand. Drafts in the scratchpad draft folder.
- Skill plans: descriptors dumped with the headless venv (protobuf 7.36.2, grpcio 1.84.0 in tools/headless-client/.venv; list them in requirements). Migration 0009 in PgSkillStore inline list (next free after 0008).
- Titles: eve_public.cosmetic.character.title.api (GetOwned, Equip, Unequip, GetEquippedForCharacter); the definition CDN comes from EveJS data, which is not committed, so its path must be configured.

## Risks

- The gateway trusts authoritative_context.identity (EveJS does the same); no JWT check. Listener is loopback by default.
- The death walk and the booster lapse test are red on the merged tree.
