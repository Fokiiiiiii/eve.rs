# Test Rules

1. Test actual player actions and client-observable behavior, not just method existence.
2. Ground expectations in CCP official specifications, matching SDE, V24.01 client consumers and working EveJS v0.12.9 scenarios.
3. Treat captures as test oracles, never as production response data.
4. Verify RPC shapes, Host registration, notification order, state changes and persistence.
5. Exercise success, rejection, concurrency, retries and restart when applicable.
6. Use different inputs and states to detect hardcoded answers.
7. Never loosen assertions just to make tests pass.
8. Distinguish PASS, FAIL, SKIPPED and not performed. Add regression coverage for every repaired defect.
