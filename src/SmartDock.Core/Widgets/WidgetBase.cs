using CommunityToolkit.Mvvm.ComponentModel;

namespace SmartDock.Core.Widgets;

/// <summary>
/// Convenience base class: observable state, lifecycle plumbing and settings access.
/// Derived classes implement <see cref="OnRefreshAsync"/> and expose bindable properties.
/// Simple property change notifications may be raised from background threads (WPF marshals them);
/// collections must not be mutated off the UI thread — expose immutable snapshots instead.
/// </summary>
public abstract class WidgetBase : ObservableObject, IDockWidget
{
    // Widgets are created on the UI thread; capturing its context lets them marshal work that WPF
    // requires on that thread (e.g. ICommand.CanExecuteChanged). Null in tests => run inline.
    private readonly SynchronizationContext? _uiContext = SynchronizationContext.Current;
    private WidgetContext? _context;
    private WidgetState _state = WidgetState.Loading;
    private string? _statusMessage;
    private DateTimeOffset? _lastUpdated;
    private double? _width;

    public string InstanceId => Context.InstanceId;

    public WidgetDescriptor Descriptor => Context.Descriptor;

    public WidgetState State
    {
        get => _state;
        protected set
        {
            if (SetProperty(ref _state, value))
            {
                OnPropertyChanged(nameof(HasProblem));
            }
        }
    }

    /// <summary>True for Error/Disconnected states; used by templates to dim content and show a badge.</summary>
    public bool HasProblem => _state is WidgetState.Error or WidgetState.Disconnected;

    public string? StatusMessage
    {
        get => _statusMessage;
        protected set => SetProperty(ref _statusMessage, value);
    }

    public DateTimeOffset? LastUpdated
    {
        get => _lastUpdated;
        protected set => SetProperty(ref _lastUpdated, value);
    }

    public double? Width
    {
        get => _width;
        set => SetProperty(ref _width, value);
    }

    public virtual TimeSpan RefreshInterval => TimeSpan.FromSeconds(5);

    public virtual IReadOnlyList<WidgetMenuItem> MenuItems => [];

    protected WidgetContext Context =>
        _context ?? throw new InvalidOperationException($"{GetType().Name} used before InitializeAsync.");

    protected WidgetSettings Settings => Context.Settings;

    public async Task InitializeAsync(WidgetContext context, CancellationToken cancellationToken)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        context.Settings.Changed += OnSettingsChanged;
        await OnInitializeAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task RefreshAsync(CancellationToken cancellationToken)
    {
        await OnRefreshAsync(cancellationToken).ConfigureAwait(false);
        LastUpdated = DateTimeOffset.Now;
        if (State is WidgetState.Loading or WidgetState.Error)
        {
            // A refresh that completes without the widget choosing a state means the data is good.
            State = WidgetState.Ready;
            StatusMessage = null;
        }
    }

    public void ReportFailure(string message)
    {
        State = WidgetState.Error;
        StatusMessage = message;
    }

    public async ValueTask DisposeAsync()
    {
        if (_context is not null)
        {
            _context.Settings.Changed -= OnSettingsChanged;
        }

        await OnDisposeAsync().ConfigureAwait(false);
        GC.SuppressFinalize(this);
    }

    protected virtual Task OnInitializeAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    protected abstract Task OnRefreshAsync(CancellationToken cancellationToken);

    protected virtual ValueTask OnDisposeAsync() => ValueTask.CompletedTask;

    /// <summary>Called after any setting of this instance changes. Default: request an immediate refresh.</summary>
    protected virtual void OnSettingChanged(string key)
    {
        OnPropertyChanged(nameof(MenuItems));
        Context.RequestRefresh?.Invoke();
    }

    /// <summary>Runs <paramref name="action"/> on the thread that created the widget (inline when already there).</summary>
    protected void PostToUi(Action action)
    {
        if (_uiContext is null || SynchronizationContext.Current == _uiContext)
        {
            action();
            return;
        }

        _uiContext.Post(static state => ((Action)state!)(), action);
    }

    protected void SetReady()
    {
        State = WidgetState.Ready;
        StatusMessage = null;
    }

    protected void SetUnavailable(string message)
    {
        State = WidgetState.Unavailable;
        StatusMessage = message;
    }

    protected void SetDisconnected(string message)
    {
        State = WidgetState.Disconnected;
        StatusMessage = message;
    }

    private void OnSettingsChanged(object? sender, string key) => OnSettingChanged(key);
}
