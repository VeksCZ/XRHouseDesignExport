using System; using System.IO; using System.Linq;
using Microsoft.CodeAnalysis; using Microsoft.CodeAnalysis.CSharp;
class P { static int Main(string[] a) {
  int bad = 0, n = 0;
  var defs = new[] { "UNITY_EDITOR", "UNITY_ANDROID", "META_XR_SDK_INSTALLED" };
  var opts = new CSharpParseOptions(LanguageVersion.CSharp9, DocumentationMode.None, SourceCodeKind.Regular, defs);
  foreach (var f in Directory.EnumerateFiles(a[0], "*.cs", SearchOption.AllDirectories)) {
    n++;
    var tree = CSharpSyntaxTree.ParseText(File.ReadAllText(f), opts, f);
    foreach (var d in tree.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error)) { bad++; Console.WriteLine(d.ToString()); }
  }
  Console.WriteLine($"{n} files, {bad} syntax errors"); return bad == 0 ? 0 : 1; } }
