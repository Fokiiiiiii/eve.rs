using System.Runtime.CompilerServices;
using Eve.Macho;
using Eve.Marshal;
using Eve.Services;

namespace Eve.Services.Core;

/// <summary>
/// The V24 Insider asset-holding toggles. EveJS v0.12.9 keeps these values in
/// a WeakMap keyed by the connected session; they are QA controls and do not
/// change inventory validation or failure handling.
/// </summary>
[Service("inventoryAssetHoldingMgr")]
public sealed partial class InventoryAssetHoldingMgrService
{
    private sealed class SessionState
    {
        public readonly object Gate = new();
        public bool ItemFailureEnabled;
        public bool ItemValidationEnabled = true;
    }

    private readonly ConditionalWeakTable<ClientSession, SessionState> _states = new();

    [Method("is_item_failure_enabled")]
    public PyValue IsItemFailureEnabled(ServiceCall call) =>
        PyValue.Of(Read(call.Session, static state => state.ItemFailureEnabled));

    [Method("set_item_failure_enabled")]
    public PyValue SetItemFailureEnabled(ServiceCall call)
    {
        Update(call, static state => state.ItemFailureEnabled,
            static (state, enabled) => state.ItemFailureEnabled = enabled);
        return PyNone.Instance;
    }

    [Method("is_item_validation_enabled")]
    public PyValue IsItemValidationEnabled(ServiceCall call) =>
        PyValue.Of(Read(call.Session, static state => state.ItemValidationEnabled));

    [Method("set_item_validation_enabled")]
    public PyValue SetItemValidationEnabled(ServiceCall call)
    {
        Update(call, static state => state.ItemValidationEnabled,
            static (state, enabled) => state.ItemValidationEnabled = enabled);
        return PyNone.Instance;
    }

    private void Update(ServiceCall call, Func<SessionState, bool> read, Action<SessionState, bool> write)
    {
        var state = _states.GetValue(call.Session, static _ => new SessionState());
        lock (state.Gate)
        {
            var fallback = read(state);
            var value = call.Args.Count == 0 ? null : call.Arg(0);
            write(state, NormalizeBoolean(value, fallback));
        }
    }

    private bool Read(ClientSession session, Func<SessionState, bool> read)
    {
        var state = _states.GetValue(session, static _ => new SessionState());
        lock (state.Gate)
            return read(state);
    }

    private static bool NormalizeBoolean(PyValue? value, bool fallback) => value switch
    {
        PyBool boolean => boolean.Value,
        PyInt number => number.Value != 0,
        PyLong number => number.Value != 0,
        PyReal number => number.Value != 0,
        PyStr text => text.Value.Trim().ToLowerInvariant() switch
        {
            "true" or "1" or "yes" or "on" => true,
            "false" or "0" or "no" or "off" => false,
            _ => fallback,
        },
        _ => fallback,
    };
}
