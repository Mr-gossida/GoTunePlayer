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

    /// <summary>Clicks go through it to the window behind (it can't be moved or used meanwhile;
    /// turned off again from 설정 or the tray menu).</summary>
    public bool ClickThrough { get; set; }

    /// <summary>With <see cref="ClickThrough"/>: the playback buttons still take clicks; the rest
    /// of the card lets them through.</summary>
    public bool ClickThroughButtons { get; set; }

    /// <summary>클리어 모드: no card behind the HUD, only its text, cover, spectrum and buttons,
    /// fully solid and outlined so they read over any window (the opacity setting is set aside).</summary>
    public bool Clear { get; set; }

    /// <summary>클리어 모드: what sets the words and buttons off from whatever is behind them.</summary>
    public HudClearEffect ClearEffect { get; set; } = HudClearEffect.Shadow;

    /// <summary>How the card is laid out (설정 › HUD 레이아웃).</summary>
    public HudLayout Layout { get; set; } = HudLayout.Card;

    public const double MinOpacity = 0.2;

    public void Normalize()
    {
        Opacity = double.IsFinite(Opacity) ? Math.Clamp(Opacity, MinOpacity, 1) : 0.85;
        if (Left is { } l && !double.IsFinite(l)) Left = null;
        if (Top is { } t && !double.IsFinite(t)) Top = null;
        if (!Enum.IsDefined(Layout)) Layout = HudLayout.Card;
        if (!Enum.IsDefined(ClearEffect)) ClearEffect = HudClearEffect.Shadow;
    }
}

/// <summary>클리어 모드's 효과: what hugs the edges of the letters and shapes (never a plate
/// behind them).</summary>
public enum HudClearEffect
{
    /// <summary>A soft, tight shadow just under each shape.</summary>
    Shadow,
    /// <summary>A faint glow in the cover's accent color, like a neon sign.</summary>
    Neon,
    /// <summary>A crisp dark outline (two tight layers): the most contrast.</summary>
    Outline,
}

public enum HudLayout
{
    /// <summary>Song, two lyric lines, spectrum, buttons and times (420 wide).</summary>
    Card,
    /// <summary>One thin strip: cover, title, the line being sung, buttons.</summary>
    Compact,
    /// <summary>The two lyric lines large and centered; the song small underneath.</summary>
    Lyrics,
    /// <summary>A narrow upright card around a large cover.</summary>
    Vertical,
}
