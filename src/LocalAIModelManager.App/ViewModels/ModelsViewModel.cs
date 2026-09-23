using System.Collections.ObjectModel;
using System.Windows.Input;
using System.Windows.Threading;
using LocalAIModelManager.App.Infrastructure;
using LocalAIModelManager.App.Services;
using LocalAIModelManager.App.Views.Models;
using LocalAIModelManager.Core.Lifecycle;
using LocalAIModelManager.Core.Models;

namespace LocalAIModelManager.App.ViewModels;

public sealed class ModelRowViewModel : ObservableObject
{
    private ModelDefinition _definition;
    private ModelRuntimeStatus? _status;

    public ModelRowViewModel(ModelDefinition definition, ModelRuntimeStatus? status)
    {
        _definition = definition;
        _status = status;
    }

    public ModelDefinition Definition => _definition;

    public string Id => _definition.Id;

    public string DisplayName => _definition.DisplayName;

    public string FilePath => _definition.FilePath;

    public string EngineId => _definition.EngineId;

    public bool Enabled => _definition.Enabled;

    public bool FileExists => _definition.FileExists();

    public ModelState State => _status?.State ?? ModelState.Standby;

    public string StateLabel => Labels.ModelState(State);

    public bool IsLoaded => State == ModelState.Ready;

    public int? Pid => _status?.Pid;

    public int? Port => _status?.Port;

    public int ActiveRequests => _status?.ActiveRequests ?? 0;

    public long TotalRequests => _status?.TotalRequests ?? 0;

    public long? VramBytes => _status?.VramBytes;

    public DateTimeOffset? LastUsedAtUtc => _status?.LastUsedAtUtc;

    public TimeSpan? IdleFor => _status?.IdleFor;

    public string EngineName => _status?.EngineName ?? _definition.EngineId;

    public string LastStopLabel => _status is null ? "—" : Labels.StopMode(_status.LastStopMode);

    /// <summary>Exact engine command line, the ground truth for "is it on the GPU?".</summary>
    public string LaunchCommandLine => _status?.LaunchCommandLine ?? string.Empty;

    public string? LaunchWarning => _status?.LaunchWarning;

    public string? LastError => _status?.LastError;

    public string ParametersSummary
    {
        get
        {
            if (_definition.Parameters.Count == 0)
            {
                return Loc.T("models.details.parametersDefault");
            }

            return string.Join(", ", _definition.Parameters
                .OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase)
                .Select(p => p.Value.Length == 0 ? p.Key : $"{p.Key}={p.Value}"));
        }
    }

    public void Update(ModelDefinition definition, ModelRuntimeStatus? status)
    {
        _definition = definition;
        _status = status;
        RaiseAllPropertiesChangedPublic();
    }

    private void RaiseAllPropertiesChangedPublic() => OnPropertyChanged(string.Empty);
}

/// <summary>Models page: the model catalog with full lifecycle control.</summary>
public sealed class ModelsViewModel : PageViewModelBase
{
    private readonly DispatcherTimer _timer;
    private ModelRowViewModel? _selected;
    private string? _testOutput;
    private bool _initialized;

