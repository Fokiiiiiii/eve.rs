using System.Collections.Immutable;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Eve.Services.Generator;

[Generator(LanguageNames.CSharp)]
public sealed class ServiceGenerator : IIncrementalGenerator
{
    private const string ServiceAttribute = "Eve.Services.ServiceAttribute";
    private const string MethodAttribute = "Eve.Services.MethodAttribute";

    private enum ReturnKind
    {
        Value,
        Task,
        ValueTask,
    }

    private sealed class MethodModel(string csName, string wireName, ReturnKind returns, Location location)
    {
        public string CsName { get; } = csName;

        public string WireName { get; } = wireName;

        public ReturnKind Returns { get; } = returns;

        public Location Location { get; } = location;
    }

    private sealed class ServiceModel(
        string typeName,
        string? ns,
        string wireName,
        ImmutableArray<string> aliases,
        ImmutableArray<MethodModel> methods,
        bool hasParameterlessConstructor,
        ImmutableArray<Diagnostic> diagnostics,
        Location location,
        string hintName)
    {
        public string TypeName { get; } = typeName;

        public string? Namespace { get; } = ns;

        public string WireName { get; } = wireName;

        public ImmutableArray<string> Aliases { get; } = aliases;

        public ImmutableArray<MethodModel> Methods { get; } = methods;

        public bool HasParameterlessConstructor { get; } = hasParameterlessConstructor;

        public ImmutableArray<Diagnostic> Diagnostics { get; } = diagnostics;

        public Location Location { get; } = location;

        public string HintName { get; } = hintName;
    }

    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var services = context.SyntaxProvider.ForAttributeWithMetadataName(
            ServiceAttribute,
            static (node, _) => node is ClassDeclarationSyntax,
            static (ctx, ct) => Model(ctx, ct));

        var compilationName = context.CompilationProvider.Select(static (compilation, _) => compilation.AssemblyName ?? "Assembly");

