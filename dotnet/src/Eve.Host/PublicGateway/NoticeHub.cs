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

/// target groups are not published by this host yet.
/// </summary>
internal sealed class NoticeHub
{
    private sealed class DeliveryState
    {
        public HashSet<NoticeSubscriber> Pending { get; } = [];

        public HashSet<NoticeSubscriber> Flushed { get; } = [];

        public bool IsDelivered => Flushed.Count > 0 && Pending.Count == 0;
    }

    private sealed record Receipt((string Key, int Index) Delivery, NoticeSubscriber Subscriber);

    private readonly object _gate = new();
    private readonly HashSet<NoticeSubscriber> _subscribers = [];
    private readonly Dictionary<(string Key, int Index), DeliveryState> _deliveries = [];
    private readonly Dictionary<byte[], Receipt> _receipts = [];

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
        {
            _subscribers.Remove(subscriber);
            foreach (var (frame, receipt) in _receipts.Where(pair => ReferenceEquals(pair.Value.Subscriber, subscriber)).ToArray())
            {
                _receipts.Remove(frame);
                if (!_deliveries.TryGetValue(receipt.Delivery, out var delivery))
                    continue;
                delivery.Pending.Remove(subscriber);
                if (delivery.Flushed.Count == 0 && delivery.Pending.Count == 0)
                    _deliveries.Remove(receipt.Delivery);
            }
        }
        subscriber.Frames.Writer.TryComplete();
    }

    /// <summary>Sends a notice of a type to the streams of a character, or else of an account.</summary>
    public bool Publish(string typeName, ReadOnlySpan<byte> payload, long? character = null, long? account = null)
    {
        var frame = GatewayEnvelope.Frame(GatewayEnvelope.EncodeNotice(typeName, payload, character, account, DateTimeOffset.UtcNow));
        List<NoticeSubscriber> targets;
        lock (_gate)
        {
            targets = [.. _subscribers.Where(subscriber => character is { } id
                ? subscriber.Character == id
                : account is { } user && subscriber.Account == user)];
        }
        if (targets.Count == 0)
            return false;
        foreach (var subscriber in targets)
            if (!subscriber.Frames.Writer.TryWrite(frame))
                return false;
        return true;
    }

    /// <summary>Queues a durable notice once per open stream until its frame is flushed by the gateway writer.</summary>
    public bool PublishDurable(string key, int index, string typeName, ReadOnlySpan<byte> payload, long character)
    {
        var frame = GatewayEnvelope.Frame(GatewayEnvelope.EncodeNotice(typeName, payload, character, null, DateTimeOffset.UtcNow));
        lock (_gate)
        {
            var deliveryKey = (key, index);
            if (!_deliveries.TryGetValue(deliveryKey, out var delivery))
            {
                delivery = new DeliveryState();
                _deliveries.Add(deliveryKey, delivery);
            }
            if (delivery.IsDelivered)
                return true;

            var targets = _subscribers.Where(subscriber => subscriber.Character == character).ToArray();
            foreach (var subscriber in targets)
            {
                if (delivery.Pending.Contains(subscriber) || delivery.Flushed.Contains(subscriber))
                    continue;
                var subscriberFrame = frame.ToArray();
                delivery.Pending.Add(subscriber);
                _receipts.Add(subscriberFrame, new Receipt(deliveryKey, subscriber));
                if (!subscriber.Frames.Writer.TryWrite(subscriberFrame))
                {
                    _receipts.Remove(subscriberFrame);
                    delivery.Pending.Remove(subscriber);
                }
            }

            if (delivery.Flushed.Count == 0 && delivery.Pending.Count == 0)
                _deliveries.Remove(deliveryKey);
            return delivery.IsDelivered || delivery.Pending.Count > 0;
        }
    }

    /// <summary>Records that the authenticated stream's writer flushed this durable notice frame.</summary>
    public void Acknowledge(NoticeSubscriber subscriber, byte[] frame)
    {
        lock (_gate)
        {
            if (!_receipts.TryGetValue(frame, out var receipt) || !ReferenceEquals(receipt.Subscriber, subscriber))
                return;
            _receipts.Remove(frame);
            if (_deliveries.TryGetValue(receipt.Delivery, out var delivery))
            {
                delivery.Pending.Remove(subscriber);
                delivery.Flushed.Add(subscriber);
            }
        }
    }

    public bool IsDelivered(string key, int index)
    {
        lock (_gate)
            return _deliveries.TryGetValue((key, index), out var delivery) && delivery.IsDelivered;
    }

    public void Forget(string key)
    {
        lock (_gate)
        {
            foreach (var deliveryKey in _deliveries.Keys.Where(delivery => delivery.Key == key).ToArray())
                _deliveries.Remove(deliveryKey);
            foreach (var frame in _receipts.Where(pair => pair.Value.Delivery.Key == key).Select(pair => pair.Key).ToArray())
                _receipts.Remove(frame);
        }
    }
}
