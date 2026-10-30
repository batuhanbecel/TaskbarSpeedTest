using System.Windows.Forms;
using Microsoft.Win32;

namespace TaskbarSpeedTest
{
    // "Start with Windows": a per-user Run entry (no administrator rights needed).
    static class Autostart
    {
        public const string Arg = "--autostart";
        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        static string Name { get { return Program.Dev ? "TaskbarSpeedTest.Dev" : "TaskbarSpeedTest"; } }

        static string Command { get { return "\"" + Application.ExecutablePath + "\" " + Arg + (Program.Dev ? " --dev" : ""); } }

        public static bool IsEnabled()
        {
            using (RegistryKey k = Registry.CurrentUser.OpenSubKey(RunKey))
                return k != null && k.GetValue(Name) != null;
        }

        public static void Set(bool enable)
        {
            using (RegistryKey k = Registry.CurrentUser.CreateSubKey(RunKey))
            {
                if (enable) k.SetValue(Name, Command);
                else k.DeleteValue(Name, false);
            }
        }

        // The exe moved (portable copy, new install folder): point the entry at this copy.
        public static void Repair()
        {
            using (RegistryKey k = Registry.CurrentUser.OpenSubKey(RunKey, true))
            {
                if (k == null) return;
                string v = k.GetValue(Name) as string;
                if (v != null && v != Command) k.SetValue(Name, Command);
            }
        }
    }
}
