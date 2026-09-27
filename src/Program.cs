using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace DNotes
{
    using Core;
    using Windows;

    internal static class Program
    {
        private const string MutexName = "DNotes-single-instance-mx";
        private const string SignalName = "DNotes-show-deck-sx";

        [DllImport("kernel32.dll")] private static extern bool AttachConsole(int pid);
        [DllImport("kernel32.dll")] private static extern bool AllocConsole();

        [STAThread]
        private static int Main(string[] args)
        {
            // Arm the guard before anything else can throw. DNotes is a
            // background tray app that is expected to run all day, so a stray
            // exception in a mouse or paint handler must be logged and stepped
            // over rather than tearing the process down with a JIT dialog and
            // taking the user's notes out of reach with it.
            InstallExceptionGuard();

            bool selftest = false, shot = false, demo = false;
            string shotPath = null;
            for (int i = 0; i < args.Length; i++)
            {
                string a = args[i].ToLowerInvariant();
                if (a == "--selftest" || a == "-t" || a == "/selftest") selftest = true;
                if (a == "--demo" || a == "/demo")
                {
                    demo = true;
                    if (i + 1 < args.Length && !args[i + 1].StartsWith("-"))
                        shotPath = args[++i];
                }
                if (a == "--shot" || a == "/shot") { shot = true; if (i + 1 < args.Length) shotPath = args[++i]; }
            }

            try { Native.SetProcessDPIAware(); } catch { }

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            if (demo)
            {
                string dir = shotPath;
                if (string.IsNullOrEmpty(dir))
                    dir = Path.Combine(Path.GetTempPath(), "DNotes-demo");
                bool at2 = AttachConsole(-1);
                if (!at2) { try { AllocConsole(); } catch { } }
                int dc = SelfTest.Demo(dir);
                try { Console.Out.Flush(); } catch { }
                return dc;
            }

            if (selftest)
            {
                bool attached = AttachConsole(-1);
                if (!attached) { try { AllocConsole(); } catch { } }
                int code = SelfTest.Run(shotPath);
                try { Console.Out.Flush(); } catch { }
                return code;
            }

            bool created;
            using (var mutex = new Mutex(true, MutexName, out created))
            {
                if (!created)
                {
                    SignalExisting();
                    return 0;
                }

                var app = new App(new Store());
                if (shot)
                {
                    // a bare path is treated as a folder
                    app.ScreenshotPath = Directory.Exists(shotPath) || shotPath == null
                        ? shotPath
                        : Path.GetDirectoryName(shotPath);
                }
                app.Start();
                GC.KeepAlive(mutex);
                return 0;
            }
        }

        private static void SignalExisting()
        {
            try
            {
                using (var ev = EventWaitHandle.OpenExisting(SignalName))
                    ev.Set();
            }
            catch { }
        }

        private static int _reported;

        /// <summary>
        /// Last-resort net. Anything that escapes a handler is written to the log
        /// and, the first few times, surfaced in the tray. The self-test counts
        /// these, because a silent "handled and carried on" is exactly how a
        /// broken feature hides.
        /// </summary>
        private static void InstallExceptionGuard()
        {
            try
            {
                Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            }
            catch { }

            AppDomain.CurrentDomain.UnhandledException += (s, e) =>
            {
                try { Log.Write("UNHANDLED (fatal): " + e.ExceptionObject); }
                catch { }
            };

            Application.ThreadException += (s, e) =>
            {
                SelfTest.CountBackgroundException();
                try
                {
                    Log.Write("unhandled, carried on: " + e.Exception);
                    if (_reported < 3)
                    {
                        _reported++;
                        App.ReportProblem(
                            "DNotes hit a problem and kept running.\n\n" +
                            e.Exception.Message +
                            "\n\nDetails: %APPDATA%\\DNotes\\dnotes.log");
                    }
                }
                catch { }
            };
        }

        internal static EventWaitHandle CreateSignal()
        {
            bool created;
            var ev = new EventWaitHandle(false, EventResetMode.AutoReset, SignalName, out created);
            return ev;
        }
    }
}
