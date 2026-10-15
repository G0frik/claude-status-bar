using System.Threading;
using System.Windows;

namespace ClaudeStatusBar;

public partial class App : Application
{
    private Mutex? _instanceLock;
    private OverlayManager? _manager;
    private UsageUpdater? _usage;

    protected override void OnStartup(StartupEventArgs e)
    {
        _instanceLock = new Mutex(initiallyOwned: true, "ClaudeStatusBar.SingleInstance", out bool isNew);
        if (!isNew) { Shutdown(); return; }

        // Keep a stray UI-thread exception from terminating the app. When CLAUDE_STATUSBAR_DEBUG=1
        // is set, the exception is appended to the hooks log for diagnostics.
        DispatcherUnhandledException += (_, args) =>
        {
            if (Environment.GetEnvironmentVariable("CLAUDE_STATUSBAR_DEBUG") == "1")
            {
                try
                {
                    var dir = System.IO.Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "statusbar");
                    System.IO.Directory.CreateDirectory(dir);
                    System.IO.File.AppendAllText(System.IO.Path.Combine(dir, "hooks.log"),
                        $"{DateTime.Now:O} [app] {args.Exception}\n");
                }
                catch { }
            }
            args.Handled = true;
        };

        base.OnStartup(e);
        _manager = new OverlayManager();
        _manager.Start();
#if OFFLINE
        _usage = new UsageUpdater();
#else
        _usage = new UsageUpdater(new UsageFetcher());
#endif
        _usage.Start();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _usage?.Dispose();
        _instanceLock?.ReleaseMutex();
        _instanceLock?.Dispose();
        base.OnExit(e);
    }
}