        context.RegisterSourceOutput(services.Collect().Combine(compilationName), static (spc, input) => Emit(spc, input.Left, input.Right));
    }

    private static ServiceModel Model(GeneratorAttributeSyntaxContext ctx, System.Threading.CancellationToken ct)
    {
        var type = (INamedTypeSymbol)ctx.TargetSymbol;
        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        var location = type.Locations.FirstOrDefault() ?? Location.None;

        var attribute = ctx.Attributes[0];
        var wireName = attribute.ConstructorArguments.Length > 0 ? attribute.ConstructorArguments[0].Value as string ?? "" : "";
        var aliases = ImmutableArray.CreateBuilder<string>();
        foreach (var named in attribute.NamedArguments)
        {
            if (named.Key == "Aliases")
            {
                foreach (var value in named.Value.Values)
                {
                    if (value.Value is string alias && alias.Length > 0)
                    {
                        aliases.Add(alias);
                    }
                }
            }
        }

        if (string.IsNullOrWhiteSpace(wireName))
        {
            diagnostics.Add(Diagnostic.Create(Diagnostics.EmptyName, location, "Service"));
        }

        var isPartial = type.DeclaringSyntaxReferences.All(reference =>
            reference.GetSyntax(ct) is ClassDeclarationSyntax declaration
            && declaration.Modifiers.Any(modifier => modifier.Text == "partial"));
        if (!isPartial)
        {
            diagnostics.Add(Diagnostic.Create(Diagnostics.ClassMustBePartial, location, type.Name));
        }
        if (type.ContainingType is not null)
        {
            diagnostics.Add(Diagnostic.Create(Diagnostics.UnsupportedShape, location, type.Name, "nested classes are not supported"));
        }
        if (type.IsGenericType)
        {
            diagnostics.Add(Diagnostic.Create(Diagnostics.UnsupportedShape, location, type.Name, "generic classes are not supported"));
        }
        if (type.IsAbstract || type.IsStatic)
        {
            diagnostics.Add(Diagnostic.Create(Diagnostics.UnsupportedShape, location, type.Name, "a service must be a concrete class"));
        }

        var methods = ImmutableArray.CreateBuilder<MethodModel>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var member in type.GetMembers().OfType<IMethodSymbol>())
        {
            var methodAttribute = member.GetAttributes().FirstOrDefault(a => a.AttributeClass?.ToDisplayString() == MethodAttribute);
            if (methodAttribute is null)
            {
                continue;
            }
            var methodLocation = member.Locations.FirstOrDefault() ?? location;
            var methodName = methodAttribute.ConstructorArguments.Length > 0 ? methodAttribute.ConstructorArguments[0].Value as string ?? "" : "";
            if (string.IsNullOrWhiteSpace(methodName))
            {
                diagnostics.Add(Diagnostic.Create(Diagnostics.EmptyName, methodLocation, "Method"));
                continue;
            }
            var returns = ClassifyReturn(member);
            if (returns is null || member.IsStatic || member.Parameters.Length != 1
                || member.Parameters[0].Type.ToDisplayString() != "Eve.Services.ServiceCall"
                || member.Parameters[0].RefKind != RefKind.None)
            {
                diagnostics.Add(Diagnostic.Create(Diagnostics.InvalidMethodSignature, methodLocation, member.Name));
                continue;
            }
            if (!seen.Add(methodName))
            {
                diagnostics.Add(Diagnostic.Create(Diagnostics.DuplicateMethod, methodLocation, wireName, methodName));
                continue;
            }
            methods.Add(new MethodModel(member.Name, methodName, returns.Value, methodLocation));
        }

        if (methods.Count == 0 && diagnostics.Count == 0)
        {
            diagnostics.Add(Diagnostic.Create(Diagnostics.NoMethods, location, wireName));
        }

        var hasParameterlessConstructor = type.InstanceConstructors.Any(c =>
            c.Parameters.Length == 0 && c.DeclaredAccessibility is Accessibility.Public or Accessibility.Internal);
        var ns = type.ContainingNamespace is { IsGlobalNamespace: false } containing ? containing.ToDisplayString() : null;
        var hint = (ns is null ? type.Name : ns + "." + type.Name) + ".Service.g.cs";
        return new ServiceModel(type.Name, ns, wireName, aliases.ToImmutable(), methods.ToImmutable(), hasParameterlessConstructor, diagnostics.ToImmutable(), location, hint);
    }

    private static ReturnKind? ClassifyReturn(IMethodSymbol method)
    {
        var text = method.ReturnType.ToDisplayString();
        return text switch
        {
            "Eve.Marshal.PyValue" => ReturnKind.Value,
            "System.Threading.Tasks.Task<Eve.Marshal.PyValue>" => ReturnKind.Task,
            "System.Threading.Tasks.ValueTask<Eve.Marshal.PyValue>" => ReturnKind.ValueTask,
            _ => null,
        };
    }

    private static void Emit(SourceProductionContext context, ImmutableArray<ServiceModel> services, string assemblyName)
    {
        foreach (var service in services)
        {
            foreach (var diagnostic in service.Diagnostics)
            {
                context.ReportDiagnostic(diagnostic);
            }
        }

        var valid = services.Where(s => s.Diagnostics.All(d => d.Severity != DiagnosticSeverity.Error)).ToList();
        var names = new Dictionary<string, ServiceModel>(StringComparer.Ordinal);
        var emitted = new List<ServiceModel>();
        foreach (var service in valid.OrderBy(s => s.HintName, StringComparer.Ordinal))
        {
            var clash = new[] { service.WireName }.Concat(service.Aliases).FirstOrDefault(name => names.ContainsKey(name));
            if (clash is not null)
            {
                context.ReportDiagnostic(Diagnostic.Create(Diagnostics.DuplicateService, service.Location, clash));
                continue;
            }
            foreach (var name in new[] { service.WireName }.Concat(service.Aliases))
            {
                names[name] = service;
            }
            emitted.Add(service);
            context.AddSource(service.HintName, ServiceSource(service));
        }

        if (emitted.Count > 0)
        {
            context.AddSource("ServiceCatalog.g.cs", CatalogSource(emitted, assemblyName));
        }
    }

    private static string Literal(string text) => "@\"" + text.Replace("\"", "\"\"") + "\"";

    private static string ServiceSource(ServiceModel service)
    {
        var sb = new StringBuilder();
        sb.AppendLine("// <auto-generated> by Eve.Services.Generator. Do not edit. </auto-generated>");
        sb.AppendLine("#nullable enable");
        sb.AppendLine();
        if (service.Namespace is not null)
        {
            sb.AppendLine("namespace " + service.Namespace);
            sb.AppendLine("{");
        }
        sb.AppendLine("partial class " + service.TypeName + " : global::Eve.Services.IService");
        sb.AppendLine("{");
        sb.AppendLine("    string global::Eve.Services.IService.Name => " + Literal(service.WireName) + ";");
        sb.AppendLine();
        sb.AppendLine("    bool global::Eve.Services.IService.HasMethod(string method)");
        sb.AppendLine("    {");
        sb.AppendLine("        switch (method)");
        sb.AppendLine("        {");
        foreach (var method in service.Methods)
        {
            sb.AppendLine("            case " + Literal(method.WireName) + ":");
        }
        if (service.Methods.Length > 0)
        {
            sb.AppendLine("                return true;");
        }
        sb.AppendLine("            default:");
        sb.AppendLine("                return false;");
        sb.AppendLine("        }");
        sb.AppendLine("    }");
        sb.AppendLine();
        sb.AppendLine("    global::System.Threading.Tasks.ValueTask<global::Eve.Marshal.PyValue> global::Eve.Services.IService.CallAsync(global::Eve.Services.ServiceCall call)");
        sb.AppendLine("    {");
        sb.AppendLine("        switch (call.Method)");
        sb.AppendLine("        {");
        foreach (var method in service.Methods)
        {
            sb.AppendLine("            case " + Literal(method.WireName) + ":");
            sb.AppendLine(method.Returns switch
            {
                ReturnKind.Value => "                return new global::System.Threading.Tasks.ValueTask<global::Eve.Marshal.PyValue>(" + method.CsName + "(call));",
                ReturnKind.Task => "                return new global::System.Threading.Tasks.ValueTask<global::Eve.Marshal.PyValue>(" + method.CsName + "(call));",
                _ => "                return " + method.CsName + "(call);",
            });
        }
        sb.AppendLine("            default:");
        sb.AppendLine("                // The reference server answers a method it does not have with None.");
        sb.AppendLine("                return new global::System.Threading.Tasks.ValueTask<global::Eve.Marshal.PyValue>(global::Eve.Marshal.PyNone.Instance);");
        sb.AppendLine("        }");
        sb.AppendLine("    }");
        sb.AppendLine("}");
        if (service.Namespace is not null)
        {
            sb.AppendLine("}");
        }
        return sb.ToString();
    }

    private static string CatalogSource(List<ServiceModel> services, string assemblyName)
    {
        var identifier = new string(assemblyName.Select(c => char.IsLetterOrDigit(c) ? c : '_').ToArray());
        var sb = new StringBuilder();
        sb.AppendLine("// <auto-generated> by Eve.Services.Generator. Do not edit. </auto-generated>");
        sb.AppendLine("#nullable enable");
        sb.AppendLine();
        sb.AppendLine("namespace Eve.Services.Generated");
        sb.AppendLine("{");
        sb.AppendLine("    /// <summary>Every [Service] class of this assembly.</summary>");
        sb.AppendLine("    public static class " + identifier + "ServiceCatalog");
        sb.AppendLine("    {");
        sb.AppendLine("        /// <summary>The service names, aliases excluded.</summary>");
        sb.AppendLine("        public static readonly string[] Names =");
        sb.AppendLine("        [");
        foreach (var service in services)
        {
            sb.AppendLine("            " + Literal(service.WireName) + ",");
        }
        sb.AppendLine("        ];");
        sb.AppendLine();
        sb.AppendLine("        /// <summary>Registers every service. An instance comes from <paramref name=\"provider\"/>, else a parameterless constructor.</summary>");
        sb.AppendLine("        public static void Register(global::Eve.Services.ServiceRegistry registry, global::System.IServiceProvider provider)");
        sb.AppendLine("        {");
        foreach (var service in services)
        {
            var fullName = "global::" + (service.Namespace is null ? "" : service.Namespace + ".") + service.TypeName;
            var create = service.HasParameterlessConstructor
                ? "(provider.GetService(typeof(" + fullName + ")) as " + fullName + ") ?? new " + fullName + "()"
                : "(provider.GetService(typeof(" + fullName + ")) as " + fullName + ") ?? throw new global::System.InvalidOperationException(" + Literal(service.TypeName + " has no parameterless constructor and is not registered with the service provider") + ")";
            sb.AppendLine("            registry.Register((global::Eve.Services.IService)(" + create + "));");
            foreach (var alias in service.Aliases)
            {
                sb.AppendLine("            registry.RegisterAlias(" + Literal(alias) + ", " + Literal(service.WireName) + ");");
            }
        }
        sb.AppendLine("        }");
        sb.AppendLine("    }");
        sb.AppendLine("}");
        return sb.ToString();
    }
}
