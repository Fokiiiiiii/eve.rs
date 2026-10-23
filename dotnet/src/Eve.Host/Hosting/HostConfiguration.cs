using System.Net;
using System.Net.Sockets;
using Eve.Characters;
using Eve.Data;
using Eve.Host;
using Eve.Identity;
using Eve.Dogma;
using Eve.Fitting;
using Eve.GameStore;
using System.Text.Json;
using System.Text.Json.Nodes;
using Eve.Inventory;
using Eve.Ships;
using Eve.Marshal;
using Eve.Npc;
using Eve.Sde;
using Eve.Skills;
using Eve.Universe;
using Eve.Wallet;
using Npgsql;
using Sentry;
using Eve.Services;
using Eve.Services.CharacterSelect;
using Eve.Services.Core;
using Eve.Services.Dogma;
using Eve.Services.Inventory;
using Eve.Services.CharacterInfo;
using Eve.Services.Skills;
using Eve.Services.Slash;
using Eve.Services.Space;
using Eve.Space;
using Eve.Services.Station;
using Eve.Services.Wallet;

namespace Eve.Host;

internal sealed record XmppSettings(
    bool Enabled,
    string BindAddress,
    int Port,
    string Domain,
    string ConnectHost,
    string? CertificatePath,
    string? CertificatePassword)
{
    internal static XmppSettings Disabled { get; } = new(false, "127.0.0.1", 5222, "localhost", "localhost", null, null);
}

internal sealed record HostSettings(string Endpoint, string NewCharacterExperienceMode, ClusterOptions Options, AccountOptions AccountOptions, bool StrictRpc, XmppSettings? Xmpp = null);

/// <summary>Every EVE_* read of the host, in one place. The catalogue of variables is below.</summary>
internal static class HostConfiguration
{
    internal static PyValue? ParseLocalizationTextChanges(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        using var document = JsonDocument.Parse(value);
        if (document.RootElement.ValueKind != JsonValueKind.Array || document.RootElement.GetArrayLength() != 3)
            throw new InvalidOperationException("EVE_LOCALIZATION_TEXT_CHANGES must be a JSON array containing three dictionaries.");
        var maps = document.RootElement.EnumerateArray().Select(element =>
        {
            if (element.ValueKind != JsonValueKind.Object)
                throw new InvalidOperationException("Each EVE_LOCALIZATION_TEXT_CHANGES entry must be a JSON object.");
            return (PyValue)new PyDict(element.EnumerateObject().Select(property =>
                new KeyValuePair<PyValue, PyValue>(new PyStr(property.Name), LocalizationValue(property.Value))).ToArray());
        }).ToArray();
        return Py.Tuple(maps);
    }

    private static PyValue LocalizationValue(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => new PyStr(value.GetString()!),
        JsonValueKind.Number when value.TryGetInt64(out var integer) => new PyLong(integer),
        JsonValueKind.Number => new PyReal(value.GetDouble()),
        JsonValueKind.True => PyBool.True,
        JsonValueKind.False => PyBool.False,
        JsonValueKind.Null => PyNone.Instance,
        JsonValueKind.Array => new PyList(value.EnumerateArray().Select(LocalizationValue).ToArray()),
        JsonValueKind.Object => new PyDict(value.EnumerateObject().Select(property =>
            new KeyValuePair<PyValue, PyValue>(new PyStr(property.Name), LocalizationValue(property.Value))).ToArray()),
        _ => throw new InvalidOperationException("EVE_LOCALIZATION_TEXT_CHANGES contains an unsupported JSON value."),
    };

