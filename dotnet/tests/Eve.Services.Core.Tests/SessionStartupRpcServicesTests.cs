using Eve.Macho;
using Eve.Marshal;
using Eve.Services;
using Eve.Services.Core;

namespace Eve.Services.Core.Tests;

public sealed class SessionStartupRpcServicesTests
{
    [Fact]
    public async Task Chat_authentication_refuses_client_arguments_before_provider_or_token_creation()
    {
        var providerCalls = 0;
        IService service = new ChatAuthenticationService(tokenProvider: (_, _, _) =>
        {
            providerCalls++;
            return ValueTask.FromResult<string?>("token");
        });
        var session = new ClientSession { CharacterId = 20 };
        foreach (var call in new[]
        {
            new ServiceCall { Method = "GetAuthenticationToken", Args = [new PyInt(20)], Session = session },
            new ServiceCall { Method = "GetAuthenticationToken", Args = [], Kwargs = Py.Dict(("characterID", new PyInt(20))), Session = session },
        })
        {
            var error = await Assert.ThrowsAsync<MachoWrappedException>(() => service.CallAsync(call).AsTask());
            Assert.Equal("CHAT_AUTHENTICATION_ARGUMENTS_INVALID", error.Error.UserErrorMessage);
        }

        Assert.Equal(0, providerCalls);
        Assert.Null(session.Get("chatAuthenticationToken"));
    }

    [Fact]
    public async Task Chat_authentication_reads_session_tokens_then_uses_the_typed_provider()
    {
        var session = new ClientSession();
        session.Set("charid", new PyInt(20));
        session.Set("chatAuthenticationToken", new PyStr(""));
        session.Set("xmppAuthenticationToken", PyNone.Instance);
        session.Set("chatAuthenticationTokensByCharacterID", Py.Dict(("20", new PyStr("session-token"))));
        IService wiredFromSession = new ChatAuthenticationService();

        Assert.True(wiredFromSession.HasMethod("GetAuthenticationToken"));
        Assert.Equal(new PyStr("session-token"), await wiredFromSession.CallAsync(Call(
            wiredFromSession, "GetAuthenticationToken", session)));

        var providerSession = new ClientSession();
        providerSession.Set("charid", new PyInt(20));
        var providerCalls = 0;
        IService futureWired = new ChatAuthenticationService(tokenProvider: (caller, characterId, _) =>
        {
            Assert.Same(providerSession, caller);
            Assert.Equal(20, characterId);
            providerCalls++;
            return ValueTask.FromResult<string?>("provider-token");
        });
        Assert.Equal(new PyStr("provider-token"), await futureWired.CallAsync(Call(
            futureWired, "GetAuthenticationToken", providerSession)));
        Assert.Equal(1, providerCalls);
        IService empty = new ChatAuthenticationService();
        var activeSession = new ClientSession();
        activeSession.CharacterId = 21;
        var issued = Assert.IsType<PyStr>(await empty.CallAsync(Call(empty, "GetAuthenticationToken", activeSession)));
        Assert.Equal(64, issued.Value.Length);
        Assert.Matches("^[0-9a-f]{64}$", issued.Value);
        Assert.Equal(issued, activeSession.Get("chatAuthenticationToken"));
        Assert.Equal(issued, await empty.CallAsync(Call(empty, "GetAuthenticationToken", activeSession)));
        Assert.Same(PyNone.Instance, await empty.CallAsync(
            Call(empty, "GetAuthenticationToken", new ClientSession())));
    }

    [Fact]
    public async Task Jump_timers_return_the_three_filetime_slots_and_only_read_the_active_character()
    {
        var session = new ClientSession();
        session.Set("charid", new PyInt(20));
        long observedCharacter = 0;
        IService service = new JumpTimersService((characterId, _) =>
        {
            observedCharacter = characterId;
            return ValueTask.FromResult<JumpTimerState?>(new(101, 202, 303));
        });

        var result = Assert.IsType<PyTuple>(await service.CallAsync(Call(
            service, "GetTimers", session, [new PyInt(20)])));
        Assert.Equal(20, observedCharacter);
        Assert.Equal(new PyTuple([new PyLong(101), new PyLong(202), new PyLong(303)]), result);

        IService emptyTimers = new JumpTimersService();
        var empty = Assert.IsType<PyTuple>(await emptyTimers.CallAsync(
            Call(emptyTimers, "GetTimers", session, [new PyInt(20)])));
        Assert.Equal(new PyTuple([PyNone.Instance, PyNone.Instance, PyNone.Instance]), empty);
    }

