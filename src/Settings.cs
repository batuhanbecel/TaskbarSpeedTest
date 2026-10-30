using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using Microsoft.Win32;

namespace TaskbarSpeedTest
{
    // User preferences, persisted under HKCU\Software\TaskbarSpeedTest (one value per public field:
    // bools and ints as DWORDs, strings as REG_SZ).
    class Settings
    {
        public static string Key = @"Software\TaskbarSpeedTest";
        public const int HistoryLength = 20;

        public bool ShowWidget = true;      // results inside the taskbar
        public bool ShowTrayIcon = false;   // the notification-area icon as well (always shown without the widget)
        public bool WidgetDownload = true;
        public bool WidgetUpload = true;
        public bool WidgetPing = false;
        public bool WidgetUnits = true;
        public bool TwoLines = true;        // stacked like the clock
        public bool CheckUpdates = true;
        public bool AutoInstallUpdates = true;
        public bool DebugLog = false;

        public int Unit = 0;                // 0 = Mbps, 1 = MB/s
        public int Seconds = 10;            // per direction
        public int AutoTestHours = 0;       // 0 = off
        public int WidgetAnchor = 0;        // 0 = next to the tray, 1 = left edge of the taskbar
        public int WidgetOffset = 8;        // gap to the anchor edge, physical px
        public int Language = 0;            // 0 = Windows language, 1 = Turkish, 2 = English

        public string History = "";         // recent results, newest first, separated by ';' (Result.Serialize)

        public List<Result> LoadHistory()
        {
            return History.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(Result.Parse).Where(r => r != null).Take(HistoryLength).ToList();
        }

        public void SaveHistory(List<Result> list)
        {
            History = string.Join(";", list.Take(HistoryLength).Select(r => r.Serialize()));
        }

        public static Settings Load()
        {
            var s = new Settings();
            using (RegistryKey k = Registry.CurrentUser.OpenSubKey(Key))
            {
                if (k == null) return s;
                foreach (FieldInfo f in Fields())
                {
                    object v = k.GetValue(f.Name);
                    if (f.FieldType == typeof(string))
                    {
                        if (v is string) f.SetValue(s, v);
                    }
                    else if (v is int)
                    {
                        if (f.FieldType == typeof(bool)) f.SetValue(s, (int)v != 0);
                        else f.SetValue(s, (int)v);
                    }
                }
            }
            return s;
        }

        public void Save()
        {
            using (RegistryKey k = Registry.CurrentUser.CreateSubKey(Key))
            {
                foreach (FieldInfo f in Fields())
                {
                    object v = f.GetValue(this);
                    if (v is string) k.SetValue(f.Name, v, RegistryValueKind.String);
                    else k.SetValue(f.Name, v is bool ? ((bool)v ? 1 : 0) : (int)v, RegistryValueKind.DWord);
                }
            }
        }

        static FieldInfo[] Fields()
        {
            return typeof(Settings).GetFields(BindingFlags.Public | BindingFlags.Instance);
        }
    }

    // UI language: 0 = follow Windows (Turkish on Turkish systems, English elsewhere), 1 = Turkish, 2 = English.
    // Also formats speeds, latencies and times in that language.
    static class L
    {
        static readonly bool SystemTurkish = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "tr";
        static readonly CultureInfo TurkishCulture = new CultureInfo("tr-TR");
        static readonly CultureInfo EnglishCulture = new CultureInfo("en-US");
        static bool turkish = SystemTurkish;

        public static bool Turkish { get { return turkish; } }
        public static CultureInfo Culture { get { return turkish ? TurkishCulture : EnglishCulture; } }

        public static void Apply(int language)
        {
            turkish = language == 1 || (language == 0 && SystemTurkish);
        }

        public static string T(string english, string turkishText)
        {
            return turkish ? turkishText : english;
        }

        public static string UnitName(int unit)
        {
            return unit == 1 ? "MB/s" : "Mbps";
        }

        // "8.4", "245", "1250"; one decimal below 10. mbps < 0: not measured.
        public static string Speed(double mbps, int unit)
        {
            if (mbps < 0) return "–";
            double v = unit == 1 ? mbps / 8 : mbps;
            return v.ToString(v < 9.95 ? "0.0" : "0", Culture);
        }

        public static string SpeedWithUnit(double mbps, int unit)
        {
            return Speed(mbps, unit) + " " + UnitName(unit);
        }

        public static string Ms(double ms)
        {
            if (ms < 0) return "–";
            return ms.ToString(ms < 9.95 ? "0.0" : "0", Culture);
        }

        public static string Ago(DateTime utc)
        {
            if (utc == DateTime.MinValue) return "";
            TimeSpan t = DateTime.UtcNow - utc;
            if (t.TotalMinutes < 1) return T("just now", "az önce");
            if (t.TotalMinutes < 60) return T((int)t.TotalMinutes + " min ago", (int)t.TotalMinutes + " dk önce");
            if (t.TotalHours < 24) return T((int)t.TotalHours + " h ago", (int)t.TotalHours + " sa önce");
            if (t.TotalDays < 2) return T("yesterday", "dün");
            return T((int)t.TotalDays + " days ago", (int)t.TotalDays + " gün önce");
        }

        // "14:32", "Yesterday 14:32", "28 Oct 14:32".
        public static string When(DateTime utc)
        {
            DateTime local = utc.ToLocalTime();
            string time = local.ToString("HH:mm", Culture);
            if (local.Date == DateTime.Today) return time;
            if (local.Date == DateTime.Today.AddDays(-1)) return T("Yesterday ", "Dün ") + time;
            return local.ToString("d MMM", Culture) + " " + time;
        }
    }
}