    // eve-host: the service host.
    //
    //   EVE_HOST               where to listen: a socket path, "unix:/path" or "tcp:host:port"   (default /tmp/eve-host.sock; tcp:127.0.0.1:26010 on Windows)
    //   EVE_IMAGE_SERVER_URL   the image server the client is told about                         (default http://127.0.0.1:26001/)
    //   EVE_PG_URL             PostgreSQL for accounts (postgres://... or an Npgsql string); without it accounts
    //                          live in memory and are lost when the host stops
    //   EVE_IDENTITY_SCHEMA    the schema of the accounts module                                  (default identity)
    //   EVE_CHARACTERS_SCHEMA  the schema of the characters module                                (default characters)
    //   EVE_INVENTORY_SCHEMA   the schema of the inventory module                                 (default inventory)
    //   EVE_SKILLS_SCHEMA      the schema of the skills module                                    (default skills)
    //   EVE_WALLET_SCHEMA      the schema of the wallet module                                    (default wallet)
    //   EVE_FITTING_SCHEMA     the schema of the fitting module (which modules are online)        (default fitting)
    //   EVE_SPACE_SCHEMA       the schema of what lies in space (cans and wrecks: where, until when) (default space)
    //   EVE_SIM_SOCKET         the Unix socket, or "tcp:host:port", of the space simulation (eve-sim)   (default /tmp/eve-sim.sock; tcp:127.0.0.1:26011 on Windows)
    //   EVE_SIM                off runs without the simulation: nobody can undock                 (default on)
    //   EVE_START_ISK / EVE_START_PLEX   what a new character starts with                         (100000 / 2222, the reference's)
    //   EVE_START_STATION      the station every new character starts at (the reference's; 60015249), or
    //                          "school" for the headquarters of the character's school corporation
    //                          Characters need EVE_PG_URL and the SDE (`eve-sde ensure`): without them the host runs
    //                          without charUnboundMgr, and a client cannot get past the character screen.
    //   EVE_MARKET_SEED        skip: do not fetch the NPC market's first orders from CCP's ESI (CI, development)  (default auto: fetch once per SDE build)
    //   EVE_SCHEDULER_SCHEMA, EVE_EVENTS_SCHEMA, EVE_RELATIONS_SCHEMA, EVE_CONTENT_SCHEMA   the schemas of the shared foundations (scheduler, event bus, relations, content)
    //   EVE_SEASONAL_LOGIN_CAMPAIGN   active seasonal login campaign dates, turnover, and configured alpha/omega rewards (default unset)
    //   EVE_LOCALIZATION_TEXT_CHANGES configured three-map JSON tuple served by localizationServer (default empty maps)

    //   EVE_CRIMEWATCH_SCHEMA  the schema of crimewatch (ship safety levels)                          (default crimewatch)


    //   EVE_GM_ACCOUNTS        user=ROLE+ROLE,... adds game-master role bits (GML, GMH, QA, PROGRAMMER, CONTENT, ADMIN,
    //                          EXT_GM1..EXT_GM4) to existing accounts at start-up; a grant never takes a bit away (default none)
    //   EVE_STRICT_RPC         1 answers an unknown service or method, an unhandled exception or a call with no service
    //                          with a Macho error (naming a correlation id) and logs its stack trace to stderr; the default
    //                          answers None and logs one line (what the reference does)                  (default 0)
    //   EVE_TRACE_RPC          1 traces unhandled calls to stderr; full also logs every call's arguments and result type tree
    //   EVE_PUBLIC_GATEWAY     host:port where the public protobuf gateway listens (cleartext HTTP/2, the client's EVE_PUBLIC_GATEWAY_ADDRESS); unset: none
    //                          (the dispatcher reads it, not this class)                               (default off)
    //   EVE_AUTO_RELOAD_ACCOUNTS  the account ids (comma-separated) of bots with no client UI: the host reloads their guns from the hold
    //                          when the rounds run out. Every other account reloads its own guns (default none)

    /// <summary>The default listening endpoint. Windows has no Unix socket for the front to meet us on, so it is a local port.</summary>
    internal static string DefaultEndpoint(bool windows) => windows ? "tcp:127.0.0.1:26010" : "/tmp/eve-host.sock";

    /// <summary>The default simulation endpoint, matching <see cref="DefaultEndpoint"/>: a local port on Windows.</summary>
    internal static string DefaultSimSocket(bool windows) => windows ? "tcp:127.0.0.1:26011" : "/tmp/eve-sim.sock";

    internal static int? ParseActiveSurveyId(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        if (!int.TryParse(value, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var id) || id <= 0)
            throw new InvalidOperationException("EVE_ACTIVE_SURVEY_ID must be a positive integer.");
        return id;
    }

    internal static string GmAccountGrantLog(bool granted) =>
        $"eve-host: EVE_GM_ACCOUNTS grant: {(granted ? "role bits added" : "no such account (it is created by its first login)")}";