    [Fact]
    public async Task Jump_timers_refuse_malformed_or_foreign_character_ids_before_reading()
    {
        var readCalls = 0;
        IService service = new JumpTimersService((_, _) =>
        {
            readCalls++;
            return ValueTask.FromResult<JumpTimerState?>(new(1, 2, 3));
        });
        var session = new ClientSession { CharacterId = 20 };
        foreach (var call in new[]
        {
            new ServiceCall { Method = "GetTimers", Args = [], Session = session },
            new ServiceCall { Method = "GetTimers", Args = [new PyInt(20), new PyInt(21)], Session = session },
            new ServiceCall { Method = "GetTimers", Args = [new PyInt(20)], Kwargs = Py.Dict(("characterID", new PyInt(20))), Session = session },
        })
        {
            var error = await Assert.ThrowsAsync<MachoWrappedException>(() => service.CallAsync(call).AsTask());
            Assert.Equal("JUMP_TIMER_ARGUMENTS_INVALID", error.Error.UserErrorMessage);
        }

        var foreign = await Assert.ThrowsAsync<MachoWrappedException>(() => service.CallAsync(new ServiceCall
        {
            Method = "GetTimers",
            Args = [new PyInt(21)],
            Session = session,
        }).AsTask());
        Assert.Equal("JUMP_TIMER_CHARACTER_INVALID", foreign.Error.UserErrorMessage);

        var wrongType = await Assert.ThrowsAsync<MachoWrappedException>(() => service.CallAsync(new ServiceCall
        {
            Method = "GetTimers",
            Args = [new PyReal(20)],
            Session = session,
        }).AsTask());
        Assert.Equal("JUMP_TIMER_CHARACTER_INVALID", wrongType.Error.UserErrorMessage);
        Assert.Equal(0, readCalls);
    }

    [Fact]
    public async Task Dynamic_bounty_returns_the_reference_disabled_pair_or_the_wired_state()
    {
        var session = new ClientSession();
        session.Set("solarsystemid", new PyInt(30000142));
        IService empty = new DynamicBountyMgrService();
        Assert.Equal(new PyTuple([new PyInt(0), new PyInt(0)]), await empty.CallAsync(
            Call(empty, "GetOutputForClientSolarSystem", session)));

        long? observedSystem = null;
        IService wired = new DynamicBountyMgrService((systemId, _) =>
        {
            observedSystem = systemId;
            return ValueTask.FromResult<DynamicBountyState?>(new(2.5, 0.75));
        });
        Assert.Equal(new PyTuple([new PyReal(2.5), new PyReal(0.75)]), await wired.CallAsync(
            Call(wired, "GetOutputForClientSolarSystem", session)));
        Assert.Equal(30000142, observedSystem);
    }

    [Fact]
    public async Task Dynamic_bounty_read_refuses_client_arguments_before_callback()
    {
        var callbackCalls = 0;
        IService service = new DynamicBountyMgrService((_, _) =>
        {
            callbackCalls++;
            return ValueTask.FromResult<DynamicBountyState?>(new(1, 1));
        });
        var session = new ClientSession();
        session.Set("solarsystemid", new PyInt(30000142));
        foreach (var call in new[]
        {
            new ServiceCall { Method = "GetOutputForClientSolarSystem", Args = [new PyInt(30000142)], Session = session },
            new ServiceCall { Method = "GetOutputForClientSolarSystem", Args = [], Kwargs = Py.Dict(("solarSystemID", new PyInt(30000142))), Session = session },
        })
        {
            var error = await Assert.ThrowsAsync<MachoWrappedException>(() => service.CallAsync(call).AsTask());
            Assert.Equal("DYNAMIC_BOUNTY_ARGUMENTS_INVALID", error.Error.UserErrorMessage);
        }

        Assert.Equal(0, callbackCalls);
    }

    [Fact]
    public async Task Season_client_events_are_forwarded_and_return_none()
    {
        var session = new ClientSession();
        SeasonClientEvent? observed = null;
        IService service = new SeasonManagerService((clientEvent, caller, _) =>
        {
            Assert.Same(session, caller);
            observed = clientEvent;
            return ValueTask.CompletedTask;
        });
        var extra = Py.KeyVal(("typeID", new PyInt(34)));
        var result = await service.CallAsync(new ServiceCall
        {
            Method = "process_client_event",
            Args = [new PyStr("mission_completed"), extra],
            Kwargs = Py.Dict(("amount", new PyReal(3.5))),
            Session = session,
        });

        Assert.Same(PyNone.Instance, result);
        Assert.Equal(new PyStr("mission_completed"), observed!.EventType);
        Assert.Equal(extra, observed.ExtraData);
        Assert.Equal(new PyReal(3.5), observed.Amount);
        Assert.True(service.HasMethod("process_client_event"));
        IService emptySeason = new SeasonManagerService();
        Assert.Same(PyNone.Instance, await emptySeason.CallAsync(new ServiceCall
        {
            Method = "process_client_event",
            Args = [],
            Session = session,
        }));
    }

    private static ServiceCall Call(
        IService service,
        string method,
        ClientSession session,
        IReadOnlyList<PyValue>? args = null) => new()
    {
        Method = method,
        Args = args ?? [],
        Session = session,
    };
}
