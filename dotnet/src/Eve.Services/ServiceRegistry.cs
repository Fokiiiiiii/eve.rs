using Eve.Marshal;

namespace Eve.Services;

/// <summary>
/// Registers services by name and alias and tracks the bound objects they handed out
/// (the reference server's ServiceManager).
/// </summary>
public sealed class ServiceRegistry
{
    private readonly Dictionary<string, IService> _services = new(StringComparer.Ordinal);
    private readonly Dictionary<string, IService> _boundObjects = new(StringComparer.Ordinal);
    private readonly Dictionary<string, HashSet<ClientSession>> _boundObjectSessions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, PyValue?> _boundObjectParams = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> _boundObjectRefIds = new(StringComparer.Ordinal);
    private readonly object _gate = new();

    public Action<string>? OnWarning { get; set; }

    /// <summary>Registers a service under its name. A second registration under the same name replaces the first.</summary>
    public void Register(IService service)
    {
        if (string.IsNullOrEmpty(service.Name))
        {
            throw new ArgumentException("service must have a name", nameof(service));
        }
        lock (_gate)
        {
            if (_services.ContainsKey(service.Name))
            {
                OnWarning?.Invoke($"service already registered: {service.Name}");
            }
            _services[service.Name] = service;
        }
    }

    /// <summary>Makes <paramref name="alias"/> reach an already registered service.</summary>
    public void RegisterAlias(string alias, string target)
    {
        var name = (alias ?? "").Trim();
        if (name.Length == 0)
        {
            throw new ArgumentException("alias must be a non-empty string", nameof(alias));
        }
        lock (_gate)
        {
            var service = Lookup(target) ?? throw new InvalidOperationException($"cannot register alias {name}; target service not found");
            if (_services.ContainsKey(name))
            {
                OnWarning?.Invoke($"service alias already registered: {name}");
            }
            _services[name] = service;
        }
    }

    /// <summary>Records that <paramref name="oid"/> is served by <paramref name="service"/>; the newest bind (largest refID) wins.</summary>
    public void RegisterBoundObject(string oid, IService service, PyValue? refId = null)
    {
        lock (_gate)
        {
            RegisterBoundObjectCore(oid, service, refId);
        }
    }

    /// <summary>Records that a bind handed <paramref name="oid"/> to <paramref name="session"/>.</summary>
    public void RegisterBoundObjectForSession(string oid, IService service, ClientSession session, PyValue? refId = null)
    {
        ArgumentNullException.ThrowIfNull(session);
        lock (_gate)
        {
            RegisterBoundObjectCore(oid, service, refId);
            if (!_boundObjectSessions.TryGetValue(oid, out var sessions))
            {
                sessions = new HashSet<ClientSession>(ReferenceEqualityComparer.Instance);
                _boundObjectSessions.Add(oid, sessions);
            }
            sessions.Add(session);
        }
        session.RememberBoundObject(oid);
    }

    private void RegisterBoundObjectCore(string oid, IService service, PyValue? refId)
    {
        _boundObjects[oid] = service;
        if (refId is PyLong { Value: var value } && (!_boundObjectRefIds.TryGetValue(oid, out var previous) || value > previous))
        {
            _boundObjectRefIds[oid] = value;
        }
    }

    /// <summary>The parameter a bound object was created with (e.g. the corporation id), so its methods act on that entity.</summary>
    public void RegisterBoundObjectParams(string oid, PyValue? parameters)
    {
        if (string.IsNullOrEmpty(oid))
        {
            return;
        }
        lock (_gate)
        {
            _boundObjectParams[oid] = parameters;
        }
    }

    public PyValue? GetBoundObjectParams(string oid)
    {
        if (string.IsNullOrEmpty(oid))
        {
            return null;
        }
        lock (_gate)
        {
            return _boundObjectParams.GetValueOrDefault(oid);
        }
    }

    /// <summary>
    /// Releases a bound object after the client says it let go of it. A release that names an older
    /// refID than the object's current bind was queued for a connection the client has since replaced,
    /// so the object is kept. Returns whether the object existed.
    /// </summary>
    public bool UnregisterBoundObject(string oid, ClientSession? session = null, long? releasedRefId = null) =>
        UnregisterBoundObjectCore(oid, session, releasedRefId, out _, out _);

