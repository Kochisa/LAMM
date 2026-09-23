using System.Windows;
using System.Windows.Markup;
using LocalAIModelManager.Core.Localization;

namespace LocalAIModelManager.App.Infrastructure;

/// <summary>
/// Remembers every element whose text came from a <c>{loc:T}</c> attribute, so switching
/// the language can re-apply it.
///
/// This deliberately does NOT use a binding. An earlier version returned a binding on a
/// "LocalizedStrings" indexer, and that had an unacceptable failure mode: when the binding
/// did not evaluate - for whatever reason - <c>Content</c> simply stayed null and the
/// control rendered as an empty box. Users saw buttons with no label, intermittently and
/// unreproducibly. Assigning the string at parse time cannot fail that way: the text is
/// there from the start, and any problem surfaces as a loud parse error instead of a
/// silently blank control.
/// </summary>
internal static class LocalizedTextRegistry
{
    private sealed class Entry
    {
        public Entry(DependencyObject target, DependencyProperty property, string key)
        {
            Target = new WeakReference<DependencyObject>(target);
            Property = property;
            Key = key;
        }

        public WeakReference<DependencyObject> Target { get; }

        public DependencyProperty Property { get; }

        public string Key { get; }
    }

    private static readonly List<Entry> Entries = new();
    private static readonly object Gate = new();
    private static bool _subscribed;

    public static void Register(DependencyObject target, DependencyProperty property, string key)
    {
        lock (Gate)
        {
            Entries.Add(new Entry(target, property, key));

            if (!_subscribed)
            {
                _subscribed = true;
                Localizer.LanguageChanged += _ => ReapplyAll();
            }
        }
    }

    private static void ReapplyAll()
    {
        // Posted rather than run inline: the language is switched from inside the settings
        // save, and re-texting the tree while that command is still on the stack would be
        // re-entrant (ShellViewModel defers its page rebuild the same way).
        UiDispatcher.Post(() =>
        {
            List<Entry> alive;
            lock (Gate)
            {
                Entries.RemoveAll(entry => !entry.Target.TryGetTarget(out _));
                alive = new List<Entry>(Entries);
            }

            foreach (var entry in alive)
            {
                if (entry.Target.TryGetTarget(out var target))
                {
                    target.SetValue(entry.Property, Localizer.T(entry.Key));
                }
            }
        });
    }
}

/// <summary>
/// XAML markup extension for localized text: <c>Text="{loc:T nav.page.models}"</c>.
///
/// The text is resolved synchronously, at parse time, and the element is registered so the
/// same text can be re-applied when the language changes. That combination is what the UI
/// needs: the text is never missing, and it still follows a runtime language switch -
/// including in the main window, which is not rebuilt when the language changes.
///
/// Targets that are not dependency properties - a <c>Setter.Value</c> inside a style or
/// template, for instance - cannot be re-applied in place, so there the key is resolved
/// once and the page rebuild handles re-translation.
/// </summary>
[MarkupExtensionReturnType(typeof(object))]
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

        var text = Localizer.T(Key);

        // TargetObject is a DependencyObject for a real element; inside a style or template
        // setter it is the Setter itself, which is not one - and then there is nothing to
        // re-apply later, so the key is simply resolved once.
        var target = serviceProvider?.GetService(typeof(IProvideValueTarget)) as IProvideValueTarget;
        if (target?.TargetObject is DependencyObject owner &&
            target.TargetProperty is DependencyProperty property)
        {
            LocalizedTextRegistry.Register(owner, property, Key);
        }

        return text;
    }
}
