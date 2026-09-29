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
            _boundObjects[oid] = service;
            if (refId is PyLong { Value: var value } && (!_boundObjectRefIds.TryGetValue(oid, out var previous) || value > previous))
            {
                _boundObjectRefIds[oid] = value;
            }
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
    public bool UnregisterBoundObject(string oid, ClientSession? session = null, long? releasedRefId = null)
    {
        if (string.IsNullOrEmpty(oid))
        {
            return false;
        }

        IService? service;
        bool existed;
        lock (_gate)
        {
            if (releasedRefId is { } released && _boundObjectRefIds.TryGetValue(oid, out var latest) && released < latest)
            {
                return false;
            }
            _boundObjectRefIds.Remove(oid);
            service = _boundObjects.GetValueOrDefault(oid);
            existed = _boundObjects.Remove(oid);
            _boundObjectParams.Remove(oid);
        }

        var preserveIdentity = service is IReusableBoundObject { ReuseBoundObjectForSession: true };
        if (service is IBoundObjectReleaseHandler handler)
        {
            handler.OnBoundObjectReleased(oid, session);
        }

        if (session is not null)
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
        return existed;
    }

    /// <summary>A service by name or alias, or the service that owns a bound object's OID.</summary>
    public IService? Lookup(string? name)
    {
        if (string.IsNullOrEmpty(name))
        {
            return null;
        }
        lock (_gate)
        {
            return _services.GetValueOrDefault(name) ?? _boundObjects.GetValueOrDefault(name);
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
