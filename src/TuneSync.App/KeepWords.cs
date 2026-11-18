using System.ComponentModel;
using System.Text;
using System.Windows;
using System.Runtime.CompilerServices;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;

namespace TuneSync.App;

/// <summary>Korean text that wraps between words (어절), not in the middle of one. WPF may break
/// Hangul between any two syllables ("저장됩" / "니다"); with KeepWords on, a WORD JOINER
/// (U+2060, invisible) goes between the letters of each word, so lines break only at spaces -
/// what CSS calls word-break: keep-all. Works for plain Text and for Runs, bound ones included.</summary>
public static partial class KeepWords
{
    private const char Joiner = '⁠';

    public static readonly DependencyProperty IsEnabledProperty = DependencyProperty.RegisterAttached(
        "IsEnabled", typeof(bool), typeof(KeepWords), new PropertyMetadata(false, OnIsEnabledChanged));

    public static bool GetIsEnabled(DependencyObject d) => (bool)d.GetValue(IsEnabledProperty);
    public static void SetIsEnabled(DependencyObject d, bool value) => d.SetValue(IsEnabledProperty, value);

    private static readonly DependencyPropertyDescriptor TextBlockText =
        DependencyPropertyDescriptor.FromProperty(TextBlock.TextProperty, typeof(TextBlock));
    private static readonly DependencyPropertyDescriptor RunText =
        DependencyPropertyDescriptor.FromProperty(Run.TextProperty, typeof(Run));

    private static void OnIsEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not TextBlock block || e.NewValue is not true) return;
        block.Loaded += (_, _) => Apply(block);
        if (block.IsLoaded) Apply(block);
    }

    private static readonly ConditionalWeakTable<object, object> Watched = new();

    /// <summary>Joins the words now and again whenever the text (or a bound Run) changes.</summary>
    private static void Apply(TextBlock block)
    {
        // Text set or bound on the block itself: join that. Otherwise it is made of Runs.
        if (block.ReadLocalValue(TextBlock.TextProperty) != DependencyProperty.UnsetValue
            || BindingOperations.IsDataBound(block, TextBlock.TextProperty))
        {
            Join(block, TextBlock.TextProperty, block.Text);
            if (Watched.TryAdd(block, block))
                TextBlockText.AddValueChanged(block, (_, _) => Join(block, TextBlock.TextProperty, block.Text));
            return;
        }
        // A copy: changing a Run's text edits the block's content, which ends a live enumeration
        // of its Inlines ("컬렉션이 수정되었습니다" when a page with several Runs, like 스트리밍 캐시, opened).
        foreach (var run in block.Inlines.OfType<Run>().ToList())
        {
            Join(run, Run.TextProperty, run.Text);
            if (Watched.TryAdd(run, run))
                RunText.AddValueChanged(run, (_, _) => Join(run, Run.TextProperty, run.Text));
        }
    }

    /// <summary>Sets the joined text without dropping a binding: SetCurrentValue keeps it, and
    /// the next value from the source comes back through the change handler.</summary>
    private static void Join(DependencyObject target, DependencyProperty property, string? text)
    {
        if (string.IsNullOrEmpty(text)) return;
        var joined = Joined(text);
        if (joined != text) target.SetCurrentValue(property, joined);
    }

    /// <summary>"저장됩니다" -> "저⁠장⁠됩⁠니⁠다": a joiner between two letters of the same word
    /// when at least one of them is Hangul (Latin words already keep together). A phrase in
    /// parentheses keeps together too (its spaces become no-break spaces), so a line breaks
    /// before "(" rather than leaving "적용)" alone on the next line.</summary>
    public static string Joined(string text)
    {
        text = KeepParenthesesTogether(text);
        text = NumberAndUnit().Replace(text, "$1\u00A0$2"); // "0.96 GB": a number keeps its unit
        var sb = new StringBuilder(text.Length * 2);
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            sb.Append(c);
            if (i + 1 >= text.Length || c == Joiner) continue;
            var next = text[i + 1];
            if (next == Joiner || char.IsWhiteSpace(c) || char.IsWhiteSpace(next)) continue;
            if (IsHangul(c) || IsHangul(next)) sb.Append(Joiner);
        }
        return sb.ToString();
    }

    /// <summary>Spaces inside each balanced "( … )" become no-break spaces. An unbalanced "(" is
    /// left alone (it would otherwise glue the rest of the text into one unbreakable run).</summary>
    private static string KeepParenthesesTogether(string text)
    {
        if (!text.Contains('(')) return text;
        var chars = text.ToCharArray();
        for (var open = text.IndexOf('('); open >= 0; open = text.IndexOf('(', open + 1))
        {
            var close = text.IndexOf(')', open + 1);
            if (close < 0) break;
            for (var i = open + 1; i < close; i++)
                if (chars[i] == ' ') chars[i] = '\u00A0';
            open = close;
        }
        return new string(chars);
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"(\d) (GB|MB|KB|TB|kHz|Hz|kbps|ms|%|곡|개|줄|초|분|비트|채널)")]
    private static partial System.Text.RegularExpressions.Regex NumberAndUnit();

    private static bool IsHangul(char c) => c is >= '가' and <= '힣' or >= 'ᄀ' and <= 'ᇿ' or >= '㄰' and <= '㆏';
}
