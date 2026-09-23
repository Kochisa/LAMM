using System.Collections.ObjectModel;
using System.Windows.Input;
using LocalAIModelManager.App.Infrastructure;
using LocalAIModelManager.App.Services;
using LocalAIModelManager.Core.Logging;

namespace LocalAIModelManager.App.ViewModels.Settings;

/// <summary>
/// One settings page, rendered from a <see cref="SettingsSection"/> descriptor.
/// Edits are staged locally and only written when the user saves, so a half typed
/// port number never reaches the gateway configuration.
/// </summary>
public sealed class SettingsPageViewModel : PageViewModelBase
{
    private readonly SettingsSection _section;
    private bool _loaded;
    private string? _savedAt;

    public SettingsPageViewModel(AppServices services, SettingsSection section)
        : base(services)
    {
        _section = section;
        Fields = new ObservableCollection<SettingFieldViewModel>(
            section.Fields.Select(f => new SettingFieldViewModel(f, services)));
        Notes = new ObservableCollection<string>();

        SaveCommand = new AsyncRelayCommand(SaveAsync, () => !IsBusy, OnCommandFailed);
        ReloadCommand = new RelayCommand(_ => Load(force: true));
        ResetCommand = new RelayCommand(_ => Load(force: true));
    }

    // Built on each access through the section's Loc.T(...) keys: the shell keeps this
    // page view model alive across a language switch, so a cached string would freeze.
    public override string Title => _section.Title;

    public override string Description => _section.Description;

    public SettingsSection Section => _section;

    public ObservableCollection<SettingFieldViewModel> Fields { get; }

    public ObservableCollection<string> Notes { get; }

    public ICommand SaveCommand { get; }

    public ICommand ReloadCommand { get; }

    public ICommand ResetCommand { get; }

    public bool HasFields => Fields.Count > 0;

    public bool RequiresGatewayRestart => _section.RequiresGatewayRestart;

    public string? SavedAt
    {
        get => _savedAt;
        private set => SetProperty(ref _savedAt, value);
    }

    public override Task InitializeAsync()
    {
        Load(force: false);
        return Task.CompletedTask;
    }

    public override Task RefreshAsync()
    {
        // Deliberately does not reload the fields: unsaved edits must survive a
        // page switch. The user can press "Reload" to discard them.
        RefreshNotes();
        return Task.CompletedTask;
    }

    private void OnCommandFailed(Exception exception)
    {
        // Saving can fail for reasons the user must see, e.g. the startup registry
        // key being denied. Never swallow it.
        SetError(exception.Message);
        Services.Logs.Error("ui", Loc.T("log.ui.saveFailed", Title, exception.Message), exception);
        Services.Dialogs.ShowError(Loc.T("settings.saveError.title", Title), exception.Message);
    }

    private void Load(bool force)
    {
        if (_loaded && !force)
        {
            RefreshNotes();
            return;
        }

        var settings = Services.Settings.Current;
        foreach (var field in Fields)
        {
            field.LoadFrom(settings);
            if (string.Equals(field.Key, "configDirectory", StringComparison.OrdinalIgnoreCase))
            {
                field.Value = Services.ConfigDirectory;
            }
        }

        _loaded = true;
        SavedAt = null;
        SetStatus(force ? Loc.T("settings.status.reloaded") : string.Empty);
        RefreshNotes();
    }

    private void RefreshNotes()
    {
        Notes.Clear();
        if (_section.Notes is not null)
        {
            foreach (var note in _section.Notes(Services))
            {
                Notes.Add(note);
            }
        }
    }

    private async Task SaveAsync()
    {
        // 1) validate against a detached copy so nothing is persisted on error.
        var candidate = Services.Settings.Snapshot();
        var invalid = new List<string>();
        foreach (var field in Fields)
        {
            if (!field.ApplyTo(candidate) && field.HasValidationError)
            {
                invalid.Add(Loc.T("settings.error.fieldInvalid", field.Label, field.ValidationError));
            }
        }

        if (invalid.Count > 0)
        {
            SetError(Loc.T("settings.error.invalid", string.Join(Loc.T("common.listSeparator"), invalid)));
            return;
        }

        // 2) re-apply for real, then persist.
        Services.SaveSettings(live =>
        {
            foreach (var field in Fields)
            {
                field.ApplyTo(live);
            }
        });

        // 3) side effects (startup registration, theme, gateway restart).
        _section.OnSaved?.Invoke(Services, Services.Current);

        if (string.Equals(_section.Key, SettingsSections.General, StringComparison.OrdinalIgnoreCase))
        {
            ThemeManager.Apply(Services.Current.General.Theme);
        }

        SavedAt = DateTimeOffset.Now.ToString("HH:mm:ss");
        SetStatus(Loc.T("settings.status.saved"));
        Services.Notify(Loc.T("settings.notify.saved", Title));

        if (_section.RequiresGatewayRestart && Services.Gateway.IsRunning)
        {
            Services.RequestGatewayRestart();
            SetStatus(Loc.T("settings.status.savedRestart"));
        }

        RefreshNotes();
        await Task.CompletedTask.ConfigureAwait(true);
    }
}
