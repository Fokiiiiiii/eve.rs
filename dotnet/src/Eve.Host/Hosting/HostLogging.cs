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
using static Eve.Host.HostConfiguration;

namespace Eve.Host;

/// <summary>Sentry startup for the host (the operator log lines are written where they are produced, with the "eve-host: " prefix).</summary>
internal static class HostLogging
{
    internal static IDisposable? StartSentry()
    {
        var sentryDsn = Env("SENTRY_DSN", "");
        return string.IsNullOrWhiteSpace(sentryDsn)
            ? null
            : SentrySdk.Init(options =>
            {
                options.Dsn = sentryDsn;
                options.Environment = Env("SENTRY_ENVIRONMENT", "development");
                options.SendDefaultPii = false;
            });
    }
}
