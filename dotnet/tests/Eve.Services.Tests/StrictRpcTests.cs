using System.Text;
using System.Text.RegularExpressions;
using Eve.Macho;
using Eve.Marshal;

namespace Eve.Services.Tests;

/// <summary>
/// EVE_STRICT_RPC: an unknown service, an unknown method, an unregistered bound object or a failing handler is
/// answered with a Macho error and logged. With the flag off an unknown service or method is answered with the
/// V24.01 server's wrapped error (no log line), and a failing handler is still answered None.
/// </summary>
[Collection(TraceSettings.Name)]
public class StrictRpcTests
{
    private const int CallResponse = 7;
    private const int ErrorResponse = 15;

    private sealed class Sink : IPacketSink
    {
        public List<byte[]> Sent { get; } = [];

        public void Send(PyValue packet) => Sent.Add(MarshalCodec.Encode(packet));
    }

    private sealed class KnownService : IService
    {
        public string Name => "knownSvc";

        public bool HasMethod(string method) => method is "Known";

        public ValueTask<PyValue> CallAsync(ServiceCall call) => new(new PyInt(1));
    }

    private sealed class ThrowingService : IService
    {
        public string Name => "boomSvc";

        public bool HasMethod(string method) => method is "Boom";

        public ValueTask<PyValue> CallAsync(ServiceCall call) => throw new InvalidOperationException("boom went the handler");
    }

    private static async Task<(Sink Sink, string Log)> Dispatch(bool strict, string service, string method, params IService[] services)
    {
        var registry = new ServiceRegistry();
        foreach (var service1 in services)
        {
            registry.Register(service1);
        }
        var sink = new Sink();
        var session = new ClientSession(sink) { SessionId = 91, UserId = 90000009 };
        var priorError = Console.Error;
        using var log = new StringWriter();
        try
        {
            Console.SetError(log);
            await new PacketDispatcher(registry) { StrictRpc = strict }
                .DispatchAsync(DispatchGoldenTests.CallRequestPacket(service, method), session);
        }
        finally
        {
            Console.SetError(priorError);
        }
        return (sink, log.ToString());
    }

    private static int KindOf(byte[] packet) =>
        (int)((PyInt)((PyTuple)((PyObject)MarshalCodec.Decode(packet)).Args).Items[0]).Value;

    private static string Ascii(byte[] packet) => Encoding.ASCII.GetString(packet);

    private static PyValue ResultOf(byte[] packet)
    {
        var payload = (PyTuple)((PyTuple)((PyObject)MarshalCodec.Decode(packet)).Args).Items[4];
        return payload.Items[0] is PySubStream { Decoded: { } inner } ? inner : payload.Items[0];
    }

    [Fact]
    public async Task Flag_off_answers_an_unknown_service_with_a_wrapped_service_not_found_and_logs_nothing()
    {
        var (sink, log) = await Dispatch(strict: false, "missingSvc", "Anything", new KnownService());

        Assert.Single(sink.Sent);
        Assert.Equal(ErrorResponse, KindOf(sink.Sent[0]));
        Assert.Contains("eveexceptions.ServiceNotFound", Ascii(sink.Sent[0]));
        Assert.DoesNotContain("[strict-rpc]", log);
    }

    [Fact]
    public async Task Flag_off_answers_an_unknown_method_with_a_wrapped_attribute_error_and_a_failing_handler_with_None()
    {
        var (unknownMethod, _) = await Dispatch(strict: false, "knownSvc", "Missing", new KnownService());
        var (failing, log) = await Dispatch(strict: false, "boomSvc", "Boom", new ThrowingService());

        Assert.Equal(ErrorResponse, KindOf(unknownMethod.Sent.Single()));
        Assert.Contains("exceptions.AttributeError", Ascii(unknownMethod.Sent.Single()));
        Assert.Equal(CallResponse, KindOf(failing.Sent.Single()));
        Assert.DoesNotContain("boom went the handler", Ascii(failing.Sent.Single()));
        Assert.DoesNotContain("[strict-rpc]", log);
    }

    [Fact]
    public async Task Strict_answers_an_unknown_service_with_an_error_and_logs_it()
    {
        var (sink, log) = await Dispatch(strict: true, "missingSvc", "Anything", new KnownService());

        Assert.Equal(ErrorResponse, KindOf(sink.Sent.Single()));
        Assert.Contains("[strict-rpc]", log);
        Assert.Contains("unknown service: missingSvc at missingSvc.Anything", log);
    }

    [Fact]
    public async Task Strict_answers_an_unknown_method_with_an_error_and_logs_it()
    {
        var (sink, log) = await Dispatch(strict: true, "knownSvc", "Missing", new KnownService());

        Assert.Equal(ErrorResponse, KindOf(sink.Sent.Single()));
        Assert.Contains("unknown method: knownSvc.Missing", log);
    }

    [Fact]
    public async Task Strict_answers_a_call_with_no_service_with_an_error()
    {
        var (sink, log) = await Dispatch(strict: true, "", "Anything", new KnownService());

        Assert.Equal(ErrorResponse, KindOf(sink.Sent.Single()));
        Assert.Contains("unknown service: the call names no service", log);
    }

    [Fact]
    public async Task Strict_turns_a_failing_handler_into_an_error_whose_correlation_id_is_in_the_log()
    {
        var (sink, log) = await Dispatch(strict: true, "boomSvc", "Boom", new ThrowingService());

        var packet = sink.Sent.Single();
        Assert.Equal(ErrorResponse, KindOf(packet));
        var text = Ascii(packet);
        Assert.Contains("eveexceptions.UserError", text);
        Assert.Contains("InvalidOperationException: boom went the handler", text);
        // the log has the same id, the call summary and the stack trace
        var correlation = Regex.Match(log, @"\[strict-rpc\] ([0-9a-f]{8}) ").Groups[1].Value;
        Assert.Matches("^[0-9a-f]{8}$", correlation);
        Assert.Contains($"Server error {correlation}:", text);
        Assert.Contains("boomSvc.Boom args=[]", log);
        Assert.Contains("InvalidOperationException: boom went the handler", log);
        Assert.Contains("   at ", log);
    }

    [Fact]
    public async Task Strict_still_answers_an_unregistered_bound_object_with_UnMachoDestination_and_logs_it()
    {
        var (sink, log) = await Dispatch(strict: true, BoundObjects.OidPrefix + "42", "Anything", new KnownService());

        Assert.Equal(ErrorResponse, KindOf(sink.Sent.Single()));
        Assert.Contains("UnMachoDestination", Ascii(sink.Sent.Single()));
        Assert.Contains("unregistered bound object", log);
    }

    [Theory]
    [InlineData("1", false)]
    [InlineData("full", true)]
    public async Task Rpc_trace_levels_add_the_argument_and_result_trees_only_at_full(string level, bool expectTrees)
    {
        var priorFlag = Environment.GetEnvironmentVariable("EVE_TRACE_RPC");
        try
        {
            Environment.SetEnvironmentVariable("EVE_TRACE_RPC", level);
            var (_, log) = await Dispatch(strict: false, "knownSvc", "Known", new KnownService());

            Assert.Contains("[rpc-trace] response", log);
            Assert.Equal(expectTrees, log.Contains("tree=int(1)"));
            Assert.Equal(expectTrees, log.Contains("values=[]"));
        }
        finally
        {
            Environment.SetEnvironmentVariable("EVE_TRACE_RPC", priorFlag);
        }
    }
}
