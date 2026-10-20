using System.Text;

namespace Eve.Host.Xmpp;

/// <summary>
/// The protocol state of one client connection: the stream, SASL PLAIN with the character id and the chat token, resource
/// binding and the session request. The stanzas after that are handled by the chat rooms.
/// </summary>
internal sealed class XmppClientSession(XmppChatHub hub, Action<string> send, Action<string?> closeWith)
{
    public const int MaxAuthenticationTextChars = 512;

    private enum Phase
    {
        AwaitingSasl,
        AwaitingBind,
        Bound,
    }

    private Phase _phase = Phase.AwaitingSasl;
    private long _characterId;
    private XmppLink? _link;

    /// <summary>True once SASL succeeded: the connection then has no idle deadline.</summary>
    public bool Authenticated => _phase != Phase.AwaitingSasl;

    public void OnStreamOpened()
    {
        send($"<?xml version='1.0'?><stream:stream from='{XmppXml.Escape(hub.Settings.Domain)}' version='1.0' xmlns='jabber:client' xmlns:stream='http://etherx.jabber.org/streams'>");
        send(_phase == Phase.AwaitingSasl
            ? $"<stream:features><mechanisms xmlns='{XmppXml.SaslNamespace}'><mechanism>PLAIN</mechanism></mechanisms></stream:features>"
            : $"<stream:features><bind xmlns='{XmppXml.BindNamespace}'/><session xmlns='{XmppXml.SessionNamespace}'/></stream:features>");
    }

    /// <summary>Handles one complete element under the stream.</summary>
    public void OnStanza(XmppElement element)
    {
        switch (element.Name)
        {
            case "auth":
                Authenticate(element);
                break;
            case "iq":
                OnIq(element);
                break;
            default:
                // presence and messages are answered once the connection is bound; before that they are ignored
                if (_phase != Phase.Bound)
                    closeWith("not-authorized");
                break;
        }
    }

    /// <summary>Called once the stream ends, after the connection is gone: the character's link (if bound) is released.</summary>
    public void OnClosed()
    {
        if (_link is { } link)
            hub.Unbind(link);
    }

    private void Authenticate(XmppElement element)
    {
        if (_phase != Phase.AwaitingSasl)
        {
            closeWith("not-authorized");
            return;
        }
        if (element.Attribute("mechanism") != "PLAIN" || element.Text.Length > MaxAuthenticationTextChars)
        {
            Fail("invalid-mechanism");
            return;
        }
        if (!TryDecodePlain(element.Text, out var user, out var token) || !long.TryParse(user, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var characterId)
            || characterId <= 0 || !hub.Authenticate(characterId, token))
        {
            Fail("not-authorized");
            return;
        }
        _characterId = characterId;
        _phase = Phase.AwaitingBind;
        send($"<success xmlns='{XmppXml.SaslNamespace}'/>");
    }

    private void Fail(string condition)
    {
        send($"<failure xmlns='{XmppXml.SaslNamespace}'><{condition}/></failure>");
        closeWith(null);
    }

    private void OnIq(XmppElement iq)
    {
        var id = iq.Attribute("id") ?? "";
        var type = iq.Attribute("type");
        var child = iq.Children.FirstOrDefault();
        if (_phase == Phase.AwaitingSasl)
        {
            Error(iq, "auth", "not-authorized");
            return;
        }
        if (type is "set" && child?.Name == "bind" && _phase == Phase.AwaitingBind)
        {
            var requested = child.Child("resource")?.Text.Trim() ?? "";
            var resource = requested.Length > 0 && requested.Length <= 64 ? requested : "eveclient";
            var link = hub.Bind(_characterId, resource, send, () => closeWith(null));
            if (link is null)
            {
                Error(iq, "cancel", "item-not-found");
                return;
            }
            _link = link;
            _phase = Phase.Bound;
            send($"<iq type='result' id='{XmppXml.Escape(id)}'><bind xmlns='{XmppXml.BindNamespace}'><jid>{XmppXml.Escape(link.Jid)}</jid></bind></iq>");
            return;
        }
        if (type is "set" && child?.Name == "session" && _phase == Phase.Bound)
        {
            send($"<iq type='result' id='{XmppXml.Escape(id)}'/>");
            return;
        }
        Error(iq, "cancel", "feature-not-implemented");
    }

    private void Error(XmppElement iq, string errorType, string condition)
    {
        var id = iq.Attribute("id") ?? "";
        var to = iq.Attribute("from");
        var toAttribute = to is null ? "" : $" to='{XmppXml.Escape(to)}'";
        send($"<iq type='error' id='{XmppXml.Escape(id)}'{toAttribute}><error type='{errorType}'><{condition} xmlns='{XmppXml.StanzasNamespace}'/></error></iq>");
    }

    /// <summary>SASL PLAIN: base64 of <c>authzid NUL authcid NUL password</c>. The authcid is the character id.</summary>
    private static bool TryDecodePlain(string base64, out string user, out string token)
    {
        user = "";
        token = "";
        byte[] decoded;
        try
        {
            decoded = Convert.FromBase64String(base64.Trim());
        }
        catch (FormatException)
        {
            return false;
        }
        var parts = Encoding.UTF8.GetString(decoded).Split('\0');
        if (parts.Length != 3)
            return false;
        user = parts[1];
        token = parts[2];
        return user.Length > 0 && token.Length > 0;
    }
}
