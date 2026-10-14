using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Timer = System.Windows.Forms.Timer;
using MSTSCLib;

namespace ChildSessionDesktop
{
    internal sealed class DesktopForm : Form
    {
        private readonly ChildSessionControl rdp;
        private readonly Timer statusTimer;
        private readonly Timer displayResizeTimer;
        private readonly Timer clipboardRelayTimer;
        private System.Windows.Forms.Timer readyTimer;        // 连接就绪 4s 后创建工具栏（P3-5：提升为字段，OnClosing 统一 Stop）
        private System.Windows.Forms.Timer lateResizeTimer;   // 分辨率校正 900ms 延迟（P3-5：提升为字段，OnClosing 统一 Stop）
        private readonly ClipboardFileRelay clipboardRelay;
        private IMsTscAxEvents_Event rdpEvents;
        private bool connecting;
        private bool closing;
        private bool eventsAttached;
        private int lastLoggedState = -1;
        private Size lastRequestedDisplaySize = Size.Empty;
        private Size lastFailedDisplaySize = Size.Empty;
        private DateTime connectionStarted;
        private DateTime connectionReadyAt = DateTime.MinValue;
        private DateTime nextResizeAttempt = DateTime.MinValue;
        private int resizeRetryCount;

        internal bool dynamicMode;   // 固定分辨率(默认) / 动态分辨率；启动参数决定初始值，工具栏"显示"按钮可运行时切换
        private int _fixedSessW, _fixedSessH;   // 进入动态模式前的固定分辨率；切回固定时恢复它（而不是动态时的临时窗口尺寸）
        private double aspect;
        private int currentSessW, currentSessH;   // 当前生效的会话分辨率（用于分辨率下拉打勾）
        private bool _resChangePending;           // 改分辨率期间屏蔽 UpdateConnectionStatus 的旧值 readback 干扰
        private bool _lastMaximized;              // 检测最大化状态切换：进最大化时重置工具栏偏移（还原后也回顶部居中）
        private int _lbLastW = -1, _lbLastH = -1;
        private double _lbLastA = -1;             // Letterbox 节流日志：客户区/aspect 变化才记
        private bool fullscreen;
        private Rectangle prevBounds;
        private Rectangle _trackedScreen = Rectangle.Empty;   // 当前窗口所在屏幕（Bounds），跨屏拖动检测用
        private bool _followScreenRes = true;   // 当前分辨率是否"跟随你的屏幕"：true 时跨屏自动改到新屏；手动选了特定分辨率后置 false，跨屏保持手动值不动
        // 会话分辨率只由用户意图设置（选/跨屏/最大化/拖拽结束），不设跨屏锁定。
        private int _maxSessW, _maxSessH;       // 进入最大化时记住当前会话分辨率，还原时恢复它（Windows 还原尺寸可能有偏差，导致分辨率漂移）
        private Size _fixedMatchClient = Size.Empty;   // 固定模式下最近窗口匹配会话的目标逻辑客户区；切动态时据此判断窗口是否被用户拉伸
        private FormBorderStyle prevBorder;
        private ToolbarForm toolBar;                 // 独立悬浮工具栏（可拖动，含全屏/退出全屏）
        private ContextMenuStrip _displayMenu, _moreMenu, _resMenu;   // 三个下拉菜单复用单实例，避免每次点击泄漏
        private Timer reconnectTimer;             // 断开/超时后的低频自动重连探测
        private int reconnectAttempts;            // 连续自动重连尝试次数（超上限停止并提示手动）
        private const int WM_SIZING = 0x0214;
        private const int WM_EXITSIZEMOVE = 0x0232;
        private const int WM_HOTKEY = 0x0312;
        private const int WM_DISPLAYCHANGE = 0x007E;   // 系统显示分辨率/屏幕集合变化（P3-12）
        private const int SW_SHOWNORMAL = 1;
        private const uint VK_F11 = 0x7A;
        private const uint SWP_NOZORDER = 0x0004;
        private const uint SWP_NOACTIVATE = 0x0010;
        private const uint SWP_FRAMECHANGED = 0x0020;

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT { public int Left, Top, Right, Bottom; }

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT { public int X, Y; }

        // Windows 官方的窗口布局结构：我们只用它同步"还原尺寸"(rcNormalPosition)，
        // 让原生移动/缩放后 Windows 记住的是当前窗口矩形，而不是某个旧的还原尺寸。
        [StructLayout(LayoutKind.Sequential)]
        private struct WINDOWPLACEMENT
        {
            public int length;
            public int flags;
            public int showCmd;
            public POINT ptMinPosition;
            public POINT ptMaxPosition;
            public RECT rcNormalPosition;
        }

        [DllImport("user32.dll")]
        private static extern bool GetWindowPlacement(IntPtr hWnd, ref WINDOWPLACEMENT lpwndpl);

        [DllImport("user32.dll")]
        private static extern bool SetWindowPlacement(IntPtr hWnd, ref WINDOWPLACEMENT lpwndpl);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint flags);

        [DllImport("user32.dll")]
        private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

        [DllImport("user32.dll")]
        private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        internal DesktopForm(bool dynamicMode)
        {
            this.dynamicMode = dynamicMode;
            Text = "Ray Desktop";
            BackColor = Color.Black;
            StartPosition = FormStartPosition.CenterScreen;
            Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);

            // 开局窗口：客户区 = 屏幕 90% 宽，高度按会话宽高比推算 → 一开始就无黑边、不变形
            int screenW = Screen.PrimaryScreen.Bounds.Width;
            int screenH = Screen.PrimaryScreen.Bounds.Height;
            if (screenW <= 0 || screenH <= 0) { screenW = 1280; screenH = 720; }
            SetSessionResolution(screenW, screenH);
            if (!dynamicMode) { _fixedSessW = screenW; _fixedSessH = screenH; }   // 固定启动：记录初始固定分辨率（主机屏幕）
            int initialW = (int)(screenW * 0.9);
            int initialH = (int)(initialW / aspect + 0.5);
            ClientSize = new Size(initialW, initialH);
            if (!dynamicMode) _fixedMatchClient = new Size(initialW, initialH);   // 固定启动即记录匹配目标客户区（切动态据此判断是否被拉伸）
            // 最小尺寸严格保持主机比例，缩到最小也不破比例、无黑边
            int minW = 640;
            MinimumSize = new Size(minW, (int)(minW / aspect + 0.5));

            // RDP 控件用 Dock=None + ApplyLetterbox 等比居中：控件宽高比恒等于画面宽高比，
            // 避免 ActiveX(SmartSizing) 在比例不匹配时用灰色填充剩余区域（灰底）。
            rdp = new ChildSessionControl { Dock = DockStyle.None, BackColor = Color.Black };
            // 会话分辨率只由用户意图设置（选分辨率/跨屏/最大化/拖拽结束 WM_EXITSIZEMOVE），
            // 不在 rdp.Resize 自动跟随——否则程序为铺满/跨屏缩放/还原而改控件尺寸也会被当成"拉窗口"
            // 反向覆盖会话分辨率（跨屏"你的屏幕"被打勾破坏、还原漂移的根源）。
            if (!dynamicMode) Program.Log("Letterbox (fixed-resolution) mode enabled; session resolution is pinned to the host screen");
            clipboardRelay = new ClipboardFileRelay(false);
            clipboardRelayTimer = new Timer { Interval = 250 };
            clipboardRelayTimer.Tick += delegate
            {
                clipboardRelay.PollIncoming();
                clipboardRelay.RetryPendingClipboardRead();
            };
            clipboardRelayTimer.Start();
            Controls.Add(rdp);

