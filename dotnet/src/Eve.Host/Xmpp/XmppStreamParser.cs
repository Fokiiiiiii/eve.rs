using System.Globalization;
using System.Text;

namespace Eve.Host.Xmpp;

/// <summary>A stream that breaks XMPP framing or a size limit. The connection closes with the stream error; no other connection is affected.</summary>
public sealed class XmppStreamException(string condition, string message) : Exception(message)
{
    /// <summary>The RFC 6120 stream error condition sent before the close.</summary>
    public string Condition { get; } = condition;
}

/// <summary>An XML element of the stream, with the names as written (prefixes kept) and decoded text.</summary>
public sealed class XmppElement(string name)
{
    private readonly StringBuilder _text = new();

    public string Name { get; } = name;
    public Dictionary<string, string> Attributes { get; } = new(StringComparer.Ordinal);
    public List<XmppElement> Children { get; } = [];
    public string Text => _text.ToString();

    public string? Attribute(string name) => Attributes.GetValueOrDefault(name);

    public XmppElement? Child(string name) => Children.FirstOrDefault(child => child.Name == name);

    internal void AppendText(string text) => _text.Append(text);
}

/// <summary>What the parser reports, in document order.</summary>
public abstract record XmppStreamEvent;

/// <summary>A stream header (<c>stream:stream</c>) opened a new stream; its attributes are the header's.</summary>
public sealed record XmppStreamOpened(IReadOnlyDictionary<string, string> Attributes) : XmppStreamEvent;

/// <summary>A complete stanza or feature element directly under the stream.</summary>
public sealed record XmppStanza(XmppElement Element) : XmppStreamEvent;

/// <summary>The peer closed the stream with <c>&lt;/stream:stream&gt;</c>.</summary>
public sealed record XmppStreamClosed : XmppStreamEvent;

/// <summary>
/// Incremental XML reader for an XMPP client stream. It takes the bytes as they arrive and reports complete elements.
/// It accepts only what XMPP needs: elements, attributes, text, the predefined and numeric entities, comments and
/// processing instructions. A DTD, an entity declaration, CDATA, an unknown entity or an oversized stanza is a
/// stream error, so untrusted input never expands entities or grows the buffer without bound.
/// </summary>
public sealed class XmppStreamParser
{
    public const int MaxStanzaChars = 64 * 1024;
    public const int MaxBufferedChars = 256 * 1024;
    public const int MaxDepth = 8;
    public const int MaxAttributes = 32;

    private readonly Decoder _decoder = new UTF8Encoding(false, true).GetDecoder();
    private readonly List<XmppElement> _open = [];
    private string _buffer = "";
    private int _position;
    private int _stanzaChars;

    /// <summary>Appends the next bytes and returns the events they complete.</summary>
    public List<XmppStreamEvent> Feed(ReadOnlySpan<byte> bytes)
    {
        var events = new List<XmppStreamEvent>();
        char[] chars;
        int written;
        try
        {
            chars = new char[_decoder.GetCharCount(bytes, false)];
            written = _decoder.GetChars(bytes, chars, false);
        }
        catch (DecoderFallbackException error)
        {
            throw new XmppStreamException("bad-format", $"the stream is not UTF-8: {error.Message}");
        }
        _buffer = string.Concat(_buffer.AsSpan(_position), new ReadOnlySpan<char>(chars, 0, written));
        _position = 0;
        while (TryParseNext(events))
        {
        }
        return events;
    }

    private bool TryParseNext(List<XmppStreamEvent> events)
    {
        if (_position >= _buffer.Length)
            return false;
        if (_buffer[_position] != '<')
            return ParseText();
        if (_position + 1 >= _buffer.Length)
            return CheckBuffered();
        return _buffer[_position + 1] switch
        {
            '?' => ParseProcessingInstruction(),
            '!' => ParseDeclaration(),
            '/' => ParseEndTag(events),
            _ => ParseStartTag(events),
        };
    }

    private bool ParseText()
    {
        var next = _buffer.IndexOf('<', _position);
        if (next < 0)
            return CheckBuffered();
        var text = Decode(_buffer.AsSpan(_position, next - _position));
        if (_open.Count >= 2)
            _open[^1].AppendText(text);
        else if (text.Trim(" \t\r\n").Length > 0)
            throw new XmppStreamException("bad-format", "text outside a stanza");
        Consume(next - _position);
        return true;
    }

