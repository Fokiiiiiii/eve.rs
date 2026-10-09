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

internal sealed record HostSettings(string Endpoint, string NewCharacterExperienceMode, ClusterOptions Options, AccountOptions AccountOptions);

/// <summary>Every EVE_* read of the host, in one place. The catalogue of variables is below.</summary>
internal static class HostConfiguration
{
    // eve-host: the service host.
    //
    //   EVE_HOST               where to listen: a socket path, "unix:/path" or "tcp:host:port"   (default /tmp/eve-host.sock)
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
    //   EVE_SIM_SOCKET         the Unix socket of the space simulation (eve-sim)                  (default /tmp/eve-sim.sock)
    //   EVE_SIM                off runs without the simulation: nobody can undock                 (default on)
    //   EVE_START_ISK / EVE_START_PLEX   what a new character starts with                         (100000 / 2222, the reference's)
    //   EVE_START_STATION      the station every new character starts at (the reference's; 60015249), or
    //                          "school" for the headquarters of the character's school corporation
    //                          Characters need EVE_PG_URL and the SDE (`eve-sde ensure`): without them the host runs
    //                          without charUnboundMgr, and a client cannot get past the character screen.
    //   EVE_MARKET_SEED        skip: do not fetch the NPC market's first orders from CCP's ESI (CI, development)  (default auto: fetch once per SDE build)
    //   EVE_SCHEDULER_SCHEMA, EVE_EVENTS_SCHEMA, EVE_RELATIONS_SCHEMA, EVE_CONTENT_SCHEMA   the schemas of the shared foundations (scheduler, event bus, relations, content)
    //                          (defaults scheduler, events, relations, content)
    //   EVE_CRIMEWATCH_SCHEMA  the schema of crimewatch (ship safety levels)                          (default crimewatch)
    //   EVE_DEV_SKIP_PASSWORD  1 accepts any password (development only)                          (default 0)
    //   EVE_DEV_AUTO_CREATE    0 refuses unknown user names                                       (default 0)

    internal static string Env(string name, string fallback) => Environment.GetEnvironmentVariable(name) is { Length: > 0 } value ? value : fallback;

    internal static bool Flag(string name, bool fallback) => Env(name, fallback ? "1" : "0") is "1" or "true" or "TRUE" or "yes";

    internal static HostSettings Read()
    {
        var endpoint = Env("EVE_HOST", "/tmp/eve-host.sock");
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
        return new HostSettings(endpoint, newCharacterExperienceMode, options, accountOptions);
    }
}
