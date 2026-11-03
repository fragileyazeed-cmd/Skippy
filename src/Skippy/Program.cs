using Skippy.Core;
using Skippy.UI;
using Vortice.MediaFoundation;

namespace Skippy;

static class Program
{
    [STAThread]
    static int Main(string[] args)
    {
        if (args.Length == 1 && args[0] == Updater.ProbeArg) return Updater.ProbeExitCode;   // "can Windows run this build?"
        ApplicationConfiguration.Initialize();   // per-monitor DPI aware: monitor sizes are real pixels
        Log.Init();
        AppDomain.CurrentDomain.UnhandledException += (_, e) => OnFatal(e.ExceptionObject as Exception, args);
        Application.ThreadException += (_, e) => Log.Error("UI thread exception", e.Exception);
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);

        MediaFactory.MFStartup(true).CheckError();
        try
        {
            if (args.Contains("--selftest"))
                return SelfTest.Run(args);
            if (args.Contains("--selftest-fullscreen"))
            {
                var rep = new System.Text.StringBuilder();
                int rc;
                try { rc = FullscreenTest.Run(rep); }
                catch (Exception ex) { rep.AppendLine($"RESULT: FAIL {ex}"); rc = 1; }
                File.WriteAllText(Path.Combine(Log.Dir, "selftest.txt"), rep.ToString());
                return rc;
            }
            if (args.Length > 0 && args[0] == "--soak")
                return SelfTest.Soak(args);
            if (args.Length == 3 && args[0] == "--analyze-audio")
            {
                var r = new System.Text.StringBuilder();
                SyncTest.AnalyzeAudio(args[1], double.Parse(args[2], System.Globalization.CultureInfo.InvariantCulture), r);
                File.WriteAllText(Path.Combine(Log.Dir, "selftest.txt"), r.ToString());
                return 0;
            }
            if (args.Contains("--selftest-aac") || args.Contains("--selftest-sync"))
            {
                var report = new System.Text.StringBuilder();
                int code;
                try
                {
                    int delay = SyncTest.MeasureAacDelay(report);
                    code = 0;
                    if (args.Contains("--selftest-sync"))
                    {
                        Skippy.Audio.AudioPipeline.EncoderDelayFrames = delay;
                        code = SyncTest.Run(args, report);
                    }
                }
                catch (Exception ex) { report.AppendLine($"RESULT: FAIL {ex}"); code = 1; }
                File.WriteAllText(Path.Combine(Log.Dir, "selftest.txt"), report.ToString());
                Log.Info("[selftest]\n" + report);
                return code;
            }

            if (args.Length == 2 && args[0] == "--write-icon") { AppIcon.WriteIco(args[1]); return 0; }
            if (Updater.IsApplyMode(args)) return Updater.Apply(args);   // runs from the downloaded update, before the single-instance check
            Updater.CleanUp();

            using var single = new Mutex(true, @"Local\Skippy.SingleInstance", out bool first);
            if (!first)
            {
                // already running: bring up its settings window instead (unless Windows is just autostarting us again)
                Log.Info("Another Skippy instance is already running; exiting");
                if (!args.Contains(Autostart.Arg) && EventWaitHandle.TryOpenExisting(TrayApp.ShowSettingsEventName, out var ev))
                    using (ev) ev.Set();
                return 0;
            }

            bool firstRun = !File.Exists(Path.Combine(Log.Dir, "settings.json"));
            var settings = Settings.Load();
            // WPF runs the message loop (settings window); the WinForms tray icon and hotkey window live on it too
            var app = new System.Windows.Application { ShutdownMode = System.Windows.ShutdownMode.OnExplicitShutdown };
            app.Resources.MergedDictionaries.Add(new System.Windows.ResourceDictionary
            {
                Source = new Uri("pack://application:,,,/Skippy;component/UI/Theme.xaml", UriKind.Absolute),
            });
            app.DispatcherUnhandledException += (_, e) => { Log.Error("UI exception", e.Exception); e.Handled = true; };

            // launched by hand (not at login): show the window so people see it started; at login stay in the tray
            var tray = new TrayApp(settings, showSettingsNow: firstRun || !args.Contains(Autostart.Arg));
            tray.ExitRequested += () => { tray.Dispose(); app.Shutdown(); };
            if (args.Contains(Updater.UpdatedArg)) Toast.Show($"Skippy was updated to version {Updater.Current}.", false);
            app.Run();
            return 0;
        }
        finally
        {
            MediaFactory.MFShutdown();
        }
    }

    static readonly DateTime Started = DateTime.Now;

    /// <summary>Last-chance handler: log it, tell the user, and come back up (once it has run a while, so a startup crash can't loop).</summary>
    static void OnFatal(Exception? ex, string[] args)
    {
        Log.Error("Fatal crash", ex);
        if (args.Any(a => a.StartsWith("--"))  && !args.Contains(Autostart.Arg)) return;   // dev/test modes
        try { Toast.Show($"Skippy crashed ({ex?.GetType().Name}: {ex?.Message}). It is restarting; details are in the log.", true); } catch { }
        if (DateTime.Now - Started > TimeSpan.FromMinutes(2))
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Environment.ProcessPath!, $"{Autostart.Arg} --after-crash") { UseShellExecute = false });
            }
            catch { }
        }
    }
}
