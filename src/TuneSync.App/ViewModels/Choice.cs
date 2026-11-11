namespace TuneSync.App.ViewModels;

/// <summary>One ComboBox option. A record (real properties) rather than a value tuple: tuple
/// element names don't exist at runtime and WPF can't bind to tuple fields, which left the
/// dropdowns blank.</summary>
public sealed record Choice<T>(T Value, string Label);
