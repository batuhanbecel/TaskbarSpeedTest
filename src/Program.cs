using System;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace TaskbarSpeedTest
{
    static class Program
    {
        // --dev: runs next to the installed copy for testing (own settings, no updates).
        public static bool Dev { get; private set; }

        [STAThread]
        static int Main(string[] args)
        {
            // --test: run one speed test in the console (for bug reports and scripts).
            if (args.Contains("--test")) return Cli.Run(args);

            Dev = args.Contains("--dev");
            if (Dev) Settings.Key = @"Software\TaskbarSpeedTest.Dev";
            // --light / --dark: force a theme (for screenshots of both).
            if (args.Contains("--light")) Win11Menu.ThemeOverride = true;
            if (args.Contains("--dark")) Win11Menu.ThemeOverride = false;

            bool owned;
            using (var mutex = new Mutex(true, Dev ? "TaskbarSpeedTest_Dev" : "TaskbarSpeedTest_SingleInstance", out owned))
            {
                // --run: start a test right away (in the copy that is already running, if any).
                bool run = args.Contains("--run");
                if (!owned)
                {
                    // Already running: started again from the Start menu, so open the flyout of that copy.
                    if (!args.Contains(Autostart.Arg))
                    {
                        IntPtr other = FindWindow(null, App.WindowTitle);
                        if (other != IntPtr.Zero) PostMessage(other, run ? App.RunTestMessage : App.ShowFlyoutMessage, IntPtr.Zero, IntPtr.Zero);
                    }
                    return 0;
                }
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.Run(new App(run));
                mutex.ReleaseMutex();
            }
            return 0;
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr FindWindow(string cls, string name);
        [DllImport("user32.dll")] static extern bool PostMessage(IntPtr hwnd, int msg, IntPtr w, IntPtr l);
    }
}
