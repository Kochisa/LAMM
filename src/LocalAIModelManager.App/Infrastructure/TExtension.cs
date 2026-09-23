using System.ComponentModel;
using System.Windows;
using System.Windows.Data;
using System.Windows.Markup;
using LocalAIModelManager.Core.Localization;

namespace LocalAIModelManager.App.Infrastructure;

/// <summary>
/// The binding source behind <see cref="TExtension"/>: an indexer that resolves a key
/// through <see cref="Localizer"/> on every read, plus one notification that invalidates
/// every XAML binding built on it when the language changes.
/// </summary>
public sealed class LocalizedStrings : INotifyPropertyChanged
{
    public static LocalizedStrings Instance { get; } = new();

    private LocalizedStrings()
    {
        Localizer.LanguageChanged += _ =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(ItemPropertyName));
    }

    /// <summary>The name WPF uses for "every indexer binding" on a string indexer.</summary>
    private const string ItemPropertyName = "Item[]";

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Localized text for <paramref name="key"/>; unknown keys degrade to the key.</summary>
    public string this[string key] => Localizer.T(key);
}

/// <summary>
/// XAML markup extension for localized text: <c>Text="{loc:T nav.page.models}"</c>.
///
/// When the target is a dependency property (the usual case) it returns a one-way
/// binding against <see cref="LocalizedStrings"/>, so the text follows a runtime
/// language change without rebuilding the element. The main window's own chrome is not
/// rebuilt when the language changes, which is exactly why this is a binding.
///
/// Targets that are not dependency properties - a <c>Setter.Value</c> inside a style or
/// template, for instance - cannot host a binding, so there the key is simply resolved
/// eagerly at parse time. Those are re-evaluated whenever the element is created, which
/// covers every style applied by a rebuilt page.
/// </summary>
[MarkupExtensionReturnType(typeof(string))]
public sealed class TExtension : MarkupExtension
{
    public TExtension()
    {
    }

    public TExtension(string key)
    {
        Key = key;
    }

    [ConstructorArgument("key")]
    public string Key { get; set; } = string.Empty;

    public override object ProvideValue(IServiceProvider serviceProvider)
    {
        if (string.IsNullOrEmpty(Key))
        {
            return string.Empty;
        }

        var target = serviceProvider?.GetService(typeof(IProvideValueTarget)) as IProvideValueTarget;
        if (target?.TargetProperty is DependencyProperty)
        {
            var binding = new Binding($"[{Key}]")
            {
                Source = LocalizedStrings.Instance,
                Mode = BindingMode.OneWay,
            };

            return binding.ProvideValue(serviceProvider!);
        }

        return Localizer.T(Key);
    }
}
