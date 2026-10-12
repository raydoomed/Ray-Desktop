using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace ChildSessionDesktop
{
    // 工具提示气泡：悬停时飞入、移开时飞出
    // 用 UpdateLayeredWindow 输出真透明浮层：仅纯白文字，无任何背景
    internal sealed class TooltipForm : Form
    {
        private string text = "";
        private float alpha = 0f;
        private bool fadingIn;
        private bool fadingOut;
        private readonly Timer anim;
        private readonly Font font;

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT { public int X; public int Y; public POINT(int x, int y) { X = x; Y = y; } }
        [StructLayout(LayoutKind.Sequential)]
        private struct SIZE { public int W; public int H; public SIZE(int w, int h) { W = w; H = h; } }
        [StructLayout(LayoutKind.Sequential)]
        private struct BLENDFUNCTION { public byte BlendOp; public byte BlendFlags; public byte SourceConstantAlpha; public byte AlphaFormat; }

        [DllImport("user32.dll")]
        private static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr hdcDst, ref POINT pptDst, ref SIZE psize, IntPtr hdcSrc, ref POINT pptSrc, int crKey, ref BLENDFUNCTION pblend, int dwFlags);
        [DllImport("user32.dll")]
        private static extern IntPtr GetDC(IntPtr hWnd);
        [DllImport("user32.dll")]
        private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);
        [DllImport("gdi32.dll")]
        private static extern IntPtr CreateCompatibleDC(IntPtr hdc);
        [DllImport("gdi32.dll")]
        private static extern bool DeleteDC(IntPtr hdc);
        [DllImport("gdi32.dll")]
        private static extern IntPtr SelectObject(IntPtr hdc, IntPtr hgdiobj);
        [DllImport("gdi32.dll")]
        private static extern bool DeleteObject(IntPtr hObject);

        internal TooltipForm()
        {
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            ShowInTaskbar = false;
            TopMost = true;
            font = new Font("Microsoft YaHei UI", 10f);
            anim = new Timer();
            anim.Interval = 12;
            anim.Tick += delegate
            {
                if (fadingIn)
                {
                    alpha = Math.Min(1f, alpha + 0.22f);
                    if (alpha >= 1f) { fadingIn = false; anim.Stop(); }
                    Redraw();
                }
                else if (fadingOut)
                {
                    alpha = Math.Max(0f, alpha - 0.25f);
                    if (alpha <= 0f) { fadingOut = false; anim.Stop(); Hide(); }
                    else Redraw();
                }
            };
            Disposed += delegate { anim.Dispose(); font.Dispose(); };
        }

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ExStyle |= 0x00080000;   // WS_EX_LAYERED
                return cp;
            }
        }

        internal void ShowFor(Rectangle anchor, string txt)
        {
            text = txt;
            var sz = TextRenderer.MeasureText(text, font);
            int w = sz.Width + 8, h = sz.Height + 6;
            int x = anchor.Left + (anchor.Width - w) / 2;
            int y = anchor.Top - h - 6;
            if (y < 0) y = anchor.Bottom + 6;   // 顶部不够则放下方
            if (!Visible) { alpha = 0.01f; Visible = true; }   // 先创建句柄，避免定位被窗口管理器重置
            Size = new Size(w, h);
            Location = new Point(x, y);
            fadingOut = false;
            fadingIn = true;
            if (!anim.Enabled) anim.Start();
            Redraw();   // 文字宽度相同时 Size/Location 不变，也强制刷新
            BringToFront();
        }

        internal void HideAway()
        {
            fadingIn = false;
            fadingOut = true;
            if (alpha > 0f) { if (!anim.Enabled) anim.Start(); }
            else Hide();
        }

        // 工具栏移动/缩放时重定位，保持气泡贴在图标上（不重置淡入状态）
        internal void Reposition(Rectangle anchor)
        {
            var sz = TextRenderer.MeasureText(text, font);
            int w = sz.Width + 8, h = sz.Height + 6;
            int x = anchor.Left + (anchor.Width - w) / 2;
            int y = anchor.Top - h - 6;
            if (y < 0) y = anchor.Bottom + 6;
            Location = new Point(x, y);
            Size = new Size(w, h);
            Redraw();
        }

        // 用带 alpha 的位图绘制纯白文字，经 UpdateLayeredWindow 输出为真透明浮层
        private void Redraw()
        {
            if (!Visible) return;
            int w = Math.Max(1, Width), h = Math.Max(1, Height);
            using (var bmp = new Bitmap(w, h, System.Drawing.Imaging.PixelFormat.Format32bppPArgb))
            {
                using (var g = Graphics.FromImage(bmp))
                {
                    g.Clear(Color.Transparent);
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAlias;
                    int a = (int)(246 * alpha);
                    using (var b = new SolidBrush(Color.FromArgb(a, 255, 255, 255)))
                    using (var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
                        g.DrawString(text, font, b, new RectangleF(0, 0, w, h), sf);
                }

                IntPtr screenDc = GetDC(IntPtr.Zero);
                IntPtr memDc = CreateCompatibleDC(screenDc);
                IntPtr hbmp = IntPtr.Zero;
                IntPtr old = IntPtr.Zero;
                try
                {
                    hbmp = bmp.GetHbitmap(Color.FromArgb(0));
                    old = SelectObject(memDc, hbmp);
                    var size = new SIZE(w, h);
                    var dst = new POINT(Location.X, Location.Y);
                    var src = new POINT(0, 0);
                    var blend = new BLENDFUNCTION { BlendOp = 0, BlendFlags = 0, SourceConstantAlpha = 255, AlphaFormat = 1 };
                    UpdateLayeredWindow(Handle, screenDc, ref dst, ref size, memDc, ref src, 0, ref blend, 2);
                }
                finally
                {
                    if (old != IntPtr.Zero) SelectObject(memDc, old);
                    if (hbmp != IntPtr.Zero) DeleteObject(hbmp);
                    if (memDc != IntPtr.Zero) DeleteDC(memDc);
                    if (screenDc != IntPtr.Zero) ReleaseDC(IntPtr.Zero, screenDc);
                }
            }
        }
    }
}
