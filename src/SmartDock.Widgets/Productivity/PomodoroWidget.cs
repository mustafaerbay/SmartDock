using CommunityToolkit.Mvvm.Input;
using SmartDock.Core.Events;
using SmartDock.Core.Models;
using SmartDock.Core.Services;
using SmartDock.Core.Widgets;

namespace SmartDock.Widgets.Productivity;

public enum PomodoroPhase
{
    Focus,
    Break,
}

/// <summary>Pure, clock-driven Pomodoro state machine (no timers inside, so it is deterministic to test).</summary>
public sealed class PomodoroTimer
{
    private DateTimeOffset? _runningSince;
    private TimeSpan _remainingAtStart;

    public PomodoroTimer(TimeSpan focus, TimeSpan breakLength)
    {
        Focus = focus;
        Break = breakLength;
        _remainingAtStart = focus;
    }

    public TimeSpan Focus { get; private set; }

    public TimeSpan Break { get; private set; }

    public PomodoroPhase Phase { get; private set; } = PomodoroPhase.Focus;

    public bool IsRunning => _runningSince is not null;

    public TimeSpan PhaseLength => Phase == PomodoroPhase.Focus ? Focus : Break;

    public TimeSpan Remaining(DateTimeOffset now)
    {
        if (_runningSince is not { } since)
        {
            return _remainingAtStart;
        }

        var left = _remainingAtStart - (now - since);
        return left < TimeSpan.Zero ? TimeSpan.Zero : left;
    }

    public void Start(DateTimeOffset now)
    {
        _runningSince ??= now;
    }

    public void Pause(DateTimeOffset now)
    {
        if (_runningSince is null)
        {
            return;
        }

        _remainingAtStart = Remaining(now);
        _runningSince = null;
    }

    public void Reset()
    {
        _runningSince = null;
        Phase = PomodoroPhase.Focus;
        _remainingAtStart = Focus;
    }

    public void Configure(TimeSpan focus, TimeSpan breakLength)
    {
        Focus = focus;
        Break = breakLength;
        if (!IsRunning && _remainingAtStart > PhaseLength)
        {
            _remainingAtStart = PhaseLength;
        }
    }

    /// <summary>Advances the machine; returns the phase that just completed, if any. The next phase starts paused.</summary>
    public PomodoroPhase? Tick(DateTimeOffset now)
    {
        if (!IsRunning || Remaining(now) > TimeSpan.Zero)
        {
            return null;
        }

        var completed = Phase;
        Phase = Phase == PomodoroPhase.Focus ? PomodoroPhase.Break : PomodoroPhase.Focus;
        _runningSince = null;
        _remainingAtStart = PhaseLength;
        return completed;
    }
}

public sealed class PomodoroWidget : WidgetBase
{
    public const string TypeId = "pomodoro";

    private readonly TimeProvider _time;
    private readonly PomodoroTimer _timer = new(TimeSpan.FromMinutes(25), TimeSpan.FromMinutes(5));
    private readonly Lock _gate = new();
    private string _remainingText = "25:00";
    private double _progress;

    public PomodoroWidget()
        : this(TimeProvider.System)
    {
    }

    public PomodoroWidget(TimeProvider time)
    {
        _time = time;
        StartPauseCommand = new RelayCommand(StartPause);
        ResetCommand = new RelayCommand(Reset);
    }

    public static WidgetDescriptor Definition { get; } = new(
        TypeId,
        "Pomodoro",
        "Focus/break timer with a notification when each phase ends.",
        WidgetCategory.Productivity,
        "",
        DockRegion.Center,
        _ => new PomodoroWidget());

    public RelayCommand StartPauseCommand { get; }

    public RelayCommand ResetCommand { get; }

    public string RemainingText
    {
        get => _remainingText;
        private set => SetProperty(ref _remainingText, value);
    }

    /// <summary>0..1 elapsed fraction of the current phase.</summary>
    public double Progress
    {
        get => _progress;
        private set => SetProperty(ref _progress, value);
    }

    public bool IsRunning => _timer.IsRunning;

    public string PhaseText => _timer.Phase == PomodoroPhase.Focus ? "Focus" : "Break";

    public int FocusMinutes => Math.Clamp(Settings.Get("focusMinutes", 25), 1, 180);

    public int BreakMinutes => Math.Clamp(Settings.Get("breakMinutes", 5), 1, 60);

    public override TimeSpan RefreshInterval => TimeSpan.FromSeconds(1);

    public override IReadOnlyList<WidgetMenuItem> MenuItems =>
    [
        new("Focus 25 min / break 5", new RelayCommand(() => SetLengths(25, 5)), FocusMinutes == 25 && BreakMinutes == 5, true),
        new("Focus 50 min / break 10", new RelayCommand(() => SetLengths(50, 10)), FocusMinutes == 50 && BreakMinutes == 10, true),
        new("Focus 15 min / break 3", new RelayCommand(() => SetLengths(15, 3)), FocusMinutes == 15 && BreakMinutes == 3, true),
    ];

    protected override Task OnInitializeAsync(CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            _timer.Configure(TimeSpan.FromMinutes(FocusMinutes), TimeSpan.FromMinutes(BreakMinutes));
            _timer.Reset();
        }

        Render();
        return Task.CompletedTask;
    }

    protected override Task OnRefreshAsync(CancellationToken cancellationToken)
    {
        PomodoroPhase? completed;
        lock (_gate)
        {
            completed = _timer.Tick(_time.GetUtcNow());
        }

        if (completed is { } phase)
        {
            var title = phase == PomodoroPhase.Focus ? "Focus session complete" : "Break is over";
            var message = phase == PomodoroPhase.Focus
                ? $"Time for a {BreakMinutes}-minute break."
                : "Ready for the next focus session?";
            Context.Notifications.Post(title, message, NotificationSeverity.Success, InstanceId);
            Context.Events.Publish(new PomodoroPhaseCompleted(InstanceId, phase.ToString()));
        }

        Render();
        return Task.CompletedTask;
    }

    protected override void OnSettingChanged(string key)
    {
        lock (_gate)
        {
            _timer.Configure(TimeSpan.FromMinutes(FocusMinutes), TimeSpan.FromMinutes(BreakMinutes));
            if (!_timer.IsRunning)
            {
                _timer.Reset();
            }
        }

        Render();
        base.OnSettingChanged(key);
    }

    private void SetLengths(int focus, int breakLength)
    {
        Settings.Set("focusMinutes", focus);
        Settings.Set("breakMinutes", breakLength);
    }

    private void StartPause()
    {
        lock (_gate)
        {
            var now = _time.GetUtcNow();
            if (_timer.IsRunning)
            {
                _timer.Pause(now);
            }
            else
            {
                _timer.Start(now);
            }
        }

        Render();
    }

    private void Reset()
    {
        lock (_gate)
        {
            _timer.Reset();
        }

        Render();
    }

    private void Render()
    {
        TimeSpan remaining;
        TimeSpan length;
        lock (_gate)
        {
            remaining = _timer.Remaining(_time.GetUtcNow());
            length = _timer.PhaseLength;
        }

        RemainingText = $"{(int)remaining.TotalMinutes:D2}:{remaining.Seconds:D2}";
        Progress = length <= TimeSpan.Zero ? 0 : 1 - (remaining.TotalSeconds / length.TotalSeconds);
        OnPropertyChanged(nameof(IsRunning));
        OnPropertyChanged(nameof(PhaseText));
    }
}
