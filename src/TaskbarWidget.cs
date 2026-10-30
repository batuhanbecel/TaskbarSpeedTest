using System;
using System.Collections.Generic;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

namespace TaskbarSpeedTest
{
    // The latest result drawn right inside the taskbar, next to the tray ("↓ 922 Mbps / ↑ 56 Mbps", stacked like
    // the clock or on one line). During a test the values update live with a thin progress bar underneath.
    // Same technique as TaskbarStatus: a layered child window of Shell_TrayWnd with GDI ClearType text turned
    // into per-pixel alpha.
    class TaskbarWidget : Form
    {
        public event Action<Rectangle> Clicked;      // left click; the widget's screen rectangle
        public event Action<Point> RightClicked;
        public event Action Moved;                   // dragged to a new offset (save settings)

        public int Side;                             // 0 = next to the tray, 1 = left edge of the taskbar
        public int Offset = 8;                       // gap to the anchor edge, physical px
        public bool TwoLines;
        public bool Dead;                            // destroyed with the old taskbar (Explorer restart)

        List<Item> items = new List<Item>();
        double progress = -1;
        readonly ToolTip tip = new ToolTip { ShowAlways = true, InitialDelay = 400, AutoPopDelay = 30000 };
        string tipText = "";
        IntPtr embeddedIn, measureDc, textFont, glyphFont;
        bool embedFailed, closing, hidden;
        string fontKey;
        int textHeight, glyphHeight;
        bool mouseDown, dragging;
        int dragStartX, dragStartOffset;

        // Sibling apps that also sit next to the tray; the widget lines up to their left.
        static readonly string[] Neighbours = { "TaskbarStatus", "TaskbarLogiBattery.Widget" };

        public class Item
        {
            public string Glyph = "";
            public string Text = "";
            public string MinText;    // reserves the width of this text, so live values do not make the widget jump
            public bool Dim;
        }