            statusTimer = new Timer { Interval = 750 };
            statusTimer.Tick += delegate { UpdateConnectionStatus(); };
            displayResizeTimer = new Timer { Interval = 300 };
            displayResizeTimer.Tick += delegate
            {
                displayResizeTimer.Stop();
                ApplyDisplaySize();
            };
            reconnectTimer = new Timer { Interval = 2000 };
            reconnectTimer.Tick += delegate
            {
                if (closing || IsDisposed) return;
                TryAutoReconnect();
            };
            Shown += delegate
            {
                Program.Log("Host form shown");
                if (dynamicMode) SetDynamicLayout();      // 动态模式：控件铺满客户区（会话分辨率随窗口）
                else ApplyLetterbox();                    // 固定模式：窗体一显示就把控件摆到正确矩形，避免左上角小黑块
                // 工具栏不在此创建：等桌面完全显示、分辨率就位后再创建（见连接就绪回调）
                ClipboardFileRelay.RegisterChildAgent();
                BeginInvoke((MethodInvoker)ConnectChildSession);
            };
            FormClosing += OnClosing;
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            if (clipboardRelay != null && !ClipboardFileRelay.AddClipboardFormatListener(Handle))
                Program.Log("Could not register the host-session clipboard listener; Windows error " + Marshal.GetLastWin32Error());
            if (!dynamicMode && !RegisterHotKey(Handle, 1, 0, VK_F11))
                Program.Log("Could not register the F11 fullscreen hotkey; Windows error " + Marshal.GetLastWin32Error());
        }

        protected override void OnHandleDestroyed(EventArgs e)
        {
            if (IsHandleCreated && clipboardRelay != null) ClipboardFileRelay.RemoveClipboardFormatListener(Handle);
            if (!dynamicMode && !UnregisterHotKey(Handle, 1))
                Program.Log("Could not unregister the F11 fullscreen hotkey; Windows error " + Marshal.GetLastWin32Error());
            base.OnHandleDestroyed(e);
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            bool max = (WindowState == FormWindowState.Maximized);
            if (max != _lastMaximized)   // 进入最大化或还原切换都重置偏移 → 还原后也回顶部居中
            {
                bool wasMax = _lastMaximized;
                _lastMaximized = max;
                if (max)
                {
                    _maxSessW = currentSessW; _maxSessH = currentSessH;   // 记住最大化前的分辨率
                    if (dynamicMode) QueueDisplayResize(true);   // 最大化 = 用户主动放大 → 会话分辨率跟随最大化窗口，并重置退避
                }
                else if (wasMax && _maxSessW > 0)
                {
                    // 从最大化还原：动态模式还原回最大化前的分辨率（Windows 还原尺寸可能有偏差，
                    // 用程序设定的分辨率统一，不跟随还原偏差窗口）。
                    ApplySessionResolution(_maxSessW, _maxSessH, false);
                }
                if (toolBar != null) toolBar.ResetToTopCenter();
            }
            if (dynamicMode) SetDynamicLayout();   // 动态模式：控件铺满客户区（分辨率跟随只由 WM_EXITSIZEMOVE/最大化触发）
            else ApplyLetterbox();
            if (toolBar != null) toolBar.FollowHost(this);
        }

        protected override void OnMove(EventArgs e)
        {
            base.OnMove(e);
            // 移动走标准 Windows 机制（分栏/双击最大化/贴边贴合都保留），这里只让工具栏跟随。
            if (toolBar != null) toolBar.FollowHost(this);
            AutoFollowScreen();   // 拖到另一个显示器：自动把会话分辨率/窗口适配到新屏幕（不弹确认）
        }

        // 跨屏拖动跟随：窗口所在屏幕变化时，自动按新屏分辨率适配（等同自动选中"你的屏幕"）。
        private void AutoFollowScreen()
        {
            if (Handle == IntPtr.Zero || IsDisposed) return;
            if (fullscreen) return;   // 全屏铺满当前屏，跨屏无意义
            var scr = Screen.FromControl(this).Bounds;
            if (scr.Width <= 0 || scr.Height <= 0) return;
            if (_trackedScreen == Rectangle.Empty) { _trackedScreen = scr; return; }   // 首次定位，仅记录
            if (scr != _trackedScreen)
            {
                _trackedScreen = scr;   // 先记录再动作，避免 SetWindowPos 回触发本检测导致抖动
                Program.Log("AutoFollow: screen=" + scr.Width + "x" + scr.Height +
                            " follow=" + _followScreenRes + " sess=" + currentSessW + "x" + currentSessH);
                // 仅当当前分辨率在"跟随你的屏幕"状态才跨屏改到新屏；手动选了特定分辨率（_followScreenRes=false）则保持手动值，不覆盖
                if (_followScreenRes && rdp != null)
                {
                    try
                    {
                        var client = (IMsRdpClient9)rdp.GetOcxObject();
                        if (client.Connected == 1)
                        {
                            int w = scr.Width & ~1;   // RDP 要求偶数像素宽（与手动选择一致），避免奇数宽失败
                            ApplySessionResolution(w, scr.Height, false);
                            // 跨屏"你的屏幕"= 所在屏原生。会话分辨率已由 ApplySessionResolution 设为目标值，
                            // 且 ApplySessionResolution 内 HoldCurrentDisplaySize 预置了当前窗口逻辑客户区，
                            // 使随后的 WM_EXITSIZEMOVE（跨屏移动结束）检测到尺寸未变而不覆盖目标分辨率。
                        }
                    }
                    catch (Exception ex) { Program.Log("AutoFollowScreen apply failed: " + ex.GetBaseException().Message); }
                }
                else if (!_followScreenRes && rdp != null)
                {
                    // 手动锁定分辨率：跨屏后把窗口重新匹配回手动分辨率比例（放不下则等比缩小）。
                    // Windows 跨屏 / 匹配会改变窗口尺寸，若被动态模式当作"拉窗口"，会用窗口尺寸覆盖手动值
                    // （如 1366×768 被改成 1364×760），导致不保持、菜单不打勾。
                    // 这里还原窗口后预置 lastRequestedDisplaySize=还原后的窗口尺寸，使随后的 ApplyDisplaySize 跳过；
                    // 用户之后主动拉窗口（尺寸 ≠ lastRequested）仍会正常跟随。
                    MatchWindowToSession(currentSessW, currentSessH);
                    HoldCurrentDisplaySize();
                }
            }
        }

        protected override void WndProc(ref Message m)
        {
            // 最大化走 Windows 原生机制：标题栏按钮正确显示"还原"状态；
            // 最大化=工作区矩形，画面由 Letterbox 等比居中（ActiveX 不放大，比例不符时四周黑边，与全屏一致）。

            if (m.Msg == WM_HOTKEY && m.WParam.ToInt32() == 1)
            {
                ToggleFullscreen();
                return;
            }

            // 系统设置里改分辨率/插拔显示器时不触发 OnMove；按窗口所在屏重新适配（P3-12）
            if (m.Msg == WM_DISPLAYCHANGE)
            {
                AutoFollowScreen();
                return;
            }

            // 拖拽边框缩放或移动结束后：把 Windows 记录的"还原尺寸"同步为当前窗口矩形。
            // 否则 Windows 在后续移动时会把窗口还原到旧的还原尺寸（表现为移动自动缩小）。
            // 移动本身仍是 Windows 原生机制（分栏/双击/贴边贴合都保留），这里只修正它记住的状态。
            // 动态模式：用户主动拖拽缩放结束 → 会话分辨率跟随新窗口逻辑客户区（唯一的用户拉伸跟随点）。
            if (m.Msg == WM_EXITSIZEMOVE)
            {
                SyncRestoreRect();
                if (dynamicMode) QueueDisplayResize(true);
            }

            // 拖拽边框缩放时锁定宽高比 == 会话宽高比，拖出来就是无黑边的正确比例。
            if (!dynamicMode && m.Msg == WM_SIZING && aspect > 0)
            {
                RECT r = (RECT)Marshal.PtrToStructure(m.LParam, typeof(RECT));
                int bw = Width - ClientSize.Width;                 // 边框总宽
                int bh = Height - ClientSize.Height;               // 标题栏+边框总高
                Rect result;
                // 锚定当前窗口的实际四边，只移动被拖的边（纯函数见 Geometry.LockAspectRect），
                // 防止 Aero Snap 在缩放时把窗口吸附到屏幕顶部/边缘（传入 top=0）。
                if (Geometry.LockAspectRect(
                    new Rect { Left = r.Left, Top = r.Top, Right = r.Right, Bottom = r.Bottom },
                    new Rect { Left = Left, Top = Top, Right = Right, Bottom = Bottom },
                    bw, bh, aspect, m.WParam.ToInt32(), out result))
                {
                    r.Left = result.Left; r.Top = result.Top; r.Right = result.Right; r.Bottom = result.Bottom;
                    Marshal.StructureToPtr(r, m.LParam, false);
                    m.Result = (IntPtr)1;
                    return;
                }
            }

            // 移动/缩放完全交给 Windows 标准机制（base.WndProc），不拦截、不改尺寸。

            if (m.Msg == ClipboardFileRelay.WmClipboardUpdate)
            {
                if (clipboardRelay != null) clipboardRelay.OnClipboardChanged();
            }
            base.WndProc(ref m);
        }

        // 把 Windows 的"还原尺寸"同步为当前窗口矩形（仅当窗口处于正常状态）。
        private void SyncRestoreRect()
        {
            try
            {
                if (Handle == IntPtr.Zero || IsDisposed) return;
                var wp = new WINDOWPLACEMENT();
                wp.length = Marshal.SizeOf(typeof(WINDOWPLACEMENT));
                if (!GetWindowPlacement(Handle, ref wp)) return;
                if (wp.showCmd != SW_SHOWNORMAL) return;   // 最大化/最小化时不改
                wp.flags = 0;
                wp.rcNormalPosition = new RECT { Left = Left, Top = Top, Right = Right, Bottom = Bottom };
                SetWindowPlacement(Handle, ref wp);
            }
            catch (Exception ex) { Program.Log("SyncRestoreRect failed: " + ex.GetBaseException().Message); }
        }

        // 会话分辨率的唯一事实源：同步 currentSessW/H 与 aspect，杜绝双源背离（P2-11）。
        private void SetSessionResolution(int w, int h)
        {
            if (w <= 0 || h <= 0) return;
            currentSessW = w; currentSessH = h;
            aspect = (double)w / (double)h;
        }

        private void ApplyLetterbox()
        {
            if (rdp == null || rdp.IsDisposed) return;
            int cw = ClientSize.Width;
            int ch = ClientSize.Height;
            if (cw <= 0 || ch <= 0) return;
            int dw, dh, dx, dy;
            // ActiveX 永不放大（只 1:1，放不下才等比缩小）→ 消除 mstsc 放大灰底（纯函数见 Geometry.ComputeLetterbox）。
            Geometry.ComputeLetterbox(cw, ch,
                currentSessW > 0 ? currentSessW : 0,
                currentSessH > 0 ? currentSessH : 0,
                out dw, out dh, out dx, out dy);
            rdp.Location = new Point(dx, dy);
            rdp.Size = new Size(dw, dh);
            if (cw != _lbLastW || ch != _lbLastH || Math.Abs(aspect - _lbLastA) > 0.0001)
            {
                _lbLastW = cw; _lbLastH = ch; _lbLastA = aspect;
                Program.Log("Letterbox: aspect=" + aspect.ToString("0.0000")
                    + " client=" + cw + "x" + ch
                    + " rdp=" + dw + "x" + dh + " at (" + rdp.Location.X + "," + rdp.Location.Y + ")");
            }
        }

        // 动态模式布局：控件铺满客户区（会话分辨率随窗口尺寸实时变化）。
        private void SetDynamicLayout()
        {
            if (rdp == null || rdp.IsDisposed) return;
            rdp.SetBounds(0, 0, Math.Max(1, ClientSize.Width), Math.Max(1, ClientSize.Height));
        }

        // "显示"按钮下拉：选固定分辨率 / 动态分辨率，当前模式打勾（对两种模式分辨率按钮都可用）。
        private bool ShowDisplayModeMenuAt(Rectangle anchor)
        {
            var menu = MenuInstance(ref _displayMenu);
            menu.ShowImageMargin = false;
            menu.ShowCheckMargin = true;   // 显示勾选列，标记当前生效的模式
            var itemFixed = new ToolStripMenuItem(Strings.T("menu_fixed"));
            itemFixed.Click += delegate { SetDisplayMode(false); };
            itemFixed.Checked = !dynamicMode;
            var itemDynamic = new ToolStripMenuItem(Strings.T("menu_dynamic"));
            itemDynamic.Click += delegate { SetDisplayMode(true); };
            itemDynamic.Checked = dynamicMode;
            menu.Items.Add(itemFixed);
            menu.Items.Add(itemDynamic);
            Point pos = new Point(anchor.Left, anchor.Bottom);
            var wa = Screen.FromControl(this).WorkingArea;
            pos.X = Math.Max(wa.Left, Math.Min(pos.X, wa.Right - 120));
            pos.Y = Math.Max(wa.Top, Math.Min(pos.Y, wa.Bottom - 60));
            menu.Show(pos);
            return true;
        }

        // "更多"按钮下拉：关于 / 打开日志 / 检查更新（复用预留的 AboutBox、OpenLogFolder）
        private bool ShowMoreMenuAt(Rectangle anchor)
        {
            var menu = MenuInstance(ref _moreMenu);
            menu.ShowImageMargin = false;
            menu.ShowCheckMargin = false;
            var itemReconnect = new ToolStripMenuItem(Strings.T("menu_reconnect"));
            itemReconnect.Click += delegate { Reconnect(); };
            var itemAbout = new ToolStripMenuItem(Strings.T("menu_about"));
            itemAbout.Click += delegate { AboutBox(); };
            var itemLog = new ToolStripMenuItem(Strings.T("menu_open_log"));
            itemLog.Click += delegate { OpenLogFolder(); };
            var itemUpdate = new ToolStripMenuItem(Strings.T("menu_check_update"));
            itemUpdate.Click += delegate
            {
                try { Process.Start("https://github.com/raydoomed/Ray-Desktop/releases"); }
                catch (Exception ex) { Program.Log("Could not open update page: " + ex.GetBaseException().Message); }
            };
            menu.Items.Add(itemReconnect);
            menu.Items.Add(itemAbout);
            menu.Items.Add(itemLog);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(itemUpdate);
            Point pos = new Point(anchor.Left, anchor.Bottom);
            var wa = Screen.FromControl(this).WorkingArea;
            pos.X = Math.Max(wa.Left, Math.Min(pos.X, wa.Right - 120));
            pos.Y = Math.Max(wa.Top, Math.Min(pos.Y, wa.Bottom - 60));
            menu.Show(pos);
            return true;
        }

        private void SetDisplayMode(bool mode)
        {
            if (dynamicMode == mode) return;
            if (mode)
            {
                // 进入动态前，记住当前固定分辨率；切回固定时恢复它（而不是动态时随窗口变小的临时值）
                _fixedSessW = currentSessW; _fixedSessH = currentSessH;
            }
            dynamicMode = mode;
            Program.Log("Display mode switched to " + (mode ? "dynamic" : "fixed"));
            // F11 全屏热键仅在固定模式注册（动态模式画面铺满、窗口自由，不提供 F11）
            if (Handle != IntPtr.Zero)
            {
                if (mode)
                {
                    if (!UnregisterHotKey(Handle, 1))
                        Program.Log("Could not unregister F11 hotkey on switching to dynamic; error " + Marshal.GetLastWin32Error());
                }
                else if (!RegisterHotKey(Handle, 1, 0, VK_F11))
                {
                    Program.Log("Could not register F11 hotkey on switching to fixed; Windows error " + Marshal.GetLastWin32Error());
                }
            }
            if (mode)
            {
                // 切到动态：
                //  - 窗口未被拉伸（逻辑客户区 == 固定模式匹配尺寸）→ 保持当前会话分辨率。
                //    否则固定模式"你的屏幕"下窗口会因工作区放不下而等比缩小显示（客户区<屏幕原生），
                //    若一律跟随客户区会把"你的屏幕"误改成缩放值（偏移）。
                //  - 窗口被用户拉伸过（逻辑客户区 != 匹配尺寸）→ 会话跟随窗口逻辑客户区。
                SetDynamicLayout();
                bool stretched = _fixedMatchClient != Size.Empty &&
                    (Math.Abs(ClientSize.Width - _fixedMatchClient.Width) > 3 ||
                     Math.Abs(ClientSize.Height - _fixedMatchClient.Height) > 3);
                if (stretched) QueueDisplayResize();
            }
            else
            {
                // 切到固定：停掉动态模式的 resize 定时器，防止残留 ApplyDisplaySize 用固定布局的
                // rdp 尺寸覆盖固定分辨率（否则分辨率菜单不会打勾"你的屏幕"）。
                if (displayResizeTimer != null) displayResizeTimer.Stop();
                // 恢复进入动态前的固定分辨率（从未固定过则用主机屏幕），把会话分辨率也改回去，
                // 再让窗口匹配该比例并 Letterbox 居中。
                int sw = Screen.PrimaryScreen.Bounds.Width;
                int sh = Screen.PrimaryScreen.Bounds.Height;
                if (sw <= 0 || sh <= 0) { sw = 1280; sh = 720; }
                int fw = (_fixedSessW > 0 && _fixedSessH > 0) ? _fixedSessW : sw;
                int fh = (_fixedSessW > 0 && _fixedSessH > 0) ? _fixedSessH : sh;
                fw &= ~1;
                try
                {
                    var client = (IMsRdpClient9)rdp.GetOcxObject();
                    if (client.Connected == 1)
                    {
                        float dpi = 96f;
                        try { using (var gr = rdp.CreateGraphics()) dpi = gr.DpiX; } catch (Exception ex) { Program.Log("Could not read RDP DPI from graphics: " + ex.GetBaseException().Message); }
                        uint pw = Geometry.ToMillimeters(fw, dpi);
                        uint ph = Geometry.ToMillimeters(fh, dpi);
                        client.UpdateSessionDisplaySettings((uint)fw, (uint)fh, pw, ph, 0, 100, 100);
                        _resChangePending = true;   // 改分辨率期间屏蔽 750ms readback 的旧值干扰
                        Program.Log("Display mode fixed: session set to " + fw + "x" + fh);
                    }
                }
                catch (Exception ex) { Program.Log("Could not set fixed session resolution: " + ex.GetBaseException().Message); }
                SetSessionResolution(fw, fh);
                MatchWindowToSession(fw, fh);
                ApplyLetterbox();
                // 900ms 后读回真实分辨率校正，并放行 750ms readback（与分辨率菜单一致）
                if (lateResizeTimer != null) { lateResizeTimer.Stop(); lateResizeTimer.Dispose(); }   // P1-1：先释放旧实例，防覆盖后旧 timer 的 Stop() 误停新实例
                lateResizeTimer = new Timer { Interval = 900 };
                lateResizeTimer.Tick += delegate
                {
                    lateResizeTimer.Stop(); lateResizeTimer.Dispose();
                    try
                    {
                        var c2 = (IMsRdpClient9)rdp.GetOcxObject();
                        int dw = c2.DesktopWidth, dh = c2.DesktopHeight;
                        if (dw > 0 && dh > 0) SetSessionResolution(dw, dh);
                    }
                    catch (Exception ex) { Program.Log("Resolution readback failed: " + ex.GetBaseException().Message); }
                    _resChangePending = false;
                    MatchWindowToSession(currentSessW, currentSessH);
                    if (toolBar != null) toolBar.FollowHost(this);
                };
                lateResizeTimer.Start();
            }
            if (toolBar != null)
            {
                toolBar.IsDynamicMode = mode;
                toolBar.Invalidate();
                toolBar.FollowHost(this);
            }
        }

        // 预置 lastRequestedDisplaySize 为当前窗口尺寸，使随后触发的 ApplyDisplaySize
        // 因"请求尺寸未变"而跳过——用于动态选分辨率/跨屏后，阻止把刚设定的会话分辨率改掉。
        private void HoldCurrentDisplaySize()
        {
            try
            {
                // 与 ApplyDisplaySize 统一：主窗体逻辑客户区 == 会话分辨率（this.ClientSize 已是逻辑值，不再除 DPI）
                int w = Math.Max(200, Math.Min(8192, ClientSize.Width));
                int h = Math.Max(200, Math.Min(8192, ClientSize.Height));
                lastRequestedDisplaySize = new Size(w & ~1, h);
            }
            catch (Exception ex) { Program.Log("HoldCurrentDisplaySize failed: " + ex.GetBaseException().Message); }
        }

        private void OpenLogFolder()
        {
            try
            {
                string dir = Program.LogDirectory;
                string log = Path.Combine(dir, "child-session.log");
                if (File.Exists(log)) Process.Start("explorer.exe", "/select,\"" + log + "\"");
                else Process.Start("explorer.exe", dir);
            }
            catch (Exception ex) { Program.Log("Could not open log folder: " + ex.GetBaseException().Message); }
        }

        private void AboutBox()
        {
            MessageBox.Show(this, Strings.T("about_body"), Strings.T("menu_about"),
                MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        private bool ToggleFullscreen()
        {
            fullscreen = !fullscreen;
            if (toolBar != null) toolBar.Fullscreen = fullscreen;   // 提前标记，让 Bounds 变更触发的 FollowHost 用全屏/窗口定位
            if (fullscreen)
            {
                prevBounds = Bounds;
                prevBorder = FormBorderStyle;
                FormBorderStyle = FormBorderStyle.None;
                Bounds = Screen.FromControl(this).Bounds;   // 全屏：窗口=屏幕，控件由 Letterbox 等比居中（画面比例），四周纯黑边
            }
            else
            {
                FormBorderStyle = prevBorder;
                if (toolBar != null) toolBar.ResetToTopCenter();   // 退出全屏统一重置到顶部居中（避免旧偏移跑出窗口）
                // 全屏是临时放大：退出一律恢复全屏前窗口原状。动态模式下，下面 QueueDisplayResize
                // 会把会话分辨率跟随回还原后的窗口尺寸（= 全屏前的会话分辨率）。
                Bounds = prevBounds;
            }
            if (dynamicMode) { SetDynamicLayout(); QueueDisplayResize(); }   // 动态全屏：会话分辨率跟随窗口（进入=整屏，退出=还原窗口）
            else ApplyLetterbox();                 // 固定模式全屏：画面等比居中，四周黑边
            if (toolBar != null) toolBar.UpdateState(fullscreen);
            Program.Log("Fullscreen " + (fullscreen ? "enter" : "exit") + " Bounds=" + Bounds + " client=" + ClientSize.Width + "x" + ClientSize.Height
                + " aspect=" + aspect.ToString("0.0000") + " sess=" + currentSessW + "x" + currentSessH);
            return fullscreen;
        }

        private void SetStatus(string message)
        {
            string t = message + " — Ray Desktop";
            if (Text != t) Text = t;   // 已连接状态定时器每 750ms 调用，值未变时跳过赋值，避免无收益的标题重绘 churn
        }

        // 桌面完全显示、分辨率就位后创建悬浮工具栏
        private void CreateToolbar()
        {
            if (toolBar != null) return;
            toolBar = new ToolbarForm();
            toolBar.ToggleFullscreen = ToggleFullscreen;
            toolBar.ShowResMenu = ShowResMenuAt;
            toolBar.ShowDisplayModeMenu = ShowDisplayModeMenuAt;
            toolBar.ShowMoreMenu = ShowMoreMenuAt;
            toolBar.IsDynamicMode = dynamicMode;
            toolBar.FollowHost(this);
            toolBar.Show();
        }

        // 下拉菜单统一样式：深色底 + 白字 + 白勾 + 深色分隔/边框，匹配工具栏黑条风格
        private static ContextMenuStrip MakeToolbarMenu()
        {
            var menu = new ContextMenuStrip();
            menu.Renderer = new ToolbarMenuRenderer();
            menu.ShowImageMargin = false;
            return menu;
        }

        // 下拉菜单复用单实例：null 或已释放才重建，每次 Show 前清空内容。
        // 不可在 Closed 中 Dispose——该事件在点击项 OnItemClicked→SetVisibleCore 流程期间触发，
        // 过早释放会让后续访问句柄抛 ObjectDisposedException（实测）。复用可同时规避泄漏与过早释放。
        private ContextMenuStrip MenuInstance(ref ContextMenuStrip field)
        {
            if (field == null || field.IsDisposed) field = MakeToolbarMenu();
            field.Items.Clear();
            return field;
        }

        private sealed class ToolbarMenuRenderer : ToolStripProfessionalRenderer
        {
            internal ToolbarMenuRenderer() : base(new ToolbarMenuColorTable()) { }

            protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
            {
                if (e.Item is ToolStripSeparator) { base.OnRenderItemText(e); return; }
                e.TextColor = e.Item.Selected ? Color.White : Color.FromArgb(216, 216, 222);
                base.OnRenderItemText(e);
            }

            protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
            {
                if (e.Item.Selected)
                {
                    var r = new Rectangle(2, 1, e.Item.Width - 4, e.Item.Height - 2);
                    using (var b = new SolidBrush(Color.FromArgb(70, 255, 255, 255)))
                        e.Graphics.FillRectangle(b, r);
                }
            }

            protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
            {
                var r = e.ImageRectangle;
                using (var b = new SolidBrush(Color.FromArgb(36, 36, 42)))
                    e.Graphics.FillRectangle(b, r);
                using (var p = new Pen(Color.White, 1.7f))
                {
                    p.StartCap = LineCap.Round; p.EndCap = LineCap.Round;
                    int x = r.X + 2, cy = r.Y + r.Height / 2 - 1;
                    e.Graphics.DrawLines(p, new[] {
                        new Point(x, cy),
                        new Point(x + 4, cy + 4),
                        new Point(x + 8, cy - 2)
                    });
                }
            }

            protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
            {
                using (var p = new Pen(Color.FromArgb(58, 58, 66)))
                    e.Graphics.DrawLine(p, 10, e.Item.Height / 2, e.Item.Width - 10, e.Item.Height / 2);
            }

            protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
            {
                using (var p = new Pen(Color.FromArgb(60, 60, 70)))
                    e.Graphics.DrawRectangle(p, new Rectangle(0, 0, e.AffectedBounds.Width - 1, e.AffectedBounds.Height - 1));
            }
        }

        private sealed class ToolbarMenuColorTable : ProfessionalColorTable
        {
            public override Color ToolStripDropDownBackground { get { return Color.FromArgb(32, 32, 38); } }
            public override Color ImageMarginGradientBegin { get { return Color.FromArgb(32, 32, 38); } }
            public override Color ImageMarginGradientMiddle { get { return Color.FromArgb(32, 32, 38); } }
            public override Color ImageMarginGradientEnd { get { return Color.FromArgb(32, 32, 38); } }
            public override Color MenuBorder { get { return Color.FromArgb(60, 60, 70); } }
            public override Color MenuItemBorder { get { return Color.Transparent; } }
            public override Color MenuItemSelected { get { return Color.FromArgb(48, 48, 56); } }
            public override Color MenuItemSelectedGradientBegin { get { return Color.FromArgb(48, 48, 56); } }
            public override Color MenuItemSelectedGradientEnd { get { return Color.FromArgb(48, 48, 56); } }
            public override Color CheckBackground { get { return Color.FromArgb(36, 36, 42); } }
            public override Color CheckSelectedBackground { get { return Color.FromArgb(36, 36, 42); } }
            public override Color CheckPressedBackground { get { return Color.FromArgb(36, 36, 42); } }
            public override Color SeparatorDark { get { return Color.FromArgb(58, 58, 66); } }
            public override Color SeparatorLight { get { return Color.FromArgb(58, 58, 66); } }
        }

        // 分辨率按钮下拉：候选 = 当前屏幕（推荐）+ 一组常见宽屏分辨率。
        // 这是独立的"会话分辨率"设置，对固定/动态两种模式都生效。
        private bool ShowResMenuAt(Rectangle anchor)
        {
            var menu = MenuInstance(ref _resMenu);
            menu.ShowImageMargin = false;
            menu.ShowCheckMargin = true;   // 显示勾选列，标记当前生效的分辨率
            int sw = Screen.FromControl(this).Bounds.Width;   // "你的屏幕"= 窗口当前所在屏幕（多显示器跟随）
            int sh = Screen.FromControl(this).Bounds.Height;
            Program.Log("ResMenu open: sess=" + currentSessW + "x" + currentSessH +
                        " screen=" + sw + "x" + sh + " follow=" + _followScreenRes);
            var itemScreen = new ToolStripMenuItem(Strings.T("res_your_screen", sw, sh));
            itemScreen.Click += delegate { ApplyResolutionPick(0, 0); };
            itemScreen.Checked = (currentSessW == (sw & ~1) && currentSessH == sh);   // 应用时宽度偶数化，打勾按偶数化后比较一致（P2-8）
            menu.Items.Add(itemScreen);
            // 常见宽屏分辨率，按像素从大到小排序
            int[,] presets = {
                {3840, 2160}, {2560, 1440}, {2560, 1080}, {1920, 1080}, {1366, 768}, {1280, 720}
            };
            for (int i = 0; i < presets.GetLength(0); i++)
            {
                int w = presets[i, 0], h = presets[i, 1];
                if (w == sw && h == sh) continue;   // 与"你的屏幕"重复则跳过
                var item = new ToolStripMenuItem(w + " × " + h);
                item.Click += delegate { ApplyResolutionPick(w, h); };
                item.Checked = (currentSessW == w && currentSessH == h);
                menu.Items.Add(item);
            }
            Point pos = new Point(anchor.Left, anchor.Bottom);
            var wa = Screen.FromControl(this).WorkingArea;
            pos.X = Math.Max(wa.Left, Math.Min(pos.X, wa.Right - 140));
            pos.Y = Math.Max(wa.Top, Math.Min(pos.Y, wa.Bottom - 60));
            menu.Show(pos);
            return true;
        }

        // 应用选定的会话分辨率：确认后「不重连」动态改（UpdateSessionDisplaySettings），
        // 更新宽高比并让窗口匹配新分辨率（优先 1:1，放不下等比缩小铺满）。
        private void ApplyResolutionPick(int pickW, int pickH)
        {
            int sw = Screen.FromControl(this).Bounds.Width;   // "你的屏幕"= 窗口当前所在屏幕（多显示器跟随）
            int sh = Screen.FromControl(this).Bounds.Height;
            int w = pickW, h = pickH;
            if (w == 0) { w = sw; h = sh; }   // "你的屏幕"：跨屏继续跟随
            if (w <= 0 || h <= 0) return;
            w &= ~1;   // RDP 要求偶数像素宽（与连接时一致），避免奇数宽导致 UpdateSessionDisplaySettings 失败

            var result = MessageBox.Show(this,
                Strings.T("res_confirm_body", w, h),
                Strings.T("res_confirm_title"), MessageBoxButtons.OKCancel, MessageBoxIcon.Question);
            if (result != DialogResult.OK) return;

            // 确认成功后才提交跟随状态：点"取消"不得改写跨屏行为（用 pickW 判定，w 已被偶数化改写）。
            _followScreenRes = (pickW == 0);
            ApplySessionResolution(w, h, true);
        }

        // 核心：不重连动态改会话分辨率（UpdateSessionDisplaySettings），更新宽高比并让窗口匹配新分辨率。
        // confirmUi=true 时（手动菜单）失败弹提示框；自动跨屏跟随传 false，仅记日志、不打扰。
        private void ApplySessionResolution(int w, int h, bool confirmUi)
        {
            try
            {
                var client = (IMsRdpClient9)rdp.GetOcxObject();
                if (client.Connected != 1)
                {
                    if (confirmUi)
                        MessageBox.Show(this, Strings.T("res_not_connected"), Strings.T("res_confirm_title"),
                            MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }
                float dpi = 96f;
                try { using (var gr = rdp.CreateGraphics()) dpi = gr.DpiX; } catch (Exception ex) { Program.Log("Could not read RDP DPI from graphics: " + ex.GetBaseException().Message); }
                uint pw = Geometry.ToMillimeters(w, dpi);
                uint ph = Geometry.ToMillimeters(h, dpi);
                client.UpdateSessionDisplaySettings((uint)w, (uint)h, pw, ph, 0, 100, 100);
                Program.Log("Resolution menu: session set to " + w + "x" + h);
                _resChangePending = true;   // 改分辨率期间屏蔽 UpdateConnectionStatus 的旧值 readback 干扰
                SetSessionResolution(w, h);
                if (toolBar != null) toolBar.ResetToTopCenter();   // 分辨率变化→工具栏回顶部居中，避免旧偏移跑出变小后的窗口
                // 窗口匹配新比例（非全屏：窗口比例=画面比例 → 控件铺满无黑边；全屏：只等比重排控件）。
                // 固定/动态一致：选什么分辨率，会话分辨率就是什么，窗口自动匹配该比例铺满。
                MatchWindowToSession(w, h);
                // 动态模式下选分辨率后，预置 lastRequestedDisplaySize 为当前窗口尺寸，
                // 使 WM_EXITSIZEMOVE 触发的 ApplyDisplaySize 因尺寸未变而跳过，不覆盖刚选定的分辨率（否则要选两次才生效）。
                if (dynamicMode) HoldCurrentDisplaySize();
                // ActiveX 显示面异步重建：等 900ms 后读回真实分辨率，再校正一次（对齐 workbuddy）
                if (lateResizeTimer != null) { lateResizeTimer.Stop(); lateResizeTimer.Dispose(); }   // P1-1：先释放旧实例，防覆盖后旧 timer 的 Stop() 误停新实例
                lateResizeTimer = new Timer { Interval = 900 };
                lateResizeTimer.Tick += delegate
                {
                    lateResizeTimer.Stop(); lateResizeTimer.Dispose();
                    try
                    {
                        var c2 = (IMsRdpClient9)rdp.GetOcxObject();
                        int dw = c2.DesktopWidth, dh = c2.DesktopHeight;
                        if (dw > 0 && dh > 0)
                        {
                            SetSessionResolution(dw, dh);
                            Program.Log("Resolution menu readback " + dw + "x" + dh);
                        }
                    }
                    catch (Exception ex) { Program.Log("Resolution menu readback failed: " + ex.GetBaseException().Message); }
                    _resChangePending = false;   // 校正完成，放行 UpdateConnectionStatus 的 readback
                    MatchWindowToSession(currentSessW, currentSessH);   // 内部已 ApplyLetterbox
                    // 匹配后 lastRequested 更新为最终窗口逻辑客户区：跨屏/缩放后窗口尺寸（≠目标分辨率）
                    // 已预置，使随后的 WM_EXITSIZEMOVE（跨屏移动结束）检测到尺寸未变而不覆盖目标分辨率。
                    if (dynamicMode) HoldCurrentDisplaySize();
                    // 画面刷新改由 ActiveX 的 OnResize 事件从源头驱动（见 HandleRemoteResize）
                    if (toolBar != null) toolBar.FollowHost(this);
                };
                lateResizeTimer.Start();
            }
            catch (Exception ex)
            {
                _resChangePending = false;
                Program.Log("Resolution menu apply failed: " + ex);
                if (confirmUi)
                    MessageBox.Show(this, Strings.T("res_fail") + ex.GetBaseException().Message,
                        Strings.T("res_confirm_title"), MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        // 把窗口匹配到指定会话分辨率：客户区宽高比 == 会话宽高比，优先 1:1（最锐利），
        // 放不下才等比缩小到能完整放进工作区；用 SetWindowPos 直接调窗口尺寸（对齐 workbuddy）。
        // 非全屏时窗口比例 == 画面比例 → 控件铺满客户区，零黑边零灰底。
        private void MatchWindowToSession(int sessW, int sessH)
        {
            if (Handle == IntPtr.Zero || IsDisposed) return;
            if (sessW <= 0 || sessH <= 0) return;
            if (fullscreen) { ApplyLetterbox(); return; }   // 全屏时窗口保持铺满屏幕，只重排控件
            // 最小尺寸跟随当前画面比例，防止后续拖动破坏比例
            MinimumSize = new Size(320, (int)(320 / aspect + 0.5));
            var area = Screen.FromControl(this).WorkingArea;
            if (area.Width <= 0 || area.Height <= 0) area = Screen.FromControl(this).Bounds;
            int bw = Width - ClientSize.Width;
            int bh = Height - ClientSize.Height;
            int availW = area.Width - bw - 8;
            int availH = area.Height - bh - 8;
            if (availW < 320) availW = 320;
            if (availH < 240) availH = 240;
            double s = Math.Min((double)availW / sessW, (double)availH / sessH);
            if (s > 1.0) s = 1.0;   // 能 1:1 就 1:1
            int cw = (int)(sessW * s + 0.5);
            int ch = (int)(sessH * s + 0.5);
            if (!dynamicMode) _fixedMatchClient = new Size(cw, ch);   // 固定模式记录匹配目标客户区（切动态时判断是否被拉伸）
            int ww = cw + bw, wh = ch + bh;
            int L = area.Left + (area.Width - ww) / 2;
            int T = area.Top + (area.Height - wh) / 2;
            if (L < area.Left) L = area.Left;
            if (T < area.Top) T = area.Top;
            SetWindowPos(Handle, IntPtr.Zero, L, T, ww, wh,
                SWP_NOZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED);
            SyncRestoreRect();   // 同步 Windows 还原尺寸：改分辨率后首次移动窗口，拖动轮廓不再用旧尺寸
            Program.Log("窗口匹配会话 " + sessW + "x" + sessH + " → 窗口 " + ww + "x" + wh
                + " 实际客户区 " + ClientSize.Width + "x" + ClientSize.Height
                + "（缩放 " + s.ToString("0.000") + " aspect=" + aspect.ToString("0.0000") + "）");
            if (dynamicMode) SetDynamicLayout();   // 动态模式：控件铺满客户区（分辨率随窗口）
            else ApplyLetterbox();                 // 固定模式：控件等比居中
            if (toolBar != null) toolBar.FollowHost(this);
        }

        private void ConnectChildSession()
        {
            if (connecting || closing || rdp.IsDisposed) return;
            try
            {
                bool childSessionsEnabled;
                if (!Program.WTSIsChildSessionsEnabled(out childSessionsEnabled))
                {
                    int error = Marshal.GetLastWin32Error();
                    throw new InvalidOperationException(Strings.T("msg_check_fail", error));
                }
                if (!childSessionsEnabled)
                {
                    connecting = false;
                    Program.Log("Child sessions are disabled on this computer");
                    RequestEnableChildSessions();
                    return;
                }
                Program.Log("Child sessions enabled on this computer");

                object ocx = rdp.GetOcxObject();
                AttachConnectionEvents(ocx);
                var client = (IMsRdpClient9)ocx;
                int state = client.Connected;
                if (state == 1)
                {
                    connecting = false;
                    SetStatus(Strings.T("status_connected"));
                    statusTimer.Start();
                    if (dynamicMode) QueueDisplayResize();
                    else ApplyLetterbox();
                    return;
                }
                if (state == 2)
                {
                    connecting = true;
                    connectionStarted = DateTime.Now;
                    SetStatus(Strings.T("status_connecting"));
                    statusTimer.Start();
                    return;
                }

                connecting = true;
                connectionStarted = DateTime.Now;
                SetStatus(Strings.T("status_connecting_win"));
                Program.Log("Begin child-session setup");
                client.Server = "localhost";
                Program.Log("Server set");
                if (!dynamicMode)
                {
                    // 用当前会话分辨率（首次=主屏；用户选过或重连后=用户所选），避免断线重连把会话拉回主屏（P2-6）
                    int sw = (currentSessW > 0 && currentSessH > 0) ? currentSessW : Screen.PrimaryScreen.Bounds.Width;
                    int sh = (currentSessW > 0 && currentSessH > 0) ? currentSessH : Screen.PrimaryScreen.Bounds.Height;
                    if (sw <= 0 || sh <= 0) { sw = 1280; sh = 720; }
                    sw &= ~1;   // RDP 要求偶数像素宽（协议约束）
                    sh &= ~1;   // 高度非协议要求，此处为偶数对齐（避免奇数高度时比例键与菜单打勾不一致）
                    client.DesktopWidth = sw;
                    client.DesktopHeight = sh;
                    SetSessionResolution(sw, sh);
                    Program.Log("Requesting child-session desktop at " + sw + "x" + sh + " from the start (fixed)");
                }
                var extended = (IMsRdpExtendedSettings)ocx;
                Program.Log("QI IMsRdpExtendedSettings succeeded");
                object connectToChild = true;
                extended.set_Property("ConnectToChildSession", ref connectToChild);
                Program.Log("ConnectToChildSession set true");
                client.AdvancedSettings9.EnableCredSspSupport = true;
                client.AdvancedSettings9.SmartSizing = true;
                client.AdvancedSettings9.RedirectClipboard = false;
                client.AdvancedSettings9.RedirectDrives = true;
                EnableDriveRedirection(ocx);
                client.AdvancedSettings9.EnableWindowsKey = 1;
                client.AdvancedSettings9.AcceleratorPassthrough = 1;
                client.SecuredSettings3.KeyboardHookMode = 1;
                Program.Log("RDP clipboard redirection disabled; Ray Desktop bridges clipboard text and files between the two sessions instead; all local drives are redirected read-write; Windows key combinations enabled");
                ((IMsTscAx)ocx).Connect();
                Program.Log("Connect() returned");
                statusTimer.Start();
            }
            catch (Exception ex)
            {
                Program.Log("Connect setup failed: " + ex);
                connecting = false;
                var message = Strings.T("msg_connect_fail") + ex.GetBaseException().Message;
                SetStatus(message);
            }
        }

        private static void EnableDriveRedirection(object ocx)
        {
            try
            {
                var drives = ((IMsRdpClientNonScriptable3)ocx).DriveCollection;
                drives.RescanDrives(true);
                uint count = drives.DriveCount;
                Program.Log("RDP drive redirection collection contains " + count + " drive(s)");
                for (uint i = 0; i < count; i++)
                {
                    var drive = drives.get_DriveByIndex(i);
                    drive.RedirectionState = true;
                    Program.Log("RDP drive redirection enabled for " + drive.Name.TrimEnd('\0') +
                        " (state=" + drive.RedirectionState + ")");
                }
                if (count == 0)
                    Program.Log("No local drives were exposed by the RDP ActiveX drive collection");
            }
            catch (Exception ex)
            {
                Program.Log("Could not enumerate or enable individual RDP drives: " + ex.GetBaseException().Message);
            }
        }

        private void RequestEnableChildSessions()
        {
            string helperPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "EnableChildSessions.exe");
            if (!File.Exists(helperPath))
            {
                SetStatus(Strings.T("status_disabled_missing_helper"));
                return;
            }

            // P1-9②: 签名缺失时的兜底——拒绝版本与主程序不一致的辅助程序，防可写目录中被替换的二进制提权
            try
            {
                string appVer = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version.ToString();
                string helperVer = System.Diagnostics.FileVersionInfo.GetVersionInfo(helperPath).FileVersion;
                if (!string.Equals(helperVer, appVer, StringComparison.Ordinal))
                {
                    Program.Log("Refusing to launch setup helper: version mismatch (helper=" + helperVer + " app=" + appVer + ")");
                    SetStatus(Strings.T("status_launch_helper_fail") + " (版本不匹配 / version mismatch)");
                    return;
                }
            }
            catch (Exception ex)
            {
                Program.Log("Could not verify setup helper version before elevation: " + ex.GetBaseException().Message);
            }

            try
            {
                var startInfo = new ProcessStartInfo
                {
                    FileName = helperPath,
                    UseShellExecute = true,
                    Verb = "runas"
                };
                Process helper = Process.Start(startInfo);
                if (helper != null) helper.Dispose();
                SetStatus(Strings.T("status_requesting"));
                Program.Log("Launched elevated Child Sessions setup helper");
            }
            catch (Win32Exception ex)
            {
                if (ex.NativeErrorCode == 1223)
                {
                    SetStatus(Strings.T("status_elevation_canceled"));
                    Program.Log("User canceled UAC elevation for Child Sessions setup");
                }
                else
                {
                    SetStatus(Strings.T("status_launch_helper_fail") + ex.Message);
                    Program.Log("Could not launch elevated setup helper: " + ex);
                }
            }
            catch (Exception ex)
            {
                SetStatus(Strings.T("status_launch_helper_fail") + ex.GetBaseException().Message);
                Program.Log("Could not launch elevated setup helper: " + ex);
            }
        }

        private void UpdateConnectionStatus()
        {
            try
            {
                int state = ((IMsRdpClient9)rdp.GetOcxObject()).Connected;
                if (state != lastLoggedState)
                {
                    Program.Log("Connected state changed to " + state);
                    lastLoggedState = state;
                    if (state == 1)
                    {
                        LogRedirectionDiagnostics();
                        connectionReadyAt = DateTime.Now.AddSeconds(2);
                        resizeRetryCount = 0;
                        lastFailedDisplaySize = Size.Empty;
                        nextResizeAttempt = DateTime.MinValue;
                        // 桌面完全显示、分辨率稳定后才创建工具栏，避免启动早期工具栏/气泡位置错误
                        if (readyTimer != null) { readyTimer.Stop(); readyTimer.Dispose(); }   // P1-1b：先释放旧实例，防覆盖泄漏与重复触发
                        readyTimer = new System.Windows.Forms.Timer();
                        readyTimer.Interval = 4000;
                        readyTimer.Tick += delegate
                        {
                            readyTimer.Stop();
                            readyTimer.Dispose();
                            CreateToolbar();
                        };
                        readyTimer.Start();
                    }
                    else if (state == 0)
                    {
                        connectionReadyAt = DateTime.MinValue;
                    }
                }
                if (connecting && (state == 0 || state == 2) &&
                    DateTime.Now - connectionStarted > TimeSpan.FromSeconds(45))
                {
                    connecting = false;
                    statusTimer.Stop();
                    EnterReconnectState();   // 启重连探测；须在 Disconnect 前，闭合其同步派发的 HandleDisconnected 递归
                    try { ((IMsTscAx)rdp.GetOcxObject()).Disconnect(); } catch (Exception ex) { Program.Log("Disconnect after connection timeout failed: " + ex.GetBaseException().Message); }
                    SetStatus(Strings.T("status_timeout"));
                    Program.Log("Child-session connection timed out after 45 seconds (Connected=" + state + ")");
                    return;
                }

                if (state == 0)
                {
                    if (connecting) SetStatus(Strings.T("status_waiting"));
                    else SetStatus(Strings.T("status_not_connected"));
                }
                else if (state == 1)
                {
                    connecting = false;
                    SetStatus(Strings.T("status_connected"));
                    // 动态跟随只在用户意图触发（WM_EXITSIZEMOVE 拖拽结束 / 最大化进入），
                    // 不在此每 750ms 自动 QueueDisplayResize——否则跨屏/选分辨率后窗口缩放值
                    // （≠目标分辨率）会被周期性 ApplyDisplaySize 反向覆盖，破坏"你的屏幕"/1:1 打勾。
                    if (!dynamicMode)
                    {
                        try
                        {
                            var c = (IMsRdpClient9)rdp.GetOcxObject();
                            int dw = c.DesktopWidth, dh = c.DesktopHeight;
                            // 只在分辨率真正变化时才更新 + 重排（避免每 750ms 无条件 ApplyLetterbox 的冗余重排）；
                            // 改分辨率（ApplyResolutionPick）期间屏蔽，防止读到过渡旧值把布局打回上一下分辨率。
                            if (dw > 0 && dh > 0 && !_resChangePending && (dw != currentSessW || dh != currentSessH))
                            {
                                SetSessionResolution(dw, dh);
                                Program.Log("Session resolution readback " + dw + "x" + dh + "; aspect=" + aspect.ToString("0.0000"));
                                ApplyLetterbox();
                            }
                        }
                        catch (Exception ex)
                        {
                            Program.Log("Could not read back session resolution: " + ex.GetBaseException().Message);
                        }
                    }
                }
                else if (state == 2)
                {
                    SetStatus(Strings.T("status_connecting"));
                }
                else SetStatus(Strings.T("status_state") + state);
            }
            catch (Exception ex)
            {
                Program.Log("Could not read connection state: " + ex.GetBaseException().Message);
                SetStatus(Strings.T("status_read_fail") + ex.GetBaseException().Message);
                statusTimer.Stop();   // 状态读不到，继续 750ms 轮询无意义
                if (!closing) EnterReconnectState();   // P1-2：兑现注释承诺——由 auto-reconnect 探测接管恢复，而非永久停摆（COM 读失败时断开事件不会来）
            }
        }

        private void AttachConnectionEvents(object ocx)
        {
            if (eventsAttached) return;
            rdpEvents = (IMsTscAxEvents_Event)ocx;
            rdpEvents.OnRemoteDesktopSizeChange += HandleRemoteResize;
            rdpEvents.OnDisconnected += HandleDisconnected;
            rdpEvents.OnFatalError += HandleFatalError;
            rdpEvents.OnLogonError += HandleLogonError;
            eventsAttached = true;
            Program.Log("RDP connection events attached");
        }

        private void LogRedirectionDiagnostics()
        {
            try
            {
                object ocx = rdp.GetOcxObject();
                var client = (IMsRdpClient9)ocx;
                var settings = client.AdvancedSettings9;
                Program.Log("RDP redirection settings after connect: RedirectClipboard=" + settings.RedirectClipboard +
                    ", RedirectDrives=" + settings.RedirectDrives + ", DisableRdpdr=" + settings.DisableRdpdr);
                Program.Log("Clipboard bridge active: it relays text and local file paths between the two sessions (functionally clipboard redirection is ON via the bridge); all local drives are redirected read-write");
            }
            catch (Exception ex)
            {
                Program.Log("Could not query RDP clipboard and drive redirection state after connect: " +
                    ex.GetBaseException().Message);
            }
        }

        private void HandleRemoteResize(int width, int height)
        {
            try
            {
                if (rdp == null || rdp.IsDisposed) return;
                if (InvokeRequired) { BeginInvoke((MethodInvoker)delegate { HandleRemoteResize(width, height); }); return; }
                // 会话分辨率真正变化时 ActiveX 触发此事件。本事件的职责是【强制 ActiveX 重绘画面】，
                // 不是改分辨率：跨屏/缩放时 rdp 尺寸变化会让 ActiveX 报告缩放窗口的逻辑值（如所在屏 2560×1600
                // 被报成窗口缩放 1978×1236），若用它覆盖 currentSessW 就会把"你的屏幕"打勾破坏。
                // 会话分辨率只由 ApplySessionResolution（选/跨屏）与 ApplyDisplaySize（动态跟随，统一逻辑客户区）维护。
                Program.Log("OnRemoteDesktopSizeChange: session=" + currentSessW + "x" + currentSessH
                    + " client=" + ClientSize.Width + "x" + ClientSize.Height
                    + " reported=" + width + "x" + height);
                if (rdp.Size.Width > 2 && rdp.Size.Height > 2)
                    rdp.Size = new Size(rdp.Size.Width - 1, rdp.Size.Height - 1);
                ApplyLetterbox();
                if (toolBar != null) toolBar.FollowHost(this);
            }
            catch (Exception ex) { Program.Log("OnRemoteDesktopSizeChange handler: " + ex); }
        }

        private void HandleDisconnected(int reason)
        {
            // reconnectTimer.Enabled 说明已在重连流程中（如 Disconnect 同步派发本事件），直接闭合，避免覆盖外层状态。
            if (closing || IsDisposed || reconnectTimer.Enabled) return;
            connecting = false;
            statusTimer.Stop();
            var message = reason == 1
                ? Strings.T("status_disconnected_local")
                : Strings.T("status_disconnected", reason);
            if (reason == 0xC07)
                message += Strings.T("status_disconnected_acct");
            Program.Log(message);
            SetStatus(message);
            // 本地主动断开（reason==1）不自动重连；远端断开才进入自动重连。
            if (reason != 1) EnterReconnectState();
        }

        private void HandleFatalError(int errorCode)
        {
            if (closing || IsDisposed) return;
            connecting = false;
            statusTimer.Stop();
            var message = Strings.T("status_fatal", errorCode);
            Program.Log(message);
            SetStatus(message);
            EnterReconnectState();
        }

        private void HandleLogonError(int errorCode)
        {
            if (closing || IsDisposed) return;
            if (errorCode == -2)
            {
                Program.Log("Windows is continuing the child-session sign-in process");
                try
                {
                    if (((IMsRdpClient9)rdp.GetOcxObject()).Connected == 1)
                    {
                        connecting = false;
                        SetStatus(Strings.T("status_connected"));
                    }
                    else
                    {
                        SetStatus(Strings.T("status_continuing"));
                    }
                }
                catch (Exception ex)
                {
                    Program.Log("Could not read RDP state after continue-logon event: " + ex.GetBaseException().Message);
                }
                return;
            }

            var message = Strings.T("status_logon_error", errorCode);
            connecting = false;
            statusTimer.Stop();
            Program.Log(message);
            SetStatus(message);
            EnterReconnectState();
        }

        private void EnterReconnectState()
        {
            if (closing || IsDisposed || reconnectTimer == null) return;
            connecting = false;
            if (!reconnectTimer.Enabled)
            {
                reconnectAttempts = 0;
                reconnectTimer.Interval = 2000;
                reconnectTimer.Start();
                Program.Log("Entering auto-reconnect state");
            }
        }

        // 手动重连入口（"更多"菜单）：复位自动重连状态后立即重新连接（ConnectChildSession 自身幂等）。
        private void Reconnect()
        {
            if (closing || rdp == null || rdp.IsDisposed || connecting) return;
            StopReconnect();
            reconnectAttempts = 0;
            Program.Log("Manual reconnect requested");
            ConnectChildSession();
        }

        private void StopReconnect()
        {
            if (reconnectTimer != null && reconnectTimer.Enabled) reconnectTimer.Stop();
        }

        private void TryAutoReconnect()
        {
            try
            {
                var client = (IMsRdpClient9)rdp.GetOcxObject();
                if (client.Connected == 1)
                {
                    // 连接已恢复：停止重连探测，恢复 750ms 状态轮询。
                    StopReconnect();
                    connecting = false;
                    statusTimer.Start();
                    SetStatus(Strings.T("status_connected"));
                    return;
                }
                reconnectAttempts++;
                if (reconnectAttempts > 6)
                {
                    StopReconnect();
                    Program.Log("Auto-reconnect gave up after 6 attempts; restart manually");
                    SetStatus(Strings.T("status_reconnect_failed"));
                    return;
                }
                Program.Log("Auto-reconnect attempt " + reconnectAttempts);
                try { ((IMsTscAx)rdp.GetOcxObject()).Disconnect(); }
                catch (Exception ex) { Program.Log("Auto-reconnect pre-disconnect failed: " + ex.GetBaseException().Message); }
                ConnectChildSession();
            }
            catch (Exception ex)
            {
                Program.Log("Auto-reconnect failed: " + ex.GetBaseException().Message);
            }
        }

        private void QueueDisplayResize(bool windowSizeChanged = false)
        {
            if (closing || displayResizeTimer == null || rdp.IsDisposed) return;
            if (windowSizeChanged)
            {
                resizeRetryCount = 0;
                lastFailedDisplaySize = Size.Empty;
                nextResizeAttempt = DateTime.MinValue;
                displayResizeTimer.Stop();
                displayResizeTimer.Interval = 300;
            }
            else if (displayResizeTimer.Enabled)
            {
                return;
            }
            else if (nextResizeAttempt > DateTime.Now)
            {
                displayResizeTimer.Interval = Math.Max(100,
                    (int)Math.Ceiling((nextResizeAttempt - DateTime.Now).TotalMilliseconds));
            }
            else
            {
                displayResizeTimer.Interval = 300;
            }
            displayResizeTimer.Start();
        }

        private void ApplyDisplaySize()
        {
            if (closing || rdp.IsDisposed || !rdp.IsHandleCreated) return;
            if (!dynamicMode) return;   // 只允许动态模式改分辨率；固定模式靠 UpdateSessionDisplaySettings 主动改，残留定时器不得干预
            try
            {
                var client = (IMsRdpClient9)rdp.GetOcxObject();
                if (client.Connected != 1) return;

                if (DateTime.Now < connectionReadyAt)
                {
                    displayResizeTimer.Interval = Math.Max(100,
                        (int)Math.Ceiling((connectionReadyAt - DateTime.Now).TotalMilliseconds));
                    displayResizeTimer.Start();
                    return;
                }

                // 根源：会话分辨率 == 主窗体的【逻辑】客户区。PerMonitorV2 下 this.ClientSize 即逻辑值
                // （选 1280×720 后它就是 1280×720），不要再除 DPI——否则在 125% 屏会把 1280 除成 1024。
                // DPI 只影响物理渲染，不影响逻辑分辨率，因此任何 DPI 下逻辑客户区恒 == 会话分辨率。
                int width = Math.Max(200, Math.Min(8192, ClientSize.Width));
                int height = Math.Max(200, Math.Min(8192, ClientSize.Height));
                width &= ~1; // RDP display-control requires an even pixel width.
                var requested = new Size(width, height);
                if (requested == lastRequestedDisplaySize) return;

                float dpiX;
                float dpiY;
                using (Graphics graphics = rdp.CreateGraphics())
                {
                    dpiX = graphics.DpiX;
                    dpiY = graphics.DpiY;
                }
                uint physicalWidth = Geometry.ToMillimeters(width, dpiX);
                uint physicalHeight = Geometry.ToMillimeters(height, dpiY);

                client.UpdateSessionDisplaySettings((uint)width, (uint)height,
                    physicalWidth, physicalHeight, 0, 100, 100);
                lastRequestedDisplaySize = requested;
                lastFailedDisplaySize = Size.Empty;
                resizeRetryCount = 0;
                nextResizeAttempt = DateTime.MinValue;
                // 通过唯一事实源 SetSessionResolution 同步 currentSessW/H：动态模式下窗口尺寸变化会真实改变会话分辨率，
                // 不更新则分辨率菜单打勾仍用旧值（如最大化后还原，还原回 1280×720 却因 currentSessW 停在最大化值而不打勾）。
                SetSessionResolution(width, height);
                Program.Log("Remote desktop resized to " + width + "x" + height);
            }
            catch (Exception ex)
            {
                var requested = new Size(Math.Max(200, Math.Min(8192, ClientSize.Width)) & ~1,
                    Math.Max(200, Math.Min(8192, ClientSize.Height)));
                if (requested != lastFailedDisplaySize)
                {
                    lastFailedDisplaySize = requested;
                    resizeRetryCount = 0;
                }
                resizeRetryCount++;
                int retryDelay = Math.Min(4000, 500 * (1 << Math.Min(resizeRetryCount - 1, 3)));
                nextResizeAttempt = DateTime.Now.AddMilliseconds(retryDelay);
                // Tick 开头会 Stop，这里必须重新 Start，否则退避重试是死代码——日志声称"retrying in N ms"但实际永不重试。
                displayResizeTimer.Interval = retryDelay;
                displayResizeTimer.Start();
                Program.Log("Dynamic display resize failed for " + requested.Width + "x" + requested.Height +
                    "; retrying in " + retryDelay + " ms: " + ex.GetBaseException().Message);
            }
        }

        private void OnClosing(object sender, FormClosingEventArgs e)
        {
            // 用户点关闭且子会话已连接时先确认——避免静默注销丢未保存工作。
            // 任务管理器结束进程 / Windows 关机注销走 CloseReason 分支跳过确认。
            if (e.CloseReason == CloseReason.UserClosing)
            {
                bool connected = false;
                try { connected = ((IMsRdpClient9)rdp.GetOcxObject()).Connected == 1; }
                catch (Exception ex) { Program.Log("Could not read RDP state for close confirm: " + ex.GetBaseException().Message); }
                if (connected)
                {
                    var r = MessageBox.Show(this, Strings.T("close_confirm_body"),
                        Strings.T("close_confirm_title"), MessageBoxButtons.OKCancel, MessageBoxIcon.Warning);
                    if (r != DialogResult.OK) { e.Cancel = true; return; }
                }
            }
            closing = true;
            _resChangePending = false;   // 兜底复位：改分辨率后的 900ms 校正若在窗口关闭前未触发，防止标志永久卡 true
            if (toolBar != null) { toolBar.Close(); toolBar.Dispose(); toolBar = null; }
            statusTimer.Stop();
            displayResizeTimer.Stop();
            if (reconnectTimer != null) reconnectTimer.Stop();
            if (_displayMenu != null) { _displayMenu.Dispose(); _displayMenu = null; }
            if (_moreMenu != null) { _moreMenu.Dispose(); _moreMenu = null; }
            if (_resMenu != null) { _resMenu.Dispose(); _resMenu = null; }
            clipboardRelayTimer.Stop();
            if (readyTimer != null) readyTimer.Stop();        // P3-5：停止连接就绪后的 4s 建工具栏定时器，避免关闭流程中创建孤儿工具栏
            if (lateResizeTimer != null) lateResizeTimer.Stop();   // P3-5：停止分辨率校正的 900ms 延迟
            clipboardRelay.Dispose();
            ClipboardFileRelay.ClearMailbox();
            // Q1（质量报告）：COM 事件退订 + 复位 eventsAttached——手动重连重建 COM 对象后 eventsAttached 残留会阻止对新对象重新订阅
            if (eventsAttached && rdpEvents != null)
            {
                try
                {
                    rdpEvents.OnRemoteDesktopSizeChange -= HandleRemoteResize;
                    rdpEvents.OnDisconnected -= HandleDisconnected;
                    rdpEvents.OnFatalError -= HandleFatalError;
                    rdpEvents.OnLogonError -= HandleLogonError;
                }
                catch (Exception ex) { Program.Log("Could not unsubscribe RDP events during shutdown: " + ex.GetBaseException().Message); }
                eventsAttached = false;
            }
            try { ((IMsTscAx)rdp.GetOcxObject()).Disconnect(); }
            catch (Exception ex) { Program.Log("Disconnect during shutdown failed: " + ex.GetBaseException().Message); }

            try
            {
                uint childSessionId;
                if (!Program.WTSGetChildSessionId(out childSessionId))
                {
                    int error = Marshal.GetLastWin32Error();
                    if (error == 1168)
                    {
                        Program.Log("No Child Session exists during shutdown; no logoff is needed");
                        return;
                    }
                    Program.Log("Could not get Child Session ID during shutdown; session was disconnected but not logged off. Windows error " + error);
                    return;
                }

                if (childSessionId == uint.MaxValue)
                {
                    Program.Log("No Child Session was present during shutdown");
                    return;
                }

                // WTS_CURRENT_SERVER_HANDLE is NULL. Do not wait synchronously from the UI close event.
                if (!Program.WTSLogoffSession(IntPtr.Zero, childSessionId, false))
                {
                    int error = Marshal.GetLastWin32Error();
                    Program.Log("Could not log off Child Session " + childSessionId + "; Windows error " + error);
                    return;
                }

                Program.Log("Requested logoff for Child Session " + childSessionId + " on host window close");
            }
            catch (Exception ex)
            {
                Program.Log("Child Session logoff failed during shutdown: " + ex);
            }
        }
    }
}
