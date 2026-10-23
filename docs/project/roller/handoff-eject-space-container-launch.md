# Handoff: launch a ship or container from a space container

## Scope
Implement the V24.01 eject.LaunchShipOrContainerFromWreckOrContainer action from the client RPC through authoritative inventory state and simulation placement.

## Findings
- The client passes (source container ID, item ID) to the global eject service.
- Both checked EveJS runtimes move an assembled singleton ship or a single container out of a visible source container, place it beside the source, synchronize inventory state, and refresh the source.
- Space source visibility, range, access rights, and item custody belong to SpaceContainers; inventory moves and the durable pose are committed in one PostgreSQL transaction.
- SpaceSessions owns spawning launched unpiloted ships. Space containers and wrecks use the existing simulation placement path.
- EVE University container guidance confirms assembled containers can be launched from cargo and station containers cannot be launched.

## Implementation in this worktree
- Host wires the RPC callback to SpaceContainers; an absent backend returns a client-visible CustomNotify.
- SDE-backed category/group checks permit only assembled ships and one container.
- The transaction locks and revalidates the source and contained item, moves the item to the system, and saves its launch pose, kind, launcher, and source container ID.
- A per-target gate serializes retries; persisted source identity prevents replay from a different source.
- A simulation placement failure leaves durable state available for retry. A retry completes placement and sends the item and source-container notifications.
- Launched ships are restored from their durable pose after a simulation restart.
- Tests cover Host forwarding and missing-backend errors, ship and container launch, unsupported stacks, concurrent requests, retry after placement failure, notification order, and restore.

## Verification and next steps
Compile checks are running for the changed .NET projects. PostgreSQL is not used locally. The database-backed space tests and the new live headless walk must be run through GitHub Actions. The TODO row stays open until those checks pass.
