# ST-E handoff (structures phase)

Done: E-01 (59e9e89, on origin/main). E-04 proximity end to end (sim scene/decloak.rs, host notice test). E-02 and E-05 host assertions (SpaceServiceTests).
Next: wire the first custom (non-proximity) decloak caller (DecloakCause::Custom, inert until then).
Files: crates/eve-space/src/scene/decloak.rs, scene/session.rs, scene/mod.rs, modules/activation.rs, modules/tests.rs; dotnet/tests/Eve.Services.Space.Tests/SpaceServiceTests.cs; docs/project/TODO.md section 43; docs/reference/compat/DEVIATIONS.md (ST-E section).
Gotchas: no protocol bump needed (existing notify path). Postgres: use /home/user/eve.rs/tools/dev/pg18.sh (the worktree has no pg install). Run heavy builds under flock /tmp/eve-heavy.lock with CARGO_TARGET_DIR=/home/user/eve.rs/target.
