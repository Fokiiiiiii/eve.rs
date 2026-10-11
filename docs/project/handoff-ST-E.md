# ST-E handoff (structures phase)

Done: E-01 (59e9e89, on origin/main). E-04 proximity end to end (sim scene/decloak.rs, host notice test). E-02 and E-05 host assertions (SpaceServiceTests).
Status: ST-E done except E-04 custom, blocked on the Mobile Observatory remote decloak ping (not in eve.rs; separate package). DecloakCause::Custom stays inert. Full suites: eve-space lib 711 passed; Eve.Services.Space.Tests 618 passed.
Files: crates/eve-space/src/scene/decloak.rs, scene/session.rs, scene/mod.rs, modules/activation.rs, modules/tests.rs; dotnet/tests/Eve.Services.Space.Tests/SpaceServiceTests.cs; docs/project/TODO.md section 43; docs/reference/compat/DEVIATIONS.md (ST-E section).
Gotchas: no protocol bump needed (existing notify path). Postgres: use /home/user/eve.rs/tools/dev/pg18.sh (the worktree has no pg install). Run heavy builds under flock /tmp/eve-heavy.lock with CARGO_TARGET_DIR=/home/user/eve.rs/target.
