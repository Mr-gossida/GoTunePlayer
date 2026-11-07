using System.IO;
using TuneSync.Core.Models;
using TuneSync.Core.Services;

namespace TuneSync.App;

/// <summary>Where the user-editable naming rules live on disk. Centralized here so every view
/// that touches rename rules reads/writes the exact same file.</summary>
public static class AppSettings
{
    public static readonly string RulesPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "TuneSync", "rules.json");

    public static RenameRuleSet LoadRenameRules() => RenameService.Load(RulesPath);

    public static void SaveRenameRules(RenameRuleSet rules) => RenameService.Save(rules, RulesPath);
}
