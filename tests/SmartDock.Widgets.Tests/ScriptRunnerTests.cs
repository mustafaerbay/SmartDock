using SmartDock.Widgets.Productivity;

namespace SmartDock.Widgets.Tests;

public class ScriptRunnerTests
{
    [Fact]
    public void Parses_scripts_and_rejects_unsafe_entries()
    {
        var scripts = ScriptRunnerWidget.Parse(
            "Backup | D:\\scripts\\backup.ps1 | -Target \"E:\\Backups\" | 60\n" +
            "D:\\tools\\cleanup.bat\n" +
            "Remote | https://example.com/run.ps1\n" +
            "Relative | scripts\\x.ps1\n" +
            "Doc | C:\\notes\\readme.txt\n" +
            "Py | C:\\py\\check.py | | 15 min\n");

        Assert.Collection(
            scripts,
            s => Assert.Equal(("Backup", 60, "-Target \"E:\\Backups\""), (s.Name, s.EveryMinutes, s.Arguments)),
            s => Assert.Equal(("cleanup", 0), (s.Name, s.EveryMinutes)),
            s => Assert.Equal(("Py", 15), (s.Name, s.EveryMinutes)));
    }

    [Fact]
    public void Splits_arguments_with_quotes() =>
        Assert.Equal(new[] { "-Target", "E:\\My Backups", "-Verbose", "" }, ScriptRunnerWidget.SplitArguments("-Target \"E:\\My Backups\" -Verbose \"\""));

    [Fact]
    public void PowerShell_scripts_run_with_process_scoped_bypass_and_no_window()
    {
        var start = ScriptRunnerWidget.BuildStartInfo(new ScriptDefinition("x", "D:\\s\\x.ps1", "-A 1", 0));

        Assert.Equal("powershell.exe", start.FileName);
        Assert.Equal(new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", "D:\\s\\x.ps1", "-A", "1" }, start.ArgumentList);
        Assert.True(start.CreateNoWindow);
        Assert.False(start.UseShellExecute);
        Assert.True(start.RedirectStandardOutput);
    }

    [Fact]
    public void Batch_files_run_through_cmd_call() =>
        Assert.Equal(new[] { "/d", "/c", "call", "D:\\s\\y.cmd" }, ScriptRunnerWidget.BuildStartInfo(new ScriptDefinition("y", "D:\\s\\y.cmd", "", 0)).ArgumentList);

    [Fact]
    public void Explains_lines_that_would_be_ignored()
    {
        var problems = ScriptRunnerWidget.Explain(
            "Pods | kubectl get pods\n" +
            "Web | https://x/run.ps1\n" +
            "Shell | C:\\scripts\\deploy.sh\n" +
            "# comment\n" +
            "Sched | C:\\does-not-exist\\x.ps1 | | hourly\n");

        Assert.Contains(problems, p => p.StartsWith("Line 1:") && p.Contains("isn't a full file path"));
        Assert.Contains(problems, p => p.StartsWith("Line 2:") && p.Contains("URL"));
        Assert.Contains(problems, p => p.StartsWith("Line 3:") && p.Contains(".sh"));
        Assert.Contains(problems, p => p.StartsWith("Line 5:") && p.Contains("file not found"));
        Assert.Contains(problems, p => p.StartsWith("Line 5:") && p.Contains("hourly"));
    }
}
