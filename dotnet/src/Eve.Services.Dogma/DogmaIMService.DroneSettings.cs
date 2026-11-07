using Eve.Macho;
using Eve.Marshal;

namespace Eve.Services.Dogma;

/// <summary>The drone aggression and focus-fire values the client changes through <c>ChangeDroneSettings</c>.</summary>
public sealed partial class DogmaIMService
{
    [Method("ChangeDroneSettings")]
    public async ValueTask<PyValue> ChangeDroneSettings(ServiceCall call)
    {
        if (BoundContextOf(call) is null || call.Session.CharacterId is not { } character || updateDroneSettings is null)
            return None;

        var validAttributes = new[]
        {
            statics.AttributeId("droneIsAgressive"),
            statics.AttributeId("droneFocusFire"),
        }.OfType<int>().ToHashSet();
        var changes = new Dictionary<int, bool>();
        if (call.Arg(0) is PyDict settings)
        {
            foreach (var (key, value) in settings.Entries)
            {
                if (IdOf(key) is not { } rawAttribute || rawAttribute < 0 || rawAttribute > int.MaxValue
                    || !validAttributes.Contains((int)rawAttribute) || BoolOf(value) is not { } enabled)
                    continue;
                changes[(int)rawAttribute] = enabled;
            }
        }

        await updateDroneSettings(character, changes, call.Cancellation);
        return PyBool.True;
    }

    private static bool? BoolOf(PyValue value) => value switch
    {
        PyBool boolean => boolean.Value,
        PyInt integer => integer.Value != 0,
        PyLong integer => integer.Value != 0,
        _ => null,
    };
}
