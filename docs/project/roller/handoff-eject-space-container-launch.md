# Handoff: launch a ship or container from a space container

## Scope
Implement the V24.01 eject.LaunchShipOrContainerFromWreckOrContainer action from the client RPC through authoritative inventory state and simulation placement.

## Findings
- The client passes (source container ID, item ID) to the global eject service.
- Both checked EveJS runtimes move an assembled singleton ship or a single container out of a visible source container, place it beside the source, synchronize inventory state, and refresh the source.
- Space source visibility, range, access rights, and item custody belong to SpaceContainers; inventory moves and the durable pose are committed in one PostgreSQL transaction.
- SpaceSessions owns spawning launched unpiloted ships. Space containers and wrecks use the existing simulation placement path.
- EVE University container guidance confirms assembled containers can be launched from cargo and station containers cannot be launched.

## Implementation
- Host wires the RPC callback to SpaceContainers; an absent backend returns a client-visible CustomNotify.
- SDE-backed category/group checks permit only assembled ships and one container.
- The transaction locks and revalidates the source and contained item, moves the item to the system, and saves its launch pose, kind, launcher, and source container ID.
- A per-target gate serializes retries; persisted source identity prevents replay from a different source.
- A simulation placement failure leaves durable state available for retry. A retry completes placement and sends the item and source-container notifications.
- Launched ships are restored from their durable pose after a simulation restart.
- Tests cover Host forwarding and missing-backend errors; ship and container launch; unsupported stacks; distant, absent, and inaccessible requests; concurrent requests; retry after placement failure; notification order; and restore. The live headless walk is tools/headless-client/tests/test_eject_live.py.

## Verification
- Earlier local compile checks succeeded for Eve.Ships, Eve.Services.Space, Eve.Host, Eve.Services.Space.Tests, and Eve.Host.Tests. Resource pressure required disabling analyzers and building references separately; a regular Space build hit memory pressure. Rechecks after the final fixture and Host registry corrections were cancelled because concurrent worktrees saturated the local machine, so the latest edits still need Actions compile/test confirmation.
- Passed locally: the focused Host missing-backend test, 1 passed, 0 skipped.
- No PostgreSQL test or local headless live test has run.
- Actions run 39974732941 reported 22 Space tests passed and 2 failed (0 skipped); run 39976590136 reproduced 22 passed and 2 failed (0 skipped). The container restart test was still comparing the first `SimChangeWreck`, which belongs to the original jettison; it now compares the final source refresh after launch. The access test had moved its source container's location to its own ID while transferring ownership; it now keeps the source in the solar system so the expected `SafetyActivated` check is reached. The changes are in commit `9d2b4778`; focused DB tests and the live walk are running together in Actions run `39983782391`.
- Push-triggered CI on 21312dec passed Rust tests, Rust format/clippy, advisory Rust, fixtures, Python client, and .NET build. Its live stack and Windows host startup failed at `CharacterServicesComposition.CharacterServiceList`: the inventory broker is wrapped by `IndustryInventoryChangePublisher`, while repackaging looked for a raw `InvBrokerService`. Commit `9d2b4778` passes the wrapped broker's inner service to repackaging. Space test shard 2/2 also failed on the two focused launch-test issues described above. An earlier push run (39974698122) had separate host SSO and market RPC budget failures; check the latest push CI for their status. The older separately dispatched live walk (39976626454) is still in progress, and run `39983782391` combines the focused DB tests and new live walk. Keep the TODO row open until focused database tests, the live walk, and relevant push-triggered checks pass.
