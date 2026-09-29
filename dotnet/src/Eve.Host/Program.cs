using System.Net;
using System.Net.Sockets;
using Eve.Characters;
using Eve.Data;
using Eve.Host;
using Eve.Identity;
using Eve.Inventory;
using Eve.Marshal;
using Eve.Sde;
using Npgsql;
using Eve.Services;
using Eve.Services.CharacterSelect;
using Eve.Services.Core;

// eve-host: the service host.
//
//   EVE_HOST               where to listen: a socket path, "unix:/path" or "tcp:host:port"   (default /tmp/eve-host.sock)
//   EVE_IMAGE_SERVER_URL   the image server the client is told about                         (default http://127.0.0.1:26001/)
//   EVE_PG_URL             PostgreSQL for accounts (postgres://... or an Npgsql string); without it accounts
//                          live in memory and are lost when the host stops
//   EVE_IDENTITY_SCHEMA    the schema of the accounts module                                  (default identity)
//   EVE_CHARACTERS_SCHEMA  the schema of the characters module                                (default characters)
//   EVE_INVENTORY_SCHEMA   the schema of the inventory module                                 (default inventory)
//   EVE_START_STATION      the station every new character starts at (the reference's; 60015249), or
//                          "school" for the headquarters of the character's school corporation
//                          Characters need EVE_PG_URL and the SDE (`eve-sde ensure`): without them the host runs
//                          without charUnboundMgr, and a client cannot get past the character screen.
//   EVE_DEV_SKIP_PASSWORD  1 accepts any password (development only)                          (default 0)
//   EVE_DEV_AUTO_CREATE    0 refuses unknown user names                                       (default 1)

static string Env(string name, string fallback) => Environment.GetEnvironmentVariable(name) is { Length: > 0 } value ? value : fallback;

static bool Flag(string name, bool fallback) => Env(name, fallback ? "1" : "0") is "1" or "true" or "TRUE" or "yes";

static Socket Listen(string endpoint)
{
    if (endpoint.StartsWith("tcp:", StringComparison.Ordinal))
    {
        var text = endpoint["tcp:".Length..];
        var colon = text.LastIndexOf(':');
        var host = colon > 0 ? text[..colon] : "127.0.0.1";
        var port = int.Parse(text[(colon + 1)..], System.Globalization.CultureInfo.InvariantCulture);
        var tcp = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        tcp.Bind(new IPEndPoint(IPAddress.Parse(host), port));
        tcp.Listen(128);
        return tcp;
    }
    var path = endpoint.StartsWith("unix:", StringComparison.Ordinal) ? endpoint["unix:".Length..] : endpoint;
    if (File.Exists(path))
    {
        File.Delete(path);
    }
    var unix = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
    unix.Bind(new UnixDomainSocketEndPoint(path));
    unix.Listen(128);
    return unix;
}

var endpoint = Env("EVE_HOST", "/tmp/eve-host.sock");
var options = new ClusterOptions { ImageServerUrl = Env("EVE_IMAGE_SERVER_URL", "http://127.0.0.1:26001/") };
var accountOptions = new AccountOptions
{
    SkipPasswordCheck = Flag("EVE_DEV_SKIP_PASSWORD", false),
    AutoCreate = Flag("EVE_DEV_AUTO_CREATE", true),
};
IAccountDirectory accounts;
IService? characterSelect = null;
var online = new OnlineCharacters();
if (Env("EVE_PG_URL", "") is { Length: > 0 } pgUrl)
{
    var schema = Env("EVE_IDENTITY_SCHEMA", PgAccountDirectory.DefaultSchema);
    var source = NpgsqlDataSource.Create(PgConnection.ToConnectionString(pgUrl));
    var applied = await PgAccountDirectory.MigrateAsync(source, schema);
    Console.Error.WriteLine($"eve-host: accounts in PostgreSQL, schema {schema}{(applied.Count > 0 ? "; applied " + string.Join(", ", applied) : "")}");
    var directory = new PgAccountDirectory(source, accountOptions, schema);
    accounts = directory;
    characterSelect = await CharacterSelectAsync(source, directory, online);
}
else
{
    Console.Error.WriteLine("eve-host: no EVE_PG_URL: accounts are kept in memory and lost when the host stops; no characters");
    accounts = new InMemoryAccountDirectory(accountOptions);
}

