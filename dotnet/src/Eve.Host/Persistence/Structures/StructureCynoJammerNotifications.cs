using Eve.Events;
using Eve.Marshal;
using Eve.Services;
using Eve.Structures;
using Npgsql;

namespace Eve.Host.Persistence.Structures;

/// <summary>
/// E-15: a change of a system cynosural jammer is told to the pilots in that system in space (EveJS sovNotifications broadcastCynoJammerChanged): OnCynoJammerChanged
/// with the system and the state GetCynoJammerState answers (the warm-up FILETIME, 0 once it runs, or None without a jammer).
/// </summary>
internal sealed class StructureCynoJammerNotifications(
    Func<NpgsqlConnection, NpgsqlTransaction?, long, CancellationToken, Task<long?>> jammerState,
    CharacterSessions sessions)
{
    public const string Subscriber = "structure.cyno_jammer_notice";

    public void Subscribe(EventBus events) => events.Subscribe(Subscriber, [StructureTimers.ServiceChangedTopic], async (context, cancellation) =>
    {
        var change = context.Event.Payload<StructureServiceChanged>()!;
        if (change.ServiceId != StructureServiceIds.CynoJammer)
            return;
        var state = await jammerState(context.Connection, context.Transaction, change.SolarSystemId, cancellation);
        foreach (var session in sessions.InSpace(change.SolarSystemId))
            session.SendNotification("OnCynoJammerChanged", "solarsystemid2", new PyLong(change.SolarSystemId), state is { } value ? new PyLong(value) : PyNone.Instance);
    });
}
