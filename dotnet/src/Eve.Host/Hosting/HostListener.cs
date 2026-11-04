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

/// <summary>Listens on the endpoint: tcp:host:port, unix:path or a bare socket path.</summary>
internal static class HostListener
{
    internal static Socket Listen(string endpoint)
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
}
