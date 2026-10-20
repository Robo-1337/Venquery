using System;
using System.Windows;

namespace Venquery;

// A single, shared progress surface for every long-running task in the app. Drives the bottom
// GlobalProgressPanel band (text + percent + bar). Callers do:
//     using var t = BeginTask("Scanning hardware…");          // animated "working" bar
//     using var t = BeginTask("Backing up…", determinate:true); t.Report(pct, "…");   // real % bar
// and the band shows while the task runs and hides when the handle is disposed. Honest by design:
// determinate=false shows an indeterminate (animated) bar for tasks with no measurable total; only
// pass determinate:true when a real 0..100 total exists (downloads, file copies, X-of-N jobs).
public partial class MainWindow
{
    private int _progressToken;         // monotonic id per BeginTask
    private int _activeProgressToken;   // the token that currently owns the band (0 = none)

    /// <summary>Show the global progress band for a task and return a handle to drive it. Must be called
    /// on the UI thread (as all the app's async handlers are). Dispose/Done the handle to hide the band.</summary>
    public TaskScope BeginTask(string title, bool determinate = false)
    {
        var token = ++_progressToken;
        _activeProgressToken = token;
        GlobalProgressText.Text = title ?? "";
        GlobalProgressBar.IsIndeterminate = !determinate;
        GlobalProgressBar.Value = 0;
        GlobalProgressPercent.Text = determinate ? "0%" : "";
        GlobalProgressPanel.Visibility = Visibility.Visible;
        return new TaskScope(this, token, determinate);
    }

    /// <summary>An <see cref="IProgress{String}"/> that fans a service's log lines to BOTH the scan console
    /// ("View Log") and the active task band's caption, so existing services need no signature change.</summary>
    private IProgress<string> ConsoleAndTask(TaskScope scope)
    {
        var console = MakeConsoleLog();
        return new Progress<string>(line =>
        {
            console.Report(line);
            scope.Report(line);
        });
    }

    // ---- band updates (only applied if the caller still owns the band) ----

    private void ApplyReport(int token, double pct, string? text)
    {
        if (token != _activeProgressToken) return;
        GlobalProgressBar.IsIndeterminate = false;
        var v = pct < 0 ? 0 : pct > 100 ? 100 : pct;
        GlobalProgressBar.Value = v;
        GlobalProgressPercent.Text = $"{v:0}%";
        if (text is not null) GlobalProgressText.Text = text;
    }

    private void ApplyReportText(int token, string text)
    {
        if (token != _activeProgressToken || text is null) return;
        GlobalProgressText.Text = text;
    }

    private void ApplyEnd(int token, string? finalStatus)
    {
        if (token != _activeProgressToken) return;   // a newer task already owns the band — leave it be
        _activeProgressToken = 0;
        GlobalProgressBar.IsIndeterminate = false;
        GlobalProgressPanel.Visibility = Visibility.Collapsed;
        if (finalStatus is not null) StatusLine.Text = finalStatus;
    }

    /// <summary>A handle to a running task that drives the global progress band. All updates marshal to the
    /// UI thread, so a background <c>Progress&lt;T&gt;</c> callback or a direct call are both safe.</summary>
    public sealed class TaskScope : IDisposable
    {
        private readonly MainWindow _w;
        private readonly int _token;
        private bool _ended;

        /// <summary>True if this task was started with a real percentage (determinate:true).</summary>
        public bool Determinate { get; }

        internal TaskScope(MainWindow w, int token, bool determinate)
        {
            _w = w; _token = token; Determinate = determinate;
        }

        /// <summary>Report determinate progress (0..100) and optionally update the caption.</summary>
        public void Report(double pct, string? text = null) => Ui(() => _w.ApplyReport(_token, pct, text));

        /// <summary>Update the caption text only (leaves the bar mode unchanged — used for the animated case).</summary>
        public void Report(string text) => Ui(() => _w.ApplyReportText(_token, text));

        /// <summary>Hide the band (if this task still owns it) and optionally leave a final status message.</summary>
        public void Done(string? finalStatus = null)
        {
            if (_ended) return;
            _ended = true;
            Ui(() => _w.ApplyEnd(_token, finalStatus));
        }

        public void Dispose() => Done();

        private void Ui(Action a)
        {
            if (_w.Dispatcher.CheckAccess()) a();
            else _w.Dispatcher.Invoke(a);
        }
    }
}
