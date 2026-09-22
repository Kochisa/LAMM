using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Input;
using LocalAIModelManager.App.Infrastructure;
using LocalAIModelManager.App.Services;
using LocalAIModelManager.Core.Logging;

namespace LocalAIModelManager.App.ViewModels;

public sealed class NavigationItemViewModel : ObservableObject
{
    private bool _isSelected;
    private string _badge = string.Empty;

    public NavigationItemViewModel(PageDescriptor descriptor)
    {
        Descriptor = descriptor;
    }

    public PageDescriptor Descriptor { get; }

    public string Title => Descriptor.Title;

    public string Glyph => Descriptor.Glyph;

    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }

    /// <summary>Short status text shown at the right of the nav row (e.g. loaded count).</summary>
    public string Badge
    {
        get => _badge;
        set => SetProperty(ref _badge, value);
    }
}

public sealed class NavigationGroupViewModel
{
    public NavigationGroupViewModel(string name, IEnumerable<NavigationItemViewModel> items)
    {
        Name = name;
        Items = new ObservableCollection<NavigationItemViewModel>(items);
    }

    public string Name { get; }

    public ObservableCollection<NavigationItemViewModel> Items { get; }
}

/// <summary>
/// Shell view model: navigation, the main status bar and the gateway controls that
/// are available from every page.
/// </summary>
public sealed class ShellViewModel : ObservableObject
{
    private readonly AppServices _services;
    private readonly Dictionary<string, PageViewModelBase> _pageCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, FrameworkElement> _viewCache = new(StringComparer.OrdinalIgnoreCase);

    private NavigationItemViewModel? _selectedItem;
    private PageViewModelBase? _currentPage;
    private FrameworkElement? _currentView;
    private string _statusText = "就绪";
    private DateTimeOffset _statusTimestamp = DateTimeOffset.Now;
    private bool _isBusy;

    public ShellViewModel(AppServices services)
    {
        _services = services;

        foreach (var group in PageCatalog.Groups)
        {
            var items = PageCatalog.Pages
                .Where(p => p.Group == group)
                .Select(p => new NavigationItemViewModel(p));

            Groups.Add(new NavigationGroupViewModel(group, items));
        }

        NavigateCommand = new AsyncRelayCommand(
            parameter => NavigateAsync(parameter as NavigationItemViewModel),
            parameter => parameter is NavigationItemViewModel);

        RestartGatewayCommand = new AsyncRelayCommand(RestartGatewayAsync, () => !IsBusy);
        StartGatewayCommand = new AsyncRelayCommand(StartGatewayAsync, () => !IsBusy && !_services.Gateway.IsRunning);
        StopGatewayCommand = new AsyncRelayCommand(StopGatewayAsync, () => !IsBusy && _services.Gateway.IsRunning);
        OpenConfigFolderCommand = new RelayCommand(_ => OpenConfigFolder());
        OpenExternalDocsCommand = new RelayCommand(_ => OpenApiDocs());

        _services.Notification += message => UiDispatcher.Invoke(() => StatusText = message);
        _services.GatewayRestartRequired += () => UiDispatcher.Invoke(() =>
        {
            StatusText = "API 设置已更改，正在按新设置重启网关…";
            _ = RestartGatewayAsync();
        });

        RefreshChrome();
    }

    public ObservableCollection<NavigationGroupViewModel> Groups { get; } = new();

    public PageViewModelBase? CurrentPage
    {
        get => _currentPage;
        private set
        {
            if (SetProperty(ref _currentPage, value))
            {
                OnPropertyChanged(nameof(CurrentPageTitle));
            }
        }
    }

    public FrameworkElement? CurrentView
    {
        get => _currentView;
        private set => SetProperty(ref _currentView, value);
    }

    public string CurrentPageTitle => CurrentPage?.Title ?? string.Empty;

    public string StatusText
    {
        get => _statusText;
        set
        {
            _statusTimestamp = DateTimeOffset.Now;
            if (SetProperty(ref _statusText, value))
            {
                OnPropertyChanged(nameof(StatusTimestampText));
            }
        }
    }

    public string StatusTimestampText => _statusTimestamp.ToString("HH:mm:ss");

    public bool IsBusy
    {
        get => _isBusy;
        private set => SetProperty(ref _isBusy, value);
    }

    public string GatewayText => _services.Gateway.IsRunning
        ? $"网关运行中 · {_services.Gateway.BaseUrl}"
        : "网关已停止";

    public string LoadedModelsText
    {
        get
        {
            var statuses = _services.Lifecycle.GetStatuses();
            var loaded = statuses.Count(s => s.IsLoaded);
            return $"已加载 {loaded} / 共 {statuses.Count} 个模型";
        }
    }

