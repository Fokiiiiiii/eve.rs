# Corporation and alliance provenance

The domain shapes, corporation creation cost (1599800 ISK), member/application states, title and role masks and
role-update rules are ported from Fokiiiiiii/evejs-v0.12.9 commit 828823a75f211fdecb945b087a26c225f111491f:
server/src/services/corporation/{corporationState,corporationRuntimeState,corpRegistryRuntime,allianceRegistryRuntime}.js.
Client contracts are checked against the private eve-logic V24.01 snapshot (1dbb571):
eve/client/script/ui/services/corporation/bco_members.py and eve/common/lib/corporations.py.
No client source or client schemas are copied into this project.

Persistence is new typed PostgreSQL storage under this module's own schema (ADR 0003); wallet settlement is delegated
to Eve.Wallet in the same transaction. No combat/simulation project is referenced. Client adapters and alliance
lifecycle integration are still in progress; the module is not a completed S5 corporation/alliance service.
