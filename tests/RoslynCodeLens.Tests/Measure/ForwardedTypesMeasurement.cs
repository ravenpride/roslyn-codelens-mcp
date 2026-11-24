using System.Text.Json;
using Microsoft.CodeAnalysis.FindSymbols;
using RoslynCodeLens;
using RoslynCodeLens.Symbols;
using RoslynCodeLens.Tools;

namespace RoslynCodeLens.Tests.Measure;

// TEMPORARY measurement, not for upstream: which tools miss framework-symbol usages on other target frameworks?
public class ForwardedTypesMeasurement
{
    private static readonly string[] Projects = ["Net48Lib", "NetStdLib", "Net10App"];

    [Fact]
    public async Task Measure()
    {
        var path = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "Fixtures", "ForwardedTypesSolution", "ForwardedTypesSolution.slnx"));
        var loaded = await new SolutionLoader().LoadAsync(path);
        var resolver = new SymbolResolver(loaded);
        var metadata = new MetadataSymbolResolver(loaded, resolver);
        var lines = new List<string> { $"compilations: {loaded.Compilations.Count}" };
        foreach (var (id, c) in loaded.Compilations)
            lines.Add($"  {loaded.Solution.GetProject(id)?.Name}: errors={c.GetDiagnostics().Count(d => d.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error)}");

        void Run(string label, Func<object?> call)
        {
            string json;
            try { json = JsonSerializer.Serialize(call(), new JsonSerializerOptions { WriteIndented = true }); }
            catch (Exception ex) { json = "EXCEPTION " + ex.GetType().Name + ": " + ex.Message; }
            var hits = string.Join(" ", Projects.Select(p => $"{p}={(json.Contains(p, StringComparison.Ordinal) ? "yes" : "NO")}"));
            lines.Add($"{label,-70} {hits}");
            File.AppendAllText(Path.Combine(Path.GetTempPath(), "rclens-measure-detail.txt"), $"===== {label}\n{json}\n");
        }

        File.Delete(Path.Combine(Path.GetTempPath(), "rclens-measure-detail.txt"));
        Run("find_attribute_usages System.ObsoleteAttribute", () => FindAttributeUsagesLogic.Execute(loaded, resolver, metadata, "System.ObsoleteAttribute"));
        Run("find_attribute_usages Obsolete", () => FindAttributeUsagesLogic.Execute(loaded, resolver, metadata, "Obsolete"));
        Run("find_implementations System.IDisposable", () => FindImplementationsLogic.Execute(loaded, resolver, metadata, "System.IDisposable"));
        Run("get_type_hierarchy System.IDisposable", () => GetTypeHierarchyLogic.Execute(resolver, metadata, "System.IDisposable"));
        Run("find_references System.IDisposable", () => FindReferencesLogic.Execute(loaded, resolver, metadata, "System.IDisposable"));
        Run("find_references System.AppDomain", () => FindReferencesLogic.Execute(loaded, resolver, metadata, "System.AppDomain"));
        Run("find_references System.AppDomain.CurrentDomain", () => FindReferencesLogic.Execute(loaded, resolver, metadata, "System.AppDomain.CurrentDomain"));
        Run("find_references System.Console.WriteLine", () => FindReferencesLogic.Execute(loaded, resolver, metadata, "System.Console.WriteLine"));
        Run("find_callers System.Console.WriteLine", () => FindCallersLogic.Execute(loaded, resolver, metadata, "System.Console.WriteLine"));
        Run("find_callers System.String.Split", () => FindCallersLogic.Execute(loaded, resolver, metadata, "System.String.Split"));
        Run("get_call_graph System.Console.WriteLine callers", () => GetCallGraphLogic.ExecuteAsync(loaded, resolver, metadata, "System.Console.WriteLine", "callers", 1, 100).GetAwaiter().GetResult());
        Run("find_event_subscribers System.AppDomain.ProcessExit", () => FindEventSubscribersLogic.Execute(loaded, resolver, metadata, "System.AppDomain.ProcessExit"));
        Run("find_throw_sites System.InvalidOperationException", () => FindThrowSitesLogic.Execute(loaded, resolver, metadata, "System.InvalidOperationException", false));
        Run("find_catch_blocks System.InvalidOperationException", () => FindCatchBlocksLogic.Execute(loaded, resolver, metadata, "System.InvalidOperationException", false));
        Run("get_extension_methods System.String", () => GetExtensionMethodsLogic.Execute(loaded, resolver, metadata, "System.String", null));
        Run("analyze_change_impact System.IDisposable", () => AnalyzeChangeImpactLogic.Execute(loaded, resolver, metadata, "System.IDisposable"));
        Run("analyze_change_impact System.Console.WriteLine", () => AnalyzeChangeImpactLogic.Execute(loaded, resolver, metadata, "System.Console.WriteLine"));

        lines.Add("");
        lines.Add("Matrix: source compilation of System.IDisposable -> projects found (Roslyn SymbolFinder | NamedTypeSignatureComparer on implementers)");
        foreach (var (id, c) in loaded.Compilations)
        {
            var name = loaded.Solution.GetProject(id)!.Name;
            var refs = c.ReferencedAssemblyNames.Select(a => a.Name).Where(n => n is "mscorlib" or "netstandard" or "System.Runtime").Order();
            var type = c.GetTypeByMetadataName("System.IDisposable")!;
            var found = SymbolFinder.FindReferencesAsync(type, loaded.Solution).GetAwaiter().GetResult()
                .SelectMany(r => r.Locations).Select(l => l.Document.Project.Name).Distinct().Order();
            var cmp = loaded.Compilations.Where(o => o.Value.GetTypeByMetadataName("System.IDisposable") is { } t2 && NamedTypeSignatureComparer.Instance.Equals(type, t2))
                .Select(o => loaded.Solution.GetProject(o.Key)!.Name).Order();
            lines.Add($"  from {name,-10} (asm {type.ContainingAssembly.Name,-14} refs {string.Join(",", refs)}): roslyn=[{string.Join(",", found)}] comparer=[{string.Join(",", cmp)}]");
        }

        File.WriteAllLines(Path.Combine(Path.GetTempPath(), "rclens-measure.txt"), lines);
    }
}