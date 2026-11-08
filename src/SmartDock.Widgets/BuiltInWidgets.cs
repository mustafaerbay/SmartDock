using Microsoft.Extensions.DependencyInjection;
using SmartDock.Core.Widgets;
using SmartDock.Widgets.AIUsage;
using SmartDock.Widgets.Calendar;
using SmartDock.Widgets.Clock;
using SmartDock.Widgets.Common;
using SmartDock.Widgets.GitHub;
using SmartDock.Widgets.Media;
using SmartDock.Widgets.Network;
using SmartDock.Widgets.Productivity;
using SmartDock.Widgets.SystemMonitor;
using SmartDock.Widgets.Weather;
using SmartDock.Widgets.Web;
using SmartDock.Widgets.Cluster;
using SmartDock.Widgets.CodeRepo;
using SmartDock.Widgets.Bookmarks;

namespace SmartDock.Widgets;

/// <summary>The widget types compiled into SmartDock. Add new built-ins here.</summary>
public static class BuiltInWidgets
{
    public static IReadOnlyList<WidgetDescriptor> All { get; } =
    [
        ClockWidget.Definition,
        CpuWidget.Definition,
        MemoryWidget.Definition,
        DiskWidget.Definition,
        NetworkWidget.Definition,
        BatteryWidget.Definition,
        MediaWidget.Definition,
        PomodoroWidget.Definition,
        CalendarWidget.Definition,
        WeatherWidget.Definition,
        GitHubWidget.Definition,
        ClaudeUsageWidget.Definition,
        OpenAiUsageWidget.Definition,
        NotesWidget.Definition,
        QuickActionsWidget.Definition,
        VolumeWidget.Definition,
        ClipboardWidget.Definition,
        EverythingWidget.Definition,
        WebStatusWidget.Definition,
        KubernetesWidget.Definition,
        QuickFoldersWidget.Definition,
        ScriptRunnerWidget.Definition,
        CodeRepoWidget.Definition,
        BookmarksWidget.Definition,
    ];

    public static IServiceCollection AddBuiltInWidgets(this IServiceCollection services)
    {
        foreach (var descriptor in All)
        {
            services.AddSingleton(descriptor);
        }

        services.AddSingleton<IWidgetRegistry, WidgetRegistry>();

        // Shared by every network widget (connection pooling, 15 s timeout).
        services.AddSingleton(_ => HttpClients.CreateClient());
        services.AddSingleton<IWeatherProvider, OpenMeteoProvider>();
        return services;
    }
}
