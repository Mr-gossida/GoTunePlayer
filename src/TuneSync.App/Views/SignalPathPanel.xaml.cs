using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using TuneSync.App.ViewModels;

namespace TuneSync.App.Views;

/// <summary>The 소리 경로 panel's look (SignalPathViewModel holds what it says).</summary>
public partial class SignalPathPanel : UserControl
{
    public SignalPathPanel()
    {
        InitializeComponent();
        DataContextChanged += (_, e) =>
        {
            if (e.OldValue is SignalPathViewModel old) old.PropertyChanged -= ViewModel_PropertyChanged;
            if (e.NewValue is SignalPathViewModel now) now.PropertyChanged += ViewModel_PropertyChanged;
            ShowMark();
        };
    }

    /// <summary>The ✕ asks whoever shows the panel to close it.</summary>
    public event EventHandler? CloseRequested;

    private void Close_Click(object sender, RoutedEventArgs e) => CloseRequested?.Invoke(this, EventArgs.Empty);

    private void ViewModel_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SignalPathViewModel.VerdictPure)) ShowMark();
    }

    /// <summary>The verdict's mark: green for 원음 그대로, the accent color for changes.</summary>
    private void ShowMark()
    {
        var key = DataContext is SignalPathViewModel { VerdictPure: true } ? "SystemFillColorSuccessBrush" : PlayerPalette.AccentKey;
        MarkBack.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, key);
        MarkText.SetResourceReference(TextBlock.ForegroundProperty, key);
    }
}
