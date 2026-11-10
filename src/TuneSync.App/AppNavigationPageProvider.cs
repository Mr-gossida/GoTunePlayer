using Wpf.Ui.Abstractions;

namespace TuneSync.App;

/// <summary>Resolves navigation pages through the DI container instead of
/// <c>Activator.CreateInstance</c>, so pages can take ViewModels/services as constructor
/// dependencies.</summary>
public sealed class AppNavigationPageProvider(IServiceProvider services) : INavigationViewPageProvider
{
    public object? GetPage(Type pageType) => services.GetService(pageType);
}
