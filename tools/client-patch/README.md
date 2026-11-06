# Client preparation for eve.rs

This folder points **your own** EVE Online client, build 3396210 (V24.01), at a
local or LAN eve.rs server. It contains scripts and documentation only. It does
not contain, download or modify any CCP binary, resource or cache file, and it
must never be pointed at a live EVE install.

Status: the patcher is unit-tested with synthetic client folders. It has **not**
been run against a real client. This environment has no Windows, no PowerShell
and no client files, so the launch helpers and the client's acceptance of the
settings below are untested.

## What must be changed, and why

Only the first two rows change files; the rest are launch arguments and one
environment variable.

| # | Change | Why | Evidence |
|---|---|---|---|
| 1 | `start.ini`: `server = <IPv4>` (and `serverip` if the key exists) | The client's built-in server names are `<name>.servers.eveonline.com`, so the address has to be redirected. | eve-logic `serverInfo.py` lines 9-24 (`_GetServerIP`). EveJS sets the same key: `tools/ClientSETUP/ClientSetup.ps1` lines 229-237 and `doc/OFFLINE.md` ("`server = 127.0.0.1`"). |
| 2 | `start.ini`: `cryptoPack = Placebo` | eve.rs speaks only the Placebo pack and refuses others. | `crates/eve-net/src/handshake.rs` lines 1-7 and 287; `crates/eve-net/tests/handshake.rs` lines 285-297; EveJS `ClientSetup.ps1` lines 229-231. |
| 3 | Launch argument `/port:26000` | The game port. eve-net listens on `127.0.0.1:26000` by default. | EveJS `Play.bat` line 214; `crates/eve-net/src/bin/eve-net.rs` line 39. |
| 4 | Launch argument `/noCrashReportUpload` | Turns off crash upload at launch. | EveJS `Play.bat` line 214 and `doc/OFFLINE.md` ("Both launch paths pass `/noCrashReportUpload`"). |
| 5 | Environment `EO_REMOTEFILECACHEFOLDER` = the `ResFiles` folder beside `tq` | The client reads its resources from this local cache. | EveJS `Play.bat` line 592; `tools/headless-client/evecli/__main__.py` (`_launch_official_client`). |

The `start.ini` edit is backed up to `start.ini.eve-rs-backup` before the first
change. `--restore` puts it back exactly.

## What is not changed, and why

EveJS applies more than this. None of the following is ported, for these reasons:

- **`bin64/blue.dll` (required by EveJS).** `Play.bat` lines 284-365 refuse to
  launch unless blue.dll reports `already_patched`. The patch changes one branch
  (`0F 84` to `0F 85`, JE to JNE, at file offset `0x001F4DE9`) and strips the
  Authenticode blob (`tools/ClientSETUP/blue_patch_recipe.json`,
  `blue-dll.patch.json`). EveJS does not say which check the branch serves, and
  no source in this repository shows it. Porting a byte patch of an unknown check
  would break the "unmodified client" goal, so this tool does not do it. This is
  the first thing to test on a real client (see Open questions).
- **`bin64/launchdarkly_stackless_client_sdk.pyd`.** EveJS rewrites six endpoint
  strings to `https://localhost` (`launchdarkly_patch_recipe.json`). This only
  contains outbound traffic. The client falls back to its built-in feature
  defaults (`doc/OFFLINE_CLIENT_FLAGS.md`). Nothing shows that the connection
  needs it.
- **`bin64/exefile.exe`.** EveJS rewrites the minidump upload URL to loopback
  (`crash_upload_patch_recipe.json`, offset `0x00063C70`). Containment only.
  Crash upload is also stopped by `/noCrashReportUpload` and by the preference
  `breakpadUpload` (eve-logic `carbon/common/lib/autoexec_client_core.py` line 128).
- **Certificates** (`tools/ClientSETUP/scripts/Install-EvEJSCerts.ps1`). It adds
  a CA to `CurrentUser\Root` and appends it to every `cacert.pem`. EveJS needs
  this for its own HTTPS gateway. eve.rs has no TLS listener (its crates have no
  server-side TLS; only the SDE download and ESI calls use HTTPS).
- **Windows Firewall rules** (`Set-EvEJSFirewall.ps1`, blocks for `exefile.exe`
  and `eve_crashmon.exe`). Optional containment in the operating system, not a
  client file. Not created by this tool.
- **Preferences** (`prefs.ini` under `%LOCALAPPDATA%\CCP\EVE\...\settings`): EveJS
  sets a loopback Sentry DSN and `breakpadUpload=0`. Not written here.
- **Display settings** (`PrepareClientSettings.ps1`). Not needed.

EveJS also contains inconsistent blue.dll expectations: the recipe gives size
12078736 and hash `24e8...`, the delta file gives size 12078728 and hash `325d...`,
and `Play.bat` checks size 12068352 with hash `7f8e...`. This tool does not use
any of them and does not inherit them.

## Prerequisites

- Your own V24.01 client, build 3396210, installed from the official launcher.
  Use a **copy** of the client folder: the tool edits `start.ini` in place.
- The full shared cache. `ResFiles` must sit beside the `tq` folder, with
  `index_tranquility.txt`. The launcher refuses to start without it.
