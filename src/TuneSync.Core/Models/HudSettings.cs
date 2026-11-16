namespace TuneSync.Core.Models;

/// <summary>The HUD: a small card with the song, its lyrics, a spectrum and the playback buttons
/// that stays above every other program.</summary>
public sealed class HudSettings
{
    /// <summary>Shown (and shown again at the next start).</summary>
    public bool Enabled { get; set; }

    /// <summary>How solid the card is, 0.2..1 (it turns solid while the mouse is over it).</summary>
    public double Opacity { get; set; } = 0.85;

    /// <summary>Where it was left (null: the top right of the screen).</summary>
    public double? Left { get; set; }
    public double? Top { get; set; }

    public const double MinOpacity = 0.2;

    public void Normalize()
    {
        Opacity = double.IsFinite(Opacity) ? Math.Clamp(Opacity, MinOpacity, 1) : 0.85;
        if (Left is { } l && !double.IsFinite(l)) Left = null;
        if (Top is { } t && !double.IsFinite(t)) Top = null;
    }
}