    public async ValueTask<bool> UnregisterBoundObjectAsync(string oid, ClientSession? session = null,
        long? releasedRefId = null, CancellationToken cancellationToken = default)
    {
        var receipt = DetachBoundObject(oid, session, releasedRefId, out var existed);
        if (receipt is not null) await receipt(cancellationToken).ConfigureAwait(false);
        return existed;
    }

    /// <summary>Releases the bound-object references held by a session which has disconnected.</summary>
    public async Task<int> ReleaseSessionBoundObjectsAsync(ClientSession session, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        var released = 0;
        var failures = new List<Exception>();
        foreach (var oid in session.BoundOids)
        {
            try
            {
                var cleanup = DetachBoundObject(oid, session, null, out var existed);
                if (existed) released++;
                if (cleanup is not null) await cleanup(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception error)
            {
                failures.Add(error);
            }
        }
        if (failures.Count > 0)
            throw new AggregateException("Bound objects held by the session could not all be released.", failures);
        return released;
    }

    private readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, Func<CancellationToken, ValueTask>> _pendingBoundReleases = new();

    /// <summary>Detach synchronously and retain cleanup until it succeeds outside the ordered callback.</summary>
    public Func<CancellationToken, ValueTask>? DetachBoundObject(string oid, ClientSession? session = null, long? releasedRefId = null)
        => DetachBoundObject(oid, session, releasedRefId, out _);

    private Func<CancellationToken, ValueTask>? DetachBoundObject(string oid, ClientSession? session, long? releasedRefId, out bool existed)
    {
        existed = UnregisterBoundObjectCore(oid, session, releasedRefId, out var service, out var captured);
        if (!existed) return null;
        var cleanup = service is ICapturedBoundObjectReleaseHandler ? captured :
            service is IAsyncBoundObjectReleaseHandler handler
                ? token => handler.OnBoundObjectReleasedAsync(oid, session, token) : null;
        if (cleanup is null) return null;
        var id = Guid.NewGuid();
        var gate = new SemaphoreSlim(1, 1);
        async ValueTask Complete(CancellationToken token)
        {
            await gate.WaitAsync(token).ConfigureAwait(false);
            try
            {
                if (!_pendingBoundReleases.ContainsKey(id)) return;
                await cleanup(token).ConfigureAwait(false);
                _pendingBoundReleases.TryRemove(id, out _);
            }
            finally { gate.Release(); }
        }
        _pendingBoundReleases[id] = Complete;
        return Complete;
    }

    /// <summary>Retry any previously detached cleanup that did not finish.</summary>
    public async Task RetryPendingBoundReleasesAsync(CancellationToken cancellationToken = default)
    {
        var failures = new List<Exception>();
        foreach (var release in _pendingBoundReleases.Values)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            try { await release(timeout.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception error) { failures.Add(error); }
        }
        if (failures.Count > 0) throw new AggregateException("Bound object cleanup remains pending.", failures);
    }

    private bool UnregisterBoundObjectCore(string oid, ClientSession? session, long? releasedRefId, out IService? releasedService,
        out Func<CancellationToken, ValueTask>? captured)
    {
        releasedService = null;
        captured = null;
        if (string.IsNullOrEmpty(oid))
        {
            return false;
        }

        IService? service;
        bool existed;
        bool removedRegistryEntry;
        bool partiallyReleased;
        var sessionsToForget = new List<ClientSession>();
        lock (_gate)
        {
            if (releasedRefId is { } released && _boundObjectRefIds.TryGetValue(oid, out var latest) && released < latest)
            {
                return false;
            }
            service = _boundObjects.GetValueOrDefault(oid);
            if (service is null)
            {
                return false;
            }

            partiallyReleased = false;
            if (session is not null && _boundObjectSessions.TryGetValue(oid, out var holders))
            {
                if (!holders.Remove(session))
                {
                    return false;
                }
                sessionsToForget.Add(session);
                if (holders.Count > 0)
                {
                    // An OID can be shared by sessions. Dropping one holder must not revoke the others.
                    partiallyReleased = true;
                }
                else
                {
                    _boundObjectSessions.Remove(oid);
                }
            }
            else if (session is not null)
            {
                // A direct registry registration can be used by internal callers and tests which do not
                // have a bind owner record. Preserve the historic one-holder release behavior for those.
                sessionsToForget.Add(session);
            }
            else if (_boundObjectSessions.Remove(oid, out var ownedSessions))
            {
                sessionsToForget.AddRange(ownedSessions);
            }

            existed = true;
            removedRegistryEntry = !partiallyReleased;
            if (removedRegistryEntry)
            {
                _boundObjectRefIds.Remove(oid);
                _boundObjects.Remove(oid);
                _boundObjectParams.Remove(oid);
                if (service is ICapturedBoundObjectReleaseHandler capturing)
                    captured = capturing.CaptureBoundObjectRelease(oid, session);
            }
        }

        releasedService = removedRegistryEntry ? service : null;
        var preserveIdentity = service is IReusableBoundObject { ReuseBoundObjectForSession: true };
        foreach (var holder in sessionsToForget)
        {
            ForgetSessionBoundObject(holder, oid, preserveIdentity);
        }
        if (removedRegistryEntry && service is IBoundObjectReleaseHandler handler)
        {
            handler.OnBoundObjectReleased(oid, session);
        }
        return existed;
    }

    private static void ForgetSessionBoundObject(ClientSession session, string oid, bool preserveIdentity)
    {
        session.ForgetBoundObject(oid);
        if (!preserveIdentity)
        {
            foreach (var name in session.BoundObjectIds.Where(pair => pair.Value == oid).Select(pair => pair.Key).ToList())
            {
                session.BoundObjectIds.Remove(name);
            }
        }
        foreach (var name in session.BoundObjectStates.Where(pair => pair.Value.ObjectId == oid).Select(pair => pair.Key).ToList())
        {
            session.BoundObjectStates.Remove(name);
        }
        if (session.CurrentBoundObjectId == oid)
        {
            session.CurrentBoundObjectId = null;
        }
        if (session.LastBoundObjectId == oid)
        {
            session.LastBoundObjectId = null;
        }
    }

    /// <summary>A service by name or alias, or the service that owns a bound object's OID.</summary>
    public IService? Lookup(string? name) => Lookup(name, null);

    /// <summary>A service by name, or a bound object's service if this session holds that object.</summary>
    public IService? Lookup(string? name, ClientSession? session)
    {
        if (string.IsNullOrEmpty(name))
        {
            return null;
        }
        lock (_gate)
        {
            if (_services.TryGetValue(name, out var service))
            {
                return service;
            }
            if (!_boundObjects.TryGetValue(name, out service) || session is null)
            {
                return service;
            }
            if (_boundObjectSessions.TryGetValue(name, out var sessions))
            {
                return sessions.Contains(session) ? service : null;
            }
            // Keep explicit test and internal registrations compatible; a production bind records its holder above.
            return session.BoundOids.Contains(name, StringComparer.Ordinal) ? service : null;
        }
    }

    public IReadOnlyList<string> ServiceNames
    {
        get
        {
            lock (_gate)
            {
                return [.. _services.Keys];
            }
        }
    }

    /// <summary>Each registered service instance once, with aliases collapsed.</summary>
    public IReadOnlyList<IService> RegisteredServices
    {
        get
        {
            lock (_gate)
            {
                var seen = new HashSet<IService>(ReferenceEqualityComparer.Instance);
                return _services.Values.Where(seen.Add).ToArray();
            }
        }
    }

    /// <summary>The number of registered names, aliases included.</summary>
    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _services.Count;
            }
        }
    }

    /// <summary>The bound objects currently registered, with the refID of their newest bind.</summary>
    public IReadOnlyDictionary<string, (string Service, long? RefId)> BoundObjects
    {
        get
        {
            lock (_gate)
            {
                return _boundObjects.ToDictionary(
                    pair => pair.Key,
                    pair => (pair.Value.Name, _boundObjectRefIds.TryGetValue(pair.Key, out var refId) ? (long?)refId : null));
            }
        }
    }
}
