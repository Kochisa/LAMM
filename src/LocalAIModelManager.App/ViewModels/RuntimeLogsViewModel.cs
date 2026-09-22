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

    public override string Title => "运行日志";

    public override string Description =>
        "内存日志（默认不写磁盘）。默认不记录 API 密钥、鉴权头、完整提示词与输出。";

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
        $"缓冲 {Services.Logs.Count} / {Services.Logs.Capacity} 条；累计写入 {Services.Logs.TotalWritten} 条；" +
        $"覆盖 {Services.Logs.DroppedCount} 条；显示 {Entries.Count} 条";

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

        SetStatus("已清空内存日志缓冲区。");
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
            SetStatus($"已复制 {Entries.Count} 条日志到剪贴板。");
        }
    }

    private void TogglePause()
    {
        LoggingPaused = !LoggingPaused;
        Services.Logs.IsPaused = LoggingPaused;
        SetStatus(LoggingPaused ? "日志捕获已暂停（仍会继续计数）。" : "日志捕获已恢复。");
    }

    private async Task SaveAsync()
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "保存日志快照",
            Filter = "日志文件 (*.log)|*.log|文本文件 (*.txt)|*.txt|所有文件 (*.*)|*.*",
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

            SetStatus($"已保存 {written} 条日志到 {dialog.FileName}");
        }, "正在保存日志…").ConfigureAwait(true);
    }
}
