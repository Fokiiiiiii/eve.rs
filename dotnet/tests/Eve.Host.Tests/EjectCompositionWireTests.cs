using System.Reflection;
using System.Runtime.CompilerServices;
using Eve.Macho;
using Eve.Marshal;
using Eve.Services;
using Xunit;

namespace Eve.Host.Tests;

/// <summary>
/// Covers only the no-space composition: with spaceContainers null, the production StationRpc wiring registers EjectRpcService
/// with no launch delegate. The present-space branch (a live SpaceContainers) is not covered here.
/// </summary>
public sealed class EjectCompositionWireTests
{
    private const string NoSpaceNotice = "Space is not available right now. Try again in a moment.";

    [Fact]
    public async Task No_space_composition_refuses_a_valid_eject_with_the_space_unavailable_notice()
    {
        var eject = EjectServiceOfNoSpaceComposition();

        var error = await Assert.ThrowsAsync<MachoWrappedException>(() => eject.CallAsync(Call(900, 901)).AsTask());

        Assert.Equal("CustomNotify", error.Error.UserErrorMessage);
        Assert.Equal(NoSpaceNotice, Notify(error));
    }

    [Fact]
    public async Task No_space_composition_checks_the_request_before_the_space_refusal()
    {
        var eject = EjectServiceOfNoSpaceComposition();

        var noCharacter = await Assert.ThrowsAsync<MachoWrappedException>(() => eject.CallAsync(new ServiceCall
        {
            Method = "LaunchShipOrContainerFromWreckOrContainer",
            Args = [new PyLong(900), new PyLong(901)],
            Session = new ClientSession(),
        }).AsTask());
        Assert.Equal("FakeItemNotFound", noCharacter.Error.UserErrorMessage);

        var zeroItem = await Assert.ThrowsAsync<MachoWrappedException>(() => eject.CallAsync(Call(900, 0)).AsTask());
        Assert.Equal("FakeItemNotFound", zeroItem.Error.UserErrorMessage);
    }

    private static ServiceCall Call(long sourceId, long itemId)
    {
        var session = new ClientSession();
        session.Set("charid", new PyInt(20));
        return new ServiceCall
        {
            Method = "LaunchShipOrContainerFromWreckOrContainer",
            Args = [new PyLong(sourceId), new PyLong(itemId)],
            Session = session,
        };
    }

    /// <summary>The eject service the production StationRpc wiring registers when no space host is attached.</summary>
    private static IService EjectServiceOfNoSpaceComposition()
    {
        var host = (CharacterServicesComposition)RuntimeHelpers.GetUninitializedObject(typeof(CharacterServicesComposition));
        // the StationRpc list reads these fields when it is built; spaceContainers stays null
        SetUninitialized(host, "sovereigntyReadModel");
        SetUninitialized(host, "stationGuestLedger");
        SetUninitialized(host, "foundations");
        Assert.Null(typeof(CharacterServicesComposition).GetField("spaceContainers", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(host));

        var stationRpc = typeof(CharacterServicesComposition).GetMethod("StationRpc", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var services = Assert.IsAssignableFrom<IReadOnlyList<IService>>(stationRpc.Invoke(host, null));
        return Assert.Single(services, service => service is EjectRpcService);
    }

    private static void SetUninitialized(CharacterServicesComposition host, string name)
    {
        var field = typeof(CharacterServicesComposition).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!;
        field.SetValue(host, RuntimeHelpers.GetUninitializedObject(field.FieldType));
    }

    private static string Notify(MachoWrappedException error)
    {
        var arguments = ((PyTuple)((PyTuple)((PyObjectEx)error.Error.Payload).Header).Items[1]).Items;
        Assert.Equal("CustomNotify", ((PyStr)arguments[0]).Value);
        var values = ((PyDict)arguments[1]).Entries.ToDictionary(e => ((PyStr)e.Key).Value, e => e.Value);
        return ((PyStr)values["notify"]).Value;
    }
}
