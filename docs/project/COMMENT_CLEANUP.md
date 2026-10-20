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
| `tools/headless-client` (106 Python files; fixtures excluded) | 1173 | 1111 | 2026-10-19 |
| `crates/eve-behaviors/src/actions/tanking.rs` | 26 | 6 | 2026-10-19 |
| `dotnet/src/Eve.Host/Composition/CharacterDeletionPurge.cs` | 10 | 9 | 2026-10-19 |
| `dotnet/src/Eve.Content` (6 C# files) | 142 | 125 | 2026-10-19 |
| `dotnet/src/Eve.Bookmarks` (3 C# files) | 106 | 93 | 2026-10-19 |
| `dotnet/src/Eve.AccessGroups` (2 C# files) | 53 | 48 | 2026-10-19 |
| `dotnet/src/Eve.Agents` (4 C# files) | 85 | 62 | 2026-10-19 |
| `dotnet/src/Eve.Host` (83 C# files) | 1,054 | 832 | 2026-10-19 |
| `dotnet/src/Eve.Services.Core` (45 C# files) | 1,148 | 874 | 2026-10-19 |
| `dotnet/src/Eve.Services.Space` (107 C# files) | 2,959 | 2,562 | 2026-10-19 |
| `tools/dev` (19 Python files) | 106 | 97 | 2026-10-19 |
| `crates/eve-space` (128 Rust files) | 3649 | 3499 | 2026-10-19 |
| `crates/eve-space` (doc-comment spacing pass, 128 Rust files) | 3505 | 3505 | 2026-10-19 |
| `crates/eve-destiny` (4 Rust files) | 185 | 182 | 2026-10-19 |
| `crates/eve-behaviors` (183 Rust files) | 2412 | 2333 | 2026-10-19 |
| `crates/eve-oracle` (8 Rust files) | 105 | 102 | 2026-10-19 |
| `crates/eve-sde` (12 Rust files) | 116 | 115 | 2026-10-19 |
| `dotnet/src/Eve.Characters` (7 C# files) | 92 | 83 | 2026-10-20 |
| `dotnet/src/Eve.Ships` (40 C# files) | 812 | 765 | 2026-10-20 |
| `dotnet/src/Eve.Skills` (10 C# files) | 213 | 207 | 2026-10-20 |
| `dotnet/src/Eve.Wallet` (8 C# files) | 66 | 56 | 2026-10-20 |
| `dotnet/src/Eve.Universe` (12 C# files) | 109 | 101 | 2026-10-20 |
| `dotnet/src/Eve.Trade` (1 C# file) | 47 | 46 | 2026-10-20 |
| `dotnet/src/Eve.Wormholes` (8 C# files) | 114 | 103 | 2026-10-20 |
| `dotnet/src/Eve.Structures` (11 C# files) | 255 | 234 | 2026-10-20 |
| `dotnet/src/Eve.Services.CharacterInfo` (3 C# files) | 59 | 53 | 2026-10-20 |

The stdlib-only `tools/dev/prune-comments.py` identifies Rust, C#, and Python comments, removes process notes, stale prose, and separators, compares code token streams before writing, collapses excess blank lines, keeps remaining Rust doc comments adjacent to their attributes and items, and skips fixture and generated-file paths. `tools/dev/test_prune_comments.py` covers raw and nested strings/comments, attributes, doc spacing, preprocessor directives, and Python string markers.
