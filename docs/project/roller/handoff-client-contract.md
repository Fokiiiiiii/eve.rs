# Client contract audit handoff

Task: enumerate V24.01 service calls, arguments, client notification subscriptions, and Host route coverage. Assigned live lane: `eve_p4` / port 26103.

The reusable extractor and focused tests are `tools/spec-extract/client_contract.py` and `tools/spec-extract/test_client_contract.py`. The generated contract is `docs/project/roller/client-contract.md`. It currently reports 610 service/method pairs, 879 direct/bound callsites, 41 method references, 657 notification names, and 73 client pairs without a parsed Host route. Four extractor tests pass. The scanner handles attributes, aliases, explicit route tables, manual method sets, and service-name switch routes.

Shipcaster services are registered, but Host production wiring has no landing-pad/faction readers or starter eligibility provider. Missing providers now return an explicit CustomNotify instead of an empty list or false eligibility. The reference travel rejection for unavailable shipcaster destinations is retained. The Space and Host test projects built cleanly; ShipcasterRpcTests passed 16/16, and the Host network-dispatch regression passed 1/1.

Structure review corrected a stale handoff: single-character cyno-beacon travel and persisted jump-bridge destination/link reads are wired. EveJS supports group beacon jumps, portal bridging, and Ansiblex travel; Host records explicit unsupported gaps for those transitions.

A focused populationCap headless scenario is still pending. Its live runner uses the existing build-3396210 SDE cache at `/home/user/eve.rs/.cache/sde/eve-online-static-data-3396210-jsonl`, skips the unrelated market seed, and runs under the `eve_p4` DB lock on port 26103. The current attempt is waiting on the shared heavy build lock. The graphical client was not used.

After the live result: update this note and the contract validation section, inspect `git status`/diff, fetch and merge `origin/main`, commit with the required identity, push `HEAD:main`, and check CI. Address any red CI result at its root.