    public string EngineText
    {
        get
        {
            var engine = _services.SelectedEngine;
            if (engine is null)
            {
                return "未配置推理引擎";
            }

            var capabilities = engine.ExecutableExists() ? _services.GetCapabilities(engine.Id) : null;
            return capabilities is null
                ? $"引擎 {engine.Id}（不可用）"
                : $"引擎 {engine.Id} · {capabilities.Version ?? "版本未知"} · {capabilities.Parameters.Count} 个参数";
        }
    }

    public ICommand NavigateCommand { get; }

    public ICommand RestartGatewayCommand { get; }

    public ICommand StartGatewayCommand { get; }

    public ICommand StopGatewayCommand { get; }

    public ICommand OpenConfigFolderCommand { get; }

    public ICommand OpenExternalDocsCommand { get; }

    /// <summary>Raised when the shell wants the window shown (tray menu).</summary>
    public event Action? WindowShown;

    /// <summary>Raised when the shell wants the window hidden (tray menu).</summary>
    public event Action? WindowHidden;

    /// <summary>Raised when the shell wants the application to exit.</summary>
    public event Action? ExitRequested;

    public void RequestShow() => WindowShown?.Invoke();

    public void RequestHide() => WindowHidden?.Invoke();

    public void RequestExit() => ExitRequested?.Invoke();

    public IReadOnlyList<NavigationItemViewModel> AllNavigationItems =>
        Groups.SelectMany(g => g.Items).ToList();

    public async Task InitializeAsync()
    {
        var first = AllNavigationItems.FirstOrDefault(i => i.Descriptor.Key == "models")
                    ?? AllNavigationItems.FirstOrDefault();

        if (first is not null)
        {
            await NavigateAsync(first).ConfigureAwait(true);
        }
    }

    public async Task NavigateAsync(NavigationItemViewModel? item)
    {
        if (item is null)
        {
            return;
        }

        if (ReferenceEquals(item, _selectedItem) && CurrentView is not null)
        {
            return;
        }

        CurrentPage?.Deactivate();

        if (_selectedItem is not null)
        {
            _selectedItem.IsSelected = false;
        }

        _selectedItem = item;
        item.IsSelected = true;

        var descriptor = item.Descriptor;

        if (!_pageCache.TryGetValue(descriptor.Key, out var page))
        {
            page = descriptor.CreateViewModel(_services);
            _pageCache[descriptor.Key] = page;
            _viewCache[descriptor.Key] = descriptor.CreateView(page);
        }

        CurrentPage = page;
        CurrentView = _viewCache[descriptor.Key];

        await page.ActivateAsync().ConfigureAwait(true);
        RefreshChrome();
    }

    /// <summary>Refreshes the status bar indicators; called periodically by the shell.</summary>
    public void RefreshChrome()
    {
        OnPropertyChanged(nameof(GatewayText));
        OnPropertyChanged(nameof(LoadedModelsText));
        OnPropertyChanged(nameof(EngineText));

        foreach (var item in AllNavigationItems)
        {
            item.Badge = item.Descriptor.Key switch
            {
                "models" => _services.Models.Count.ToString(),
                "runtime-status" => _services.Lifecycle.GetStatuses().Count(s => s.IsLoaded).ToString(),
                _ => string.Empty,
            };
        }

        (StartGatewayCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (StopGatewayCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (RestartGatewayCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
    }

    public async Task RestartGatewayAsync()
    {
        IsBusy = true;
        try
        {
            await _services.Runtime.RestartGatewayAsync().ConfigureAwait(true);
            StatusText = $"网关已按当前设置重启：{_services.Gateway.BaseUrl}";
        }
        catch (Exception ex)
        {
            _services.Logs.Error("ui", "重启网关失败", ex);
            StatusText = $"重启网关失败：{ex.Message}";
        }
        finally
        {
            IsBusy = false;
            RefreshChrome();
        }
    }

    private async Task StartGatewayAsync()
    {
        try
        {
            await _services.Gateway.StartAsync().ConfigureAwait(true);
            StatusText = $"网关已启动：{_services.Gateway.BaseUrl}";
        }
        catch (Exception ex)
        {
            StatusText = $"启动网关失败：{ex.Message}";
        }

        RefreshChrome();
    }

    private async Task StopGatewayAsync()
    {
        await _services.Gateway.StopAsync().ConfigureAwait(true);
        StatusText = "网关已停止。";
        RefreshChrome();
    }

    private void OpenConfigFolder()
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "explorer.exe",
                ArgumentList = { _services.ConfigDirectory },
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            StatusText = $"打开配置目录失败：{ex.Message}";
        }
    }

    private void OpenApiDocs()
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = $"{_services.Gateway.BaseUrl}/v1/models",
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            StatusText = $"打开链接失败：{ex.Message}";
        }
    }
}
