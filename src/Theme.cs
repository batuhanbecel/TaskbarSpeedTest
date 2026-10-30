using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace TaskbarSpeedTest
{
    // Colours and fonts shared by the taskbar text, the flyout and the tray icon (Windows 11 Fluent values).
    static class Theme
    {
        public static readonly string GlyphFont = FontExists("Segoe Fluent Icons") ? "Segoe Fluent Icons" : "Segoe MDL2 Assets";
        public static readonly string TextFont = FontExists("Segoe UI Variable Text") ? "Segoe UI Variable Text" : "Segoe UI";
        public static readonly string DisplayFont = FontExists("Segoe UI Variable Display") ? "Segoe UI Variable Display" : "Segoe UI";
        // GDI cuts family names at 31 characters: the semibold display face is known as "...Display Semib".
        static readonly string DisplaySemibold = Resolve("Segoe UI Variable Display Semibold");
        static readonly string TextSemibold = Resolve("Segoe UI Variable Text Semibold");

        // The taskbar (and its flyouts) follow the system theme, not the app theme.
        public static bool Light { get { return Win11Menu.IsLightTheme(); } }

        public static Color Background { get { return Light ? Color.FromArgb(0xF3, 0xF3, 0xF3) : Color.FromArgb(0x20, 0x20, 0x20); } }
        public static Color Card { get { return Light ? Color.FromArgb(0xFB, 0xFB, 0xFB) : Color.FromArgb(0x2B, 0x2B, 0x2B); } }
        public static Color CardBorder { get { return Light ? Color.FromArgb(0xE5, 0xE5, 0xE5) : Color.FromArgb(0x1D, 0x1D, 0x1D); } }
        public static Color Footer { get { return Light ? Color.FromArgb(0xEE, 0xEE, 0xEE) : Color.FromArgb(0x1C, 0x1C, 0x1C); } }
        public static Color Divider { get { return Light ? Color.FromArgb(0xE0, 0xE0, 0xE0) : Color.FromArgb(0x30, 0x30, 0x30); } }
        public static Color Text { get { return Light ? Color.FromArgb(0x1A, 0x1A, 0x1A) : Color.White; } }
        public static Color SecondaryText { get { return Light ? Color.FromArgb(0x5F, 0x5F, 0x5F) : Color.FromArgb(0xC5, 0xC5, 0xC5); } }
        public static Color TertiaryText { get { return Light ? Color.FromArgb(0x8A, 0x8A, 0x8A) : Color.FromArgb(0x87, 0x87, 0x87); } }
        public static Color Track { get { return Light ? Color.FromArgb(0xE3, 0xE3, 0xE3) : Color.FromArgb(0x3A, 0x3A, 0x3A); } }
        public static Color Subtle { get { return Light ? Color.FromArgb(0x0A, 0, 0, 0) : Color.FromArgb(0x0F, 0xFF, 0xFF, 0xFF); } }
        public static Color SubtleStrong { get { return Light ? Color.FromArgb(0x12, 0, 0, 0) : Color.FromArgb(0x18, 0xFF, 0xFF, 0xFF); } }
        public static Color Critical { get { return Light ? Color.FromArgb(0xC4, 0x2B, 0x1C) : Color.FromArgb(0xFF, 0x99, 0xA4); } }

        // Upload gets its own colour next to the accent-coloured download.
        public static Color Upload { get { return Light ? Color.FromArgb(0x87, 0x64, 0xB8) : Color.FromArgb(0xC1, 0xA7, 0xF5); } }

        // Text on an accent-filled button.
        public static Color OnAccent { get { return Light ? Color.White : Color.Black; } }

        // Taskbar icon foreground.
        public static Color TrayForeground { get { return Light ? Color.FromArgb(0x1C, 0x1C, 0x1C) : Color.White; } }

        // System accent as Windows 11 uses it for controls: Light 2 on dark, Dark 1 on light.
        public static Color Accent
        {
            get
            {
                try
                {
                    using (RegistryKey k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\Accent"))
                    {
                        var p = k != null ? k.GetValue("AccentPalette") as byte[] : null;
                        if (p != null && p.Length >= 32)
                        {
                            int i = (Light ? 4 : 1) * 4;
                            return Color.FromArgb(p[i], p[i + 1], p[i + 2]);
                        }
                    }
                }
                catch (Exception) { }
                return Light ? Color.FromArgb(0x00, 0x5F, 0xB8) : Color.FromArgb(0x60, 0xCD, 0xFF);
            }
        }

        public static Color Mix(Color a, Color b, float t)
        {
            return Color.FromArgb((int)(a.A + (b.A - a.A) * t), (int)(a.R + (b.R - a.R) * t), (int)(a.G + (b.G - a.G) * t), (int)(a.B + (b.B - a.B) * t));
        }

        public static Font NewFont(string family, float px, FontStyle style = FontStyle.Regular)
        {
            return new Font(family, px, style, GraphicsUnit.Pixel);
        }

        // Semibold numbers and headings like Windows 11 uses them; bold where the variable fonts are missing.
        public static Font Strong(float px, bool display = true)
        {
            string family = display ? DisplaySemibold : TextSemibold;
            return family != null ? NewFont(family, px) : NewFont(display ? DisplayFont : TextFont, px, FontStyle.Bold);
        }

        static bool FontExists(string family)
        {
            using (var f = new Font(family, 10f)) return f.Name == family;
        }

        static string Resolve(string family)
        {
            using (var f = new Font(family, 10f))
                return family.StartsWith(f.Name, StringComparison.Ordinal) && f.Name.Length >= 24 ? f.Name : null;
        }

        public static string LinkGlyph(string link)
        {
            if (link == "Wi-Fi") return Glyphs.Wifi;
            if (link == "Ethernet") return Glyphs.Ethernet;
            return Glyphs.Globe;
        }
    }

    // Segoe Fluent Icons code points.
    static class Glyphs
    {
        public const string Speed = "";
        public const string Download = "";
        public const string Upload = "";
        public const string Ping = "";      // stopwatch
        public const string Play = "";
        public const string Stop = "";
        public const string Again = "";
        public const string Settings = "";
        public const string Wifi = "";
        public const string Ethernet = "";
        public const string Globe = "";
        public const string Error = "";
    }

    static class Icons
    {
        // The tray icon: the speedometer glyph in the taskbar's foreground colour.
        public static Icon Tray(int size)
        {
            using (var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb))
            {
                using (Graphics g = Graphics.FromImage(bmp))
                {
                    g.Clear(Color.Transparent);
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.TextRenderingHint = TextRenderingHint.AntiAlias;
                    DrawGlyph(g, Glyphs.Speed, size, new RectangleF(0, 0, size, size), Theme.TrayForeground);
                }
                return ToIcon(bmp);
            }
        }

        // GenericTypographic minus LineLimit, which drops a glyph whose line box is taller than the rectangle.
        public static StringFormat Centered()
        {
            var sf = new StringFormat(StringFormat.GenericTypographic) { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            sf.FormatFlags = (sf.FormatFlags & ~StringFormatFlags.LineLimit) | StringFormatFlags.NoClip | StringFormatFlags.NoWrap;
            return sf;
        }

        public static void DrawGlyph(Graphics g, string glyph, float px, RectangleF r, Color color)
        {
            using (var f = Theme.NewFont(Theme.GlyphFont, px))
            using (var b = new SolidBrush(color))
            using (StringFormat sf = Centered())
                g.DrawString(glyph, f, b, r, sf);
        }

        // Bitmap.GetHicon loses the alpha channel; build a 32-bit (straight alpha) icon from a DIB instead.
        public static Icon ToIcon(Bitmap bmp)
        {
            int w = bmp.Width, h0 = bmp.Height;
            var bi = new BITMAPINFOHEADER { biSize = 40, biWidth = w, biHeight = -h0, biPlanes = 1, biBitCount = 32 };
            IntPtr bits;
            IntPtr color = CreateDIBSection(IntPtr.Zero, ref bi, 0, out bits, IntPtr.Zero, 0);
            BitmapData data = bmp.LockBits(new Rectangle(0, 0, w, h0), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            try
            {
                var row = new byte[w * 4];
                for (int y = 0; y < h0; y++)
                {
                    Marshal.Copy(data.Scan0 + y * data.Stride, row, 0, row.Length);
                    Marshal.Copy(row, 0, bits + y * w * 4, row.Length);
                }
            }
            finally { bmp.UnlockBits(data); }
            IntPtr mask = CreateBitmap(w, h0, 1, 1, IntPtr.Zero);
            try
            {
                var info = new ICONINFO { fIcon = true, hbmColor = color, hbmMask = mask };
                IntPtr h = CreateIconIndirect(ref info);
                Icon icon = (Icon)Icon.FromHandle(h).Clone();
                DestroyIcon(h);
                return icon;
            }
            finally
            {
                DeleteObject(color);
                DeleteObject(mask);
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        struct ICONINFO { public bool fIcon; public int xHotspot, yHotspot; public IntPtr hbmMask, hbmColor; }

        [StructLayout(LayoutKind.Sequential)]
        struct BITMAPINFOHEADER
        {
            public int biSize, biWidth, biHeight;
            public short biPlanes, biBitCount;
            public int biCompression, biSizeImage, biXPelsPerMeter, biYPelsPerMeter, biClrUsed, biClrImportant;
        }

        [DllImport("gdi32.dll")] static extern IntPtr CreateDIBSection(IntPtr hdc, ref BITMAPINFOHEADER bmi, uint usage, out IntPtr bits, IntPtr section, uint offset);
        [DllImport("user32.dll")] static extern IntPtr CreateIconIndirect(ref ICONINFO info);
        [DllImport("user32.dll")] static extern bool DestroyIcon(IntPtr h);
        [DllImport("gdi32.dll")] static extern IntPtr CreateBitmap(int w, int h, uint planes, uint bpp, IntPtr bits);
        [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr h);
    }
}
