using Eve.Data;
using Eve.Events;
using Eve.Marshal;
using Eve.Relations;
using Eve.Scheduler;
using Eve.Services;
using Eve.Services.Corporations;
using Eve.Wallet;
using Npgsql;

namespace Eve.Corporations.Tests;

public sealed class WarRegistryTests
{
    private const long Declarer = 9100001;
    private const long Defender = 9100002;
    private const long Bystander = 9100003;

    [PgFact]
    public async Task Ally_offers_are_listed_after_restart_and_only_the_pending_owner_can_transition_them()
    {
        await using var rig = await WarRig.CreateAsync();
        var war = await rig.DeclareAsync(Declarer, Defender, mutual: false);
        var service = rig.Service();
        var create = new ServiceCall
        {
            Method = "CreateWarAllyOffer",
            Args = [new PyLong(war), new PyLong(5_000), new PyLong(Defender), new PyStr("Support requested")],
            Session = Session(Bystander),
        };

        Assert.Equal(PyNone.Instance, await service.CallAsync(create));

        var reopened = rig.Service();
        var listedByAlly = Assert.IsType<PyList>(await reopened.CallAsync(new ServiceCall
        {
            Method = "GetNegotiations", Args = [], Session = Session(Bystander),
        }));
        var offer = Assert.Single(listedByAlly.Items);
        Assert.Equal(InventoryWire.Id(war), Field(offer, "warID"));
        Assert.Equal(new PyInt(0), Field(offer, "warNegotiationTypeID"));
        Assert.Equal(InventoryWire.Id(Bystander), Field(offer, "ownerID1"));
        Assert.Equal(InventoryWire.Id(Defender), Field(offer, "ownerID2"));
        Assert.Equal(new PyReal(5_000), Field(offer, "iskValue"));
        Assert.Equal(new PyWString("Support requested"), Field(offer, "description"));
        Assert.Equal(new PyInt(0), Field(offer, "negotiationState"));

        var listedByDefender = Assert.IsType<PyList>(await reopened.CallAsync(new ServiceCall
        {
            Method = "GetNegotiations", Args = [], Session = Session(Defender),
        }));
        Assert.Single(listedByDefender.Items);
        Assert.Equal(offer, await reopened.CallAsync(new ServiceCall
        {
            Method = "GetWarNegotiation", Args = [Field(offer, "warNegotiationID")], Session = Session(Defender),
        }));

        var offerId = Field(offer, "warNegotiationID");
        await reopened.CallAsync(new ServiceCall
        {
            Method = "RetractWarAllyOffer", Args = [offerId], Session = Session(Defender),
        });
        Assert.Single(Assert.IsType<PyList>(await reopened.CallAsync(new ServiceCall
        {
            Method = "GetNegotiations", Args = [], Session = Session(Defender),
        })).Items);

        await reopened.CallAsync(new ServiceCall
        {
            Method = "RetractWarAllyOffer", Args = [offerId], Session = Session(Bystander),
        });
        var afterRetraction = await reopened.CallAsync(new ServiceCall
        {
            Method = "GetWarNegotiation", Args = [offerId], Session = Session(Bystander),
        });
        Assert.Equal(new PyInt(3), Field(afterRetraction, "negotiationState"));
        Assert.Empty(Assert.IsType<PyList>(await reopened.CallAsync(new ServiceCall
        {
            Method = "GetNegotiations", Args = [], Session = Session(Defender),
        })).Items);
    }

