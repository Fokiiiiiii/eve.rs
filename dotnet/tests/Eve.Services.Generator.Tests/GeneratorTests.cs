using System.Reflection;
using System.Runtime.Loader;
using Eve.Marshal;
using Eve.Services;
using Eve.Services.Generator;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Eve.Services.Generator.Tests;

public class GeneratorTests
{
    private static readonly MetadataReference[] References = BuildReferences();

    private static MetadataReference[] BuildReferences()
    {
        var trusted = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator);
        // The whole trusted platform: type forwards (IServiceProvider lives behind System.ComponentModel) need it.
        var paths = trusted.Where(p => Path.GetFileName(p).StartsWith("System.", StringComparison.Ordinal) || Path.GetFileName(p) is "netstandard.dll" or "mscorlib.dll").ToList();
        paths.Add(typeof(PyValue).Assembly.Location);
        paths.Add(typeof(ServiceAttribute).Assembly.Location);
        paths.Add(typeof(Eve.Macho.MachoPacket).Assembly.Location);
        return [.. paths.Select(p => MetadataReference.CreateFromFile(p))];
    }

    private sealed record Result(Compilation Output, IReadOnlyList<Diagnostic> GeneratorDiagnostics, IReadOnlyList<(string Hint, string Text)> Sources)
    {
        public IReadOnlyList<Diagnostic> CompileErrors => [.. Output.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error)];

        public string Text(string hintContains) => Sources.Single(s => s.Hint.Contains(hintContains)).Text;
    }

    private static Result Run(string source, string assemblyName = "Sample")
    {
        var compilation = CSharpCompilation.Create(
            assemblyName,
            [CSharpSyntaxTree.ParseText(source)],
            References,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));
        var driver = CSharpGeneratorDriver.Create(new ServiceGenerator()).RunGeneratorsAndUpdateCompilation(compilation, out var output, out var diagnostics);
        var run = driver.GetRunResult();
        var sources = run.Results.SelectMany(r => r.GeneratedSources).Select(s => (s.HintName, s.SourceText.ToString())).ToList();
        return new Result(output, diagnostics, sources);
    }

    private static object Load(Compilation compilation, string typeName)
    {
        using var stream = new MemoryStream();
        var emit = compilation.Emit(stream);
        Assert.True(emit.Success, string.Join("\n", emit.Diagnostics));
        stream.Position = 0;
        var context = new AssemblyLoadContext(null, isCollectible: true);
        var assembly = context.LoadFromStream(stream);
        return Activator.CreateInstance(assembly.GetType(typeName)!)!;
    }

    private const string Usings = "using Eve.Marshal; using Eve.Services; using System.Threading.Tasks;\n";

    [Fact]
    public async Task A_service_class_gets_a_switch_dispatch_that_runs()
    {
        var result = Run(Usings + """
            namespace App;
            [Service("greeter", Aliases = ["hello"])]
            public partial class Greeter
            {
                [Method("Hi")] public PyValue Hi(ServiceCall call) => new PyStr("hi");
                [Method("Later")] public Task<PyValue> Later(ServiceCall call) => Task.FromResult<PyValue>(new PyInt(2));
                [Method("Fast")] public ValueTask<PyValue> Fast(ServiceCall call) => new(new PyInt(3));
            }
            """);

        Assert.Empty(result.CompileErrors);
        Assert.Empty(result.GeneratorDiagnostics);
        var text = result.Text("Greeter.Service.g.cs");
        Assert.Contains("partial class Greeter : global::Eve.Services.IService", text);
        Assert.Contains("switch (call.Method)", text);
        Assert.DoesNotContain("Reflection", text);

        var service = (IService)Load(result.Output, "App.Greeter");
        Assert.Equal("greeter", service.Name);
        Assert.True(service.HasMethod("Hi"));
        Assert.False(service.HasMethod("Nope"));
        var call = new ServiceCall { Method = "Hi", Args = [], Session = new ClientSession() };
        Assert.Equal(new PyStr("hi"), await service.CallAsync(call));
        Assert.Equal(new PyInt(2), await service.CallAsync(new ServiceCall { Method = "Later", Args = [], Session = call.Session }));
        Assert.Equal(new PyInt(3), await service.CallAsync(new ServiceCall { Method = "Fast", Args = [], Session = call.Session }));
        Assert.Equal(PyNone.Instance, await service.CallAsync(new ServiceCall { Method = "Nope", Args = [], Session = call.Session }));
    }

    [Fact]
    public void The_catalog_registers_services_and_aliases()
    {
        var result = Run(Usings + """
            [Service("a", Aliases = ["a1", "a2"])] public partial class A { [Method("X")] public PyValue X(ServiceCall c) => PyNone.Instance; }
            [Service("b")] public partial class B { public B(int unused) {} [Method("X")] public PyValue X(ServiceCall c) => PyNone.Instance; }
            """, "My.Assembly-Name");
        Assert.Empty(result.CompileErrors);
        var catalog = result.Text("ServiceCatalog.g.cs");
        Assert.Contains("class My_Assembly_NameServiceCatalog", catalog);
        Assert.Contains("registry.RegisterAlias(@\"a1\", @\"a\");", catalog);
        Assert.Contains("registry.RegisterAlias(@\"a2\", @\"a\");", catalog);
        // A class without a parameterless constructor must come from the provider.
        Assert.Contains("has no parameterless constructor", catalog);
        Assert.Contains("?? new global::A()", catalog);
    }

    [Fact]
    public void A_service_outside_any_namespace_works()
    {
        var result = Run(Usings + "[Service(\"g\")] public partial class Global { [Method(\"X\")] public PyValue X(ServiceCall c) => PyNone.Instance; }");
        Assert.Empty(result.CompileErrors);
        Assert.Contains("partial class Global : global::Eve.Services.IService", result.Text("Global.Service.g.cs"));
    }

    [Fact]
    public void Names_with_quotes_are_escaped()
    {
        var result = Run(Usings + "[Service(\"q\\\"uote\")] public partial class Q { [Method(\"m\\\"x\")] public PyValue X(ServiceCall c) => PyNone.Instance; }");
        Assert.Empty(result.CompileErrors);
        var service = (IService)Load(result.Output, "Q");
        Assert.Equal("q\"uote", service.Name);
        Assert.True(service.HasMethod("m\"x"));
    }

    private static string[] ErrorIds(Result result) => [.. result.GeneratorDiagnostics.Select(d => d.Id)];

    [Fact]
    public void A_class_that_is_not_partial_is_an_error()
    {
        var result = Run(Usings + "[Service(\"s\")] public class S { [Method(\"X\")] public PyValue X(ServiceCall c) => PyNone.Instance; }");
        Assert.Equal(["EVESVC001"], ErrorIds(result));
        Assert.DoesNotContain(result.Sources, s => s.Hint.Contains("S.Service"));
    }

    [Theory]
    [InlineData("public PyValue X() => PyNone.Instance;")]
    [InlineData("public PyValue X(ServiceCall c, int extra) => PyNone.Instance;")]
    [InlineData("public int X(ServiceCall c) => 1;")]
    [InlineData("public void X(ServiceCall c) { }")]
    [InlineData("public static PyValue X(ServiceCall c) => PyNone.Instance;")]
    [InlineData("public PyValue X(int c) => PyNone.Instance;")]
    [InlineData("public Task X(ServiceCall c) => Task.CompletedTask;")]
    [InlineData("public PyValue X(in ServiceCall c) => PyNone.Instance;")]
    public void A_method_with_the_wrong_shape_is_an_error(string method)
    {
        var result = Run(Usings + $"[Service(\"s\")] public partial class S {{ [Method(\"X\")] {method} }}");
        Assert.Contains("EVESVC002", ErrorIds(result));
    }

    [Fact]
    public void Two_methods_with_one_name_are_an_error()
    {
        var result = Run(Usings + "[Service(\"s\")] public partial class S { [Method(\"X\")] public PyValue A(ServiceCall c) => PyNone.Instance; [Method(\"X\")] public PyValue B(ServiceCall c) => PyNone.Instance; }");
        Assert.Contains("EVESVC003", ErrorIds(result));
    }

    [Fact]
    public void Two_classes_with_one_service_name_or_alias_are_an_error()
    {
        var byName = Run(Usings + """
            [Service("s")] public partial class A { [Method("X")] public PyValue X(ServiceCall c) => PyNone.Instance; }
            [Service("s")] public partial class B { [Method("X")] public PyValue X(ServiceCall c) => PyNone.Instance; }
            """);
        Assert.Contains("EVESVC004", ErrorIds(byName));
        var byAlias = Run(Usings + """
            [Service("a", Aliases = ["x"])] public partial class A { [Method("X")] public PyValue X(ServiceCall c) => PyNone.Instance; }
            [Service("b", Aliases = ["x"])] public partial class B { [Method("X")] public PyValue X(ServiceCall c) => PyNone.Instance; }
            """);
        Assert.Contains("EVESVC004", ErrorIds(byAlias));
    }

    [Fact]
    public void Empty_names_are_an_error()
    {
        Assert.Contains("EVESVC005", ErrorIds(Run(Usings + "[Service(\"\")] public partial class S { [Method(\"X\")] public PyValue X(ServiceCall c) => PyNone.Instance; }")));
        Assert.Contains("EVESVC005", ErrorIds(Run(Usings + "[Service(\"s\")] public partial class S { [Method(\" \")] public PyValue X(ServiceCall c) => PyNone.Instance; }")));
    }

    [Fact]
    public void Nested_generic_and_abstract_classes_are_refused()
    {
        var nested = Run(Usings + "public partial class Outer { [Service(\"n\")] public partial class N { [Method(\"X\")] public PyValue X(ServiceCall c) => PyNone.Instance; } }");
        Assert.Contains("EVESVC006", ErrorIds(nested));
        var generic = Run(Usings + "[Service(\"g\")] public partial class G<T> { [Method(\"X\")] public PyValue X(ServiceCall c) => PyNone.Instance; }");
        Assert.Contains("EVESVC006", ErrorIds(generic));
        var @abstract = Run(Usings + "[Service(\"a\")] public abstract partial class A { [Method(\"X\")] public PyValue X(ServiceCall c) => PyNone.Instance; }");
        Assert.Contains("EVESVC006", ErrorIds(@abstract));
    }

    [Fact]
    public void A_service_without_methods_is_a_warning_not_an_error()
    {
        var result = Run(Usings + "[Service(\"s\")] public partial class S { }");
        Assert.Equal(["EVESVC007"], ErrorIds(result));
        Assert.All(result.GeneratorDiagnostics, d => Assert.Equal(DiagnosticSeverity.Warning, d.Severity));
        Assert.Empty(result.CompileErrors);
    }

    [Fact]
    public void Private_and_internal_methods_can_be_services_methods()
    {
        var result = Run(Usings + "[Service(\"s\")] internal partial class S { [Method(\"X\")] private PyValue X(ServiceCall c) => PyNone.Instance; }");
        Assert.Empty(result.CompileErrors);
        Assert.Empty(result.GeneratorDiagnostics);
    }

    [Fact]
    public void The_output_is_deterministic()
    {
        const string source = Usings + """
            [Service("b")] public partial class B { [Method("Y")] public PyValue Y(ServiceCall c) => PyNone.Instance; [Method("X")] public PyValue X(ServiceCall c) => PyNone.Instance; }
            [Service("a")] public partial class A { [Method("X")] public PyValue X(ServiceCall c) => PyNone.Instance; }
            """;
        var first = Run(source);
        var second = Run(source);
        Assert.Equal(first.Sources, second.Sources);
        // Catalog order does not depend on declaration order.
        var catalog = first.Text("ServiceCatalog.g.cs");
        Assert.True(catalog.IndexOf("@\"a\"", StringComparison.Ordinal) < catalog.IndexOf("@\"b\"", StringComparison.Ordinal));
    }
}
