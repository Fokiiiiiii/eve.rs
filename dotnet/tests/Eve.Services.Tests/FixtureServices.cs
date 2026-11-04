using Eve.Macho;
using Eve.Marshal;

namespace Eve.Services.Tests;

/// <summary>
/// The C# twin of FixtureService in tools/oracle/serviceFixtures.js: the same methods, answering the same
/// way. The golden dispatch fixtures were recorded from the JS one.
/// </summary>
[Service("fixtureSvc", Aliases = ["fixtureAlias"])]
public sealed partial class FixtureService
{
    private static PyValue Tuple(params PyValue[] items) => new PyTuple(items);

    private static PyValue Received(IReadOnlyList<PyValue> args) => new PyTuple([.. args.Select(JsSemantics.AsReceived)]);

    [Method("Echo")]
    public PyValue Echo(ServiceCall call) => Received(call.Args);

    [Method("Null")]
    public PyValue Null(ServiceCall call) => PyNone.Instance;

    [Method("Undefined")]
    public PyValue Undefined(ServiceCall call) => PyNone.Instance;

    [Method("Int")]
    public PyValue Int(ServiceCall call) => new PyInt(42);

    [Method("Negative")]
    public PyValue Negative(ServiceCall call) => new PyInt(-100000);

    [Method("Real")]
    public PyValue Real(ServiceCall call) => new PyReal(1.5);

    [Method("True")]
    public PyValue True(ServiceCall call) => PyBool.True;

    [Method("Text")]
    public PyValue Text(ServiceCall call) => new PyStr("hello");

    [Method("TableText")]
    public PyValue TableText(ServiceCall call) => new PyStr("beyonce");

    [Method("Wide")]
    public PyValue Wide(ServiceCall call) => new PyWString("日本語");

    [Method("Bytes")]
    public PyValue Bytes(ServiceCall call) => new PyBuffer(new byte[] { 1, 2, 3 });

    [Method("Long")]
    public PyValue Long(ServiceCall call) => new PyLong(1L << 40);

    [Method("List")]
    public PyValue List(ServiceCall call) => new PyList([new PyInt(1), new PyStr("two"), new PyReal(3.5)]);

    [Method("Dict")]
    public PyValue Dict(ServiceCall call) =>
        new PyDict([new(new PyStr("a"), new PyInt(1)), new(new PyStr("b"), PyNone.Instance)]);

    [Method("KeyVal")]
    public PyValue KeyVal(ServiceCall call) => Py.KeyVal(("name", new PyStr("x")), ("n", new PyInt(7)), ("flag", PyBool.False));

    [Method("Kwargs")]
    public PyValue Kwargs(ServiceCall call) => JsSemantics.AsReceived(call.Kwargs);

    [Method("Async")]
    public async Task<PyValue> AsyncResult(ServiceCall call)
    {
        await Task.Yield();
        return new PyList([new PyInt(1), new PyInt(2), new PyInt(3)]);
    }

    [Method("AsyncBoom")]
    public async Task<PyValue> AsyncBoom(ServiceCall call)
    {
        await Task.Yield();
        throw new InvalidOperationException("async boom");
    }

    [Method("Boom")]
    public PyValue Boom(ServiceCall call) => throw new InvalidOperationException("boom");

    [Method("UserError")]
    public PyValue UserError(ServiceCall call) => throw MachoErrors.ThrowUserError("FixtureMessage", ("count", new PyInt(3)), ("who", new PyStr("me")));

    [Method("UserErrorBare")]
    public PyValue UserErrorBare(ServiceCall call) => throw MachoErrors.ThrowUserError("Bare");

    [Method("WrongNode")]
    public PyValue WrongNode(ServiceCall call) => throw new MachoWrappedException(MachoErrors.WrongMachoNode(3));

    [Method("Wrapped")]
    public PyValue Wrapped(ServiceCall call) =>
        throw new MachoWrappedException(MachoErrors.Wrapped(
            "fixture.SomeError",
            [new PyStr("detail")],
            [new(new PyStr("msg"), new PyStr("detail"))]));

    private static PyValue Current(ServiceCall call) => call.Session.CurrentBoundObjectId is { Length: > 0 } id ? new PyStr(id) : PyNone.Instance;

    [Method("CurrentBound")]
    public PyValue CurrentBound(ServiceCall call) => Current(call);

    private static PyValue Registration(string oid, PyValue refId) =>
        new PySubStruct(new PySubStream(new PyTuple([new PyStr(oid), refId])));

    [Method("Bind")]
    public PyValue Bind(ServiceCall call) => Tuple(Registration("N=1:5", new PyLong(133000000000000000L)), PyNone.Instance);

    [Method("BindAgain")]
    public PyValue BindAgain(ServiceCall call) => Tuple(Registration("N=1:5", new PyLong(133000000000000500L)), PyNone.Instance);

    [Method("BindOther")]
    public PyValue BindOther(ServiceCall call) => Registration("N=1:6", PyNone.Instance);

    [Method("BindTwice")]
    public PyValue BindTwice(ServiceCall call) =>
        Tuple(Registration("N=2:2", new PyLong(100)), Registration("N=2:2", new PyLong(200)));

    [Method("Deferred")]
    public PyValue Deferred(ServiceCall call) => new PyDeferred(context =>
    {
        context.Reply(new PyList([new PyStr("later"), new PyInt(2)]));
        return Task.CompletedTask;
    });

    [Method("DeferredSilent")]
    public PyValue DeferredSilent(ServiceCall call) => new PyDeferred(_ => Task.CompletedTask);

    [Method("BoundCall")]
    public PyValue BoundCall(ServiceCall call) => Tuple(Current(call), Received(call.Args));
}