    [PgFact]
    public async Task Accepting_an_ally_offer_transfers_once_and_joins_the_war_after_four_hours()
    {
        await using var rig = await WarRig.CreateAsync();
        var war = await rig.DeclareAsync(Declarer, Defender, mutual: false);
        await rig.SetBalanceAsync(new WalletAccount(Defender, 1001), 20_000m);
        await rig.SetBalanceAsync(new WalletAccount(Bystander), 0m);
        var service = rig.Service();
        await service.CallAsync(new ServiceCall
        {
            Method = "CreateWarAllyOffer",
            Args = [new PyLong(war), new PyReal(5_000), new PyLong(Defender), new PyStr("Support requested")],
            Session = Session(Bystander),
        });

        var offer = Assert.Single(Assert.IsType<PyList>(await service.CallAsync(new ServiceCall
        {
            Method = "GetNegotiations", Args = [], Session = Session(Defender),
        })).Items);
        var offerId = Field(offer, "warNegotiationID");
        await Task.WhenAll(
            service.CallAsync(new ServiceCall { Method = "AcceptAllyNegotiation", Args = [offerId], Session = Session(Defender, 1001) }).AsTask(),
            service.CallAsync(new ServiceCall { Method = "AcceptAllyNegotiation", Args = [offerId], Session = Session(Defender, 1001) }).AsTask());
        await service.CallAsync(new ServiceCall
        {
            Method = "AcceptAllyNegotiation", Args = [offerId], Session = Session(Defender, 1001),
        });

        var reopened = rig.Service();
        var accepted = await reopened.CallAsync(new ServiceCall
        {
            Method = "GetWarNegotiation", Args = [offerId], Session = Session(Defender),
        });
        Assert.Equal(new PyInt(1), Field(accepted, "negotiationState"));
        Assert.Equal(15_000m, await rig.BalanceAsync(new WalletAccount(Defender, 1001)));
        Assert.Equal(5_000m, await rig.BalanceAsync(new WalletAccount(Bystander)));

        var warPayload = await Payload(service, Defender, war);
        var allies = Assert.IsType<PyDict>(Field(warPayload, "allies"));
        var ally = Assert.Single(allies.Entries);
        Assert.Equal(InventoryWire.Id(Bystander), ally.Key);
        Assert.Equal(new PyLong(Py.FileTimeFromUnixMs(rig.NowMs + 4 * 60 * 60 * 1000)), Field(ally.Value, "timeStarted"));
        Assert.Equal(new PyLong(Py.FileTimeFromUnixMs(rig.NowMs + 14 * 24 * 60 * 60 * 1000)), Field(ally.Value, "timeFinished"));
        Assert.Empty(Assert.IsType<PyList>(await service.CallAsync(new ServiceCall
        {
            Method = "GetNegotiations", Args = [], Session = Session(Defender),
        })).Items);
    }

    [PgFact]
    public async Task Accepting_a_surrender_transfers_the_offerers_wallet_and_ends_the_war_with_reason_four()
    {
        await using var rig = await WarRig.CreateAsync();
        var war = await rig.DeclareAsync(Declarer, Defender, mutual: false);
        await rig.SetBalanceAsync(new WalletAccount(Declarer, 1002), 3_000m);
        await rig.SetBalanceAsync(new WalletAccount(Defender), 0m);
        var service = rig.Service();
        await service.CallAsync(new ServiceCall
        {
            Method = "CreateSurrenderNegotiation",
            Args = [new PyLong(war), new PyReal(1_250), new PyStr("Ceasefire")],
            Session = Session(Declarer, 1002),
        });
        var offer = Assert.Single(Assert.IsType<PyList>(await service.CallAsync(new ServiceCall
        {
            Method = "GetNegotiations", Args = [], Session = Session(Defender),
        })).Items);
        var offerId = Field(offer, "warNegotiationID");

        await service.CallAsync(new ServiceCall
        {
            Method = "AcceptSurrender", Args = [offerId], Session = Session(Defender),
        });

        Assert.Equal(new PyInt(1), Field(await service.CallAsync(new ServiceCall
        {
            Method = "GetWarNegotiation", Args = [offerId], Session = Session(Declarer),
        }), "negotiationState"));
        Assert.Equal(1_750m, await rig.BalanceAsync(new WalletAccount(Declarer, 1002)));
        Assert.Equal(1_250m, await rig.BalanceAsync(new WalletAccount(Defender)));
        var ended = await Payload(service, Declarer, war);
        Assert.Equal(new PyLong(Py.FileTimeFromUnixMs(rig.NowMs)), Field(ended, "timeFinished"));
        var relation = await rig.WarRelationAsync(Declarer, Defender);
        using var attrs = System.Text.Json.JsonDocument.Parse(relation!.AttrsJson);
        Assert.Equal(4, attrs.RootElement.GetProperty("reasonEnded").GetInt32());
        var treaties = await rig.PeaceTreatiesAsync(Declarer, Py.FileTimeFromUnixMs(rig.NowMs));
        Assert.Equal(2, treaties.Count);
        Assert.Contains(treaties, treaty => treaty.OwnerId == Declarer && treaty.OtherOwnerId == Defender && treaty.PeaceReason == 4);
        Assert.Contains(treaties, treaty => treaty.OwnerId == Defender && treaty.OtherOwnerId == Declarer && treaty.PeaceReason == 4);
    }

