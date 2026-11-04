using System.Text;

namespace Eve.Host.Xmpp;

/// <summary>Escaping and JID helpers for the stanzas the chat server writes.</summary>
public static class XmppXml
{
    public const string SaslNamespace = "urn:ietf:params:xml:ns:xmpp-sasl";
    public const string BindNamespace = "urn:ietf:params:xml:ns:xmpp-bind";
    public const string SessionNamespace = "urn:ietf:params:xml:ns:xmpp-session";
    public const string StreamsNamespace = "urn:ietf:params:xml:ns:xmpp-streams";
    public const string StanzasNamespace = "urn:ietf:params:xml:ns:xmpp-stanzas";
    public const string MucNamespace = "http://jabber.org/protocol/muc";
    public const string MucUserNamespace = "http://jabber.org/protocol/muc#user";
    public const string DiscoItemsNamespace = "http://jabber.org/protocol/disco#items";
    public const string DiscoInfoNamespace = "http://jabber.org/protocol/disco#info";

    /// <summary>Escapes text for an element body or an attribute value (both quote kinds).</summary>
    public static string Escape(string value)
    {
        var builder = new StringBuilder(value.Length + 8);
        foreach (var c in value)
        {
            switch (c)
            {
                case '&': builder.Append("&amp;"); break;
                case '<': builder.Append("&lt;"); break;
                case '>': builder.Append("&gt;"); break;
                case '"': builder.Append("&quot;"); break;
                case '\'': builder.Append("&apos;"); break;
                default: builder.Append(c); break;
            }
        }
        return builder.ToString();
    }

    /// <summary>The bare part of a JID: <c>room@conference.host/nick</c> gives <c>room@conference.host</c>.</summary>
    public static string Bare(string jid)
    {
        var slash = jid.IndexOf('/');
        return slash < 0 ? jid : jid[..slash];
    }

    /// <summary>The resource (or nick) part of a JID, or an empty string.</summary>
    public static string Resource(string jid)
    {
        var slash = jid.IndexOf('/');
        return slash < 0 ? "" : jid[(slash + 1)..];
    }

    /// <summary>The local part of a JID: <c>charid@host</c> gives <c>charid</c>.</summary>
    public static string Local(string jid)
    {
        var at = jid.IndexOf('@');
        return at < 0 ? jid : jid[..at];
    }

    /// <summary>The character id a client JID names (<c>charid@host[/resource]</c>), or 0 when it names none.</summary>
    public static long CharacterId(string jid) =>
        long.TryParse(Local(Bare(jid)), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var id) && id > 0
            ? id
            : 0;
}
