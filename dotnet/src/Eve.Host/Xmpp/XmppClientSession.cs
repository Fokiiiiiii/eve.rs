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
    public void OnStanza(XmppElement element) =>
        OnStanzaAsync(element, CancellationToken.None).AsTask().GetAwaiter().GetResult();

    public async ValueTask OnStanzaAsync(XmppElement element, CancellationToken cancellationToken)
    {
        if (_phase != Phase.Bound && element.Name is "presence" or "message")
        {
            closeWith("not-authorized");
            return;
        }

        switch (element.Name)
        {
            case "auth":
                Authenticate(element);
                break;
            case "iq":
                OnIq(element);
                break;
            case "presence":
                OnPresence(element);
                break;
            case "message":
                await OnMessageAsync(element, cancellationToken);
                break;
            default:
                if (_phase != Phase.Bound)
                    closeWith("not-authorized");
                break;
        }
    }

    private void OnPresence(XmppElement stanza)
    {
        if (_link is not { } link || !TryRoomAddress(stanza.Attribute("to"), out var room, out var nickname, requireNickname: true))
        {
            StanzaError(stanza, "cancel", "item-not-found");
            return;
        }

        if (stanza.Attribute("type") == "unavailable")
        {
            hub.LeaveRoom(link, room);
            return;
        }

        if (!hub.TryJoinRoom(link, room, nickname, out _))
            StanzaError(stanza, "auth", "not-authorized");
    }

    private async ValueTask OnMessageAsync(XmppElement stanza, CancellationToken cancellationToken)
    {
        if (_link is not { } link || !TryRoomAddress(stanza.Attribute("to"), out var room, out _, requireNickname: false))
        {
            StanzaError(stanza, "cancel", "feature-not-implemented");
            return;
        }

        var invite = stanza.Child("x")?.Child("invite");
        if (invite is not null)
        {
            var target = invite.Attribute("to")?.Split('@')[0];
            var reason = invite.Child("reason")?.Text ?? "";
            if (!long.TryParse(target, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var targetCharacterId)
                || !await hub.InvitePrivateParticipantAsync(link, room, targetCharacterId, reason, cancellationToken))
                StanzaError(stanza, "cancel", "not-allowed");
            return;
        }

        if (stanza.Attribute("type") != "groupchat")
        {
            StanzaError(stanza, "cancel", "feature-not-implemented");
            return;
        }
        if (stanza.Child("subject") is { } subject)
        {
            if (!await hub.SetRoomMotdAsync(link, room, subject.Text, cancellationToken).ConfigureAwait(false))
                StanzaError(stanza, "auth", "not-allowed");
            return;
        }
        var body = stanza.Child("body")?.Text ?? "";
        if (!await hub.SendRoomMessageAsync(link, room, body, cancellationToken))
            StanzaError(stanza, "wait", "resource-constraint");
    }

    private bool TryRoomAddress(string? address, out string room, out string nickname, bool requireNickname)
    {
        room = "";
        nickname = "";
        if (address is null)
            return false;
        var at = address.IndexOf('@');
        var slash = address.IndexOf('/', at + 1);
        var domainEnd = slash < 0 ? address.Length : slash;
        if (at <= 0 || domainEnd <= at + 1 || !hub.IsConferenceDomain(address[(at + 1)..domainEnd])
            || requireNickname && (slash < 0 || slash == address.Length - 1))
            return false;
        room = address[..at];
        if (slash >= 0)
            nickname = address[(slash + 1)..];
        return true;
    }

    private void StanzaError(XmppElement stanza, string errorType, string condition)
    {
        var id = stanza.Attribute("id");
        var idAttribute = id is null ? "" : $" id='{XmppXml.Escape(id)}'";
        var from = stanza.Attribute("from");
        var fromAttribute = from is null ? "" : $" to='{XmppXml.Escape(from)}'";
        send($"<{stanza.Name} type='error'{idAttribute}{fromAttribute}><error type='{errorType}'><{condition} xmlns='{XmppXml.StanzasNamespace}'/></error></{stanza.Name}>");
    }

    /// <summary>Called once the stream ends, after the connection is gone: the character's link (if bound) is released.</summary>
    public void OnClosed()
    {
        if (_link is { } link)
        {
            hub.LeaveAllRooms(link);
            hub.Unbind(link);
        }
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
        if (type == "get" && child?.Name == "query" && _phase == Phase.Bound && _link is { } bound)
        {
            var to = iq.Attribute("to") ?? "";
            if (hub.IsConferenceDomain(to))
            {
                var payload = child.Attribute("xmlns") == "http://jabber.org/protocol/disco#info"
                    ? "<identity category='conference' type='text'/><feature var='http://jabber.org/protocol/disco#items'/><feature var='http://jabber.org/protocol/disco#info'/><feature var='http://jabber.org/protocol/muc'/><feature var='muc_temporary'/><feature var='muc_open'/>" :
                    $"<query xmlns='http://jabber.org/protocol/disco#items'>{hub.LocalRoomItemsXml(bound)}</query>";
                send($"<iq type='result' id='{XmppXml.Escape(id)}'>{payload}</iq>");
                return;
            }
            var at = to.IndexOf('@');
            if (at > 0 && hub.IsConferenceDomain(to[(at + 1)..])
                && child.Attribute("xmlns") == "http://jabber.org/protocol/disco#items")
            {
                var room = to[..at];
                var payload = $"<query xmlns='http://jabber.org/protocol/disco#items'>{hub.LocalRoomOccupantsXml(bound, room)}</query>";
                send($"<iq type='result' id='{XmppXml.Escape(id)}'>{payload}</iq>");
                return;
            }
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