    [PgFact]
    public async Task An_unpayable_ally_offer_stays_pending_and_does_not_change_wallets_or_war_state()
    {
        await using var rig = await WarRig.CreateAsync();
        var war = await rig.DeclareAsync(Declarer, Defender, mutual: false);
        await rig.SetBalanceAsync(new WalletAccount(Defender, 1001), 1_000m);
        await rig.SetBalanceAsync(new WalletAccount(Bystander), 0m);
        var service = rig.Service();
        await service.CallAsync(new ServiceCall
        {
            Method = "CreateWarAllyOffer",
            Args = [new PyLong(war), new PyReal(5_000), new PyLong(Defender), new PyStr("Support requested")],
            Session = Session(Bystander),
        });
        var offer = Assert.Single(Assert.IsType<PyList>(await service.CallAsync(new ServiceCall
        {
            Method = "GetNegotiations", Args = [], Session = Session(Defender),
        })).Items);

        await service.CallAsync(new ServiceCall
        {
            Method = "AcceptAllyNegotiation", Args = [Field(offer, "warNegotiationID")], Session = Session(Defender, 1001),
        });

        var unchanged = await service.CallAsync(new ServiceCall
        {
            Method = "GetWarNegotiation", Args = [Field(offer, "warNegotiationID")], Session = Session(Defender),
        });
        Assert.Equal(new PyInt(0), Field(unchanged, "negotiationState"));
        Assert.Equal(1_000m, await rig.BalanceAsync(new WalletAccount(Defender, 1001)));
        Assert.Equal(0m, await rig.BalanceAsync(new WalletAccount(Bystander)));
        var warPayload = await Payload(service, Defender, war);
        Assert.Empty(Assert.IsType<PyDict>(Field(warPayload, "allies")).Entries);
    }

    [PgFact]
    public async Task The_offer_recipients_can_decline_ally_and_surrender_offers_once()
    {
        await using var rig = await WarRig.CreateAsync();
        var war = await rig.DeclareAsync(Declarer, Defender, mutual: false);
        var service = rig.Service();
        await service.CallAsync(new ServiceCall
        {
            Method = "CreateWarAllyOffer",
            Args = [new PyLong(war), new PyReal(0), new PyLong(Defender), new PyStr("Ally")],
            Session = Session(Bystander),
        });
        await service.CallAsync(new ServiceCall
        {
            Method = "CreateSurrenderNegotiation",
            Args = [new PyLong(war), new PyReal(0), new PyStr("Surrender")],
            Session = Session(Declarer),
        });
        var offers = Assert.IsType<PyList>(await service.CallAsync(new ServiceCall
        {
            Method = "GetNegotiations", Args = [], Session = Session(Defender),
        })).Items;
        var allyOffer = Assert.Single(offers, offer => Field(offer, "warNegotiationTypeID") == new PyInt(0));
        var surrender = Assert.Single(offers, offer => Field(offer, "warNegotiationTypeID") == new PyInt(2));

        await service.CallAsync(new ServiceCall { Method = "DeclineAllyOffer", Args = [Field(allyOffer, "warNegotiationID")], Session = Session(Defender) });
        await service.CallAsync(new ServiceCall { Method = "DeclineSurrender", Args = [Field(surrender, "warNegotiationID")], Session = Session(Defender) });

        Assert.Equal(new PyInt(2), Field(await service.CallAsync(new ServiceCall
        {
            Method = "GetWarNegotiation", Args = [Field(allyOffer, "warNegotiationID")], Session = Session(Bystander),
        }), "negotiationState"));
        Assert.Equal(new PyInt(2), Field(await service.CallAsync(new ServiceCall
        {
            Method = "GetWarNegotiation", Args = [Field(surrender, "warNegotiationID")], Session = Session(Declarer),
        }), "negotiationState"));
    }

