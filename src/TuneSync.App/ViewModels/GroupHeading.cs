using Wpf.Ui.Controls;

namespace TuneSync.App.ViewModels;

/// <summary>A heading in the queue: a group (artist, album) or a folder level of the folder tree.
/// <see cref="Key"/> is what folding goes by; <see cref="Depth"/> sets it in under its parent.</summary>
public sealed record GroupHeading(string Key, string Name, int Depth, int Count, bool IsFolded, SymbolRegular Icon)
{
    /// <summary>Set in by its depth (16 px a level), with more room above a top-level heading.</summary>
    public System.Windows.Thickness Indent => new(6 + 16 * Depth, Depth == 0 ? 12 : 4, 18, 3);

    /// <summary>A top-level heading reads in the accent color; the levels under it in the text color.</summary>
    public bool IsTop => Depth == 0;
}
