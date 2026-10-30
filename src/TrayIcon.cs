using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace TaskbarSpeedTest
{
    // A notification-area icon on Shell_NotifyIcon directly (NotifyIcon caps tooltips at 63 characters,
    // has no large-icon notifications and does not report where the icon is).
    class TrayIcon : IDisposable
    {
        public const int CallbackMessage = 0x8000 + 0x42;   // WM_APP + n

        public event Action<Point> Click;          // left click / keyboard select; anchor point
        public event Action<Point> ContextMenu;
        public event Action BalloonClicked;

        readonly IntPtr hwnd;
        readonly uint id;
        Icon icon;
        string tip = "";
        bool added;

        public TrayIcon(IntPtr hwnd, uint id)
        {
            this.hwnd = hwnd;
            this.id = id;
        }

        public uint Id { get { return id; } }

        public void Update(Icon newIcon, string newTip)
        {
            Icon old = icon;
            icon = newIcon;
            tip = newTip ?? "";
            if (tip.Length > 127) tip = tip.Substring(0, 126) + "…";
            var d = Data(NIF_MESSAGE | NIF_ICON | NIF_TIP | NIF_SHOWTIP);
            if (!added)
            {
                added = Shell_NotifyIcon(NIM_ADD, ref d);
                if (added)
                {
                    d.uVersion = NOTIFYICON_VERSION_4;
                    Shell_NotifyIcon(NIM_SETVERSION, ref d);
                }
            }
            else if (!Shell_NotifyIcon(NIM_MODIFY, ref d))
            {
                added = false;   // Explorer restarted behind our back
            }
            if (old != null && old != icon) old.Dispose();
        }

        // Explorer restarted (TaskbarCreated): the icon has to be added again.
        public void Recreate()
        {
            added = false;
            if (icon != null) Update(icon, tip);
        }

        // Shown by Windows as a toast. largeIcon may be null (a stock icon is used then); the tray icon takes
        // ownership of it, since the shell reads it after Shell_NotifyIcon returns.
        public void Notify(string title, string text, Icon largeIcon, bool warning)
        {
            if (!added)
            {
                if (largeIcon != null) largeIcon.Dispose();
                return;
            }
            var d = Data(NIF_INFO);
            d.szInfoTitle = Trim(title, 63);
            d.szInfo = Trim(text, 255);
            d.dwInfoFlags = NIIF_RESPECT_QUIET_TIME;
            if (largeIcon != null)
            {
                d.dwInfoFlags |= NIIF_USER | NIIF_LARGE_ICON;
                d.hBalloonIcon = largeIcon.Handle;
            }
            else d.dwInfoFlags |= warning ? NIIF_WARNING : NIIF_INFO;
            Shell_NotifyIcon(NIM_MODIFY, ref d);
            if (balloonIcon != null) balloonIcon.Dispose();
            balloonIcon = largeIcon;
        }

        Icon balloonIcon;

        // Screen rectangle of the icon (or of the overflow button when it is hidden there).
        public Rectangle Bounds
        {
            get
            {
                var nid = new NOTIFYICONIDENTIFIER { cbSize = Marshal.SizeOf(typeof(NOTIFYICONIDENTIFIER)), hWnd = hwnd, uID = id };
                RECT r;
                if (Shell_NotifyIconGetRect(ref nid, out r) != 0) return Rectangle.Empty;
                return Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom);
            }
        }

        // Called by the owning window for CallbackMessage. lParam: low word = event, high word = icon id.
        public bool HandleMessage(Message m)
        {
            if ((uint)(m.LParam.ToInt64() >> 16 & 0xFFFF) != id) return false;
            int evt = (int)(m.LParam.ToInt64() & 0xFFFF);
            long w = m.WParam.ToInt64();
            var anchor = new Point((short)(w & 0xFFFF), (short)(w >> 16 & 0xFFFF));
            switch (evt)
            {
                case NIN_SELECT:
                case NIN_KEYSELECT:
                    if (Click != null) Click(anchor);
                    return true;
                case WM_CONTEXTMENU:
                    if (ContextMenu != null) ContextMenu(anchor);
                    return true;
                case NIN_BALLOONUSERCLICK:
                    if (BalloonClicked != null) BalloonClicked();
                    return true;
            }
            return false;
        }

        public void Dispose()
        {
            if (added)
            {
                var d = Data(0);
                Shell_NotifyIcon(NIM_DELETE, ref d);
                added = false;
            }
            if (icon != null) icon.Dispose();
            icon = null;
            if (balloonIcon != null) balloonIcon.Dispose();
            balloonIcon = null;
        }

        NOTIFYICONDATA Data(int flags)
        {
            return new NOTIFYICONDATA
            {
                cbSize = Marshal.SizeOf(typeof(NOTIFYICONDATA)),
                hWnd = hwnd,
                uID = id,
                uFlags = flags,
                uCallbackMessage = CallbackMessage,
                hIcon = icon != null ? icon.Handle : IntPtr.Zero,
                szTip = tip,
                szInfo = "",
                szInfoTitle = ""
            };
        }

        static string Trim(string s, int max)
        {
            s = s ?? "";
            return s.Length > max ? s.Substring(0, max) : s;
        }

        // --- native ---

        const int NIM_ADD = 0, NIM_MODIFY = 1, NIM_DELETE = 2, NIM_SETVERSION = 4;
        const int NIF_MESSAGE = 1, NIF_ICON = 2, NIF_TIP = 4, NIF_INFO = 0x10, NIF_SHOWTIP = 0x80;
        const int NIIF_INFO = 1, NIIF_WARNING = 2, NIIF_USER = 4, NIIF_LARGE_ICON = 0x20, NIIF_RESPECT_QUIET_TIME = 0x80;
        const int NOTIFYICON_VERSION_4 = 4;
        const int NIN_SELECT = 0x400, NIN_KEYSELECT = 0x401, NIN_BALLOONUSERCLICK = 0x405, WM_CONTEXTMENU = 0x7B;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct NOTIFYICONDATA
        {
            public int cbSize;
            public IntPtr hWnd;
            public uint uID;
            public int uFlags;
            public int uCallbackMessage;
            public IntPtr hIcon;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
            public int dwState, dwStateMask;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
            public int uVersion;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
            public int dwInfoFlags;
            public Guid guidItem;
            public IntPtr hBalloonIcon;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct NOTIFYICONIDENTIFIER { public int cbSize; public IntPtr hWnd; public uint uID; public Guid guidItem; }

        [StructLayout(LayoutKind.Sequential)]
        struct RECT { public int Left, Top, Right, Bottom; }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)] static extern bool Shell_NotifyIcon(int message, ref NOTIFYICONDATA data);
        [DllImport("shell32.dll")] static extern int Shell_NotifyIconGetRect(ref NOTIFYICONIDENTIFIER id, out RECT rect);
    }
}