- Python 3.8 or newer (the patcher uses only the standard library).
- An eve.rs server listening on the chosen port, for example
  `docker compose up` (port 26000 is published) or `eve-net` on `127.0.0.1:26000`.
- Close the EVE client before changing its files.

## Use

```bat
rem Windows, in the repository root
python tools\client-patch\patch_client.py --client-dir "C:\EVE\SharedCache\tq" --server 127.0.0.1:26000 --dry-run
python tools\client-patch\patch_client.py --client-dir "C:\EVE\SharedCache\tq" --server 127.0.0.1:26000
tools\client-patch\launch-client.bat -ClientDir "C:\EVE\SharedCache\tq" -Port 26000
```

```sh
# Linux (client under Wine; untested here)
python3 tools/client-patch/patch_client.py --client-dir /path/to/tq --server 127.0.0.1:26000
tools/client-patch/launch-client.sh /path/to/tq 26000
```

`--server` takes an IPv4 literal, with an optional `:PORT`. Host names are not
resolved. The port is **not** written to `start.ini`; give the same port to the
launcher. The default is 26000 everywhere.

To route through a capture proxy (for example `tools/mitm`, on the debug-tools
branch and not required here), start the proxy so it listens on a port and
forwards to eve.rs, then pass that port to the launcher:
`launch-client.bat -ClientDir "..." -Port 26200`.

## Verify the change

1. `patch_client.py --client-dir ... --dry-run` after applying prints
   `[ok] start.ini already points at 127.0.0.1:26000 ...` and exits with code 0.
2. In `start.ini`, the lines `server = 127.0.0.1` and `cryptoPack = Placebo` are
   present; the line `build = 3396210` is unchanged.
3. While the client is at the login screen, `netstat -ano | findstr 26000`
   (Windows) should show the client connected to the eve.rs port.

Verifying the server side alone does not need a client. The headless client in
`tools/headless-client` connects to the same port with the same Placebo handshake.

## Restore

```bat
python tools\client-patch\patch_client.py --client-dir "C:\EVE\SharedCache\tq" --restore
```

This replaces `start.ini` with the backup taken before the first change and
removes the backup. Any edit made to `start.ini` after the patch is lost.

## Safety checks in the patcher

The patcher changes nothing unless all of these hold:

- `start.ini` has exactly one `build = 3396210` line.
- `bin64/exefile.exe` is 1003152 bytes and has SHA-256
  `2aaf7a9a8dfcde85e4adb50c1eccd3756a4d29aeb854dfe69629846ba56ee979`, the
  unmodified build-3396210 executable (the size and hash come from EveJS's
  crash recipe, as fingerprints only).
- `--server` is an IPv4 literal that is neither unspecified, multicast nor broadcast.

Refusals exit with code 1 and print the reason. Nothing is written on a refusal.

## Tests

```bat
python -m unittest discover -s tools\client-patch\tests -v
```

The tests use synthetic client folders in a temporary directory. They cover:
the apply with CRLF and LF endings, the serverip alias, backup taken once,
idempotence, exact restore, restore without a backup, wrong build, modified
executable, dry-run and server-argument rules. They never read a CCP file.

## Open questions and known limits

- **Does the unmodified client connect to eve.rs?** Not established. The first
  real-client test should check: login reaches eve.rs; whether `start.ini`
  `server` is read by V24.01 (EveJS relies on it; eve.rs's headless tool passes
  `/server:` on the command line instead, and that path is also untested against
  a client); and whether the client refuses to continue without the blue.dll
  change. If the blue.dll change is needed, record it as a deviation in
  `docs/reference/compat/DEVIATIONS.md` with evidence. Do not commit a patched
  binary.
- Missing resource files: `/resfileserver` is not passed. Without it the client
  uses its built-in resource host (eve-logic `autoexec_client_core.py` lines
  151-162) for anything not in the cache. Outbound traffic to CCP is therefore
  not blocked by this tool.
- LaunchDarkly, Sentry and the other client endpoints are not blocked. Windows
  Firewall rules are the user's choice (see EveJS `doc/OFFLINE.md`).
- The launchers do not verify `exefile.exe` (the patcher does). `launch-client.ps1`
  and `.bat` were not executed (no PowerShell here). `launch-client.sh` was run
  only against a synthetic folder and a fake `wine`.
- IPv4 only. The port is not stored in `start.ini`; keep the launcher port and
  the server's port in step.
- The patcher edits a file in place. Keep a copy of the client and do not point
  it at a live install.

## Relationship to the goal "unmodified client" (AGENTS.md)

The tool leaves every client binary, resource and cache file byte-identical.
It changes one text configuration file (`start.ini`: two keys) and passes two
launch arguments and one environment variable. It does not ship or download
any CCP file. Whether a configuration edit counts as "unmodified" is a judgement;
the launch arguments alone would avoid it, but the sources do not show that the
client accepts a server address from the command line.

The sources do not show that the unmodified client connects without EveJS's
blue.dll change. That is the open point. Until a real-client test shows it, the
claim "unmodified client works with eve.rs" stays unverified.