    private bool ParseProcessingInstruction()
    {
        var end = _buffer.IndexOf("?>", _position + 2, StringComparison.Ordinal);
        if (end < 0)
            return CheckBuffered();
        // a new XML declaration starts a new document: the stream restarts after SASL and after bind
        if (string.CompareOrdinal(_buffer, _position, "<?xml", 0, 5) == 0)
        {
            _open.Clear();
            _stanzaChars = 0;
        }
        Consume(end + 2 - _position);
        return true;
    }

    private bool ParseDeclaration()
    {
        if (_buffer.Length - _position < 4)
            return CheckBuffered();
        if (string.CompareOrdinal(_buffer, _position, "<!--", 0, 4) != 0)
            throw new XmppStreamException("restricted-xml", "DTDs, entity declarations and CDATA are not accepted");
        var end = _buffer.IndexOf("-->", _position + 4, StringComparison.Ordinal);
        if (end < 0)
            return CheckBuffered();
        Consume(end + 3 - _position);
        return true;
    }

    private bool ParseEndTag(List<XmppStreamEvent> events)
    {
        var end = _buffer.IndexOf('>', _position);
        if (end < 0)
            return CheckBuffered();
        var name = _buffer.AsSpan(_position + 2, end - _position - 2).Trim(" \t\r\n").ToString();
        if (_open.Count == 0 || _open[^1].Name != name)
            throw new XmppStreamException("xml-not-well-formed", $"unexpected end tag {name}");
        var closed = _open[^1];
        _open.RemoveAt(_open.Count - 1);
        Consume(end + 1 - _position);
        if (_open.Count == 0)
        {
            events.Add(new XmppStreamClosed());
        }
        else if (_open.Count == 1)
        {
            _stanzaChars = 0;
            events.Add(new XmppStanza(closed));
        }
        return true;
    }

    private bool ParseStartTag(List<XmppStreamEvent> events)
    {
        var index = _position + 1;
        var name = ReadName(ref index);
        if (name.Length == 0)
            throw new XmppStreamException("xml-not-well-formed", "invalid element name");
        var attributes = new Dictionary<string, string>(StringComparer.Ordinal);
        var selfClosing = false;
        while (true)
        {
            index = SkipSpaces(index);
            if (index >= _buffer.Length)
                return CheckBuffered();
            var c = _buffer[index];
            if (c == '>')
            {
                index++;
                break;
            }
            if (c == '/')
            {
                if (index + 1 >= _buffer.Length)
                    return CheckBuffered();
                if (_buffer[index + 1] != '>')
                    throw new XmppStreamException("xml-not-well-formed", "expected '>' after '/'");
                selfClosing = true;
                index += 2;
                break;
            }
            var attributeName = ReadName(ref index);
            if (attributeName.Length == 0)
                throw new XmppStreamException("xml-not-well-formed", "invalid attribute name");
            index = SkipSpaces(index);
            if (index >= _buffer.Length)
                return CheckBuffered();
            if (_buffer[index] != '=')
                throw new XmppStreamException("xml-not-well-formed", "expected '=' after an attribute name");
            index = SkipSpaces(index + 1);
            if (index >= _buffer.Length)
                return CheckBuffered();
            var quote = _buffer[index];
            if (quote is not ('"' or '\''))
                throw new XmppStreamException("xml-not-well-formed", "attribute values must be quoted");
            var valueEnd = _buffer.IndexOf(quote, index + 1);
            if (valueEnd < 0)
                return CheckBuffered();
            var rawValue = _buffer.AsSpan(index + 1, valueEnd - index - 1);
            if (rawValue.IndexOf('<') >= 0)
                throw new XmppStreamException("xml-not-well-formed", "'<' in an attribute value");
            if (!attributes.TryAdd(attributeName, Decode(rawValue)) || attributes.Count > MaxAttributes)
                throw new XmppStreamException("xml-not-well-formed", "duplicate or excessive attributes");
            index = valueEnd + 1;
        }

        if (_open.Count == 0)
        {
            if (name != "stream:stream")
                throw new XmppStreamException("invalid-namespace", "the stream must open with stream:stream");
            if (selfClosing)
                throw new XmppStreamException("bad-format", "the stream header closed itself");
            var root = new XmppElement(name);
            foreach (var (key, value) in attributes)
                root.Attributes[key] = value;
            _open.Add(root);
            Consume(index - _position);
            events.Add(new XmppStreamOpened(root.Attributes));
            return true;
        }
        if (_open.Count >= MaxDepth)
            throw new XmppStreamException("policy-violation", "elements nested too deeply");
        if (_open.Count == 1)
            _stanzaChars = 0;
        var element = new XmppElement(name);
        foreach (var (key, value) in attributes)
            element.Attributes[key] = value;
        if (_open.Count >= 2)
            _open[^1].Children.Add(element);
        Consume(index - _position);
        if (selfClosing)
        {
            if (_open.Count == 1)
                events.Add(new XmppStanza(element));
            return true;
        }
        _open.Add(element);
        return true;
    }

