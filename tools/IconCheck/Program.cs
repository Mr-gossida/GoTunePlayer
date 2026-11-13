// Lists SymbolRegular icon names used in the app (XAML/C#) that do not exist in WPF-UI 4.3.
// Usage: dotnet run --project tools/IconCheck -- src/TuneSync.App
using System.IO;
using System.Text.RegularExpressions;

var names = new HashSet<string>(Enum.GetNames(typeof(Wpf.Ui.Controls.SymbolRegular)));
var root = args[0];
var used = new SortedSet<string>();
var rx = new Regex(@"(?:SymbolIcon\s+|Symbol=""|SymbolRegular\.|, ""|Icon\s*=\s*"")([A-Z][A-Za-z0-9]+(?:16|20|24|28|32|48))\b");
foreach (var f in Directory.EnumerateFiles(root, "*.*", SearchOption.AllDirectories))
{
    if (!(f.EndsWith(".xaml") || f.EndsWith(".cs"))) continue;
    var parts = f.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    if (parts.Contains("obj") || parts.Contains("bin")) continue;
    foreach (Match m in rx.Matches(File.ReadAllText(f))) used.Add(m.Groups[1].Value);
}
Console.WriteLine($"checked {used.Count}");
foreach (var u in used.Where(u => !names.Contains(u)))
{
    var stem = Regex.Replace(u, @"\d+$", "");
    var key = stem.Length > 5 ? stem[..5] : stem;
    Console.WriteLine($"MISSING {u} -> " + string.Join(", ", names.Where(n => n.StartsWith(key) && (n.EndsWith("24") || n.EndsWith("20"))).Take(14)));
}