    [PgFact]
    public async Task The_wars_of_an_owner_are_listed_and_the_negotiation_calls_are_refused_or_empty()
    {
        await using var rig = await WarRig.CreateAsync();
        var war = await rig.DeclareAsync(Declarer, Defender, mutual: true);
        var service = rig.Service((characterId, ownerId, _) =>
            Task.FromResult(ownerId is Declarer or Defender && characterId == ownerId + 1000));
        Assert.True(service.HasMethod("MachoResolveObject"));
        Assert.Equal(new PyInt(Eve.Macho.MachoMessage.ProxyNodeId), await service.CallAsync(new ServiceCall
        {
            Method = "MachoResolveObject", Args = [Py.Tuple(new PyInt(Declarer), new PyInt(1))], Session = Session(Declarer),
        }));

        var wars = Assert.IsType<PyDict>(await service.CallAsync(new ServiceCall { Method = "GetWars", Args = [], Session = Session(Declarer) }));
        var entry = Assert.Single(wars.Entries);
        Assert.Equal(InventoryWire.Id(war), entry.Key);
        Assert.Equal(PyNone.Instance, Field(entry.Value, "retracted"));

        var none = Assert.IsType<PyDict>(await service.CallAsync(new ServiceCall { Method = "GetWars", Args = [], Session = Session(Bystander) }));
        Assert.Empty(none.Entries);
        Assert.IsType<PyList>(await service.CallAsync(new ServiceCall { Method = "GetNegotiations", Args = [], Session = Session(Declarer) }));
        Assert.Equal(PyNone.Instance, await service.CallAsync(new ServiceCall { Method = "GetWarNegotiation", Args = [new PyInt(1)], Session = Session(Declarer) }));

        foreach (var method in new[]
                 {
                     "CreateWarAllyOffer", "RetractWarAllyOffer", "CreateSurrenderNegotiation",
                     "AcceptAllyNegotiation", "DeclineAllyOffer", "AcceptSurrender", "DeclineSurrender"
                 })
        {
            var refused = await Assert.ThrowsAsync<MachoWrappedException>(async () =>
                await ((IService)service).CallAsync(new ServiceCall
                {
                    Method = method,
                    Args = [new PyInt(war), new PyInt(0), new PyInt(Defender), new PyStr("")],
                    Session = Session(Bystander),
                }));
            Assert.Equal("CrpAccessDenied", refused.Error.UserErrorMessage);
        }
    }

    [PgFact]
    public async Task The_defender_opens_its_war_for_allies_and_the_declarer_cannot()
    {
        await using var rig = await WarRig.CreateAsync();
        var war = await rig.DeclareAsync(Declarer, Defender, mutual: false);
        var service = rig.Service();

        await service.CallAsync(new ServiceCall { Method = "SetOpenForAllies", Args = [new PyInt(war), PyValue.Of(true)], Session = Session(Declarer) });
        Assert.Equal(new PyInt(0), Field(await Payload(service, Declarer, war), "openForAllies"));

        await service.CallAsync(new ServiceCall { Method = "SetOpenForAllies", Args = [new PyInt(war), PyValue.Of(true)], Session = Session(Defender) });
        Assert.Equal(new PyInt(1), Field(await Payload(service, Declarer, war), "openForAllies"));
    }