var registry = new ServiceRegistry();
Func<long> now = () => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
foreach (var service in CoreServices.Create(new SubscriptionSettings(), new NoOmegaAccounts(), now))
{
    registry.Register(service);
}
if (characterSelect is not null)
{
    registry.Register(characterSelect);
}
var config = GlobalConfig.Build(options);
var info = new HostMachoNetInfo(registry, config);
registry.Register(new MachoNetService(info, now));

var dispatcher = new PacketDispatcher(registry, now);
dispatcher.OnProblem = (message, error) => Console.Error.WriteLine($"eve-host: {message}{(error is null ? "" : ": " + error.Message)}");
var clusterConfig = MarshalCodec.Encode(config);
var server = new HostServer(
    new HostOptions
    {
        Log = message => Console.Error.WriteLine($"eve-host: {message}"),
        SessionOpened = session => session.Closed += () => online.Leave(session.SessionId),
    },
    dispatcher,
    accounts,
    () => clusterConfig);

using var listener = Listen(endpoint);
using var cancel = new CancellationTokenSource();
Console.CancelKeyPress += (_, args) =>
{
    args.Cancel = true;
    cancel.Cancel();
};
Console.Error.WriteLine($"eve-host: listening on {endpoint}; {registry.Count} services");
await server.ServeAsync(listener, cancel.Token);
return 0;

/// <summary>
/// The characters and inventory modules and the character selection service, or null (with the reason logged) when the
/// static data they need is not in the database.
/// </summary>
static async Task<IService?> CharacterSelectAsync(NpgsqlDataSource source, IAccountLookup accounts, OnlineCharacters online)
{
    CharacterNames.EnsureUnicodeSupport();
    var characterSchema = Env("EVE_CHARACTERS_SCHEMA", PgCharacterStore.DefaultSchema);
    var inventorySchema = Env("EVE_INVENTORY_SCHEMA", PgItemStore.DefaultSchema);
    await PgCharacterStore.MigrateAsync(source, characterSchema);
    await PgItemStore.MigrateAsync(source, inventorySchema);

    var sde = new SdeReader(source);
    if (await sde.BuildAsync() is not { } build)
    {
        Console.Error.WriteLine("eve-host: the static data is not loaded (run `eve-sde ensure --pg $EVE_PG_URL`): no charUnboundMgr");
        return null;
    }
    var startText = Env("EVE_START_STATION", "60015249");
    long? startStation = startText == "school" ? null : long.Parse(startText, System.Globalization.CultureInfo.InvariantCulture);
    var catalog = await CreationCatalog.LoadAsync(sde, startStation);
    var characters = new PgCharacterStore(source, characterSchema);
    var items = new PgItemStore(source, inventorySchema);
    Console.Error.WriteLine($"eve-host: characters in PostgreSQL, schemas {characterSchema} and {inventorySchema}; static data build {build}; new characters start at {(startStation is { } id ? $"station {id}" : "their school's headquarters")}");
    return new CharUnboundMgrService(accounts, characters, items, new CharacterCreation(source, characters, items, catalog), catalog, online, Random.Shared);
}

/// <summary>Until accounts live in the database (S2), nobody has a stored Omega expiry.</summary>
sealed class NoOmegaAccounts : IOmegaAccountStore
{
    public string? GetOmegaExpiry(long accountId) => null;
}

/// <summary>machoNet's view of this host: the registered services, with the locations the client routes by.</summary>
sealed class HostMachoNetInfo(ServiceRegistry registry, PyDict config) : IMachoNetInfo
{
    public IReadOnlyList<KeyValuePair<string, string?>> Services() =>
        [.. registry.ServiceNames.Select(name => new KeyValuePair<string, string?>(name, ServiceLocations.Of(name)))];

    public PyDict GlobalConfig() => config;

    public int UserCount() => 0;
}