    internal static string Env(string name, string fallback) => Environment.GetEnvironmentVariable(name) is { Length: > 0 } value ? value : fallback;

    internal static bool Flag(string name, bool fallback) => Env(name, fallback ? "1" : "0") is "1" or "true" or "TRUE" or "yes";

    /// <summary>The address and port of the public protobuf gateway (EVE_PUBLIC_GATEWAY), or null when none is asked for.</summary>
    internal static (System.Net.IPAddress Address, int Port)? PublicGatewayListen() => Env("EVE_PUBLIC_GATEWAY", "") is { Length: > 0 } value
        ? (System.Net.IPAddress.Parse(value[..value.LastIndexOf(':')]), int.Parse(value[(value.LastIndexOf(':') + 1)..], System.Globalization.CultureInfo.InvariantCulture))
        : null;

    /// <summary>The accounts EVE_AUTO_RELOAD_ACCOUNTS names. An entry that is not an account id stops the host: a typo must not switch a client's reload on or off unnoticed.</summary>
    internal static IReadOnlySet<long> AutoReloadUserIds(string value) =>
        value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(id => long.TryParse(id, out var parsed) && parsed > 0 ? parsed : throw new InvalidOperationException($"EVE_AUTO_RELOAD_ACCOUNTS names {id}, which is not an account id."))
            .ToHashSet();

    internal static HostSettings Read()
    {
        var endpoint = Env("EVE_HOST", DefaultEndpoint(OperatingSystem.IsWindows()));
        var newCharacterExperienceMode = Env(
            "EVE_NEW_CHARACTER_EXPERIENCE_MODE",
            Flag("EVE_AIR_NPE_ENABLED", false) ? "tutorial" : "disabled").Trim().ToLowerInvariant();
        if (newCharacterExperienceMode is not ("disabled" or "tutorial" or "cinematic"))
            throw new InvalidOperationException("EVE_NEW_CHARACTER_EXPERIENCE_MODE must be disabled, tutorial, or cinematic.");
        var options = new ClusterOptions
        {
            ImageServerUrl = Env("EVE_IMAGE_SERVER_URL", "http://127.0.0.1:26001/"),
            NewCharacterExperienceEnabled = newCharacterExperienceMode == "tutorial",
            NewCharacterExperienceMode = newCharacterExperienceMode,
        };
        var accountOptions = new AccountOptions
        {
            SkipPasswordCheck = Flag("EVE_DEV_SKIP_PASSWORD", false),
            AutoCreate = Flag("EVE_DEV_AUTO_CREATE", false),
        };
        var xmppPortText = Env("EVE_XMPP_PORT", "5222");
        if (!int.TryParse(xmppPortText, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var xmppPort)
            || xmppPort is < 1 or > 65535)
            throw new InvalidOperationException("EVE_XMPP_PORT must be a TCP port between 1 and 65535.");
        var xmpp = new XmppSettings(
            Enabled: Flag("EVE_XMPP_ENABLED", false),
            BindAddress: Env("EVE_XMPP_BIND_ADDRESS", "127.0.0.1").Trim(),
            Port: xmppPort,
            Domain: Env("EVE_XMPP_DOMAIN", "localhost").Trim(),
            ConnectHost: Env("EVE_XMPP_CONNECT_HOST", "localhost").Trim(),
            CertificatePath: NullIfEmpty(Env("EVE_XMPP_CERTIFICATE_PATH", "").Trim()),
            CertificatePassword: NullIfEmpty(Env("EVE_XMPP_CERTIFICATE_PASSWORD", "")));
        if (xmpp.Enabled)
        {
            if (!System.Net.IPAddress.TryParse(xmpp.BindAddress, out _))
                throw new InvalidOperationException("EVE_XMPP_BIND_ADDRESS must be a numeric IP address.");
            if (xmpp.Domain.Length == 0 || xmpp.ConnectHost.Length == 0)
                throw new InvalidOperationException("EVE_XMPP_DOMAIN and EVE_XMPP_CONNECT_HOST must be non-empty when XMPP is enabled.");
        }
        return new HostSettings(endpoint, newCharacterExperienceMode, options, accountOptions,
            Flag("EVE_STRICT_RPC", false), xmpp);
    }

    private static string? NullIfEmpty(string value) => value.Length == 0 ? null : value;
}
