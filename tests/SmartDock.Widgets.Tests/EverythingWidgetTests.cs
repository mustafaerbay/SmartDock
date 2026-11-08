using SmartDock.Core.Events;
using SmartDock.Core.Interfaces;
using SmartDock.Core.Widgets;
using SmartDock.Widgets.Productivity;

namespace SmartDock.Widgets.Tests;

internal sealed class FakeFileSearch(FileSearchAvailability availability) : IFileSearchService
{
    public string ProviderName => "Fake";

    public string? ExecutableOverride { get; set; }

    public FileSearchAvailability GetAvailability() => availability;

    public Task<FileSearchResult> SearchAsync(string query, int maxResults, bool matchPath, CancellationToken cancellationToken) =>
        Task.FromResult(new FileSearchResult([new FileSearchHit("a.txt", "C:\\Temp", false)], 1));

    public bool OpenProviderWindow(string? query) => true;

    public bool StartProvider() => true;
}

public class EverythingWidgetTests
{
    [Theory]
    [InlineData(FileSearchAvailability.Ready, WidgetState.Ready, "Search files")]
    [InlineData(FileSearchAvailability.NotRunning, WidgetState.Unavailable, "Not running")]
    [InlineData(FileSearchAvailability.NotInstalled, WidgetState.Unavailable, "Not installed")]
    public async Task Shows_real_engine_state(FileSearchAvailability availability, WidgetState state, string text)
    {
        var widget = new EverythingWidget(new FakeFileSearch(availability));
        var (context, _) = Ctx.For(EverythingWidget.Definition);
        await widget.InitializeAsync(context, CancellationToken.None);
        await widget.RefreshAsync(CancellationToken.None);

        Assert.Equal(state, widget.State);
        Assert.Equal(text, widget.StatusText);
    }

    [Fact]
    public async Task Click_asks_the_app_to_open_the_search_window()
    {
        var widget = new EverythingWidget(new FakeFileSearch(FileSearchAvailability.Ready));
        var (context, _) = Ctx.For(EverythingWidget.Definition);
        OpenFileSearchRequested? request = null;
        context.Events.Subscribe<OpenFileSearchRequested>(e => request = e);
        await widget.InitializeAsync(context, CancellationToken.None);

        widget.OnClick();

        Assert.NotNull(request);
        Assert.Equal(100, request!.MaxResults);
        Assert.False(request.MatchPath);
    }

    [Fact]
    public void Full_path_joins_directory_and_name() =>
        Assert.Equal(Path.Combine("C:\\Temp", "a.txt"), new FileSearchHit("a.txt", "C:\\Temp", false).FullPath);
}
