using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Effects;

namespace TuneSync.App.Effects;

/// <summary>Flowing aurora-lamp light (Aurora.hlsl), drawn on the GPU. Put it on any filled
/// element; the element's own pixels are ignored. Animate <see cref="Time"/> to make it move.</summary>
public sealed class AuroraEffect : ShaderEffect
{
    private static readonly PixelShader Shader = new()
    {
        UriSource = new Uri("pack://application:,,,/Effects/Aurora.ps", UriKind.Absolute),
    };

    public static readonly DependencyProperty InputProperty =
        RegisterPixelShaderSamplerProperty(nameof(Input), typeof(AuroraEffect), 0);

    public static readonly DependencyProperty TimeProperty = DependencyProperty.Register(
        nameof(Time), typeof(double), typeof(AuroraEffect), new UIPropertyMetadata(0.0, PixelShaderConstantCallback(0)));

    public static readonly DependencyProperty Color1Property = DependencyProperty.Register(
        nameof(Color1), typeof(Color), typeof(AuroraEffect), new UIPropertyMetadata(Color.FromRgb(0xB0, 0x60, 0xFF), PixelShaderConstantCallback(1)));

    public static readonly DependencyProperty Color2Property = DependencyProperty.Register(
        nameof(Color2), typeof(Color), typeof(AuroraEffect), new UIPropertyMetadata(Color.FromRgb(0xFF, 0x80, 0x40), PixelShaderConstantCallback(2)));

    public static readonly DependencyProperty AspectProperty = DependencyProperty.Register(
        nameof(Aspect), typeof(double), typeof(AuroraEffect), new UIPropertyMetadata(16 / 9.0, PixelShaderConstantCallback(3)));

    public static readonly DependencyProperty IntensityProperty = DependencyProperty.Register(
        nameof(Intensity), typeof(double), typeof(AuroraEffect), new UIPropertyMetadata(0.7, PixelShaderConstantCallback(4)));

    public static readonly DependencyProperty OffsetProperty = DependencyProperty.Register(
        nameof(Offset), typeof(Point), typeof(AuroraEffect), new UIPropertyMetadata(new Point(), PixelShaderConstantCallback(5)));

    public static readonly DependencyProperty SpanProperty = DependencyProperty.Register(
        nameof(Span), typeof(Size), typeof(AuroraEffect), new UIPropertyMetadata(new Size(), PixelShaderConstantCallback(6)));

    /// <summary>With several monitors: where this screen's top-left sits in the shared picture,
    /// in units of the tallest screen's height.</summary>
    public Point Offset
    {
        get => (Point)GetValue(OffsetProperty);
        set => SetValue(OffsetProperty, value);
    }

    /// <summary>With several monitors: this screen's size in the shared picture (same units).
    /// Empty (the default) for a single screen, which then uses <see cref="Aspect"/>.</summary>
    public Size Span
    {
        get => (Size)GetValue(SpanProperty);
        set => SetValue(SpanProperty, value);
    }

    public AuroraEffect()
    {
        PixelShader = Shader;
        UpdateShaderValue(InputProperty);
        UpdateShaderValue(TimeProperty);
        UpdateShaderValue(Color1Property);
        UpdateShaderValue(Color2Property);
        UpdateShaderValue(AspectProperty);
        UpdateShaderValue(IntensityProperty);
        UpdateShaderValue(OffsetProperty);
        UpdateShaderValue(SpanProperty);
    }

    public Brush Input
    {
        get => (Brush)GetValue(InputProperty);
        set => SetValue(InputProperty, value);
    }

    /// <summary>Seconds; the light drifts as this grows.</summary>
    public double Time
    {
        get => (double)GetValue(TimeProperty);
        set => SetValue(TimeProperty, value);
    }

    public Color Color1
    {
        get => (Color)GetValue(Color1Property);
        set => SetValue(Color1Property, value);
    }

    public Color Color2
    {
        get => (Color)GetValue(Color2Property);
        set => SetValue(Color2Property, value);
    }

    /// <summary>The element's width / height, so the waves aren't stretched.</summary>
    public double Aspect
    {
        get => (double)GetValue(AspectProperty);
        set => SetValue(AspectProperty, value);
    }

    public double Intensity
    {
        get => (double)GetValue(IntensityProperty);
        set => SetValue(IntensityProperty, value);
    }
}
