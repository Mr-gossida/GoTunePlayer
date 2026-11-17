using System.Text.Json;
using TuneSync.Core.Models;

namespace TuneSync.Core.Tests;

public class HudSettingsTests
{
    [Fact]
    public void Defaults_CardLayout_ClicksNotPassedThrough()
    {
        var hud = new HudSettings();
        Assert.Equal(HudLayout.Card, hud.Layout);
        Assert.False(hud.ClickThrough);
    }

    [Fact]
    public void Normalize_UnknownLayout_FallsBackToCard()
    {
        var hud = new HudSettings { Layout = (HudLayout)42, Opacity = double.NaN };
        hud.Normalize();
        Assert.Equal(HudLayout.Card, hud.Layout);
        Assert.Equal(0.85, hud.Opacity);
    }

    [Fact]
    public void LayoutAndClickThrough_SurviveARoundTrip()
    {
        var json = JsonSerializer.Serialize(new HudSettings { Layout = HudLayout.Vertical, ClickThrough = true, Left = 12, Top = 34 });
        var back = JsonSerializer.Deserialize<HudSettings>(json)!;
        Assert.Equal(HudLayout.Vertical, back.Layout);
        Assert.True(back.ClickThrough);
        Assert.Equal(12, back.Left);
        Assert.Equal(34, back.Top);
    }
}