    [PgFact]
    public async Task A_mutual_war_is_retracted_by_one_of_its_sides_and_a_war_that_is_not_mutual_is_left_alone()
    {
        await using var rig = await WarRig.CreateAsync();
        var mutual = await rig.DeclareAsync(Declarer, Defender, mutual: true);
        var plain = await rig.DeclareAsync(Declarer, Bystander, mutual: false);
        var service = rig.Service();

        await service.CallAsync(new ServiceCall { Method = "RetractMutualWar", Args = [new PyInt(plain)], Session = Session(Declarer) });
        Assert.Equal(PyNone.Instance, Field(await Payload(service, Declarer, plain), "retracted"));

        await service.CallAsync(new ServiceCall { Method = "RetractMutualWar", Args = [new PyInt(mutual)], Session = Session(Defender) });
        Assert.NotEqual(PyNone.Instance, Field(await Payload(service, Declarer, mutual), "retracted"));
        Assert.NotEqual(PyNone.Instance, Field(await Payload(service, Declarer, mutual), "timeFinished"));
    }

    [PgFact]
    public async Task War_changes_refuse_a_session_without_persisted_director_authority()
    {
        await using var rig = await WarRig.CreateAsync();
        var mutual = await rig.DeclareAsync(Declarer, Defender, mutual: true);
        var open = await rig.DeclareAsync(Declarer, Defender, mutual: false);
        var service = rig.Service((_, _, _) => Task.FromResult(false));

        await Assert.ThrowsAsync<MachoWrappedException>(() => service.CallAsync(new ServiceCall
        {
            Method = "RetractMutualWar", Args = [new PyInt(mutual)], Session = Session(Declarer),
        }).AsTask());
        await Assert.ThrowsAsync<MachoWrappedException>(() => service.CallAsync(new ServiceCall
        {
            Method = "SetOpenForAllies", Args = [new PyInt(open), PyValue.Of(true)], Session = Session(Defender),
        }).AsTask());

        Assert.Equal(PyNone.Instance, Field(await Payload(service, Declarer, mutual), "retracted"));
        Assert.Equal(new PyInt(0), Field(await Payload(service, Declarer, open), "openForAllies"));
    }

    private static ClientSession Session(long corporationId, int accountKey = 1000)
    {
        var session = new ClientSession { CharacterId = corporationId + 1000 };
        session.Set("corpid", new PyInt(corporationId));
        session.Set("corpaccountkey", new PyInt(accountKey));
        return session;
    }

    private static PyValue Field(PyValue keyVal, string name)
    {
        var fields = Assert.IsType<PyObject>(keyVal).Args;
        return fields is PyDict dict ? dict.Entries.Single(entry => ((PyStr)entry.Key).Value == name).Value : PyNone.Instance;
    }

    private static async Task<PyValue> Payload(IService service, long corporationId, long warId)
    {
        var wars = Assert.IsType<PyDict>(await service.CallAsync(new ServiceCall { Method = "GetWars", Args = [], Session = Session(corporationId) }));
        return wars.Entries.Single(entry => entry.Key.Equals(InventoryWire.Id(warId))).Value;
    }

    /// <summary>A relations store with its scheduler and event bus, in schemas of their own.</summary>
    private sealed class WarRig : IAsyncDisposable
    {
        private readonly string[] _schemas;
        private readonly NpgsqlDataSource _source;
        private readonly RelationStore _relations;
        private readonly WalletTransactions _wallets;
        public long NowMs { get; } = 1_800_000_000_000;

        private WarRig(NpgsqlDataSource source, string[] schemas, DurableScheduler scheduler, EventBus bus)
        {
            _source = source;
            _schemas = schemas;
            _relations = new RelationStore(source, scheduler, bus, schemas[2]);
            _wallets = new WalletTransactions(source, schemas[4]);
        }

