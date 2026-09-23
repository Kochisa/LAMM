using System.Collections.ObjectModel;
using System.Text;
using System.Windows.Input;
using System.Windows.Threading;
using LocalAIModelManager.App.Infrastructure;
using LocalAIModelManager.App.Services;
using LocalAIModelManager.Core.Logging;

namespace LocalAIModelManager.App.ViewModels;

public sealed class LogRowViewModel
{
    public LogRowViewModel(LogEntry entry)
    {
        Entry = entry;
    }

    public LogEntry Entry { get; }

    public long Sequence => Entry.Sequence;

    public string Time => Entry.Timestamp.ToString("HH:mm:ss.fff");

    public string Level => Entry.Level.ToString();

    public string Category => Entry.Category;

    public string Message => Entry.Message;

    public string? Exception => Entry.Exception;

    public string Tone => Entry.Level switch
    {
        LogLevel.Critical or LogLevel.Error => "error",
        LogLevel.Warning => "warn",
        LogLevel.Debug or LogLevel.Trace => "muted",
        _ => "normal",
    };
}

/// <summary>
/// Runtime Logs page. Logs live in a bounded in-memory ring buffer; nothing is
/// written to disk unless the operator presses Save.
/// </summary>
public sealed class RuntimeLogsViewModel : PageViewModelBase
{
    private const int MaxDisplayedRows = 4000;

    private readonly DispatcherTimer _flushTimer;
    private readonly Queue<LogEntry> _pending = new();
    private readonly object _pendingGate = new();

    private string _searchText = string.Empty;
    private LogLevel _minimumLevel = LogLevel.Information;
    private bool _autoScroll = true;
    private bool _loggingPaused;
    private bool _initialized;

    public RuntimeLogsViewModel(AppServices services)
        : base(services)
    {
        _flushTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(300),
        };
        _flushTimer.Tick += (_, _) => FlushPending();

        ClearCommand = new RelayCommand(_ => Clear());
        CopyCommand = new RelayCommand(_ => Copy());
        SaveCommand = new AsyncRelayCommand(SaveAsync, () => !IsBusy);
        SearchCommand = new RelayCommand(_ => Rebuild());
        TogglePauseCommand = new RelayCommand(_ => TogglePause());

        Services.Logs.EntryAdded += OnEntryAdded;
    }

    public override string Title => Loc.T("page.runtimeLogs.title");

    public override string Description => Loc.T("page.runtimeLogs.desc");

    public ObservableCollection<LogRowViewModel> Entries { get; } = new();

    public IReadOnlyList<LogLevel> Levels { get; } = Enum.GetValues<LogLevel>();

    public string SearchText
    {
        get => _searchText;
        set
        {
            if (SetProperty(ref _searchText, value))
            {
                Rebuild();
            }
        }
    }

    public LogLevel MinimumLevel
    {
        get => _minimumLevel;
        set
        {
            if (SetProperty(ref _minimumLevel, value))
            {
                Rebuild();
            }
        }
    }

    public bool AutoScroll
    {
        get => _autoScroll;
        set => SetProperty(ref _autoScroll, value);
    }

    public bool LoggingPaused
    {
        get => _loggingPaused;
        private set => SetProperty(ref _loggingPaused, value);
    }

    public string Statistics =>
        Loc.T("logs.stats", Services.Logs.Count, Services.Logs.Capacity, Services.Logs.TotalWritten, Services.Logs.DroppedCount, Entries.Count);

    public ICommand ClearCommand { get; }

    public ICommand CopyCommand { get; }

    public ICommand SaveCommand { get; }

    public ICommand SearchCommand { get; }

    public ICommand TogglePauseCommand { get; }

    /// <summary>Raised after rows change so the view can scroll to the newest entry.</summary>
    public event Action? RowsChanged;

    public override Task InitializeAsync()
    {
        if (!_initialized)
        {
            _initialized = true;
            Rebuild();
        }

        return Task.CompletedTask;
    }

    public override Task RefreshAsync()
    {
        Rebuild();
        return Task.CompletedTask;
    }

    protected override void OnActivated() => _flushTimer.Start();

    protected override void OnDeactivated() => _flushTimer.Stop();

    private void OnEntryAdded(LogEntry entry)
    {
        lock (_pendingGate)
        {
            _pending.Enqueue(entry);
        }
    }

    private void FlushPending()
    {
        List<LogEntry> batch;
        lock (_pendingGate)
        {
            if (_pending.Count == 0)
            {
                return;
            }

            batch = _pending.ToList();
            _pending.Clear();
        }

        var appended = false;
        foreach (var entry in batch)
        {
            if (!Matches(entry))
            {
                continue;
            }

            Entries.Add(new LogRowViewModel(entry));
            appended = true;
        }

        while (Entries.Count > MaxDisplayedRows)
        {
            Entries.RemoveAt(0);
        }

        OnPropertyChanged(nameof(Statistics));

        if (appended)
        {
            RowsChanged?.Invoke();
        }
    }

    private bool Matches(LogEntry entry)
    {
        if (entry.Level < _minimumLevel)
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(_searchText))
        {
            return true;
        }

        return entry.Message.Contains(_searchText, StringComparison.OrdinalIgnoreCase) ||
               entry.Category.Contains(_searchText, StringComparison.OrdinalIgnoreCase) ||
               (entry.Exception?.Contains(_searchText, StringComparison.OrdinalIgnoreCase) ?? false);
    }

    private void Rebuild()
    {
        Entries.Clear();
        foreach (var entry in Services.Logs.Query(_searchText, _minimumLevel))
        {
            Entries.Add(new LogRowViewModel(entry));
        }

        OnPropertyChanged(nameof(Statistics));
        RowsChanged?.Invoke();
    }

    private void Clear()
    {
        Services.Logs.Clear();
        Entries.Clear();
        lock (_pendingGate)
        {
            _pending.Clear();
        }

        SetStatus(Loc.T("logs.status.cleared"));
        OnPropertyChanged(nameof(Statistics));
    }

    private void Copy()
    {
        var builder = new StringBuilder();
        foreach (var row in Entries)
        {
            builder.AppendLine(row.Entry.ToString());
        }

        if (ClipboardHelper.SetText(builder.ToString()))
        {
            SetStatus(Loc.T("logs.status.copied", Entries.Count));
        }
    }

    private void TogglePause()
    {
        LoggingPaused = !LoggingPaused;
        Services.Logs.IsPaused = LoggingPaused;
        SetStatus(LoggingPaused ? Loc.T("logs.status.paused") : Loc.T("logs.status.resumed"));
    }

    private async Task SaveAsync()
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = Loc.T("logs.saveDialog.title"),
            Filter = Loc.T("logs.saveDialog.filter"),
            FileName = $"lamm-log-{DateTime.Now:yyyyMMdd-HHmmss}.log",
            InitialDirectory = Services.ConfigDirectory,
            AddExtension = true,
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        await RunAsync(async () =>
        {
            var written = await Services.Logs
                .SaveToFileAsync(dialog.FileName, string.IsNullOrWhiteSpace(_searchText) ? null : _searchText, _minimumLevel)
                .ConfigureAwait(true);

            SetStatus(Loc.T("logs.status.saved", written, dialog.FileName));
        }, Loc.T("logs.busy.saving")).ConfigureAwait(true);
    }
}