    public ModelsViewModel(AppServices services)
        : base(services)
    {
        AddCommand = new AsyncRelayCommand(AddAsync, () => !IsBusy);
        EditCommand = new AsyncRelayCommand(EditAsync, () => Selected is not null && !IsBusy);
        RenameCommand = new AsyncRelayCommand(RenameAsync, () => Selected is not null && !IsBusy);
        DeleteCommand = new AsyncRelayCommand(DeleteAsync, () => Selected is not null && !IsBusy);
        StartCommand = new AsyncRelayCommand(() => StartAsync(), () => Selected is not null && !IsBusy);
        StopCommand = new AsyncRelayCommand(() => StopAsync(), () => Selected is not null && !IsBusy);
        RestartCommand = new AsyncRelayCommand(() => RestartAsync(), () => Selected is not null && !IsBusy);
        TestCommand = new AsyncRelayCommand(() => TestAsync(), () => Selected is not null && !IsBusy);
        AutoTuneCommand = new AsyncRelayCommand(AutoTuneAsync, () => Selected is not null && !IsBusy);
        AutoTuneAllCommand = new AsyncRelayCommand(AutoTuneAllAsync, () => Models.Count > 0 && !IsBusy);
        UnloadAllCommand = new AsyncRelayCommand(UnloadAllAsync, () => !IsBusy);
        RefreshCommand = new RelayCommand(_ => ReloadRows());
        CopyIdCommand = new RelayCommand(_ => CopySelectedId(), _ => Selected is not null);
        OpenFolderCommand = new RelayCommand(_ => OpenSelectedFolder(), _ => Selected is not null);

        _timer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(2),
        };
        _timer.Tick += (_, _) => RefreshStatuses();

