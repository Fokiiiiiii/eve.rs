using System.Threading.Channels;

namespace Eve.Host.PublicGateway;

/// <summary>
/// An open Notices.Consume stream: the account its bearer token names, and the active character it identified (null until a request
/// names one the account owns). Its frames are the notices routed to it, already in the gRPC framing's message form.
/// </summary>
internal sealed class NoticeSubscriber(long? account)
{
    public long? Account { get; } = account;

    public long? Character { get; set; }

    public Channel<byte[]> Frames { get; } = Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions { SingleReader = true });
}

/// <summary>
/// The notices the public gateway pushes to its open Notices.Consume streams. A notice goes to the streams whose identity it is for, as
/// EveJS publicGatewayLocal.js routes them: by character (the stream's active character) or by user (the stream's account). The other
/// target groups are not published by this host yet.
/// </summary>
internal sealed class NoticeHub
{
    private readonly object _gate = new();
    private readonly HashSet<NoticeSubscriber> _subscribers = [];

    public NoticeSubscriber Subscribe(long? account)
    {
        var subscriber = new NoticeSubscriber(account);
        lock (_gate)
            _subscribers.Add(subscriber);
        return subscriber;
    }

    public void Unsubscribe(NoticeSubscriber subscriber)
    {
        lock (_gate)
            _subscribers.Remove(subscriber);
        subscriber.Frames.Writer.TryComplete();
    }

    /// <summary>Sends a notice of a type to the streams of a character, or else of an account.</summary>
    public void Publish(string typeName, ReadOnlySpan<byte> payload, long? character = null, long? account = null)
    {
        var frame = GatewayEnvelope.Frame(GatewayEnvelope.EncodeNotice(typeName, payload, character, account, DateTimeOffset.UtcNow));
        List<NoticeSubscriber> targets;
        lock (_gate)
        {
            targets = [.. _subscribers.Where(subscriber => character is { } id
                ? subscriber.Character == id
                : account is { } user && subscriber.Account == user)];
        }
        foreach (var subscriber in targets)
            subscriber.Frames.Writer.TryWrite(frame);
    }
}
