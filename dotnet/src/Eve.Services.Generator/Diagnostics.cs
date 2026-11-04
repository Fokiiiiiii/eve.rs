using Microsoft.CodeAnalysis;

namespace Eve.Services.Generator;

internal static class Diagnostics
{
    private const string Category = "Eve.Services";

    public static readonly DiagnosticDescriptor ClassMustBePartial = new(
        "EVESVC001", "Service class must be partial",
        "'{0}' is marked [Service] but is not declared partial, so its dispatch cannot be generated",
        Category, DiagnosticSeverity.Error, isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor InvalidMethodSignature = new(
        "EVESVC002", "Invalid service method signature",
        "'{0}' is marked [Method] but must have the form 'PyValue|Task<PyValue>|ValueTask<PyValue> Name(ServiceCall call)' and be an instance method",
        Category, DiagnosticSeverity.Error, isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor DuplicateMethod = new(
        "EVESVC003", "Duplicate service method name",
        "Service '{0}' has more than one method named '{1}'",
        Category, DiagnosticSeverity.Error, isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor DuplicateService = new(
        "EVESVC004", "Duplicate service name",
        "Service name '{0}' is used by more than one class",
        Category, DiagnosticSeverity.Error, isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor EmptyName = new(
        "EVESVC005", "Empty service or method name",
        "{0} names must not be empty",
        Category, DiagnosticSeverity.Error, isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor UnsupportedShape = new(
        "EVESVC006", "Unsupported service class",
        "'{0}' cannot be a service: {1}",
        Category, DiagnosticSeverity.Error, isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor NoMethods = new(
        "EVESVC007", "Service has no methods",
        "Service '{0}' has no [Method] methods, so every call to it is answered with None",
        Category, DiagnosticSeverity.Warning, isEnabledByDefault: true);
}