    private int SkipSpaces(int index)
    {
        while (index < _buffer.Length && _buffer[index] is ' ' or '\t' or '\r' or '\n')
            index++;
        return index;
    }

    private string ReadName(ref int index)
    {
        var start = index;
        while (index < _buffer.Length && IsNameChar(_buffer[index], index == start))
            index++;
        return _buffer[start..index];
    }

    private static bool IsNameChar(char c, bool first) =>
        char.IsAsciiLetter(c) || c == '_' || (!first && (char.IsAsciiDigit(c) || c is '-' or '.' or ':'));

    private static string Decode(ReadOnlySpan<char> raw)
    {
        ValidateCharacters(raw);
        if (raw.IndexOf('&') < 0)
            return raw.ToString();
        var builder = new StringBuilder(raw.Length);
        for (var i = 0; i < raw.Length; i++)
        {
            if (raw[i] != '&')
            {
                builder.Append(raw[i]);
                continue;
            }
            var semicolon = raw[i..].IndexOf(';');
            if (semicolon < 0 || semicolon > 12)
                throw new XmppStreamException("restricted-xml", "unknown or unterminated entity");
            var entity = raw.Slice(i + 1, semicolon - 1).ToString();
            switch (entity)
            {
                case "lt": builder.Append('<'); break;
                case "gt": builder.Append('>'); break;
                case "amp": builder.Append('&'); break;
                case "quot": builder.Append('"'); break;
                case "apos": builder.Append('\''); break;
                default:
                    builder.Append(char.ConvertFromUtf32(NumericReference(entity)));
                    break;
            }
            i += semicolon;
        }
        return builder.ToString();
    }

    private static int NumericReference(string entity)
    {
        if (entity.Length < 2 || entity[0] != '#')
            throw new XmppStreamException("restricted-xml", "only the predefined and numeric entities are accepted");
        var hex = entity[1] == 'x';
        var digits = entity[(hex ? 2 : 1)..];
        var styles = hex ? NumberStyles.AllowHexSpecifier : NumberStyles.None;
        if (digits.Length == 0 || digits.Length > 8 || !int.TryParse(digits, styles, CultureInfo.InvariantCulture, out var code))
            throw new XmppStreamException("restricted-xml", "invalid character reference");
        if (code > 0x10FFFF || code is >= 0xD800 and <= 0xDFFF || code is < 0x20 and not (9 or 10 or 13))
            throw new XmppStreamException("restricted-xml", "invalid character reference");
        return code;
    }

    private static void ValidateCharacters(ReadOnlySpan<char> raw)
    {
        foreach (var c in raw)
            if (c < 0x20 && c is not ('\t' or '\n' or '\r'))
                throw new XmppStreamException("restricted-xml", "control character in the stream");
    }

    /// <summary>An incomplete token waits for more bytes; it is an error only when it outgrows the limits.</summary>
    private bool CheckBuffered()
    {
        var pending = _buffer.Length - _position;
        if (pending > MaxBufferedChars || (_open.Count >= 2 && _stanzaChars + pending > MaxStanzaChars))
            throw new XmppStreamException("policy-violation", "an element is larger than the stanza limit");
        return false;
    }

    private void Consume(int count)
    {
        if (_open.Count >= 2)
        {
            _stanzaChars += count;
            if (_stanzaChars > MaxStanzaChars)
                throw new XmppStreamException("policy-violation", "a stanza is larger than the stanza limit");
        }
        _position += count;
    }
}
