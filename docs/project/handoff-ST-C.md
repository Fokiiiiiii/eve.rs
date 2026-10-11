# Handoff: ST-C in-space compression (ship compressor module)

Branch wt-stc (worktree /home/user/wt-stc). Increment 1 pushed to main with this commit.

## Done (green)
- CompressionHostTests: 11/11 passed (clean run, Eve.Services.Space.Tests).
- ModuleEffectsTests (includes StructureModuleEffectsTests): 28/28 passed.
- Effect body CompressionModuleEffect; facilities CompressionFacilities; CompressInSpaceAsync (named CustomNotify refusals, fleet, range, type list 3255, one inventory transaction); 6-value tuple wired in ItemRpc.cs.
- Refusal texts corrected (no structure or phase 3 wording). Gap manifest drops industrialItemCompression.
- TODO row for inSpaceCompressionMgr.CompressItemInSpace rewritten; DEVIATIONS section In-space compression (ST-C) added.

## Increment 2 (verified on the rebased tree)
- OperationalRpcServiceTests (Eve.Host.Tests): 12/12 passed. Also Eve.Services.Space.Tests CompressionHostTests + ModuleEffectsTests: 39/39 passed.

## Open (not in ST-C scope)
- Graphical-client run of the compress action (unverified).

## Gotchas
- Lock: always flock -o -w 1800 /tmp/eve-heavy.lock; dotnet test with -p:UseSharedCompilation=false, then dotnet build-server shutdown (a build server holding the fd stalls every session).
- Sim distance is computed on the C# side from PositionOfAsync (no Rust change).
- Serena MCP timed out in this session; Read and Bash were the fallback.
