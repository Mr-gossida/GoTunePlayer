using Wpf.Ui.Abstractions;

namespace TuneSync.App;

/// <summary>Creates pages by reflection. NavigationView.Navigate(Type) silently does nothing
/// (no exception, no content) unless a page provider is registered - this app has no DI container,
/// so a plain Activator.CreateInstance stands in for one.</summary>
public sealed class SimplePageProvider : INavigationViewPageProvider
{
    public object? GetPage(Type pageType) => Activator.CreateInstance(pageType);
}