        public static async Task<WarRig> CreateAsync()
        {
            var source = NpgsqlDataSource.Create(PgConnection.ForTest(Environment.GetEnvironmentVariable("EVE_PG_URL")!));
            var id = Guid.NewGuid().ToString("N")[..12];
            string[] schemas = ["wsch_" + id, "wevt_" + id, "wrel_" + id, "wcorp_" + id, "wwal_" + id];
            await DurableScheduler.MigrateAsync(source, schemas[0]);
            await EventBus.MigrateAsync(source, schemas[1]);
            await RelationStore.MigrateAsync(source, schemas[2]);
            await Eve.Corporations.CorporationStore.MigrateAsync(source, schemas[3]);
            await PgWalletStore.MigrateAsync(source, schemas[4]);
            var scheduler = new DurableScheduler(source, () => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), new SchedulerOptions { Concurrency = 1 }, schemas[0]);
            return new WarRig(source, schemas, scheduler, new EventBus(source, scheduler, null, schemas[1]));
        }

        public IService Service(Func<long, long, CancellationToken, Task<bool>>? canManageWar = null)
        {
            var negotiations = new Eve.Corporations.WarNegotiationStore(_source, _schemas[3]);
            return new WarRegistryService(_source, _relations, negotiations, _wallets, null, () => NowMs,
                canManageWar ?? ((_, _, _) => Task.FromResult(true)),
                resolveWalletOwnerAsync: (ownerId, _) => ValueTask.FromResult<long?>(ownerId),
                readCharacterAllyBaseCostAsync: (_, _) => ValueTask.FromResult(0L));
        }

        public async Task SetBalanceAsync(WalletAccount account, decimal balance)
        {
            await using var connection = await _source.OpenConnectionAsync();
            await using var transaction = await connection.BeginTransactionAsync();
            await _wallets.EnsureAsync(connection, transaction, account.OwnerId);
            if (balance != 0)
                await _wallets.AdjustAsync(connection, transaction, account, balance, 0, 0,
                    "test wallet seed", NowMs, $"test-seed:{account.OwnerId}:{account.AccountKey}", "test-seed");
            await transaction.CommitAsync();
        }

        public Task<decimal?> BalanceAsync(WalletAccount account) => _wallets.AccountBalanceAsync(account);

        public Task<Eve.Relations.Relation?> WarRelationAsync(long declaredById, long againstId) =>
            _relations.GetAsync("war", declaredById, againstId);

        public Task<IReadOnlyList<Eve.Corporations.PeaceTreatyRecord>> PeaceTreatiesAsync(long ownerId, long nowFiletime) =>
            new Eve.Corporations.WarNegotiationStore(_source, _schemas[3]).PeaceTreatiesForOwnerAsync(ownerId, nowFiletime);

        /// <summary>Declares a war as the declarer does (the relation the war is kept in), with its attributes.</summary>
        public async Task<long> DeclareAsync(long declarer, long against, bool mutual)
        {
            var filetime = Py.FileTimeFromUnixMs(NowMs).ToString(System.Globalization.CultureInfo.InvariantCulture);
            await using var connection = await _source.OpenConnectionAsync();
            await using var transaction = await connection.BeginTransactionAsync();
            await _relations.SetAsync(connection, transaction, "war", declarer, against, 1.0, "declared", new
            {
                timeDeclared = filetime,
                timeStarted = filetime,
                mutual = mutual ? 1 : 0,
                openForAllies = 0,
            }, null, default);
            await transaction.CommitAsync();
            var relation = await _relations.GetAsync("war", declarer, against);
            return relation!.Id;
        }

        public async ValueTask DisposeAsync()
        {
            foreach (var schema in _schemas)
            {
                await using var command = _source.CreateCommand($"DROP SCHEMA IF EXISTS {schema} CASCADE");
                await command.ExecuteNonQueryAsync();
            }
            await _source.DisposeAsync();
        }
    }
}
