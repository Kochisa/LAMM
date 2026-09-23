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
///
/// MainWindow.xaml binds to the string properties below and is NOT rebuilt when the
/// language changes, so every string exposed to the window is computed on each get
/// (never cached in a field) and a fresh OnPropertyChanged is raised for it from
/// <see cref="OnLanguageChanged"/>.
/// </summary>
public sealed class ShellViewModel : ObservableObject, IDisposable
{
    private readonly AppServices _services;
    private readonly Dictionary<string, PageViewModelBase> _pageCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, FrameworkElement> _viewCache = new(StringComparer.OrdinalIgnoreCase);

    private readonly Action<string> _languageChangedHandler;

    private NavigationItemViewModel? _selectedItem;
    private PageViewModelBase? _currentPage;
    private FrameworkElement? _currentView;
    private string _statusText = Loc.T("common.ready");
    private DateTimeOffset _statusTimestamp = DateTimeOffset.Now;
    private bool _isBusy;
    private bool _disposed;

    public ShellViewModel(AppServices services)
    {
        _services = services;

        _languageChangedHandler = OnLanguageChanged;
        Localizer.LanguageChanged += _languageChangedHandler;

        BuildNavigation();

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
            StatusText = Loc.T("log.gateway.restartNeeded");
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

    /// <summary>True while the API gateway is listening; bound by the status bar indicator.</summary>
    public bool IsGatewayRunning => _services.Gateway.IsRunning;

    public string GatewayText => _services.Gateway.IsRunning
        ? Loc.T("shell.gateway.running", _services.Gateway.BaseUrl)
        : Loc.T("shell.gateway.stopped");

    public string LoadedModelsText
    {
        get
        {
            var statuses = _services.Lifecycle.GetStatuses();
            var loaded = statuses.Count(s => s.IsLoaded);
            return Loc.T("shell.modelsLoaded", loaded, statuses.Count);
        }
    }

    public string EngineText
    {
        get
        {
            var engine = _services.SelectedEngine;
            if (engine is null)
            {
                return Loc.T("shell.engine.none");
            }

            var capabilities = engine.ExecutableExists() ? _services.GetCapabilities(engine.Id) : null;
            return capabilities is null
                ? Loc.T("shell.engine.unavailable", engine.Id)
                : Loc.T(
                    "shell.engine.summary",
                    engine.Id,
                    capabilities.Version ?? Loc.T("common.unknown"),
                    capabilities.Parameters.Count);
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
        OnPropertyChanged(nameof(IsGatewayRunning));
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
            StatusText = Loc.T("shell.status.gatewayRestarted", _services.Gateway.BaseUrl);
        }
        catch (Exception ex)
        {
            _services.Logs.Error("ui", Loc.T("log.ui.restartGatewayFailed"), ex);
            StatusText = Loc.T("shell.status.restartFailed", ex.Message);
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
            StatusText = Loc.T("shell.status.gatewayStarted", _services.Gateway.BaseUrl);
        }
        catch (Exception ex)
        {
            StatusText = Loc.T("shell.status.startFailed", ex.Message);
        }

        RefreshChrome();
    }

    private async Task StopGatewayAsync()
    {
        await _services.Gateway.StopAsync().ConfigureAwait(true);
        StatusText = Loc.T("shell.status.gatewayStopped");
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
            StatusText = Loc.T("shell.status.openConfigFolderFailed", ex.Message);
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
            StatusText = Loc.T("shell.status.openLinkFailed", ex.Message);
        }
    }

    /// <summary>
    /// Rebuilds everything the shell derived from the previous language: the navigation
    /// catalog (group names and page titles), the page/view caches - which would
    /// otherwise keep the old strings alive - and the window level status strings.
    /// The currently selected page is re-created and re-activated so its own localized
    /// strings, notes and field labels are rebuilt too.
    /// </summary>
    private void OnLanguageChanged(string language)
    {
        // Localizer.LanguageChanged is raised from inside the settings save, on the UI
        // thread, and rebuilding would tear down the very page that is still running that
        // command. Posting defers the rebuild to the next dispatcher turn, after the save
        // has finished. A failure to rebuild must never take the application down.
        UiDispatcher.Post(() =>
        {
            try
            {
                RebuildForLanguage();
            }
            catch (Exception ex)
            {
                _services.Logs.Warn("ui", Loc.T("shell.status.languageRebuildFailed"), ex);
            }
        });
    }

    private void RebuildForLanguage()
    {
        var selectedKey = _selectedItem?.Descriptor.Key;

        BuildNavigation();

        _pageCache.Clear();
        _viewCache.Clear();
        CurrentPage = null;
        CurrentView = null;

        var target = AllNavigationItems.FirstOrDefault(i => i.Descriptor.Key == selectedKey)
                     ?? AllNavigationItems.FirstOrDefault();

        if (target is not null)
        {
            _ = NavigateAsync(target);
        }

        RefreshChrome();
        OnPropertyChanged(nameof(CurrentPageTitle));
    }

    /// <summary>Rebuilds the navigation groups from the (per-language) page catalog.</summary>
    private void BuildNavigation()
    {
        var previous = _selectedItem?.Descriptor.Key;

        Groups.Clear();
        foreach (var group in PageCatalog.Groups)
        {
            var items = PageCatalog.Pages
                .Where(p => p.Group == group)
                .Select(p => new NavigationItemViewModel(p));

            Groups.Add(new NavigationGroupViewModel(PageCatalog.GroupTitle(group), items));
        }

        // The old item instances are gone with the rebuilt groups, so re-point the
        // selection at the fresh instance with the same page key.
        _selectedItem = previous is null
            ? null
            : AllNavigationItems.FirstOrDefault(i => i.Descriptor.Key == previous);

        if (_selectedItem is not null)
        {
            _selectedItem.IsSelected = true;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Localizer.LanguageChanged -= _languageChangedHandler;
    }
}
