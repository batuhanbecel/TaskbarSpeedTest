using System;
using System.IO;

namespace TaskbarSpeedTest
{
    // Optional diagnostic log (%LOCALAPPDATA%\TaskbarSpeedTest\log.txt), off unless enabled from the menu.
    static class Log
    {
        public static bool Enabled;
        public static Action<string> Echo;   // the --test console mode can print everything
        static readonly object gate = new object();

        public static string FilePath
        {
            get
            {
                return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "TaskbarSpeedTest", "log.txt");
            }
        }

        public static void Write(string message)
        {
            string line = DateTime.Now.ToString("HH:mm:ss.fff") + "  " + message;
            var echo = Echo;
            if (echo != null) echo(line);
            if (!Enabled) return;
            lock (gate)
            {
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
                    var info = new FileInfo(FilePath);
                    if (info.Exists && info.Length > 1024 * 1024) info.Delete();
                    File.AppendAllText(FilePath, line + Environment.NewLine);
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
    }
}
