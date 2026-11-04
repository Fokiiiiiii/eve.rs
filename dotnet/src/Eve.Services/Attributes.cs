namespace Eve.Services;

/// <summary>
/// Marks a partial class as a game service reachable by the client under <paramref name="name"/>.
/// A source generator writes its <see cref="IService"/> dispatch: no reflection at run time.
/// </summary>
/// <param name="name">The service name the client calls.</param>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public sealed class ServiceAttribute(string name) : Attribute
{
    public string Name { get; } = name;

    /// <summary>Other names that reach the same instance.</summary>
    public string[] Aliases { get; init; } = [];
}

/// <summary>
/// Marks a method of a <see cref="ServiceAttribute"/> class as callable by the client under
/// <paramref name="name"/> (the reference server's <c>Handle_&lt;name&gt;</c>). Signature:
/// <c>(ServiceCall call)</c> returning <c>PyValue</c>, <c>Task&lt;PyValue&gt;</c> or <c>ValueTask&lt;PyValue&gt;</c>.
/// </summary>
/// <param name="name">The method name the client calls.</param>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = false)]
public sealed class MethodAttribute(string name) : Attribute
{
    public string Name { get; } = name;
}