        Services.Runtime.Models.Changed += OnRegistryChanged;
    }

    public override string Title => Loc.T("page.models.title");

    public override string Description => Loc.T("page.models.desc");

    public ObservableCollection<ModelRowViewModel> Models { get; } = new();

    public ModelRowViewModel? Selected
    {
        get => _selected;
        set
        {
            if (SetProperty(ref _selected, value))
            {
                UpdateCommandStates();
                OnPropertyChanged(nameof(SelectedDetails));
            }
        }
    }

    public string SelectedDetails
    {
        get
        {
            var row = Selected;
            if (row is null)
            {
                return Loc.T("models.details.none");
            }

            var lines = new List<string>
            {
                Loc.T("models.details.id", row.Id),
                Loc.T("models.details.file", row.FilePath, Loc.T(row.FileExists ? "models.details.fileExists" : "models.details.fileMissing")),
                Loc.T("models.details.engine", row.EngineName),
                row.IsLoaded
                    ? Loc.T("models.details.stateLoaded", row.StateLabel, row.Pid, row.Port)
                    : Loc.T("models.details.state", row.StateLabel),
                Loc.T("models.details.parameters", row.ParametersSummary),
            };

            if (row.LastError is { Length: > 0 } error)
            {
                lines.Add(Loc.T("models.details.lastError", error));
            }

            if (row.LaunchWarning is { Length: > 0 } warning)
            {
                lines.Add(string.Empty);
                lines.Add($"⚠ {warning}");
            }

            if (row.LaunchCommandLine.Length > 0)
            {
                lines.Add(string.Empty);
                lines.Add(Loc.T("models.details.commandLine"));
                lines.Add(row.LaunchCommandLine);

                if (!row.LaunchCommandLine.Contains("--n-gpu-layers", StringComparison.OrdinalIgnoreCase) &&
                    !row.LaunchCommandLine.Contains("-ngl", StringComparison.OrdinalIgnoreCase))
                {
                    lines.Add(string.Empty);
                    lines.Add(Loc.T("models.details.noGpuLayersTip"));
                }
            }

            return string.Join(Environment.NewLine, lines);
        }
    }

    public string? TestOutput
    {
        get => _testOutput;
        private set => SetProperty(ref _testOutput, value);
    }

    public ICommand AutoTuneCommand { get; }

    public ICommand AutoTuneAllCommand { get; }

    public ICommand AddCommand { get; }

    public ICommand EditCommand { get; }

    public ICommand RenameCommand { get; }

    public ICommand DeleteCommand { get; }

    public ICommand StartCommand { get; }

    public ICommand StopCommand { get; }

    public ICommand RestartCommand { get; }

    public ICommand TestCommand { get; }

    public ICommand UnloadAllCommand { get; }

    public ICommand RefreshCommand { get; }

    public ICommand CopyIdCommand { get; }

    public ICommand OpenFolderCommand { get; }

    public override Task InitializeAsync()
    {
        if (!_initialized)
        {
            _initialized = true;
            ReloadRows();
        }

        return Task.CompletedTask;
    }

    public override Task RefreshAsync()
    {
        ReloadRows();
        return Task.CompletedTask;
    }

    protected override void OnActivated() => _timer.Start();

    protected override void OnDeactivated() => _timer.Stop();

    private void OnRegistryChanged() => UiDispatcher.Invoke(ReloadRows);

    private void ReloadRows()
    {
        var statuses = Services.Lifecycle.GetStatuses().ToDictionary(s => s.ModelId, StringComparer.OrdinalIgnoreCase);
        var selectedId = Selected?.Id;

        Models.Clear();
        foreach (var definition in Services.Models.All.OrderBy(m => m.Id, StringComparer.OrdinalIgnoreCase))
        {
            statuses.TryGetValue(definition.Id, out var status);
            Models.Add(new ModelRowViewModel(definition, status));
        }

        Selected = Models.FirstOrDefault(m => string.Equals(m.Id, selectedId, StringComparison.OrdinalIgnoreCase))
                   ?? Models.FirstOrDefault();

        UpdateCommandStates();
        OnPropertyChanged(nameof(SelectedDetails));
    }

    private void RefreshStatuses()
    {
        var statuses = Services.Lifecycle.GetStatuses().ToDictionary(s => s.ModelId, StringComparer.OrdinalIgnoreCase);
        foreach (var row in Models)
        {
            statuses.TryGetValue(row.Id, out var status);
            row.Update(Services.Models.Get(row.Id) ?? row.Definition, status);
        }

        OnPropertyChanged(nameof(SelectedDetails));
    }

    private void UpdateCommandStates()
    {
        foreach (var command in new ICommand[] { EditCommand, RenameCommand, DeleteCommand, StartCommand, StopCommand, RestartCommand, TestCommand, AutoTuneCommand, AutoTuneAllCommand })
        {
            (command as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        }

        (CopyIdCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (OpenFolderCommand as RelayCommand)?.RaiseCanExecuteChanged();
    }

    private async Task AddAsync()
    {
        if (Services.Engines.Count == 0)
        {
            Services.Dialogs.ShowError(Loc.T("editor.model.title.new"), Loc.T("models.needEngine"));
            return;
        }

        var dialog = new ModelEditorWindow(null, Services) { Owner = System.Windows.Application.Current?.MainWindow };
        if (dialog.ShowDialog() != true || dialog.Result is null)
        {
            return;
        }

        try
        {
            var added = Services.Models.Add(dialog.Result);
            SetStatus(Loc.T("models.status.registered", added.Id));
            Services.Notify(Loc.T("models.notify.registered", added.Id));
        }
        catch (InvalidOperationException ex)
        {
            Services.Dialogs.ShowError(Loc.T("editor.model.title.new"), ex.Message);
            SetError(ex.Message);
        }

        ReloadRows();
        await Task.CompletedTask.ConfigureAwait(true);
    }

    private async Task EditAsync()
    {
        var row = Selected;
        if (row is null)
        {
            return;
        }

        var dialog = new ModelEditorWindow(row.Definition, Services) { Owner = System.Windows.Application.Current?.MainWindow };
        if (dialog.ShowDialog() != true || dialog.Result is null)
        {
            return;
        }

        Services.Models.Update(dialog.Result);
        SetStatus(Loc.T("models.status.updated", dialog.Result.Id));
        ReloadRows();
        await Task.CompletedTask.ConfigureAwait(true);
    }

    private async Task RenameAsync()
    {
        var row = Selected;
        if (row is null)
        {
            return;
        }

        var displayName = Services.Dialogs.Prompt(
            Loc.T("models.rename.title"),
            Loc.T("models.rename.displayName"),
            row.DisplayName);
        if (displayName is null)
        {
            return;
        }

        var newId = Services.Dialogs.Prompt(
            Loc.T("models.rename.title"),
            Loc.T("models.rename.modelId", row.Id),
            row.Id);

        if (newId is null)
        {
            return;
        }

        try
        {
            // A loaded model must be unloaded before its id can change safely.
            var wasLoaded = row.IsLoaded;
            if (wasLoaded)
            {
                await Services.Lifecycle.StopAsync(row.Id, CancellationToken.None).ConfigureAwait(true);
            }

            Services.Models.Rename(row.Id, displayName, newId);
            SetStatus(Loc.T("models.status.renamed", row.Id, newId));
            ReloadRows();
        }
        catch (InvalidOperationException ex)
        {
            SetError(ex.Message);
            Services.Dialogs.ShowError(Loc.T("models.rename.title"), ex.Message);
        }
    }

    private async Task DeleteAsync()
    {
        var row = Selected;
        if (row is null)
        {
            return;
        }

        var confirmed = Services.Dialogs.Confirm(
            Loc.T("models.delete.title"),
            Loc.T("models.delete.confirm", row.DisplayName, row.Id));

        if (!confirmed)
        {
            return;
        }

        await RunAsync(async () =>
        {
            await Services.Lifecycle.StopAsync(row.Id, CancellationToken.None).ConfigureAwait(true);
            Services.Models.Remove(row.Id);
            SetStatus(Loc.T("models.status.deleted", row.Id));
            Services.Notify(Loc.T("models.notify.deleted", row.Id));
            ReloadRows();
        }).ConfigureAwait(true);
    }

    private Task StartAsync() => RunAsync(async () =>
    {
        var row = Selected!;
        await Services.Lifecycle.StartAsync(row.Id, CancellationToken.None).ConfigureAwait(true);
        SetStatus(Loc.T("models.status.loaded", row.Id));
        RefreshStatuses();
    }, Loc.T("models.busy.loading"));

    private Task StopAsync() => RunAsync(async () =>
    {
        var row = Selected!;
        await Services.Lifecycle.StopAsync(row.Id, CancellationToken.None).ConfigureAwait(true);
        SetStatus(Loc.T("models.status.unloaded", row.Id));
        RefreshStatuses();
    }, Loc.T("models.busy.unloading"));

    private Task RestartAsync() => RunAsync(async () =>
    {
        var row = Selected!;
        await Services.Lifecycle.RestartAsync(row.Id, CancellationToken.None).ConfigureAwait(true);
        SetStatus(Loc.T("models.status.restarted", row.Id));
        RefreshStatuses();
    }, Loc.T("models.busy.restarting"));

    /// <summary>
    /// Reads the model file's own GGUF metadata and combines it with the card's VRAM to
    /// write sensible parameters, so importing a model is one click instead of hand tuning.
    /// </summary>
    private Task AutoTuneAsync() => RunAsync(async () =>
    {
        var row = Selected!;
        var model = Services.Models.Get(row.Id)
                    ?? throw new InvalidOperationException(Loc.T("models.error.missing", row.Id));

        var result = await Services.AutoTuneAsync(model.FilePath).ConfigureAwait(true);

        var updated = model.Clone();
        foreach (var (key, value) in result.Parameters)
        {
            updated.Parameters[key] = value;
        }

        Services.Models.Update(updated);

        TestOutput = BuildTuningReport(model, result, wasLoaded: row.IsLoaded);
        SetStatus(result.UsedFallback
            ? Loc.T("models.status.tunedFallback", model.Id)
            : Loc.T("models.status.tuned", model.Id)
              + (row.IsLoaded ? Loc.T("models.status.tunedReload") : string.Empty));

        ReloadRows();
    }, Loc.T("models.busy.tuning"));

    private Task AutoTuneAllAsync() => RunAsync(async () =>
    {
        var all = Services.Models.All;
        var lines = new List<string>();
        var changed = 0;

        foreach (var model in all)
        {
            var result = await Services.AutoTuneAsync(model.FilePath).ConfigureAwait(true);
            if (result.UsedFallback)
            {
                lines.Add(Loc.T("models.autotune.allSkipped", model.Id, string.Join(" ", result.Warnings)));
                continue;
            }

            var updated = model.Clone();
            foreach (var (key, value) in result.Parameters)
            {
                updated.Parameters[key] = value;
            }

            Services.Models.Update(updated);
            changed++;
            lines.Add(Loc.T("models.autotune.allEntry", model.Id, string.Join("　", result.Explanation.Skip(1))));
        }

        TestOutput = Loc.T("models.autotune.allHeader", changed, all.Count)
                     + Environment.NewLine + Environment.NewLine
                     + string.Join(Environment.NewLine, lines)
                     + Environment.NewLine + Environment.NewLine
                     + Loc.T("models.autotune.allNote");

        SetStatus(Loc.T("models.status.tunedAll", changed));
        ReloadRows();
    }, Loc.T("models.busy.tuningAll"));

    private static string BuildTuningReport(
        Core.Models.ModelDefinition model,
        Core.Models.AutoTuneResult result,
        bool wasLoaded)
    {
        var lines = new List<string>
        {
            Loc.T("models.autotune.reportHeader", model.Id),
            Loc.T("models.autotune.reportFile", model.FilePath),
            string.Empty,
        };

        lines.AddRange(result.Explanation.Select(e => "· " + e));

        if (result.Warnings.Count > 0)
        {
            lines.Add(string.Empty);
            lines.Add(Loc.T("models.autotune.reportWarnings"));
            lines.AddRange(result.Warnings.Select(w => "! " + w));
        }

        lines.Add(string.Empty);
        lines.Add(Loc.T("models.autotune.reportWritten"));
        lines.AddRange(result.Parameters.Select(p => $"  {p.Key} = {p.Value}"));

        if (wasLoaded)
        {
            lines.Add(string.Empty);
            lines.Add(Loc.T("models.autotune.reportLoaded"));
        }

        return string.Join(Environment.NewLine, lines);
    }

    private Task TestAsync() => RunAsync(async () =>
    {        var row = Selected!;
        var result = await Services.Lifecycle.TestAsync(row.Id, CancellationToken.None).ConfigureAwait(true);

        var lines = new List<string>
        {
            Loc.T("models.test.header", row.Id),
            Loc.T("models.test.verdict", result.Summary),
            string.Empty,
        };

        foreach (var detail in result.Details)
        {
            lines.Add("· " + detail);
        }

        if (result.Warnings.Count > 0)
        {
            lines.Add(string.Empty);
            lines.Add(Loc.T("models.test.warnings"));
            lines.AddRange(result.Warnings.Select(w => "! " + w));
        }

        if (result.Errors.Count > 0)
        {
            lines.Add(string.Empty);
            lines.Add(Loc.T("models.test.errors"));
            lines.AddRange(result.Errors.Select(e => "× " + e));
        }

        TestOutput = string.Join(Environment.NewLine, lines);
        SetStatus(result.Success ? Loc.T("models.status.testPassed") : Loc.T("models.status.testFailed"));
    }, Loc.T("models.busy.testing"));

    private Task UnloadAllAsync() => RunAsync(async () =>
    {
        await Services.Lifecycle.UnloadAllAsync(CancellationToken.None).ConfigureAwait(true);
        SetStatus(Loc.T("models.status.unloadedAll"));
        RefreshStatuses();
    }, Loc.T("models.busy.unloadingAll"));

    private void CopySelectedId()
    {
        if (Selected is null)
        {
            return;
        }

        if (ClipboardHelper.SetText(Selected.Id))
        {
            SetStatus(Loc.T("models.status.idCopied", Selected.Id));
        }
    }

    private void OpenSelectedFolder()
    {
        var path = Selected?.FilePath;
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        try
        {
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory) && Directory.Exists(directory))
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    ArgumentList = { "/select,", path },
                    UseShellExecute = true,
                });
            }
        }
        catch (Exception ex)
        {
            SetError(Loc.T("models.openFolder.failed", ex.Message));
        }
    }
}
