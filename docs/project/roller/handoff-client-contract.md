# Client contract audit handoff

Task: enumerate V24.01 service calls, arguments, client notification subscriptions, and Host route coverage. Assigned live lane: `eve_p4` / port 26103.

The reusable extractor and focused tests are `tools/spec-extract/client_contract.py` and `tools/spec-extract/test_client_contract.py`. The generated contract is `docs/project/roller/client-contract.md`. It reports 610 service/method pairs, 879 direct/bound callsites, 41 method references, 657 notification names, and 73 client pairs without a parsed Host route. Four extractor tests pass. The scanner handles attributes, aliases, explicit route tables, manual method sets, and service-name switch routes.

Shipcaster production wiring has no landing-pad/faction readers or starter eligibility provider. Missing providers now return an explicit CustomNotify instead of an empty list or false eligibility. EveJS unavailable-destination travel rejections are retained. Both touched .NET test projects built cleanly; ShipcasterRpcTests passed 16/16 and the Host network-dispatch regression passed 1/1.

The live headless populationCap scenario passed on `eve_p4` / port 26103 using the build-3396210 SDE JSONL cache. It compared the bound slot-map shape and rejected move cases with the recorded EveJS scenario. The graphical client was not used.

Structure review corrected a stale handoff: single-character cyno-beacon travel and persisted jump-bridge destination/link reads are wired. EveJS supports group beacon jumps, portal bridging, and Ansiblex travel; Host records explicit unsupported gaps for those transitions.

Current branch includes commit `5fcf3adc` and the merge of `origin/main`. The contract was regenerated after the merge. Remaining: inspect the final status/diff, commit the refreshed contract and validation record, push `HEAD:main`, check CI, fix any red result, then remove task build artifacts that are no longer needed.
