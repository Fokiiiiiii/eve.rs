using System.Runtime.CompilerServices;
using Eve.FactionWarfare;
using Eve.Macho;
using Eve.Marshal;
using Eve.Services;
using Eve.Services.Space;
using Eve.Ships;
using Eve.Universe;

namespace Eve.Host;

/// <summary>Executes the V24 shipcaster travel calls against the published network and persistent starter-use state.</summary>
internal sealed class ShipcasterTravel(
    PgShipcasterStore store,
    SpaceSessions? space,
    SolarSystemCatalog systems,
    Func<long, CancellationToken, ValueTask<long>> warFactionOf,
    Func<long> now)
{
    private const double InteractionRangeMeters = 5_000;
    private const string RegularFailure = "No active shipcaster destination is available.";
    private const string StarterFailure = "No starter shipcaster destination is available.";

    private sealed record AcceptedJump(
        string Method,
        long LauncherId,
        int? DestinationSystemId,
        long? DestinationLandingPadId,
        int SourceSystemId,
        PyValue Result);

    private sealed class SessionJumpState
    {
        public SemaphoreSlim Gate { get; } = new(1, 1);
        public AcceptedJump? Accepted { get; set; }
    }

    private readonly ConditionalWeakTable<ClientSession, SessionJumpState> _sessionJumps = new();

    public async ValueTask<bool> CanCharacterJumpAsync(ServiceCall call)
    {
        if (call.Session.CharacterId is not { } characterId || characterId <= 0 ||
            !await store.NetworkPublishedAsync(call.Cancellation).ConfigureAwait(false))
            return false;

        var currentSystem = space?.SolarSystemOf(call.Session) is { } inSpace
            ? inSpace
            : call.Session.GetInt("solarsystemid");
        return currentSystem is > 0 and <= int.MaxValue &&
            await store.HasUnusedStarterAtAsync(characterId, (int)currentSystem.Value, call.Cancellation).ConfigureAwait(false);
    }

    public async ValueTask<PyValue> TravelAsync(ServiceCall call)
    {
        var launcherId = PositiveId(call.Arg(0));
        if (launcherId is null)
            throw Notify(call.Method == "CmdJumpThroughShipcaster" ? RegularFailure : StarterFailure);

        var request = call.Method == "CmdJumpThroughShipcaster"
            ? new AcceptedJump(call.Method, launcherId.Value, PositiveSystem(call.Arg(1)), PositiveId(call.Arg(2)), 0, PyNone.Instance)
            : new AcceptedJump(call.Method, launcherId.Value, null, null, 0, PyNone.Instance);
        var state = _sessionJumps.GetValue(call.Session, static _ => new SessionJumpState());
        await state.Gate.WaitAsync(call.Cancellation).ConfigureAwait(false);
        try
        {
            if (state.Accepted is { } accepted)
            {
                if (space?.SolarSystemOf(call.Session) == accepted.SourceSystemId)
                {
                    if (accepted.Method == request.Method && accepted.LauncherId == request.LauncherId &&
                        (request.Method == "CmdJumpThroughStarterShipcaster" ||
                            (accepted.DestinationSystemId == request.DestinationSystemId &&
                             accepted.DestinationLandingPadId == request.DestinationLandingPadId)))
                        return accepted.Result;
                    throw Notify("Another shipcaster jump is still pending.");
                }

                state.Accepted = null;
            }

            return call.Method == "CmdJumpThroughShipcaster"
                ? await RegularJumpAsync(call, request, state).ConfigureAwait(false)
                : await StarterJumpAsync(call, request, state).ConfigureAwait(false);
        }
        finally
        {
            state.Gate.Release();
        }
    }

    private async ValueTask<PyValue> RegularJumpAsync(ServiceCall call, AcceptedJump request, SessionJumpState state)
    {
        if (request.DestinationSystemId is not { } destinationSystemId ||
            request.DestinationLandingPadId is not { } destinationPadId)
            throw Notify(RegularFailure);
        var pilot = PilotOf(call, RegularFailure);
        var network = await store.TravelNetworkStateAsync(
            request.LauncherId, Py.FileTimeFromUnixMs(now()), call.Cancellation).ConfigureAwait(false);
        var launcher = network?.Launcher;
        if (launcher is null || launcher.IsStarter || launcher.SolarSystemId != pilot.SystemId)
            throw Notify(RegularFailure);
        await EnsureWithinInteractionRangeAsync(call, launcher.ItemId, RegularFailure).ConfigureAwait(false);

        var target = network?.ActiveTarget;
        if (target is null || target.SolarSystemId != destinationSystemId || target.LandingPadId != destinationPadId)
            throw Notify(RegularFailure);
        var pad = network?.LandingPad;
        if (pad is null || !pad.IsBuilt || pad.IsDisrupted || pad.LinkTimestamp is null ||
            pad.SolarSystemId != target.SolarSystemId || pad.FactionId != target.FactionId ||
            !FwFactions.IsWarfareFaction(target.FactionId))
            throw Notify(RegularFailure);

        var warFaction = await warFactionOf(pilot.CharacterId, call.Cancellation).ConfigureAwait(false);
        if (!FwFactions.IsWarfareFaction(warFaction) || warFaction != target.FactionId ||
            (launcher.FactionId != warFaction && !launcher.ExtraLinkedFactionIds.Contains((int)warFaction)))
            throw Notify(RegularFailure);

        var result = await (space ?? throw Notify(RegularFailure)).StructureJumpAsync(
            call.Session, launcher.ItemId, target.SolarSystemId, target.LandingPadId, call.BoundObjectId, call.Cancellation);
        state.Accepted = request with
        {
            DestinationSystemId = target.SolarSystemId,
            DestinationLandingPadId = target.LandingPadId,
            SourceSystemId = pilot.SystemId,
            Result = result,
        };
        return result;
    }

    private async ValueTask<PyValue> StarterJumpAsync(ServiceCall call, AcceptedJump request, SessionJumpState state)
    {
        var pilot = PilotOf(call, StarterFailure);
        var network = await store.TravelNetworkStateAsync(request.LauncherId, nowFileTime: null,
            cancellationToken: call.Cancellation).ConfigureAwait(false);
        var launcher = network?.Launcher;
        if (launcher is not { IsStarter: true, DestinationSolarSystemId: { } destinationSystemId,
                DestinationLandingPadId: { } destinationPadId } || launcher.SolarSystemId != pilot.SystemId)
            throw Notify(StarterFailure);
        await EnsureWithinInteractionRangeAsync(call, launcher.ItemId, StarterFailure).ConfigureAwait(false);
        var pad = network?.LandingPad;
        if (pad is null || !pad.IsBuilt || pad.IsDisrupted || pad.SolarSystemId != destinationSystemId ||
            systems.Get(destinationSystemId) is null)
            throw Notify(StarterFailure);

        if (!await store.ClaimStarterJumpAsync(pilot.CharacterId, launcher.ItemId, destinationSystemId,
                destinationPadId, now(), call.Cancellation).ConfigureAwait(false))
            throw Notify(StarterFailure);

        var completed = false;
        try
        {
            var result = await (space ?? throw Notify(StarterFailure)).StructureJumpAsync(
                call.Session, launcher.ItemId, destinationSystemId, destinationPadId, call.BoundObjectId,
                call.Cancellation, async cancellationToken =>
                {
                    if (!await store.CompleteStarterJumpAsync(pilot.CharacterId, now(), cancellationToken).ConfigureAwait(false))
                        throw new InvalidOperationException("The accepted starter shipcaster jump lost its persistent claim.");
                    completed = true;
                });
            state.Accepted = request with
            {
                DestinationSystemId = destinationSystemId,
                DestinationLandingPadId = destinationPadId,
                SourceSystemId = pilot.SystemId,
                Result = result,
            };
            return result;
        }
        catch
        {
            if (!completed)
                await store.ReleaseStarterJumpAsync(pilot.CharacterId, CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    private async ValueTask EnsureWithinInteractionRangeAsync(ServiceCall call, long launcherId, string failure)
    {
        var distance = await (space ?? throw Notify(failure))
            .SurfaceDistanceToItemAsync(call.Session, launcherId, call.Cancellation).ConfigureAwait(false);
        if (distance is null || !double.IsFinite(distance.Value) || distance.Value > InteractionRangeMeters + 1)
            throw Notify(failure);
    }

    private (long CharacterId, int SystemId) PilotOf(ServiceCall call, string failure)
    {
        if (space is null || call.Session.CharacterId is not { } characterId || characterId <= 0 ||
            space.ShipIdOf(call.Session) is null || !space.IsInSpace(call.Session) ||
            space.SolarSystemOf(call.Session) is not { } systemId || systems.Get(systemId) is null)
            throw Notify(failure);
        return (characterId, systemId);
    }

    private static long? PositiveId(PyValue value) => value switch
    {
        PyInt integer when integer.Value > 0 => integer.Value,
        PyLong integer when integer.Value > 0 => integer.Value,
        _ => null,
    };

    private static int? PositiveSystem(PyValue value) => value switch
    {
        PyInt integer when integer.Value is > 0 and <= int.MaxValue => (int)integer.Value,
        PyLong integer when integer.Value is > 0 and <= int.MaxValue => (int)integer.Value,
        _ => null,
    };

    private static MachoWrappedException Notify(string text) =>
        MachoErrors.ThrowUserError("CustomNotify", ("notify", new PyStr(text)));
}
