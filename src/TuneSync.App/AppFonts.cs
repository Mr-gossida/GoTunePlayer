using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using TuneSync.Core.Models;

namespace TuneSync.App;

/// <summary>설정 › 일반 › 앱 글꼴 and 설정 › 재생 › 가사 글꼴. Both are application resources
/// (<see cref="AppKey"/>, <see cref="LyricsKey"/>), so a change shows everywhere at once.
/// WPF-UI sets no font of its own - text inherits it from its window - so every window, context
/// menu and tooltip (each its own tree) takes the app font through a resource reference.</summary>
public static class AppFonts
{
    public const string AppKey = "AppFontFamily";
    public const string LyricsKey = "LyricsFontFamily";

    private static bool _installed;

    /// <summary>Applies the saved fonts and makes every window, menu and tooltip follow the app
    /// font from now on. Call once, before the first window is shown.</summary>
    public static void Install(AppSettings settings)
    {
        Apply(settings);
        if (_installed) return;
        _installed = true;
        void Follow(object sender, RoutedEventArgs e) =>
            ((FrameworkElement)sender).SetResourceReference(TextElement.FontFamilyProperty, AppKey);
        EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent, new RoutedEventHandler(Follow));
        EventManager.RegisterClassHandler(typeof(ContextMenu), ContextMenu.OpenedEvent, new RoutedEventHandler(Follow));
        EventManager.RegisterClassHandler(typeof(ToolTip), ToolTip.OpenedEvent, new RoutedEventHandler(Follow));
    }

    /// <summary>Puts the chosen fonts in the resources. An empty name is the default: Windows'
    /// UI font for the app, the app font for the lyrics.</summary>
    public static void Apply(AppSettings settings)
    {
        var resources = Application.Current.Resources;
        var app = Family(settings.AppFontFamily) ?? SystemFonts.MessageFontFamily;
        resources[AppKey] = app;
        resources[LyricsKey] = Family(settings.LyricsFontFamily) ?? app;
    }

    private static FontFamily? Family(string? name) =>
        string.IsNullOrWhiteSpace(name) ? null : new FontFamily(name);

    /// <summary>A font to pick: its name as Windows shows it in Korean when it has one (맑은 고딕),
    /// and the family itself, so the list can show each name in its own font.</summary>
    public sealed record Option(string Value, string Label, FontFamily Family);

    private static IReadOnlyList<Option>? _installedFonts;

    /// <summary>The fonts installed on this PC, by name (read once; a few hundred).</summary>
    public static IReadOnlyList<Option> InstalledFonts()
    {
        if (_installedFonts is not null) return _installedFonts;
        var korean = System.Windows.Markup.XmlLanguage.GetLanguage("ko-kr");
        var english = System.Windows.Markup.XmlLanguage.GetLanguage("en-us");
        var list = new List<Option>();
        foreach (var family in Fonts.SystemFontFamilies)
        {
            var label = family.FamilyNames.TryGetValue(korean, out var ko) ? ko
                : family.FamilyNames.TryGetValue(english, out var en) ? en
                : family.Source;
            list.Add(new Option(family.Source, label, family));
        }
        var compare = StringComparer.Create(CultureInfo.GetCultureInfo("ko-KR"), ignoreCase: true);
        list.Sort((a, b) => compare.Compare(a.Label, b.Label));
        return _installedFonts = list.DistinctBy(o => o.Value, StringComparer.OrdinalIgnoreCase).ToList();
    }
}
