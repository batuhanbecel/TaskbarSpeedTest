using System;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace TaskbarSpeedTest
{
    // "TaskbarSpeedTest.exe --test [--seconds N] [--verbose]": runs one test in the console and prints the result.
    // Exit code 0 when the test went through. Meant for bug reports and scripts.
    static class Cli
    {
        public static int Run(string[] args)
        {
            if (!AttachConsole(-1)) AllocConsole();
            Console.OutputEncoding = Encoding.UTF8;
            if (args.Contains("--verbose")) Log.Echo = line => Console.WriteLine("  " + line);
            L.Apply(Settings.Load().Language);

            var test = new SpeedTest();
            int i = Array.IndexOf(args, "--seconds"), seconds;
            if (i >= 0 && i + 1 < args.Length && int.TryParse(args[i + 1], out seconds)) test.Seconds = Math.Max(3, Math.Min(30, seconds));
            int v;
            i = Array.IndexOf(args, "--chunk");
            if (i >= 0 && i + 1 < args.Length && int.TryParse(args[i + 1], out v)) test.DownloadChunk = v;
            i = Array.IndexOf(args, "--streams");
            if (i >= 0 && i + 1 < args.Length && int.TryParse(args[i + 1], out v)) test.DownloadStreams = v;
            i = Array.IndexOf(args, "--upchunk");
            if (i >= 0 && i + 1 < args.Length && int.TryParse(args[i + 1], out v)) test.UploadChunk = v;

            Console.WriteLine();
            Console.WriteLine("TaskbarSpeedTest " + Updater.CurrentText + " · Cloudflare");
            var done = new ManualResetEvent(false);
            TestStatus last = null;
            Phase shown = Phase.Done;
            DateTime printed = DateTime.MinValue;
            test.Changed += st =>
            {
                last = st;
                if (!st.Running) { done.Set(); return; }
                if (st.Phase == Phase.Preparing || st.Progress == 0 || (st.Phase == shown && DateTime.UtcNow - printed < TimeSpan.FromSeconds(1))) return;
                shown = st.Phase;
                printed = DateTime.UtcNow;
                Console.WriteLine("  {0,-9} {1}", st.Phase, st.Phase == Phase.Ping ? L.Ms(st.Live) + " ms" : L.SpeedWithUnit(st.Live, 0));
            };
            test.Start();
            done.WaitOne();

            Result r = last.Result;
            Console.WriteLine();
            if (r.Error != null) Console.WriteLine("  " + L.T("Failed: ", "Başarısız: ") + r.Error);
            Console.WriteLine("  Ping      " + L.Ms(r.PingMs) + " ms  (jitter " + L.Ms(r.JitterMs) + " ms)");
            Console.WriteLine("  Download  " + L.SpeedWithUnit(r.DownMbps, 0));
            Console.WriteLine("  Upload    " + L.SpeedWithUnit(r.UpMbps, 0));
            Console.WriteLine("  Server    Cloudflare " + r.Server);
            Console.WriteLine("  Network   " + string.Join(" · ", new[] { r.Link, r.Isp }.Where(s => !string.IsNullOrEmpty(s))));
            Console.WriteLine();
            return r.Complete ? 0 : 1;
        }

        [DllImport("kernel32.dll")] static extern bool AttachConsole(int pid);
        [DllImport("kernel32.dll")] static extern bool AllocConsole();
    }
}
