using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace TaskbarSpeedTest
{
    // Gives a ContextMenuStrip the look of a Windows 11 menu: rounded corners and border from DWM,
    // light/dark colours, rounded hover highlight, roomier items and Fluent icon glyphs.
    static class Win11Menu
    {
        const int DWMWA_WINDOW_CORNER_PREFERENCE = 33, DWMWA_BORDER_COLOR = 34, DWMWCP_ROUND = 2;

        [DllImport("dwmapi.dll")]
        static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

        static readonly bool Windows11 = Environment.OSVersion.Version.Build >= 22000;
        static readonly Renderer renderer = new Renderer();
        static Font font;

        // Call once after the items exist (and again after rebuilding them); light follows the taskbar.
        public static void Apply(ContextMenuStrip menu)
        {
            if (font == null) font = NewFont("Segoe UI Variable Text", 10f) ?? new Font("Segoe UI", 9.5f);
            if (menu.Renderer != renderer)
            {
                menu.Renderer = renderer;
                menu.Opening += (s, e) => renderer.Light = IsLightTheme();
            }
            Style(menu);
        }

        static void Style(ToolStripDropDownMenu dropDown)
        {
            dropDown.Font = font;
            dropDown.Padding = new Padding(2, 4, 2, 4);
            dropDown.ShowImageMargin = true;
            dropDown.DropShadowEnabled = !Windows11;   // DWM draws the shadow for rounded popups
            dropDown.HandleCreated -= RoundCorners;
            dropDown.HandleCreated += RoundCorners;
            dropDown.Opening -= Recolor;
            dropDown.Opening += Recolor;
            if (dropDown.IsHandleCreated) RoundCorners(dropDown, EventArgs.Empty);

            foreach (ToolStripItem item in dropDown.Items)
            {
                if (item is ToolStripSeparator)
                {
                    item.Margin = new Padding(0, 2, 0, 2);
                    continue;
                }
                if (item.Font.Bold) item.Font = new Font(font, FontStyle.Bold);
                item.Padding = new Padding(0, 4, 0, 4);
                var menuItem = item as ToolStripMenuItem;
                if (menuItem != null && menuItem.HasDropDownItems)
                    Style((ToolStripDropDownMenu)menuItem.DropDown);
            }
        }

        static void RoundCorners(object sender, EventArgs e)
        {
            if (!Windows11) return;
            var dropDown = (Control)sender;
            int corner = DWMWCP_ROUND;
            DwmSetWindowAttribute(dropDown.Handle, DWMWA_WINDOW_CORNER_PREFERENCE, ref corner, 4);
            Recolor(sender, e);
        }

        static void Recolor(object sender, EventArgs e)
        {
            var dropDown = (Control)sender;
            if (!Windows11 || !dropDown.IsHandleCreated) return;
            Color c = IsLightTheme() ? Color.FromArgb(0xE0, 0xE0, 0xE0) : Color.FromArgb(0x45, 0x45, 0x45);
            int colorRef = c.R | (c.G << 8) | (c.B << 16);
            DwmSetWindowAttribute(dropDown.Handle, DWMWA_BORDER_COLOR, ref colorRef, 4);
        }

        internal static bool? ThemeOverride;   // for screenshots of both themes

        internal static bool IsLightTheme()
        {
            if (ThemeOverride.HasValue) return ThemeOverride.Value;
            using (var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
            {
                object v = k != null ? k.GetValue("SystemUsesLightTheme") : null;
                return v is int && (int)v != 0;
            }
        }

        static Font NewFont(string family, float size)
        {
            var f = new Font(family, size);
            if (f.Name == family) return f;
            f.Dispose();
            return null;
        }

        class Renderer : ToolStripRenderer
        {
            public bool Light;
            static readonly string GlyphFamily = new Font("Segoe Fluent Icons", 9f).Name == "Segoe Fluent Icons"
                ? "Segoe Fluent Icons" : "Segoe MDL2 Assets";
            const string CheckGlyph = "", ChevronGlyph = "";

            Color Back { get { return Light ? Color.FromArgb(0xF9, 0xF9, 0xF9) : Color.FromArgb(0x2C, 0x2C, 0x2C); } }
            Color Hover { get { return Light ? Color.FromArgb(0xEA, 0xEA, 0xEA) : Color.FromArgb(0x3D, 0x3D, 0x3D); } }
            Color Text { get { return Light ? Color.FromArgb(0x1A, 0x1A, 0x1A) : Color.White; } }
            Color Disabled { get { return Light ? Color.FromArgb(0x8A, 0x8A, 0x8A) : Color.FromArgb(0x9D, 0x9D, 0x9D); } }
            Color Line { get { return Light ? Color.FromArgb(0xE5, 0xE5, 0xE5) : Color.FromArgb(0x40, 0x40, 0x40); } }

            protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e)
            {
                e.Graphics.Clear(Back);
            }

            protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
            {
                if (Windows11) return;   // DWM draws a rounded border
                using (var pen = new Pen(Line))
                    e.Graphics.DrawRectangle(pen, 0, 0, e.ToolStrip.Width - 1, e.ToolStrip.Height - 1);
            }

            protected override void OnRenderImageMargin(ToolStripRenderEventArgs e) { }

            protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
            {
                var item = e.Item as ToolStripMenuItem;
                if (item == null || !item.Enabled) return;
                if (!item.Selected && !(item.HasDropDownItems && item.DropDown.Visible)) return;
                var r = new Rectangle(4, 1, item.Width - 8, item.Height - 2);
                e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                using (GraphicsPath path = Rounded(r, 4))
                using (var brush = new SolidBrush(Hover))
                    e.Graphics.FillPath(brush, path);
            }

            protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
            {
                e.TextColor = e.Item.Enabled ? Text : Disabled;
                // Segoe UI Variable centres its line box, not its letters, so the text sits a little high; most visible
                // on a bold item right above a separator. Line the middle of the capitals up with the middle of the item.
                Rectangle r = e.TextRectangle;
                if (r.Height > 0)
                {
                    r.Offset(0, e.Item.Height / 2 - (r.Y + CapMiddle(e.TextFont, r.Size, e.TextFormat)));
                    e.TextRectangle = r;
                }
                base.OnRenderItemText(e);
            }

            static readonly System.Collections.Generic.Dictionary<string, int> capMiddles = new System.Collections.Generic.Dictionary<string, int>();

            // Where the middle of a capital letter lands when text is drawn into a box of this size.
            static int CapMiddle(Font font, Size box, TextFormatFlags flags)
            {
                string key = font.Name + "|" + font.Size + "|" + font.Style + "|" + box.Height + "|" + (int)flags;
                int middle;
                if (capMiddles.TryGetValue(key, out middle)) return middle;
                middle = box.Height / 2;
                using (var bmp = new Bitmap(Math.Max(box.Width, 32), box.Height))
                {
                    using (Graphics g = Graphics.FromImage(bmp))
                    {
                        g.Clear(Color.Black);
                        TextRenderer.DrawText(g, "H", font, new Rectangle(0, 0, bmp.Width, bmp.Height), Color.White, Color.Black, flags);
                    }
                    int top = -1, bottom = -1;
                    for (int y = 0; y < bmp.Height; y++)
                        for (int x = 0; x < bmp.Width; x++)
                        {
                            if (bmp.GetPixel(x, y).G < 128) continue;
                            if (top < 0) top = y;
                            bottom = y;
                            break;
                        }
                    if (top >= 0) middle = (top + bottom + 1) / 2;
                }
                capMiddles[key] = middle;
                return middle;
            }

            protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
            {
                DrawGlyph(e.Graphics, CheckGlyph, 9f, e.ImageRectangle, e.Item.Enabled ? Text : Disabled);
            }

            protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
            {
                var r = e.ArrowRectangle;
                r.X -= 6;   // pull the chevron in from the rounded edge
                DrawGlyph(e.Graphics, ChevronGlyph, 7f, r, e.Item.Enabled ? Text : Disabled);
            }

            protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
            {
                int y = e.Item.Height / 2;
                using (var pen = new Pen(Line))
                    e.Graphics.DrawLine(pen, 0, y, e.Item.Width, y);
            }

            static void DrawGlyph(Graphics g, string glyph, float size, Rectangle r, Color color)
            {
                using (var f = new Font(GlyphFamily, size))
                    TextRenderer.DrawText(g, glyph, f, r, color,
                        TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            }

            static GraphicsPath Rounded(Rectangle r, int radius)
            {
                int d = radius * 2;
                var p = new GraphicsPath();
                p.AddArc(r.X, r.Y, d, d, 180, 90);
                p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
                p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
                p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
                p.CloseFigure();
                return p;
            }
        }
    }
}