        public TaskbarWidget()
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            Text = "TaskbarSpeedTest.Widget";
            measureDc = CreateCompatibleDC(IntPtr.Zero);
        }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ExStyle |= WS_EX_LAYERED | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE | WS_EX_TOPMOST;
                return cp;
            }
        }

        protected override bool ShowWithoutActivation { get { return true; } }

        // progress: 0..1 shows the bar, below 0 hides it.
        public void SetItems(List<Item> list, double progress, string tooltip)
        {
            items = list;
            this.progress = progress;
            tipText = tooltip;
            Render();
        }

        public Rectangle ScreenBounds
        {
            get
            {
                RECT r;
                return IsHandleCreated && GetWindowRect(Handle, out r) ? Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom) : Rectangle.Empty;
            }
        }

        // Called every second by the app: follows taskbar size / position changes and fullscreen apps.
        public void Render()
        {
            if (!IsHandleCreated || Dead) return;
            IntPtr tray = FindWindow("Shell_TrayWnd", null);
            bool fullscreen = tray != IntPtr.Zero && FullscreenInFront(tray);
            if (tray == IntPtr.Zero || (fullscreen && embeddedIn == IntPtr.Zero))
            {
                if (!hidden) { ShowWindow(Handle, SW_HIDE); hidden = true; }
                return;
            }
            if (hidden) { ShowWindow(Handle, SW_SHOWNOACTIVATE); hidden = false; }
            Embed(tray);
            Draw(tray);
        }

        void Embed(IntPtr tray)
        {
            if (embedFailed || embeddedIn == tray) return;
            int style = GetWindowLong(Handle, GWL_STYLE);
            SetWindowLong(Handle, GWL_STYLE, (style & ~WS_POPUP) | WS_CHILD);
            SetLastError(0);
            if (SetParent(Handle, tray) == IntPtr.Zero && Marshal.GetLastWin32Error() != 0)
            {
                SetWindowLong(Handle, GWL_STYLE, style);   // not allowed here: stay a topmost overlay
                embedFailed = true;
                return;
            }
            embeddedIn = tray;
        }

        // As a child of the taskbar the widget is destroyed with it when Explorer restarts.
        protected override void OnHandleDestroyed(EventArgs e)
        {
            if (!closing && !RecreatingHandle) Dead = true;
            base.OnHandleDestroyed(e);
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            closing = true;
            base.OnFormClosing(e);
        }

        protected override void Dispose(bool disposing)
        {
            closing = true;
            if (textFont != IntPtr.Zero) DeleteObject(textFont);
            if (glyphFont != IntPtr.Zero) DeleteObject(glyphFont);
            if (measureDc != IntPtr.Zero) DeleteDC(measureDc);
            textFont = glyphFont = measureDc = IntPtr.Zero;
            if (disposing) tip.Dispose();
            base.Dispose(disposing);
        }

        // --- drawing ---

        struct DrawOp
        {
            public int X, Y, Width, Height;
            public string Text;    // null: a filled rectangle
            public IntPtr Font;
            public uint Color;     // ARGB
        }

        void Draw(IntPtr tray)
        {
            RECT tb;
            GetWindowRect(tray, out tb);
            int rightEdge = tb.Right;
            IntPtr notify = FindWindowEx(tray, IntPtr.Zero, "TrayNotifyWnd", null);
            RECT nr;
            if (notify != IntPtr.Zero && GetWindowRect(notify, out nr) && nr.Right > nr.Left) rightEdge = nr.Left;

            // TaskbarStatus and TaskbarLogiBattery sit next to the tray too: line up to the left of them.
            if (Side == 0)
                for (bool moved = true; moved; )
                {
                    moved = false;
                    foreach (string title in Neighbours)
                    {
                        IntPtr other = FindWindowEx(tray, IntPtr.Zero, null, title);
                        RECT sr;
                        if (other != IntPtr.Zero && IsWindowVisible(other) && GetWindowRect(other, out sr) &&
                            sr.Left < rightEdge && sr.Right <= rightEdge + 2 && sr.Right > rightEdge - 600)
                        {
                            rightEdge = sr.Left;
                            moved = true;
                        }
                    }
                }

            uint dpi = GetDpiForWindow(tray);
            if (dpi == 0) dpi = 96;
            EnsureFonts(dpi);

            bool light = Theme.Light;
            uint normal = light ? 0xFF1A1A1Au : 0xFFF2F2F2u;
            uint dim = (normal & 0x00FFFFFFu) | 0x80000000u;
            uint track = (normal & 0x00FFFFFFu) | 0x30000000u;
            uint accent = 0xFF000000u | ((uint)Theme.Accent.ToArgb() & 0x00FFFFFFu);

            int height = Math.Max(1, tb.Bottom - tb.Top);
            int pad = Scale(4, dpi), gap = Scale(4, dpi), columnGap = Scale(TwoLines ? 10 : 12, dpi);
            List<Item> list = items.Count > 0 ? items : new List<Item> { new Item { Glyph = Glyphs.Speed } };

            // One line puts the items side by side; two lines stacks them in columns of two, like the clock.
            int rows = TwoLines && list.Count > 1 ? 2 : 1;
            int lineHeight = Math.Max(textHeight, glyphHeight), lineGap = Scale(1, dpi);
            int blockHeight = rows * lineHeight + (rows - 1) * lineGap;
            int top = Math.Max(0, (height - blockHeight) / 2);

            var ops = new List<DrawOp>();
            int x = pad;
            for (int c = 0; c < list.Count; c += rows)
            {
                int glyphWidth = 0, textWidth = 0;
                for (int r = 0; r < rows && c + r < list.Count; r++)
                {
                    Item it = list[c + r];
                    glyphWidth = Math.Max(glyphWidth, TextWidth(glyphFont, it.Glyph));
                    int w = TextWidth(textFont, it.Text);
                    if (it.MinText != null) w = Math.Max(w, TextWidth(textFont, it.MinText));
                    textWidth = Math.Max(textWidth, w);
                }
                int textX = x + glyphWidth + (glyphWidth > 0 && textWidth > 0 ? gap : 0);
                for (int r = 0; r < rows && c + r < list.Count; r++)
                {
                    Item it = list[c + r];
                    uint color = it.Dim ? dim : normal;
                    int y = top + r * (lineHeight + lineGap);
                    if (it.Glyph.Length > 0)
                        ops.Add(new DrawOp { X = x, Y = y + (lineHeight - glyphHeight) / 2, Width = glyphWidth, Height = glyphHeight, Text = it.Glyph, Font = glyphFont, Color = color });
                    // Numbers line up on the right within a column.
                    int w = TextWidth(textFont, it.Text);
                    ops.Add(new DrawOp { X = textX + textWidth - w, Y = y + (lineHeight - textHeight) / 2, Width = w, Height = textHeight, Text = it.Text, Font = textFont, Color = color });
                }
                x = textX + textWidth + columnGap;
            }
            x -= columnGap;
            int width = x + pad;

            if (progress >= 0)
            {
                int barHeight = Math.Max(2, Scale(2, dpi));
                int barY = Math.Min(top + blockHeight + Scale(3, dpi), height - barHeight - Scale(1, dpi));
                int barWidth = width - 2 * pad;
                ops.Add(new DrawOp { X = pad, Y = barY, Width = barWidth, Height = barHeight, Color = track });
                int filled = (int)Math.Round(barWidth * Math.Max(0, Math.Min(1, progress)));
                if (filled > 0) ops.Add(new DrawOp { X = pad, Y = barY, Width = filled, Height = barHeight, Color = accent });
            }

            int left = Side == 1 ? tb.Left + Offset : rightEdge - Offset - width;
            Present(ops, width, height);
            if (embeddedIn != IntPtr.Zero)
            {
                var origin = new POINT();
                ClientToScreen(tray, ref origin);
                SetWindowPos(Handle, HWND_TOP, left - origin.x, tb.Top - origin.y, width, height, SWP_NOACTIVATE);
            }
            else
                SetWindowPos(Handle, HWND_TOPMOST, left, tb.Top, width, height, SWP_NOACTIVATE);
        }

        static int Scale(int px, uint dpi) { return (int)Math.Round(px * dpi / 96.0); }

        void EnsureFonts(uint dpi)
        {
            string key = dpi + "|" + TwoLines;
            if (key == fontKey) return;
            if (textFont != IntPtr.Zero) DeleteObject(textFont);
            if (glyphFont != IntPtr.Zero) DeleteObject(glyphFont);

            // 12 px matches the taskbar clock; the arrows are a touch smaller when two lines share the height.
            textFont = CreateFont(-Scale(12, dpi), 0, 0, 0, 400, 0, 0, 0, 1, 0, 0, CLEARTYPE_NATURAL_QUALITY, 0, Theme.TextFont);
            glyphFont = CreateFont(-Scale(TwoLines ? 12 : 14, dpi), 0, 0, 0, 400, 0, 0, 0, 1, 0, 0, ANTIALIASED_QUALITY, 0, Theme.GlyphFont);
            TEXTMETRIC tm;
            SelectObject(measureDc, textFont);
            GetTextMetrics(measureDc, out tm);
            textHeight = tm.tmHeight;
            SelectObject(measureDc, glyphFont);
            GetTextMetrics(measureDc, out tm);
            glyphHeight = tm.tmHeight;
            fontKey = key;
        }

        int TextWidth(IntPtr font, string s)
        {
            if (string.IsNullOrEmpty(s)) return 0;
            SelectObject(measureDc, font);
            SIZE size;
            GetTextExtentPoint32(measureDc, s, s.Length, out size);
            return size.cx;
        }

        // Draws white text (and bars) with GDI onto black, turns the coverage into per-pixel alpha and tints it,
        // then hands the premultiplied bitmap to UpdateLayeredWindow.
        void Present(List<DrawOp> ops, int width, int height)
        {
            IntPtr screenDc = GetDC(IntPtr.Zero);
            IntPtr memDc = CreateCompatibleDC(screenDc);
            var bi = new BITMAPINFOHEADER { biSize = Marshal.SizeOf(typeof(BITMAPINFOHEADER)), biWidth = width, biHeight = -height, biPlanes = 1, biBitCount = 32 };
            IntPtr bits;
            IntPtr dib = CreateDIBSection(memDc, ref bi, 0, out bits, IntPtr.Zero, 0);
            IntPtr oldBitmap = SelectObject(memDc, dib);
            IntPtr oldFont = SelectObject(memDc, textFont);
            IntPtr white = CreateSolidBrush(0xFFFFFF);
            try
            {
                SetBkMode(memDc, TRANSPARENT);
                SetTextColor(memDc, 0xFFFFFF);
                foreach (DrawOp op in ops)
                {
                    if (op.Text == null)
                    {
                        var r = new RECT { Left = op.X, Top = op.Y, Right = op.X + op.Width, Bottom = op.Y + op.Height };
                        FillRect(memDc, ref r, white);
                        continue;
                    }
                    SelectObject(memDc, op.Font);
                    TextOut(memDc, op.X, op.Y, op.Text, op.Text.Length);
                }
                GdiFlush();

                int n = width * height;
                var px = new int[n];
                var tint = new uint[n];
                foreach (DrawOp op in ops)
                {
                    int grow = op.Text == null ? 0 : 1;
                    int x0 = Math.Max(0, op.X - grow), x1 = Math.Min(width, op.X + op.Width + 2 * grow);
                    int y0 = Math.Max(0, op.Y), y1 = Math.Min(height, op.Y + op.Height);
                    for (int yy = y0; yy < y1; yy++)
                        for (int xx = x0; xx < x1; xx++) tint[yy * width + xx] = op.Color;
                }
                Marshal.Copy(bits, px, 0, n);
                for (int i = 0; i < n; i++)
                {
                    int p = px[i];
                    int coverage = (((p >> 16) & 255) + ((p >> 8) & 255) + (p & 255)) / 3;
                    uint c = tint[i];
                    int a = coverage * (int)(c >> 24) / 255;
                    // Alpha 1 everywhere else keeps the whole rectangle clickable while invisible.
                    if (a <= 1) { px[i] = 0x01000000; continue; }
                    int r = (int)((c >> 16) & 255) * a / 255, g = (int)((c >> 8) & 255) * a / 255, b = (int)(c & 255) * a / 255;
                    px[i] = (a << 24) | (r << 16) | (g << 8) | b;
                }
                Marshal.Copy(px, 0, bits, n);

                var size = new SIZE { cx = width, cy = height };
                var src = new POINT();
                var blend = new BLENDFUNCTION { BlendOp = 0, SourceConstantAlpha = 255, AlphaFormat = 1 };
                UpdateLayeredWindow(Handle, screenDc, IntPtr.Zero, ref size, memDc, ref src, 0, ref blend, ULW_ALPHA);
            }
            finally
            {
                DeleteObject(white);
                SelectObject(memDc, oldFont);
                SelectObject(memDc, oldBitmap);
                DeleteObject(dib);
                DeleteDC(memDc);
                ReleaseDC(IntPtr.Zero, screenDc);
            }
        }

        static bool FullscreenInFront(IntPtr tray)
        {
            IntPtr fg = GetForegroundWindow();
            if (fg == IntPtr.Zero || fg == tray || fg == GetDesktopWindow() || fg == GetShellWindow()) return false;
            var cls = new StringBuilder(64);
            GetClassName(fg, cls, cls.Capacity);
            string c = cls.ToString();
            if (c == "WorkerW" || c == "Progman") return false;
            RECT r;
            if (!GetWindowRect(fg, out r)) return false;
            Rectangle screen = Screen.FromHandle(fg).Bounds;
            if (Screen.FromHandle(tray).Bounds != screen) return false;
            return r.Left <= screen.Left && r.Top <= screen.Top && r.Right >= screen.Right && r.Bottom >= screen.Bottom;
        }

        // --- mouse: click for the flyout, right-click for the menu, drag to move along the taskbar ---

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left) return;
            mouseDown = true;
            dragging = false;
            dragStartX = Cursor.Position.X;
            dragStartOffset = Offset;
            Capture = true;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (!mouseDown)
            {
                if (tip.GetToolTip(this) != tipText) tip.SetToolTip(this, tipText);
                return;
            }
            int dx = Cursor.Position.X - dragStartX;
            if (!dragging && Math.Abs(dx) < SystemInformation.DragSize.Width) return;
            dragging = true;
            Offset = Math.Max(0, Side == 1 ? dragStartOffset + dx : dragStartOffset - dx);
            Render();
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (e.Button == MouseButtons.Left)
            {
                bool wasDrag = dragging;
                mouseDown = dragging = false;
                Capture = false;
                if (wasDrag) { if (Moved != null) Moved(); }
                else
                {
                    tip.Hide(this);
                    if (Clicked != null) Clicked(ScreenBounds);
                }
            }
            else if (e.Button == MouseButtons.Right)
            {
                tip.Hide(this);
                if (RightClicked != null) RightClicked(Cursor.Position);
            }
        }

        // --- Win32 ---

        const int WS_EX_LAYERED = 0x80000, WS_EX_TOOLWINDOW = 0x80, WS_EX_NOACTIVATE = 0x8000000, WS_EX_TOPMOST = 0x8;
        const int ULW_ALPHA = 2, SW_HIDE = 0, SW_SHOWNOACTIVATE = 4, TRANSPARENT = 1;
        const uint SWP_NOACTIVATE = 0x10, CLEARTYPE_NATURAL_QUALITY = 6, ANTIALIASED_QUALITY = 4;
        static readonly IntPtr HWND_TOPMOST = new IntPtr(-1), HWND_TOP = IntPtr.Zero;
        const int GWL_STYLE = -16, WS_CHILD = 0x40000000;
        const int WS_POPUP = unchecked((int)0x80000000);

        [StructLayout(LayoutKind.Sequential)] struct RECT { public int Left, Top, Right, Bottom; }
        [StructLayout(LayoutKind.Sequential)] struct POINT { public int x, y; }
        [StructLayout(LayoutKind.Sequential)] struct SIZE { public int cx, cy; }
        [StructLayout(LayoutKind.Sequential, Pack = 1)] struct BLENDFUNCTION { public byte BlendOp, BlendFlags, SourceConstantAlpha, AlphaFormat; }
        [StructLayout(LayoutKind.Sequential)]
        struct BITMAPINFOHEADER
        {
            public int biSize, biWidth, biHeight;
            public short biPlanes, biBitCount;
            public int biCompression, biSizeImage, biXPelsPerMeter, biYPelsPerMeter, biClrUsed, biClrImportant;
        }
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct TEXTMETRIC
        {
            public int tmHeight, tmAscent, tmDescent, tmInternalLeading, tmExternalLeading, tmAveCharWidth,
                       tmMaxCharWidth, tmWeight, tmOverhang, tmDigitizedAspectX, tmDigitizedAspectY;
            public char tmFirstChar, tmLastChar, tmDefaultChar, tmBreakChar;
            public byte tmItalic, tmUnderlined, tmStruckOut, tmPitchAndFamily, tmCharSet;
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr FindWindow(string cls, string name);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string cls, string name);
        [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr hWnd, out RECT r);
        [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr hWnd);
        [DllImport("user32.dll")] static extern uint GetDpiForWindow(IntPtr hWnd);
        [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] static extern IntPtr GetDesktopWindow();
        [DllImport("user32.dll")] static extern IntPtr GetShellWindow();
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr hWnd, StringBuilder sb, int max);
        [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr hWnd, int cmd);
        [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
        [DllImport("user32.dll")] static extern bool UpdateLayeredWindow(IntPtr hWnd, IntPtr hdcDst, IntPtr dst, ref SIZE size,
            IntPtr hdcSrc, ref POINT src, int key, ref BLENDFUNCTION blend, int flags);
        [DllImport("user32.dll", SetLastError = true)] static extern IntPtr SetParent(IntPtr child, IntPtr parent);
        [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr hWnd, int index);
        [DllImport("user32.dll")] static extern int SetWindowLong(IntPtr hWnd, int index, int value);
        [DllImport("user32.dll")] static extern bool ClientToScreen(IntPtr hWnd, ref POINT p);
        [DllImport("user32.dll")] static extern int FillRect(IntPtr hdc, ref RECT r, IntPtr brush);
        [DllImport("kernel32.dll")] static extern void SetLastError(int error);
        [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr hWnd);
        [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr hWnd, IntPtr hdc);
        [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleDC(IntPtr hdc);
        [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr hdc);
        [DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);
        [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr obj);
        [DllImport("gdi32.dll")] static extern IntPtr CreateSolidBrush(uint color);
        [DllImport("gdi32.dll")] static extern IntPtr CreateDIBSection(IntPtr hdc, ref BITMAPINFOHEADER bmi, uint usage, out IntPtr bits, IntPtr section, uint offset);
        [DllImport("gdi32.dll", CharSet = CharSet.Unicode, EntryPoint = "CreateFontW")]
        static extern IntPtr CreateFont(int height, int width, int escapement, int orientation, int weight, uint italic, uint underline,
            uint strikeOut, uint charSet, uint outPrecision, uint clipPrecision, uint quality, uint pitchAndFamily, string face);
        [DllImport("gdi32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetTextMetricsW")] static extern bool GetTextMetrics(IntPtr hdc, out TEXTMETRIC tm);
        [DllImport("gdi32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetTextExtentPoint32W")] static extern bool GetTextExtentPoint32(IntPtr hdc, string s, int len, out SIZE size);
        [DllImport("gdi32.dll", CharSet = CharSet.Unicode, EntryPoint = "TextOutW")] static extern bool TextOut(IntPtr hdc, int x, int y, string s, int len);
        [DllImport("gdi32.dll")] static extern int SetBkMode(IntPtr hdc, int mode);
        [DllImport("gdi32.dll")] static extern uint SetTextColor(IntPtr hdc, uint color);
        [DllImport("gdi32.dll")] static extern bool GdiFlush();
    }
}
