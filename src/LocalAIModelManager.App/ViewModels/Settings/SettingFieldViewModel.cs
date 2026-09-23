using System.Windows.Input;
using LocalAIModelManager.App.Infrastructure;
using LocalAIModelManager.App.Services;
using LocalAIModelManager.Core.Configuration;

namespace LocalAIModelManager.App.ViewModels.Settings;

/// <summary>Editable view model for a single <see cref="SettingField"/>.</summary>
public sealed class SettingFieldViewModel : ObservableObject
{
    private const string Mask = "••••••••";

    private readonly SettingField _field;
    private readonly AppServices _services;
    private string _value = string.Empty;
    private bool _revealed;
    private string? _validationError;

    public SettingFieldViewModel(SettingField field, AppServices services)
    {
        _field = field;
        _services = services;

        CopyCommand = new RelayCommand(
            _ => Infrastructure.ClipboardHelper.SetText(_field.Kind == SettingFieldKind.Secret && !_revealed ? _value : _value),
            _ => !string.IsNullOrEmpty(_value));

        ToggleRevealCommand = new RelayCommand(_ => Revealed = !Revealed);
        GenerateKeyCommand = new RelayCommand(_ => Value = ApiKeyGenerator.Create());
        BrowseCommand = new RelayCommand(_ => Browse());
    }

    public SettingField Field => _field;

    public string Key => _field.Key;

    public string Label => _field.Label;

    public string? Help => _field.Help;

    public string? Suffix => _field.Suffix;

    public SettingFieldKind Kind => _field.Kind;

    public bool IsBool => _field.Kind == SettingFieldKind.Bool;

    public bool IsChoice => _field.Kind == SettingFieldKind.Choice;

    public bool IsSecret => _field.Kind == SettingFieldKind.Secret;

    public bool IsReadOnly => _field.Kind is SettingFieldKind.ReadOnly;

    public bool IsEditable => !IsReadOnly && !_field.Enforced;

    public bool IsPath => _field.Kind is SettingFieldKind.Folder or SettingFieldKind.File;

    /// <summary>Plain editable text/number input.</summary>
    public bool IsPlainInput => !IsBool && !IsChoice && !IsSecret && !IsReadOnly;

    public bool ShowBrowse => IsPath && IsEditable;

    public bool ShowGenerate => IsSecret && IsEditable;

    public bool ShowCopy => IsSecret || IsReadOnly;

    public bool ShowSecretEditor => IsSecret && IsEditable && Revealed;

    public bool ShowSecretMasked => IsSecret && IsEditable && !Revealed;

    public bool SecurityRelevant => _field.SecurityRelevant;

    public IReadOnlyList<string> Choices => _field.Choices;

    public ICommand CopyCommand { get; }
    public ICommand ToggleRevealCommand { get; }

    public ICommand GenerateKeyCommand { get; }

    public ICommand BrowseCommand { get; }

    public string Value
    {
        get => _value;
        set
        {
            if (!SetProperty(ref _value, value))
            {
                return;
            }

            OnPropertyChanged(nameof(DisplayValue));
            OnPropertyChanged(nameof(BoolValue));
            OnPropertyChanged(nameof(SelectionValue));
            Validate();
        }
    }

    /// <summary>Value shown to the user; secrets stay masked until revealed.</summary>
    public string DisplayValue => IsSecret && !_revealed ? Mask : _value;

    public bool Revealed
    {
        get => _revealed;
        set
        {
            if (SetProperty(ref _revealed, value))
            {
                OnPropertyChanged(nameof(DisplayValue));
                OnPropertyChanged(nameof(ShowSecretEditor));
                OnPropertyChanged(nameof(ShowSecretMasked));
            }
        }
    }

    public bool BoolValue
    {
        get => string.Equals(_value, "true", StringComparison.OrdinalIgnoreCase);
        set => Value = value ? "true" : "false";
    }

    /// <summary>
    /// The selected option. For a choice field the option list and the stored value are the
    /// same strings - which is exactly why the language picker does NOT translate its
    /// options: its choices are the native names (简体中文, English, 日本語, Français) and
    /// the field's Write callback maps the chosen name back to a language code.
    /// </summary>
    public string? SelectionValue
    {
        get => _value;
        set
        {
            if (!string.IsNullOrEmpty(value))
            {
                Value = value;
            }
        }
    }

    public string? ValidationError
    {
        get => _validationError;
        private set
        {
            if (SetProperty(ref _validationError, value))
            {
                OnPropertyChanged(nameof(HasValidationError));
            }
        }
    }

    public bool HasValidationError => !string.IsNullOrEmpty(_validationError);

    public void LoadFrom(AppSettings settings) => Value = _field.Read(settings);

    /// <summary>Writes the current value back into the settings object.</summary>
    public bool ApplyTo(AppSettings settings)
    {
        Validate();
        if (HasValidationError || _field.Enforced || _field.Write is null)
        {
            return false;
        }

        _field.Write(settings, _value);
        return true;
    }

    private void Validate()
    {
        if (_field.Kind == SettingFieldKind.Number)
        {
            if (!long.TryParse(_value, out var parsed))
            {
                ValidationError = Loc.T("settings.validation.integer");
                return;
            }

            if (_field.Min is { } min && parsed < min)
            {
                ValidationError = Loc.T("settings.validation.min", min);
                return;
            }

            if (_field.Max is { } max && parsed > max)
            {
                ValidationError = Loc.T("settings.validation.max", max);
                return;
            }
        }

        if (_field.Kind == SettingFieldKind.Secret && _services.Current.Api.ApiKeyEnabled && string.IsNullOrWhiteSpace(_value))
        {
            ValidationError = Loc.T("settings.validation.apiKeyRequired");
            return;
        }

        ValidationError = null;
    }

    private void Browse()
    {
        if (_field.Kind == SettingFieldKind.Folder)
        {
            using var dialog = new System.Windows.Forms.FolderBrowserDialog
            {
                Description = _field.Label,
                UseDescriptionForTitle = true,
                ShowNewFolderButton = true,
            };

            if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
            {
                Value = dialog.SelectedPath;
            }

            return;
        }

        var fileDialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = _field.Label,
            Filter = Loc.T("settings.fileFilter.executables"),
            CheckFileExists = true,
        };

        if (fileDialog.ShowDialog() == true)
        {
            Value = fileDialog.FileName;
        }
    }
}
