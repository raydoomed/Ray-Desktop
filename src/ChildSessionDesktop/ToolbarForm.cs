using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using Timer = System.Windows.Forms.Timer;

namespace ChildSessionDesktop
{
    // 独立悬浮工具栏：顶层无边框小窗，不叠在 RDP 控件上（避免此前"一层包一层"的叠加问题）。
    // 整个条可自由拖动，一排按钮；"全屏"为已实现功能，其余为预留占位（暂无动作）。
    // 方形深灰条 + 方形按钮，宽度自适应文字，紧凑缩小。
    internal sealed class ToolbarForm : Form
    {
        internal Func<bool> ToggleFullscreen;
        internal Func<Rectangle, bool> ShowResMenu;   // 分辨率按钮回调（锚点=按钮屏幕矩形，由主窗体弹下拉）
        internal Func<Rectangle, bool> ShowDisplayModeMenu;   // "显示"按钮回调：弹下拉选固定/动态分辨率模式
        internal Func<Rectangle, bool> ShowMoreMenu;          // "更多"按钮回调：弹下拉（关于/打开日志/检查更新）
        internal bool IsDynamicMode;                  // 当前显示模式：true=动态（会话分辨率随窗口）/ false=固定
        private readonly string[] labels = {
            Strings.T("btn_fullscreen"), Strings.T("btn_display"), Strings.T("btn_resolution"),
            Strings.T("btn_exit"), Strings.T("btn_more"), Strings.T("btn_collapse"), Strings.T("btn_drag")
        };
        private const int DragBtnIndex = 6;   // 最右侧的专用"拖动"按钮：只有点住它才能移动工具栏
        private const int CollapseBtnIndex = 5;   // 拖动按钮左侧的"收起/展开"按钮：点击收起成小圆点 / 点击展开恢复（图标随状态切换）
        private int hoverIndex = -1;
        private int pressedIndex = -1;
        private int selectedIndex = -1;
        private float indicatorX = -1f;
        private bool dragging;
        private Point dragStart;
        private bool dragMoved;
        private bool _collapsed;   // 收起态：工具栏缩成一个可拖动的小圆点（点击圆点展开）；默认启动为完整工具栏
        private const double CollapsedOpacity = 0.35;   // 收起态平时透明度（不挡画面）；鼠标悬停到圆点上方时恢复 1 高亮
        private Form _host;
        private Point _hostOffset = new Point(int.MinValue, int.MinValue);   // 相对主窗体客户区的偏移（拖动后记住，主窗体移动/缩放时跟随不复位）
        private Rectangle[] buttonRects;
        private readonly Timer animTimer;
        private readonly TooltipForm tooltip;
        private const int BtnW = 30, BtnH = 26, Gap = 6, Pad = 6;   // 逻辑像素基准（100% DPI）
        private const int DotSize = 24;   // 收起态小圆点直径（逻辑像素基准）
        private float _scale = 1f;   // 当前 DPI 缩放系数（DpiX/96），图标/圆角/间距按它放大，保证高 DPI 下与背景条匹配

        // 桌面就绪前不显示气泡，避免启动早期位置错误
        internal bool Ready;

        internal ToolbarForm()
        {
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            ShowInTaskbar = false;
            TopMost = true;
            AutoScaleMode = AutoScaleMode.None;   // 手动按 DPI 布局，避免 WinForms 自动缩放与自绘图标双重/漏缩放
            using (var g = Graphics.FromHwnd(IntPtr.Zero))
                _scale = Math.Max(1f, g.DpiX / 96f);
            RebuildLayout();
            BackColor = Color.FromArgb(28, 28, 32);
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                     ControlStyles.OptimizedDoubleBuffer, true);
            animTimer = new Timer();
            animTimer.Interval = 10;
            animTimer.Tick += delegate { AnimateIndicator(); };
            animTimer.Start();
            tooltip = new TooltipForm();
            Disposed += delegate { animTimer.Stop(); animTimer.Dispose(); tooltip.Dispose(); };
        }

