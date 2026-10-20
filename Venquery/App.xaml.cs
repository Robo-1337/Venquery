using System.Windows;

namespace Venquery;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
#if PROTOOLS
        // Pro Tools relaunches this exe elevated as a headless "health probe" to read
        // admin-only hardware data. In that mode we do the read, write the JSON, and exit WITHOUT
        // creating a window (we return before base.OnStartup, which is what honours StartupUri).
        if (Services.ProToolsService.TryRunProbe(e.Args))
        {
            Shutdown(0);
            return;
        }
#endif
        // Headless CLI (--version/--help everywhere; --health/--report/--diag/--fix where CLI is built). Runs and exits
        // without a window so Venquery works on Server Core, where WPF can't launch.
        if (Services.Cli.TryHandle(e.Args))
        {
            Shutdown(0);
            return;
        }

        // Last-resort safety net for EVERY edition: the app must NOT die on a transient hiccup (a locked
        // file, a WMI blip, a background timeout). Individual actions already handle their own errors;
        // this catches anything that still escapes to the UI thread, surfaces it, and keeps the app alive
        // instead of terminating. (Every build has async void
        // handlers, so a keep-alive net belongs everywhere.)
        // Single-flight: show at most ONE notice at a time and never repeat the same message back to back. A
        // repeating fault (a per-item binding error, a poller ticking every few seconds) would otherwise
        // stack a fresh modal box per exception and bury the app dozens deep. Handled is set first,
        // unconditionally, so the app stays alive whether or not we surface the notice.
        var noticeOpen = false;
        string? lastMsg = null;
        // Background-thread failures are recorded too (the process may still end for an unhandled one, but the
        // crash log tells us why); an unobserved faulted Task is recorded and marked observed.
        AppDomain.CurrentDomain.UnhandledException += (_, a) =>
        {
            if (a.ExceptionObject is Exception ex) Services.AppLog.WriteCrash(ex, a.IsTerminating ? "background (app closing)" : "background");
        };
        System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (_, a) =>
        {
            Services.AppLog.WriteCrash(a.Exception, "unobserved task");
            a.SetObserved();
        };

        DispatcherUnhandledException += (_, args) =>
        {
            args.Handled = true;
            // Always record it (with the stack trace) BEFORE deciding whether to show a notice.
            Services.AppLog.WriteCrash(args.Exception, "UI");
            var msg = args.Exception.Message;
            if (noticeOpen || msg == lastMsg) return;
            noticeOpen = true;
            lastMsg = msg;
            try
            {
                MessageBox.Show(
                    "That action hit a problem, but Venquery has kept running.\n\n" + msg +
                    "\n\nThe details were saved to the log (View Log > Open logs folder).",
                    Edition.Current.DisplayName, MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            catch { /* never let the handler itself throw */ }
            finally { noticeOpen = false; }
        };

        base.OnStartup(e);
    }
}
