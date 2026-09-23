using LocalAIModelManager.Core.Logging;

namespace LocalAIModelManager.App.ViewModels;

/// <summary>Base class for every navigable page view model.</summary>
public abstract class PageViewModelBase : Infrastructure.ObservableObject
{
    private bool _isBusy;
    private string? _statusMessage;
    private bool _statusIsError;

    protected PageViewModelBase(Services.AppServices services)
    {
        Services = services;
    }

    protected Services.AppServices Services { get; }

    public abstract string Title { get; }

    public abstract string Description { get; }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value))
            {
                OnPropertyChanged(nameof(IsNotBusy));
            }
        }
    }

    /// <summary>Convenience inverse used for button enablement in XAML.</summary>
    public bool IsNotBusy => !_isBusy;

    public string? StatusMessage
    {
        get => _statusMessage;
        private set => SetProperty(ref _statusMessage, value);
    }

    public bool StatusIsError
    {
        get => _statusIsError;
        private set => SetProperty(ref _statusIsError, value);
    }

    /// <summary>Called once, the first time the page becomes visible.</summary>
    public virtual Task InitializeAsync() => Task.CompletedTask;

    /// <summary>Called every time the page becomes visible again.</summary>
    public virtual Task RefreshAsync() => Task.CompletedTask;

    /// <summary>True while this page is the selected navigation target.</summary>
    public bool IsActive { get; private set; }

    /// <summary>Called by the shell when the page becomes visible.</summary>
    public async Task ActivateAsync()
    {
        IsActive = true;
        await InitializeAsync().ConfigureAwait(true);
        await RefreshAsync().ConfigureAwait(true);
        OnActivated();
    }

    /// <summary>Called by the shell when another page is selected.</summary>
    public void Deactivate()
    {
        IsActive = false;
        OnDeactivated();
    }

    protected virtual void OnActivated()
    {
    }

    protected virtual void OnDeactivated()
    {
    }

    protected void SetStatus(string message)
    {
        StatusIsError = false;
        StatusMessage = message;
    }

    protected void SetError(string message)
    {
        StatusIsError = true;
        StatusMessage = message;
    }

    /// <summary>
    /// Runs UI work with busy tracking and uniform error reporting. Exceptions never
    /// escape into the dispatcher.
    /// </summary>
    protected async Task RunAsync(Func<Task> work, string? busyMessage = null)
    {
        if (IsBusy)
        {
            return;
        }

        IsBusy = true;
        if (busyMessage is not null)
        {
            SetStatus(busyMessage);
        }

        try
        {
            await work().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            SetError(ex.Message);
            Services.NotifyFailure(Loc.T("common.operationFailed", ex.Message));
            Services.Logs.Error("ui", $"{Title}: {ex.Message}", ex);
        }
        finally
        {
            IsBusy = false;
        }
    }
}
