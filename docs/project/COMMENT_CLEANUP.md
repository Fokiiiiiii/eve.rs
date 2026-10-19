# Comment cleanup

| Area | Comment lines before | Comment lines after | Date |
| --- | ---: | ---: | --- |
| `crates/eve-sde` (13 Rust files present) | 167 | 138 | 2026-10-19 |
| `crates/eve-net` (14 Rust files present) | 208 | 154 | 2026-10-19 |
| `crates/eve-bench` (1 Rust file present) | 20 | 5 | 2026-10-19 |
| `crates/eve-marshal` (12 Rust files present) | 440 | 418 | 2026-10-19 |
| `crates/eve-sim` (4 Rust files present) | 28 | 19 | 2026-10-19 |
| `crates/eve-sde/src/fetch.rs` | 49 | 27 | 2026-10-19 |
| `dotnet/src/Eve.Market` (10 C# files) | 108 | 100 | 2026-10-19 |
| `crates/eve-space/src/modules/activation.rs` | 103 | 94 | 2026-10-19 |
| `tools/client-patch/patch_client.py` | 5 | 1 | 2026-10-19 |
| `tools/headless-client/evecli/client.py` | 5 | 3 | 2026-10-19 |
| `crates/eve-behaviors/src/actions/tanking.rs` | 26 | 6 | 2026-10-19 |
| `dotnet/src/Eve.Host/Composition/CharacterDeletionPurge.cs` | 10 | 9 | 2026-10-19 |
| `tools/dev` (19 Python files) | 106 | 97 | 2026-10-19 |
| `crates/eve-space` (128 Rust files) | 3649 | 3499 | 2026-10-19 |
| `crates/eve-destiny` (4 Rust files) | 185 | 182 | 2026-10-19 |
| `crates/eve-behaviors` (183 Rust files) | 2412 | 2333 | 2026-10-19 |
| `crates/eve-oracle` (8 Rust files) | 105 | 102 | 2026-10-19 |
| `crates/eve-sde` (12 Rust files) | 116 | 115 | 2026-10-19 |

The stdlib-only `tools/dev/prune-comments.py` identifies Rust, C#, and Python comments, removes process notes, stale prose, and separators, and compares code token streams before writing. `tools/dev/test_prune_comments.py` covers raw and nested strings/comments, attributes, preprocessor directives, and Python string markers.