        // 按当前 _scale 重建按钮布局与窗体尺寸（构造、DPI 变化时调用）
        private void RebuildLayout()
        {
            int bw = (int)Math.Round(BtnW * _scale), bh = (int)Math.Round(BtnH * _scale);
            int gap = (int)Math.Round(Gap * _scale), pad = (int)Math.Round(Pad * _scale);
            int n = labels.Length;
            if (buttonRects == null || buttonRects.Length != n) buttonRects = new Rectangle[n];
            int totalW = pad;
            for (int i = 0; i < n; i++)
            {
                buttonRects[i] = new Rectangle(totalW, pad, bw, bh);
                totalW += bw + gap;
            }
            if (_collapsed)
                Size = new Size((int)Math.Round(DotSize * _scale), (int)Math.Round(DotSize * _scale));
            else
                Size = new Size(totalW - gap + pad, pad + bh + pad);
            Invalidate();
        }

        // 运行中系统 DPI 变化（PerMonitorV2 热切换/跨监视器）：按新 DPI 重建布局并跟随主窗体重定位。
        // 用 WM_DPICHANGED 消息而非 OnDpiChanged/DPIChangedEventArgs（项目引用的 WinForms 版本不支持该类型）
        private const int WM_DPICHANGED = 0x02E0;
        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_DPICHANGED)
            {
                int newDpi = (ushort)((int)m.WParam & 0xFFFF);   // LOWORD = 新 DPI
                if (newDpi > 0)
                {
                    _scale = Math.Max(1f, newDpi / 96f);
                    RebuildLayout();
                    if (_host != null) FollowHost(_host);
                }
            }
            base.WndProc(ref m);
        }

        // 指示器平滑滑动：悬停优先，否则选中项；空闲时自停，避免持续重绘
        private void AnimateIndicator()
        {
            int targetIdx = (hoverIndex >= 0) ? hoverIndex : selectedIndex;
            if (targetIdx < 0)
            {
                if (indicatorX > -100f)
                {
                    indicatorX += (-100f - indicatorX) * 0.2f;
                    if (indicatorX < -99f) indicatorX = -100f;
                    Invalidate();
                }
                else animTimer.Stop();
                return;
            }
            float targetCenter = buttonRects[targetIdx].X + buttonRects[targetIdx].Width / 2f;
            if (indicatorX < 0 || indicatorX == -100f) indicatorX = targetCenter;
            else indicatorX += (targetCenter - indicatorX) * 0.15f;
            if (Math.Abs(indicatorX - targetCenter) < 0.5f) indicatorX = targetCenter;
            if (indicatorX == targetCenter) animTimer.Stop();
            else Invalidate();
        }

        internal bool Fullscreen;   // 全屏时工具栏固定顶部居中，忽略拖动偏移

        internal void ResetToTopCenter() { _hostOffset = new Point(int.MinValue, int.MinValue); }   // 退出全屏统一重置到顶部居中

        // 收起态 ↔ 展开态：由最右"收起/展开"按钮手动切换（收起成可拖动小圆点，点击圆点展开）
        private void Expand()
        {
            if (!_collapsed) return;
            _collapsed = false;
            Opacity = 1f;   // 展开态不透明
            RebuildLayout();
            if (_host != null) FollowHost(_host);
            Invalidate();
        }
        private void Collapse()
        {
            if (_collapsed) return;
            _collapsed = true;
            Opacity = CollapsedOpacity;   // 收起态平时半透明；悬停圆点时高亮（见 OnMouseEnter/Leave）
            hoverIndex = -1; selectedIndex = -1; indicatorX = -100f;
            RebuildLayout();
            if (_host != null) FollowHost(_host);
            Invalidate();
        }

        internal void UpdateState(bool fs)
        {
            Fullscreen = fs;
            Invalidate();
        }

        // 跟随主窗体：标题栏下方、顶部居中；主窗体移动/缩放时保持相对位置
        internal void FollowHost(Form host)
        {
            _host = host;
            Rectangle clientScreen = host.RectangleToScreen(host.ClientRectangle);
            int x, y;
            if (Fullscreen || (_host != null && _host.WindowState == FormWindowState.Maximized))
            {
                // 全屏/最大化：固定客户区顶部居中 + 34，忽略拖动偏移（避免切过去后位置偏移）
                x = clientScreen.Left + (clientScreen.Width - Width) / 2;
                y = clientScreen.Top + 34;
            }
            else
            {
                if (_hostOffset.X == int.MinValue)   // 初始位置：主窗体顶部居中 + 34
                    _hostOffset = new Point(Math.Max(0, (clientScreen.Width - Width) / 2), 34);
                x = clientScreen.Left + _hostOffset.X;
                y = clientScreen.Top + _hostOffset.Y;
                // 窗口缩到很小、偏移超出客户区时，夹回客户区内（保证工具栏可见）
                x = Math.Max(clientScreen.Left, Math.Min(x, clientScreen.Right - Width));
                y = Math.Max(clientScreen.Top, Math.Min(y, clientScreen.Bottom - Height));
            }
            if (Location != new Point(x, y)) Location = new Point(x, y);
            // 工具栏移动/缩放时，把正在显示的气泡重新锚到对应图标上
            if (hoverIndex >= 0 && tooltip != null && tooltip.Visible)
                tooltip.Reposition(RectangleToScreen(buttonRects[hoverIndex]));
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;

            // —— 收起态：单个小圆点（可拖动，点击展开）；深色底+白色描边+线条品牌字母 R ——
            if (_collapsed)
            {
                using (var bg = new SolidBrush(Color.FromArgb(235, 24, 24, 28)))
                    g.FillEllipse(bg, 0, 0, Width - 1, Height - 1);
                using (var pen = new Pen(Color.FromArgb(230, 205, 205, 215), Math.Max(1.5f, 2f * _scale)))
                    g.DrawEllipse(pen, 1f, 1f, Width - 3f, Height - 3f);
                // 品牌字母 R（线条风格，与工具栏图标统一；整体右移+压低 bowl，使视觉重心居中于圆点）
                using (var p = new Pen(Color.FromArgb(245, 245, 248), Math.Max(1.8f, 2f * _scale)))
                {
                    p.StartCap = LineCap.Round;
                    p.EndCap = LineCap.Round;
                    float cx = Width / 2f, cy = Height / 2f;
                    float s = _scale;
                    g.DrawLine(p, cx - 5f * s, cy - 7f * s, cx - 5f * s, cy + 7f * s);   // 左竖线
                    g.DrawBezier(p, cx - 5f * s, cy - 7f * s, cx + 3f * s, cy - 8f * s, cx + 4f * s, cy - 3f * s, cx - 5f * s, cy + 1f * s);   // 右凸弧 bowl
                    g.DrawLine(p, cx - 3f * s, cy + 1f * s, cx + 6f * s, cy + 7f * s);   // 斜腿
                }
                return;
            }

            // —— 背景条：黑色圆角，底部轻影（简洁，单层）——
            int radBg = (int)Math.Round(9 * _scale);
            using (var sh = new SolidBrush(Color.FromArgb(70, 0, 0, 0)))
                g.FillRectangle(sh, new Rectangle(0, Height - 2, Width, 2));
            var barRect = new Rectangle(0, 0, Width - 1, Height - 2);
            using (var p = RoundedRectPath(barRect, radBg))
            using (var bg = new SolidBrush(Color.FromArgb(225, 24, 24, 28)))
                g.FillPath(bg, p);

            // —— 滑动指示器：平滑滑动到悬停/选中工具 ——
            int activeIdx = (hoverIndex >= 0) ? hoverIndex : selectedIndex;
            if (activeIdx >= 0 && indicatorX >= 0)
            {
                var ar = buttonRects[activeIdx];
                int iw = ar.Width, ih = ar.Height;
                Rectangle ind = new Rectangle((int)(indicatorX - iw / 2f), ar.Y, iw, ih);
                using (var ip = RoundedRectPath(ind, (int)Math.Round(6 * _scale)))
                using (var ib = new SolidBrush(Color.FromArgb(55, 255, 255, 255)))
                    g.FillPath(ib, ip);
            }

            // —— 按钮：图标，按下时高亮并下沉 ——
            for (int i = 0; i < buttonRects.Length; i++)
            {
                var r = buttonRects[i];
                if (pressedIndex == i)
                {
                    int d = Math.Min(r.Width, r.Height) - (int)Math.Round(4 * _scale);
                    Rectangle cir = new Rectangle(r.X + (r.Width - d) / 2, r.Y + (r.Height - d) / 2, d, d);
                    using (var hb = new SolidBrush(Color.FromArgb(90, 255, 255, 255)))
                        g.FillEllipse(hb, cir);
                }

                int cx = r.X + r.Width / 2, cy = r.Y + r.Height / 2;
                if (pressedIndex == i) cy += (int)Math.Round(1 * _scale);
                // 图标按 DPI 缩放绘制（ScaleTransform 放大逻辑坐标，线条/圆角随之等比例放大）
                var st = g.Save();
                g.ScaleTransform(_scale, _scale);
                DrawIcon(g, i, (int)(cx / _scale), (int)(cy / _scale));
                g.Restore(st);
            }

            // —— 背景条描边 ——
            using (var pen = new Pen(Color.FromArgb(120, 60, 60, 68)))
            using (var p = RoundedRectPath(barRect, radBg))
                g.DrawPath(pen, p);
        }

        private void DrawIcon(Graphics g, int index, int cx, int cy)
        {
            using (var pen = new Pen(Color.FromArgb(240, 240, 242), 2f))
            {
                pen.StartCap = LineCap.Round;
                pen.EndCap = LineCap.Round;
                switch (index)
                {
                    case 0: // 全屏 / 退出全屏：最大化窗口 / 还原窗口图标
                        if (Fullscreen)
                        {
                            g.DrawRectangle(pen, cx - 8, cy - 6, 13, 13);   // 后窗
                            g.DrawRectangle(pen, cx - 4, cy - 1, 13, 13);   // 前窗（还原）
                        }
                        else
                        {
                            g.DrawRectangle(pen, cx - 8, cy - 8, 16, 16);   // 最大化
                        }
                        break;
                    case 1: // 显示：显示器（屏幕 + 支杆 + 底座）
                        g.DrawRectangle(pen, cx - 7, cy - 7, 14, 11);
                        g.DrawLine(pen, cx, cy + 4, cx, cy + 6);
                        g.DrawLine(pen, cx - 4, cy + 6, cx + 4, cy + 6);
                        break;
                    case 2: // 分辨率：像素网格（田字格，2×2 分辨率格）
                        g.DrawRectangle(pen, cx - 7, cy - 7, 14, 14);
                        g.DrawLine(pen, cx, cy - 7, cx, cy + 7);
                        g.DrawLine(pen, cx - 7, cy, cx + 7, cy);
                        break;
                    case 3: // 结束：叉号（两笔对角线，关闭窗口，与工具栏白线图标同风格）
                        g.DrawLine(pen, cx - 6, cy - 6, cx + 6, cy + 6);
                        g.DrawLine(pen, cx - 6, cy + 6, cx + 6, cy - 6);
                        break;
                    case 4: // 更多：横三点
                        using (var b = new SolidBrush(Color.FromArgb(240, 240, 242)))
                        {
                            g.FillEllipse(b, cx - 8, cy - 2, 4, 4);
                            g.FillEllipse(b, cx - 2, cy - 2, 4, 4);
                            g.FillEllipse(b, cx + 4, cy - 2, 4, 4);
                        }
                        break;
                    case 5: // 收起/展开：单尖角 chevron（展开态工具栏按钮=收起 <，比其它图标略小）
                        g.DrawLine(pen, cx + 4, cy - 5, cx - 4, cy);
                        g.DrawLine(pen, cx - 4, cy, cx + 4, cy + 5);
                        break;
                    case 6: // 拖动：暗灰方块 + 暗灰九宫格点（与其他白色图标区分）
                        using (var sq = new SolidBrush(Color.FromArgb(80, 80, 88)))
                        using (var dot = new SolidBrush(Color.FromArgb(150, 150, 158)))
                        {
                            using (var p = RoundedRectPath(new Rectangle(cx - 6, cy - 6, 12, 12), 3))
                                g.FillPath(sq, p);
                            for (int row = -1; row <= 1; row++)
                                for (int col = -1; col <= 1; col++)
                                    g.FillEllipse(dot, cx + col * 4 - 1.5f, cy + row * 4 - 1.5f, 3, 3);
                        }
                        break;
                }
            }
        }

        private static GraphicsPath RoundedRectPath(Rectangle r, int rad)
        {
            int d = rad * 2;
            var p = new GraphicsPath();
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        internal string HoverText(int idx)
        {
            if (idx == 1) return Strings.T(IsDynamicMode ? "tip_display_dynamic" : "tip_display_fixed");   // 显示按钮：tooltip 显示当前模式
            if (idx == CollapseBtnIndex) return Strings.T(_collapsed ? "tip_expand" : "tip_collapse");   // 收起/展开按钮
            return labels[idx];
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left)
            {
                dragMoved = false;   // 每次按下都重置，避免上次拖动残留导致后续点击被跳过
                if (_collapsed)
                {
                    // 收起态：整个圆点即拖动把手，按住圆点可移动工具栏
                    dragging = true;
                    dragStart = e.Location;
                    if (tooltip != null) tooltip.HideAway();
                }
                else
                {
                    pressedIndex = HitTest(e.Location);
                    if (pressedIndex >= 0) Invalidate();
                    if (pressedIndex == DragBtnIndex)
                    {
                        dragging = true;
                        dragStart = e.Location;
                        if (tooltip != null) tooltip.HideAway();   // 拖动时不显示文字气泡
                    }
                    else dragging = false;
                }
            }
            base.OnMouseDown(e);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            if (_collapsed)
            {
                if (dragging && (Math.Abs(e.X - dragStart.X) > 3 || Math.Abs(e.Y - dragStart.Y) > 3))
                {
                    dragMoved = true;
                    int nx = Location.X + e.X - dragStart.X;
                    int ny = Location.Y + e.Y - dragStart.Y;
                    if (_host != null)
                    {
                        // 拖动限制在主窗体客户区内，不能拖出窗口
                        Rectangle cs = _host.RectangleToScreen(_host.ClientRectangle);
                        nx = Math.Max(cs.Left, Math.Min(nx, cs.Right - Width));
                        ny = Math.Max(cs.Top, Math.Min(ny, cs.Bottom - Height));
                    }
                    Location = new Point(nx, ny);
                    if (tooltip != null) tooltip.HideAway();
                }
                base.OnMouseMove(e);
                return;
            }

            int idx = -1;
            for (int i = 0; i < buttonRects.Length; i++)
                if (buttonRects[i].Contains(e.Location)) { idx = i; break; }
            if (idx != hoverIndex)
            {
                hoverIndex = idx;
                Invalidate();
                animTimer.Start();   // 重启滑动动画（进入按钮或离开按钮区）
                if (idx >= 0)
                {
                    if (Ready) tooltip.ShowFor(RectangleToScreen(buttonRects[idx]), HoverText(idx));
                    else tooltip.HideAway();   // 桌面未就绪不显示气泡
                }
                else tooltip.HideAway();
            }

            if (dragging && (Math.Abs(e.X - dragStart.X) > 3 || Math.Abs(e.Y - dragStart.Y) > 3))
            {
                dragMoved = true;
                if (pressedIndex >= 0) { pressedIndex = -1; Invalidate(); }
                int nx = Location.X + e.X - dragStart.X;
                int ny = Location.Y + e.Y - dragStart.Y;
                if (_host != null)
                {
                    // 拖动限制在主窗体客户区内，不能拖出窗口
                    Rectangle cs = _host.RectangleToScreen(_host.ClientRectangle);
                    nx = Math.Max(cs.Left, Math.Min(nx, cs.Right - Width));
                    ny = Math.Max(cs.Top, Math.Min(ny, cs.Bottom - Height));
                }
                Location = new Point(nx, ny);
                // 拖动时不显示文字气泡（保持在原位置会滞留，直接隐藏）
                if (tooltip != null) tooltip.HideAway();
            }
            base.OnMouseMove(e);
        }

        protected override void OnMouseEnter(EventArgs e)
        {
            base.OnMouseEnter(e);
            if (_collapsed) Opacity = 1f;   // 悬停收起圆点 → 高亮不透明（平时半透明不挡画面）
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            hoverIndex = -1;
            selectedIndex = -1;   // 离开工具栏即取消所有锁定（含点击选中）
            Invalidate();
            animTimer.Start();   // 指示器滑出
            tooltip.HideAway();
            if (_collapsed) Opacity = CollapsedOpacity;   // 移出收起圆点 → 恢复半透明
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left)
            {
                dragging = false;
                if (dragMoved && _host != null)   // 拖动结束：记住相对主窗体的偏移，主窗体移动/缩放时跟随不复位
                {
                    Rectangle cs = _host.RectangleToScreen(_host.ClientRectangle);
                    _hostOffset = new Point(Location.X - cs.Left, Location.Y - cs.Top);
                }
                if (!dragMoved)
                {
                    if (_collapsed) { Expand(); }   // 点击圆点（未拖）→ 展开
                    else
                    {
                        int hit = HitTest(e.Location);
                        if (hit >= 0) ActivateButton(hit);
                    }
                }
                if (pressedIndex >= 0) { pressedIndex = -1; Invalidate(); }
            }
            base.OnMouseUp(e);
        }

        // 鼠标点击与键盘 Enter/Space 共用：触发指定按钮的命令（P2-12 无障碍键盘导航复用）
        private void ActivateButton(int idx)
        {
            if (idx == 0 && ToggleFullscreen != null)
            {
                bool fs = ToggleFullscreen();
                UpdateState(fs);
            }
            else if (idx == 2 && ShowResMenu != null)
            {
                // 分辨率按钮：由主窗体弹出下拉，指示器停在分辨率按钮上
                ShowResMenu(RectangleToScreen(buttonRects[2]));
                selectedIndex = idx;
                Invalidate();
                animTimer.Start();
            }
            else if (idx == 1 && ShowDisplayModeMenu != null)
            {
                // 显示按钮：弹下拉选固定/动态分辨率模式，指示器停在显示按钮上
                ShowDisplayModeMenu(RectangleToScreen(buttonRects[1]));
                selectedIndex = idx;
                Invalidate();
                animTimer.Start();
            }
            else if (idx == 3 && _host != null)
            {
                // 结束按钮：关闭主窗口（与窗口右上角 X 相同，触发注销子会话等正常关闭流程）
                _host.Close();
            }
            else if (idx == 4 && ShowMoreMenu != null)
            {
                // 更多按钮：弹下拉（关于/打开日志/检查更新），指示器停在更多按钮上
                ShowMoreMenu(RectangleToScreen(buttonRects[4]));
                selectedIndex = idx;
                Invalidate();
                animTimer.Start();
            }
            else if (idx == CollapseBtnIndex)   // 收起/展开按钮：收起成小圆点（再点圆点展开）
            {
                Collapse();
            }
            else if (idx > 0 && idx != DragBtnIndex)
            {
                selectedIndex = idx;   // 预留按钮：点击锁定选中（指示器停在该位置）
                Invalidate();
                animTimer.Start();   // 指示器滑向新选中的工具
            }
            // 拖动按钮（DragBtnIndex）点击不触发命令，只作拖动把手
        }

        // —— 无障碍（P2-12）：读屏可识别每个按钮（Name 复用 HoverText，Role=PushButton），方向键在按钮间移动焦点，Enter/Space 触发 ——
        protected override AccessibleObject CreateAccessibilityInstance()
        {
            return new ToolbarAccessibleObject(this);
        }

        private sealed class ToolbarAccessibleObject : ControlAccessibleObject
        {
            public ToolbarAccessibleObject(ToolbarForm owner) : base(owner) { }
            public override AccessibleRole Role { get { return AccessibleRole.Grouping; } }
            public override string Name { get { return "Ray Desktop toolbar"; } }
            public override int GetChildCount() { return ((ToolbarForm)Owner)._collapsed ? 0 : ((ToolbarForm)Owner).buttonRects.Length; }
            public override AccessibleObject GetChild(int index)
            {
                return new ToolbarButtonAccessibleObject((ToolbarForm)Owner, index);
            }
        }

        private sealed class ToolbarButtonAccessibleObject : AccessibleObject
        {
            private readonly ToolbarForm _owner;
            private readonly int _index;
            public ToolbarButtonAccessibleObject(ToolbarForm owner, int index) { _owner = owner; _index = index; }
            public override string Name { get { return _owner.HoverText(_index); } }
            public override AccessibleRole Role { get { return AccessibleRole.PushButton; } }
            public override Rectangle Bounds { get { return _owner.RectangleToScreen(_owner.buttonRects[_index]); } }
        }

        protected override bool IsInputKey(Keys keyData)
        {
            if (_collapsed) return base.IsInputKey(keyData);
            switch (keyData)
            {
                case Keys.Left:
                case Keys.Right:
                case Keys.Up:
                case Keys.Down:
                case Keys.Enter:
                case Keys.Space:
                    return true;
            }
            return base.IsInputKey(keyData);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (_collapsed) return;
            if (e.KeyCode == Keys.Left || e.KeyCode == Keys.Up)
            {
                selectedIndex = selectedIndex <= 0 ? buttonRects.Length - 1 : selectedIndex - 1;
                hoverIndex = selectedIndex;
                Invalidate();
                animTimer.Start();
                if (Ready && selectedIndex >= 0) tooltip.ShowFor(RectangleToScreen(buttonRects[selectedIndex]), HoverText(selectedIndex));
                e.Handled = true;
            }
            else if (e.KeyCode == Keys.Right || e.KeyCode == Keys.Down)
            {
                selectedIndex = (selectedIndex + 1) % buttonRects.Length;
                hoverIndex = selectedIndex;
                Invalidate();
                animTimer.Start();
                if (Ready && selectedIndex >= 0) tooltip.ShowFor(RectangleToScreen(buttonRects[selectedIndex]), HoverText(selectedIndex));
                e.Handled = true;
            }
            else if (e.KeyCode == Keys.Enter || e.KeyCode == Keys.Space)
            {
                if (selectedIndex >= 0) ActivateButton(selectedIndex);
                e.Handled = true;
            }
        }

        private int HitTest(Point p)
        {
            for (int i = 0; i < buttonRects.Length; i++)
                if (buttonRects[i].Contains(p)) return i;
            return -1;
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            if (Region != null) Region.Dispose();
            if (_collapsed)
            {
                // 收起态：正圆窗口（避免圆角矩形在 24px 尺寸下呈胶囊/橄榄形）
                using (var p = new GraphicsPath())
                {
                    p.AddEllipse(0, 0, Width - 1, Height - 1);
                    Region = new Region(p);
                }
            }
            else
            {
                using (var p = RoundedRectPath(new Rectangle(0, 0, Width, Height), (int)Math.Round(9 * _scale)))
                    Region = new Region(p);
            }
        }
    }
}
