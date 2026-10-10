using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Timer = System.Windows.Forms.Timer;
using MSTSCLib;

namespace ChildSessionDesktop
{
    internal static class Program
    {
        private static readonly object LogLock = new object();
        private static bool logInitialized;
        private static Mutex singleInstance;
        [STAThread]
        private static void Main(string[] args)
        {
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += delegate(object sender, System.Threading.ThreadExceptionEventArgs e)
            {
                Log(e.Exception.ToString());
                MessageBox.Show(e.Exception.ToString(), "子会话桌面错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
            };
            AppDomain.CurrentDomain.UnhandledException += delegate(object sender, UnhandledExceptionEventArgs e)
            {
                Log(Convert.ToString(e.ExceptionObject));
            };
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            try
            {
                if (args != null && args.Length > 0 && string.Equals(args[0], "--clipboard-agent", StringComparison.OrdinalIgnoreCase))
                {
                    uint parentSessionId = 0;
                    for (int i = 1; i + 1 < args.Length; i++)
                        if (string.Equals(args[i], "--parent-session", StringComparison.OrdinalIgnoreCase))
                            uint.TryParse(args[i + 1], out parentSessionId);
                    if (ClipboardFileRelay.ShouldRunChildAgent(parentSessionId))
                    {
                        Log("Child-session file clipboard agent started");
                        Application.Run(new ClipboardAgentContext());
                    }
                    return;
                }
                bool dynamicMode = args != null && Array.IndexOf(args, "--dynamic") >= 0;
                bool createdNew;
                singleInstance = new Mutex(true, @"Local\RayDesktop_SingleInstance", out createdNew);
                if (!createdNew)
                {
                    Log("Another Ray Desktop instance is already running; this instance will exit.");
                    return;
                }
                Application.Run(new DesktopForm(dynamicMode));
            }
            catch (Exception ex)
            {
                Log(ex.ToString());
                MessageBox.Show(ex.ToString(), "子会话桌面启动失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        internal static void Log(string message)
        {
            try
            {
                lock (LogLock)
                {
                    using (var mutex = new Mutex(false, @"Global\RayDesktopLog"))
                    {
                        bool ownsMutex = false;
                        try
                        {
                            try
                            {
                                mutex.WaitOne();
                                ownsMutex = true;
                            }
                            catch (AbandonedMutexException)
                            {
                                // The previous process exited while logging; this process now owns the mutex.
                                ownsMutex = true;
                            }

                            string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "child-session.log");
                            if (!logInitialized)
                            {
                                string existing = string.Empty;
                                if (File.Exists(path))
                                {
                                    using (var reader = new StreamReader(path, Encoding.UTF8, true))
                                        existing = reader.ReadToEnd();
                                }
                                existing = existing.Replace("\0", string.Empty);
                                byte[] preamble = new UTF8Encoding(true).GetPreamble();
                                byte[] content = Encoding.UTF8.GetBytes(existing);
                                using (var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read))
                                {
                                    stream.Write(preamble, 0, preamble.Length);
                                    stream.Write(content, 0, content.Length);
                                }
                                logInitialized = true;
                            }

                            string cleanMessage = (message ?? string.Empty).Replace("\0", string.Empty);
                            File.AppendAllText(path, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + " " +
                                cleanMessage + Environment.NewLine, new UTF8Encoding(false));
                        }
                        finally
                        {
                            if (ownsMutex) mutex.ReleaseMutex();
                        }
                    }
                }
            }
            catch { }
        }

        [DllImport("wtsapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool WTSIsChildSessionsEnabled([MarshalAs(UnmanagedType.Bool)] out bool enabled);

        [DllImport("wtsapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool WTSGetChildSessionId(out uint sessionId);

        [DllImport("wtsapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool WTSLogoffSession(IntPtr server, uint sessionId,
            [MarshalAs(UnmanagedType.Bool)] bool wait);

        [DllImport("kernel32.dll")]
        internal static extern uint WTSGetActiveConsoleSessionId();

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool ProcessIdToSessionId(uint processId, out uint sessionId);

        [DllImport("kernel32.dll")]
        internal static extern uint GetCurrentProcessId();
    }

    internal sealed class DesktopForm : Form
    {
        private readonly ChildSessionControl rdp;
        private readonly Timer statusTimer;
        private readonly Timer displayResizeTimer;
        private readonly Timer clipboardRelayTimer;
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

        private readonly bool dynamicMode;   // 固定分辨率(默认) / --dynamic 动态分辨率（仅启动参数决定）
        private double aspect;
        private int currentSessW, currentSessH;   // 当前生效的会话分辨率（用于分辨率下拉打勾）
        private int fsEnterW, fsEnterH;           // 进入全屏时的会话分辨率（退出时判断是否改过）
        private bool _resChangePending;           // 改分辨率期间屏蔽 UpdateConnectionStatus 的旧值 readback 干扰
        private bool _lastMaximized;              // 检测最大化状态切换：进最大化时重置工具栏偏移（还原后也回顶部居中）
        private int _lbLastW = -1, _lbLastH = -1;
        private double _lbLastA = -1;             // Letterbox 节流日志：客户区/aspect 变化才记
        private bool fullscreen;
        private Rectangle prevBounds;
        private FormBorderStyle prevBorder;
        private ToolbarForm toolBar;                 // 独立悬浮工具栏（可拖动，含全屏/退出全屏）
        private const int WM_SIZING = 0x0214;
        private const int WM_EXITSIZEMOVE = 0x0232;
        private const int WM_HOTKEY = 0x0312;
        private const int SW_SHOWNORMAL = 1;
        private const int WMSZ_LEFT = 1, WMSZ_RIGHT = 2, WMSZ_TOP = 3, WMSZ_TOPLEFT = 4,
            WMSZ_TOPRIGHT = 5, WMSZ_BOTTOM = 6, WMSZ_BOTTOMLEFT = 7, WMSZ_BOTTOMRIGHT = 8;
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
            aspect = (double)screenW / (double)screenH;
            currentSessW = screenW; currentSessH = screenH;
            int initialW = (int)(screenW * 0.9);
            int initialH = (int)(initialW / aspect + 0.5);
            ClientSize = new Size(initialW, initialH);
            // 最小尺寸严格保持主机比例，缩到最小也不破比例、无黑边
            int minW = 640;
            MinimumSize = new Size(minW, (int)(minW / aspect + 0.5));

            // RDP 控件用 Dock=None + ApplyLetterbox 等比居中：控件宽高比恒等于画面宽高比，
            // 避免 ActiveX(SmartSizing) 在比例不匹配时用灰色填充剩余区域（灰底）。
            rdp = new ChildSessionControl { Dock = DockStyle.None, BackColor = Color.Black };
            if (dynamicMode) rdp.Resize += delegate { QueueDisplayResize(true); };
            else Program.Log("Letterbox (fixed-resolution) mode enabled; session resolution is pinned to the host screen");
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
            Shown += delegate
            {
                Program.Log("Host form shown");
                if (!dynamicMode) ApplyLetterbox();   // 窗体一显示就把控件摆到正确矩形，避免左上角小黑块
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
            if (!dynamicMode) UnregisterHotKey(Handle, 1);
            base.OnHandleDestroyed(e);
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            bool max = (WindowState == FormWindowState.Maximized);
            if (max != _lastMaximized)   // 进入最大化或还原切换都重置偏移 → 还原后也回顶部居中
            {
                _lastMaximized = max;
                if (toolBar != null) toolBar.ResetToTopCenter();
            }
            if (!dynamicMode) ApplyLetterbox();
            if (toolBar != null) toolBar.FollowHost(this);
        }

        protected override void OnMove(EventArgs e)
        {
            base.OnMove(e);
            // 移动走标准 Windows 机制（分栏/双击最大化/贴边贴合都保留），这里只让工具栏跟随。
            if (toolBar != null) toolBar.FollowHost(this);
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

            // 拖拽边框缩放或移动结束后：把 Windows 记录的"还原尺寸"同步为当前窗口矩形。
            // 否则 Windows 在后续移动时会把窗口还原到旧的还原尺寸（表现为移动自动缩小）。
            // 移动本身仍是 Windows 原生机制（分栏/双击/贴边贴合都保留），这里只修正它记住的状态。
            if (m.Msg == WM_EXITSIZEMOVE)
            {
                SyncRestoreRect();
            }

            // 拖拽边框缩放时锁定宽高比 == 会话宽高比，拖出来就是无黑边的正确比例。
            if (!dynamicMode && m.Msg == WM_SIZING && aspect > 0)
            {
                RECT r = (RECT)Marshal.PtrToStructure(m.LParam, typeof(RECT));
                int bw = Width - ClientSize.Width;                 // 边框总宽
                int bh = Height - ClientSize.Height;               // 标题栏+边框总高
                int cw = (r.Right - r.Left) - bw;
                int ch = (r.Bottom - r.Top) - bh;
                if (cw > 160 && ch > 120)
                {
                    int ncw, nch;
                    if ((double)cw / ch > aspect) { ncw = (int)(ch * aspect + 0.5); nch = ch; }
                    else                          { ncw = cw; nch = (int)(cw / aspect + 0.5); }

                    int W = bw + ncw;
                    int H = bh + nch;
                    // 锚定当前窗口的实际四边，只移动被拖的边，
                    // 防止 Aero Snap 在缩放时把窗口吸附到屏幕顶部/边缘（传入 top=0）
                    int L = this.Left, T = this.Top, R = this.Right, B = this.Bottom;
                    switch (m.WParam.ToInt32())
                    {
                        case WMSZ_LEFT:        r.Left = R - W; r.Right = R; r.Top = T; r.Bottom = T + H; break;
                        case WMSZ_RIGHT:       r.Right = L + W; r.Left = L; r.Top = T; r.Bottom = T + H; break;
                        case WMSZ_TOP:         r.Top = B - H; r.Bottom = B; r.Left = L; r.Right = L + W; break;
                        case WMSZ_BOTTOM:      r.Bottom = T + H; r.Top = T; r.Left = L; r.Right = L + W; break;
                        case WMSZ_TOPLEFT:     r.Left = R - W; r.Right = R; r.Top = B - H; r.Bottom = B; break;
                        case WMSZ_TOPRIGHT:    r.Right = L + W; r.Left = L; r.Top = B - H; r.Bottom = B; break;
                        case WMSZ_BOTTOMLEFT:  r.Left = R - W; r.Right = R; r.Bottom = T + H; r.Top = T; break;
                        case WMSZ_BOTTOMRIGHT: r.Right = L + W; r.Left = L; r.Bottom = T + H; r.Top = T; break;
                        default:               r.Right = L + W; r.Left = L; r.Bottom = T + H; r.Top = T; break;
                    }
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
            catch { }
        }

        private void ApplyLetterbox()
        {
            if (rdp == null || rdp.IsDisposed) return;
            int cw = ClientSize.Width;
            int ch = ClientSize.Height;
            if (cw <= 0 || ch <= 0) return;
            int dw, dh;
            if (currentSessW > 0 && currentSessH > 0)
            {
                // ActiveX 永不放大（只 1:1，放不下才等比缩小）→ 消除 mstsc 放大灰底。
                // 客户区比画面大时（拉大窗口/低分辨率），画面以原始分辨率等比居中，四周露纯黑窗口底；
                // 客户区比画面小时，等比缩小铺满（ActiveX 缩小，无灰）。窗口模式与全屏共用这套逻辑。
                double s = Math.Min((double)cw / currentSessW, (double)ch / currentSessH);
                if (s > 1.0) s = 1.0;
                dw = (int)(currentSessW * s + 0.5);
                dh = (int)(currentSessH * s + 0.5);
            }
            else
            {
                dw = cw; dh = ch;
            }
            rdp.Location = new Point((cw - dw) / 2, (ch - dh) / 2);
            rdp.Size = new Size(dw, dh);
            if (cw != _lbLastW || ch != _lbLastH || Math.Abs(aspect - _lbLastA) > 0.0001)
            {
                _lbLastW = cw; _lbLastH = ch; _lbLastA = aspect;
                Program.Log("Letterbox: aspect=" + aspect.ToString("0.0000")
                    + " client=" + cw + "x" + ch
                    + " rdp=" + dw + "x" + dh + " at (" + rdp.Location.X + "," + rdp.Location.Y + ")");
            }
        }

        private void OpenLogFolder()
        {
            try
            {
                string dir = Path.GetDirectoryName(Application.ExecutablePath);
                string log = Path.Combine(dir, "child-session.log");
                if (File.Exists(log)) Process.Start("explorer.exe", "/select,\"" + log + "\"");
                else Process.Start("explorer.exe", dir);
            }
            catch { }
        }

        private void AboutBox()
        {
            MessageBox.Show(this,
                "Ray Desktop" + Environment.NewLine +
                "在现有 Windows 中开启一个独立子会话虚拟桌面，不影响主桌面。" + Environment.NewLine +
                "开源：github.com/raydoomed/Ray-Desktop" + Environment.NewLine +
                "作者：raydoomed",
                "关于 Ray Desktop", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        private bool ToggleFullscreen()
        {
            fullscreen = !fullscreen;
            if (toolBar != null) toolBar.Fullscreen = fullscreen;   // 提前标记，让 Bounds 变更触发的 FollowHost 用全屏/窗口定位
            if (fullscreen)
            {
                prevBounds = Bounds;
                prevBorder = FormBorderStyle;
                fsEnterW = currentSessW; fsEnterH = currentSessH;   // 记住进入全屏时的会话分辨率
                FormBorderStyle = FormBorderStyle.None;
                Bounds = Screen.FromControl(this).Bounds;   // 全屏：窗口=屏幕，控件由 Letterbox 等比居中（画面比例），四周纯黑边
            }
            else
            {
                FormBorderStyle = prevBorder;
                if (toolBar != null) toolBar.ResetToTopCenter();   // 退出全屏统一重置到顶部居中（避免旧偏移跑出窗口）
                // 全屏期间改过分辨率 → 退出后把窗口重新匹配到当前会话分辨率（1:1，ActiveX 不放大 → 无灰底）。
                // 未改过 → 恢复进入前的窗口原状。
                if (currentSessW != fsEnterW || currentSessH != fsEnterH)
                {
                    MatchWindowToSession(currentSessW, currentSessH);
                    if (toolBar != null) toolBar.FollowHost(this);
                    return fullscreen;
                }
                Bounds = prevBounds;
            }
            ApplyLetterbox();
            if (toolBar != null) toolBar.UpdateState(fullscreen);
            Program.Log("全屏 " + (fullscreen ? "进" : "出") + " Bounds=" + Bounds + " client=" + ClientSize.Width + "x" + ClientSize.Height
                + " aspect=" + aspect.ToString("0.0000") + " sess=" + currentSessW + "x" + currentSessH);
            return fullscreen;
        }

        private void SetStatus(string message)
        {
            Text = message + " — Ray Desktop";
        }

        // 桌面完全显示、分辨率就位后创建悬浮工具栏
        private void CreateToolbar()
        {
            if (toolBar != null) return;
            toolBar = new ToolbarForm();
            toolBar.Ready = true;
            toolBar.ToggleFullscreen = ToggleFullscreen;
            toolBar.ShowResMenu = ShowResMenuAt;
            toolBar.FollowHost(this);
            toolBar.Show();
        }

        // 分辨率按钮下拉：候选 = 当前屏幕（推荐）+ 一组常见宽屏分辨率。
        // 这是独立的"会话分辨率"设置，对固定/动态两种模式都生效。
        private bool ShowResMenuAt(Rectangle anchor)
        {
            var menu = new ContextMenuStrip { RenderMode = ToolStripRenderMode.System };
            menu.ShowImageMargin = false;
            menu.ShowCheckMargin = true;   // 显示勾选列，标记当前生效的分辨率
            int sw = Screen.PrimaryScreen.Bounds.Width;
            int sh = Screen.PrimaryScreen.Bounds.Height;
            var itemScreen = new ToolStripMenuItem(sw + " × " + sh + "（你的屏幕，推荐）");
            itemScreen.Click += delegate { ApplyResolutionPick(0, 0); };
            itemScreen.Checked = (currentSessW == sw && currentSessH == sh);
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
            int sw = Screen.PrimaryScreen.Bounds.Width;
            int sh = Screen.PrimaryScreen.Bounds.Height;
            int w = pickW, h = pickH;
            if (w == 0) { w = sw; h = sh; }
            if (w <= 0 || h <= 0) return;
            w &= ~1;   // RDP 要求偶数像素宽（与连接时一致），避免奇数宽导致 UpdateSessionDisplaySettings 失败

            var result = MessageBox.Show(this,
                "立即把虚拟桌面改成 " + w + " × " + h + " 吗？\n\n" +
                "• 虚拟桌面里已打开的程序会保留，不会关闭\n" +
                "• 但正在运行的全屏游戏可能会退出",
                "改分辨率", MessageBoxButtons.OKCancel, MessageBoxIcon.Question);
            if (result != DialogResult.OK) return;

            try
            {
                var client = (IMsRdpClient9)rdp.GetOcxObject();
                if (client.Connected != 1)
                {
                    MessageBox.Show(this, "尚未连接，无法改分辨率。", "改分辨率",
                        MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }
                float dpi = 96f;
                try { using (var gr = rdp.CreateGraphics()) dpi = gr.DpiX; } catch { }
                uint pw = ToMillimeters(w, dpi);
                uint ph = ToMillimeters(h, dpi);
                client.UpdateSessionDisplaySettings((uint)w, (uint)h, pw, ph, 0, 100, 100);
                Program.Log("Resolution menu: session set to " + w + "x" + h);
                _resChangePending = true;   // 改分辨率期间屏蔽 UpdateConnectionStatus 的旧值 readback 干扰
                currentSessW = w; currentSessH = h;
                aspect = (double)w / (double)h;
                if (toolBar != null) toolBar.ResetToTopCenter();   // 分辨率变化→工具栏回顶部居中，避免旧偏移跑出变小后的窗口
                // 窗口匹配新比例（非全屏：窗口比例=画面比例 → 控件铺满无黑边；全屏：只等比重排控件）
                MatchWindowToSession(w, h);
                // ActiveX 显示面异步重建：等 900ms 后读回真实分辨率，再校正一次（对齐 workbuddy）
                var late = new Timer { Interval = 900 };
                late.Tick += delegate
                {
                    late.Stop(); late.Dispose();
                    try
                    {
                        var c2 = (IMsRdpClient9)rdp.GetOcxObject();
                        int dw = c2.DesktopWidth, dh = c2.DesktopHeight;
                        if (dw > 0 && dh > 0)
                        {
                            currentSessW = dw; currentSessH = dh;
                            aspect = (double)dw / (double)dh;
                            Program.Log("Resolution menu readback " + dw + "x" + dh);
                        }
                    }
                    catch { }
                    _resChangePending = false;   // 校正完成，放行 UpdateConnectionStatus 的 readback
                    MatchWindowToSession(currentSessW, currentSessH);   // 内部已 ApplyLetterbox
                    if (toolBar != null) toolBar.FollowHost(this);
                };
                late.Start();
            }
            catch (Exception ex)
            {
                _resChangePending = false;
                Program.Log("Resolution menu apply failed: " + ex);
                MessageBox.Show(this, "改分辨率失败：" + ex.GetBaseException().Message, "改分辨率",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
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
            ApplyLetterbox();
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
                    throw new InvalidOperationException("无法检查 Windows 子会话状态，系统错误码 " + error + "。请确认此 Windows 版本支持远程桌面子会话。");
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
                    SetStatus("已连接");
                    statusTimer.Start();
                    if (dynamicMode) QueueDisplayResize();
                    else ApplyLetterbox();
                    return;
                }
                if (state == 2)
                {
                    connecting = true;
                    connectionStarted = DateTime.Now;
                    SetStatus("正在连接本机子会话…");
                    statusTimer.Start();
                    return;
                }

                connecting = true;
                connectionStarted = DateTime.Now;
                SetStatus("正在连接本机 Windows 子会话…");
                Program.Log("Begin child-session setup");
                client.Server = "localhost";
                Program.Log("Server set");
                if (!dynamicMode)
                {
                    int sw = Screen.PrimaryScreen.Bounds.Width;
                    int sh = Screen.PrimaryScreen.Bounds.Height;
                    if (sw <= 0 || sh <= 0) { sw = 1280; sh = 720; }
                    client.DesktopWidth = sw & ~1;    // RDP 要求偶数像素宽
                    client.DesktopHeight = sh & ~1;
                    aspect = (double)(sw & ~1) / (double)(sh & ~1);
                    Program.Log("Requesting child-session desktop at " + (sw & ~1) + "x" + (sh & ~1) + " from the start (fixed)");
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
                Program.Log("RDP clipboard redirection off (the Ray Desktop bridge relays text and files); drive redirection and Windows key combinations enabled");
                ((IMsTscAx)ocx).Connect();
                Program.Log("Connect() returned");
                statusTimer.Start();
            }
            catch (Exception ex)
            {
                Program.Log("Connect setup failed: " + ex);
                connecting = false;
                var message = "连接失败：" + ex.GetBaseException().Message;
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
                SetStatus("此电脑未启用 Windows 子会话；缺少管理员启用程序 EnableChildSessions.exe。");
                return;
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
                SetStatus("正在请求管理员启用子会话。启用后请注销并重新登录；若出现凭据提示，请取消，不要输入空密码。");
                Program.Log("Launched elevated Child Sessions setup helper");
            }
            catch (Win32Exception ex)
            {
                if (ex.NativeErrorCode == 1223)
                {
                    SetStatus("已取消管理员授权；Child Sessions 尚未启用。");
                    Program.Log("User canceled UAC elevation for Child Sessions setup");
                }
                else
                {
                    SetStatus("无法启动管理员启用程序：" + ex.Message);
                    Program.Log("Could not launch elevated setup helper: " + ex);
                }
            }
            catch (Exception ex)
            {
                SetStatus("无法启动管理员启用程序：" + ex.GetBaseException().Message);
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
                        var readyTimer = new System.Windows.Forms.Timer();
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
                    try { ((IMsTscAx)rdp.GetOcxObject()).Disconnect(); } catch { }
                    SetStatus("连接超时。请确认此电脑已启用子会话，并在启用后重新登录 Windows。");
                    Program.Log("Child-session connection timed out after 45 seconds (Connected=" + state + ")");
                    return;
                }

                if (state == 0)
                {
                    if (connecting) SetStatus("等待 Windows 建立子会话…");
                    else SetStatus("尚未连接");
                }
                else if (state == 1)
                {
                    connecting = false;
                    SetStatus("已连接");
                    if (dynamicMode)
                    {
                        QueueDisplayResize();
                    }
                    else
                    {
                        try
                        {
                            var c = (IMsRdpClient9)rdp.GetOcxObject();
                            int dw = c.DesktopWidth, dh = c.DesktopHeight;
                            // 只在分辨率真正变化时才更新 + 重排（避免每 750ms 无条件 ApplyLetterbox 的冗余重排）；
                            // 改分辨率（ApplyResolutionPick）期间屏蔽，防止读到过渡旧值把布局打回上一下分辨率。
                            if (dw > 0 && dh > 0 && !_resChangePending && (dw != currentSessW || dh != currentSessH))
                            {
                                currentSessW = dw; currentSessH = dh;
                                aspect = (double)dw / (double)dh;
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
                    SetStatus("正在连接本机子会话…");
                }
                else SetStatus("RDP 控件状态：" + state);
            }
            catch (Exception ex)
            {
                SetStatus("读取连接状态失败：" + ex.GetBaseException().Message);
            }
        }

        private void AttachConnectionEvents(object ocx)
        {
            if (eventsAttached) return;
            rdpEvents = (IMsTscAxEvents_Event)ocx;
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
                Program.Log("Clipboard bridge active; it mirrors text and local file paths between the two sessions (RDP clipboard redirection is off)");
            }
            catch (Exception ex)
            {
                Program.Log("Could not query RDP clipboard and drive redirection state after connect: " +
                    ex.GetBaseException().Message);
            }
        }

        private void HandleDisconnected(int reason)
        {
            if (closing || IsDisposed) return;
            connecting = false;
            statusTimer.Stop();
            var message = reason == 1
                ? "本地断开了 RDP 连接（0x00000001）；这是状态码，不是错误码。"
                : "子会话已断开，错误码 0x" + reason.ToString("X8");
            if (reason == 0xC07)
                message += "（账户受限）。请检查 Windows 账户状态和登录策略；若刚启用 Child Sessions，请注销并重新登录后再试。";
            Program.Log(message);
            SetStatus(message);
        }

        private void HandleFatalError(int errorCode)
        {
            if (closing || IsDisposed) return;
            connecting = false;
            statusTimer.Stop();
            var message = "RDP 致命错误 0x" + errorCode.ToString("X8");
            Program.Log(message);
            SetStatus(message);
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
                        SetStatus("已连接");
                    }
                    else
                    {
                        SetStatus("Windows 正在继续建立子会话…");
                    }
                }
                catch (Exception ex)
                {
                    Program.Log("Could not read RDP state after continue-logon event: " + ex.GetBaseException().Message);
                }
                return;
            }

            var message = "子会话登录错误 0x" + errorCode.ToString("X8");
            connecting = false;
            statusTimer.Stop();
            Program.Log(message);
            SetStatus(message);
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

                int width = Math.Max(200, Math.Min(8192, rdp.ClientSize.Width));
                int height = Math.Max(200, Math.Min(8192, rdp.ClientSize.Height));
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
                uint physicalWidth = ToMillimeters(width, dpiX);
                uint physicalHeight = ToMillimeters(height, dpiY);

                client.UpdateSessionDisplaySettings((uint)width, (uint)height,
                    physicalWidth, physicalHeight, 0, 100, 100);
                lastRequestedDisplaySize = requested;
                lastFailedDisplaySize = Size.Empty;
                resizeRetryCount = 0;
                nextResizeAttempt = DateTime.MinValue;
                Program.Log("Remote desktop resized to " + width + "x" + height);
            }
            catch (Exception ex)
            {
                var requested = new Size(Math.Max(200, Math.Min(8192, rdp.ClientSize.Width)) & ~1,
                    Math.Max(200, Math.Min(8192, rdp.ClientSize.Height)));
                if (requested != lastFailedDisplaySize)
                {
                    lastFailedDisplaySize = requested;
                    resizeRetryCount = 0;
                }
                resizeRetryCount++;
                int retryDelay = Math.Min(4000, 500 * (1 << Math.Min(resizeRetryCount - 1, 3)));
                nextResizeAttempt = DateTime.Now.AddMilliseconds(retryDelay);
                Program.Log("Dynamic display resize failed for " + requested.Width + "x" + requested.Height +
                    "; retrying in " + retryDelay + " ms: " + ex.GetBaseException().Message);
            }
        }

        private static uint ToMillimeters(int pixels, float dpi)
        {
            if (dpi <= 0) dpi = 96;
            return (uint)Math.Max(10, Math.Round(pixels * 25.4 / dpi));
        }

        private void OnClosing(object sender, FormClosingEventArgs e)
        {
            closing = true;
            if (toolBar != null) { toolBar.Close(); toolBar.Dispose(); toolBar = null; }
            statusTimer.Stop();
            displayResizeTimer.Stop();
            clipboardRelayTimer.Stop();
            clipboardRelay.Dispose();
            ClipboardFileRelay.ClearMailbox();
            try { ((IMsTscAx)rdp.GetOcxObject()).Disconnect(); }
            catch { }

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

    internal sealed class ChildSessionControl : AxHost
    {
        internal ChildSessionControl() : base("8b918b82-7985-4c24-89df-c33ad2bbfbcd") { }
        internal object GetOcxObject() { return GetOcx(); }
    }

    // 独立悬浮工具栏：顶层无边框小窗，不叠在 RDP 控件上（避免此前"一层包一层"的叠加问题）。
    // 整个条可自由拖动，一排按钮；"全屏"为已实现功能，其余为预留占位（暂无动作）。
    // 方形深灰条 + 方形按钮，宽度自适应文字，紧凑缩小。
    internal sealed class ToolbarForm : Form
    {
        internal Func<bool> ToggleFullscreen;
        internal Func<Rectangle, bool> ShowResMenu;   // 分辨率按钮回调（锚点=按钮屏幕矩形，由主窗体弹下拉）
        private readonly string[] labels = { "全屏", "显示", "分辨率", "设置", "更多", "拖动" };
        private const int DragBtnIndex = 5;   // 最右侧的专用"拖动"按钮：只有点住它才能移动工具栏
        private bool fullscreenState;
        private int hoverIndex = -1;
        private int pressedIndex = -1;
        private int selectedIndex = -1;
        private float indicatorX = -1f;
        private bool dragging;
        private Point dragStart;
        private bool dragMoved;
        private Form _host;
        private Point _hostOffset = new Point(int.MinValue, int.MinValue);   // 相对主窗体客户区的偏移（拖动后记住，主窗体移动/缩放时跟随不复位）
        private readonly Rectangle[] buttonRects;
        private readonly Timer animTimer;
        private readonly TooltipForm tooltip;
        private const int BtnW = 30, BtnH = 26, Gap = 6, Pad = 6;

        // 桌面就绪前不显示气泡，避免启动早期位置错误
        internal bool Ready;

        internal ToolbarForm()
        {
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            ShowInTaskbar = false;
            TopMost = true;
            int n = labels.Length;   // 5 个按钮：全屏/显示/分辨率/设置/更多
            buttonRects = new Rectangle[n];
            int totalW = Pad;
            for (int i = 0; i < n; i++)
            {
                buttonRects[i] = new Rectangle(totalW, Pad, BtnW, BtnH);
                totalW += BtnW + Gap;
            }
            Size = new Size(totalW - Gap + Pad, Pad + BtnH + Pad);
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
            float targetCenter = buttonRects[targetIdx].X + BtnW / 2f;
            if (indicatorX < 0 || indicatorX == -100f) indicatorX = targetCenter;
            else indicatorX += (targetCenter - indicatorX) * 0.15f;
            if (Math.Abs(indicatorX - targetCenter) < 0.5f) indicatorX = targetCenter;
            if (indicatorX == targetCenter) animTimer.Stop();
            else Invalidate();
        }

        internal bool Fullscreen;   // 全屏时工具栏固定顶部居中，忽略拖动偏移

        internal void ResetToTopCenter() { _hostOffset = new Point(int.MinValue, int.MinValue); }   // 退出全屏统一重置到顶部居中

        internal void UpdateState(bool fs)
        {
            fullscreenState = fs;
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

            // —— 背景条：黑色圆角，底部轻影（简洁，单层）——
            using (var sh = new SolidBrush(Color.FromArgb(70, 0, 0, 0)))
                g.FillRectangle(sh, new Rectangle(0, Height - 2, Width, 2));
            var barRect = new Rectangle(0, 0, Width - 1, Height - 2);
            using (var p = RoundedRectPath(barRect, 9))
            using (var bg = new SolidBrush(Color.FromArgb(225, 24, 24, 28)))
                g.FillPath(bg, p);

            // —— 滑动指示器：平滑滑动到悬停/选中工具 ——
            int activeIdx = (hoverIndex >= 0) ? hoverIndex : selectedIndex;
            if (activeIdx >= 0 && indicatorX >= 0)
            {
                var ar = buttonRects[activeIdx];
                int iw = ar.Width, ih = ar.Height;
                Rectangle ind = new Rectangle((int)(indicatorX - iw / 2f), ar.Y, iw, ih);
                using (var ip = RoundedRectPath(ind, 6))
                using (var ib = new SolidBrush(Color.FromArgb(55, 255, 255, 255)))
                    g.FillPath(ib, ip);
            }

            // —— 按钮：图标，按下时高亮并下沉 ——
            for (int i = 0; i < buttonRects.Length; i++)
            {
                var r = buttonRects[i];
                if (pressedIndex == i)
                {
                    int d = Math.Min(BtnW, BtnH) - 4;
                    Rectangle cir = new Rectangle(r.X + (r.Width - d) / 2, r.Y + (r.Height - d) / 2, d, d);
                    using (var hb = new SolidBrush(Color.FromArgb(90, 255, 255, 255)))
                        g.FillEllipse(hb, cir);
                }

                int cx = r.X + BtnW / 2, cy = r.Y + BtnH / 2;
                if (pressedIndex == i) cy += 1;
                DrawIcon(g, i, cx, cy);
            }

            // —— 背景条描边 ——
            using (var pen = new Pen(Color.FromArgb(120, 60, 60, 68)))
                g.DrawPath(pen, RoundedRectPath(barRect, 9));
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
                        if (fullscreenState)
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
                    case 3: // 设置：齿轮（外圈 + 内孔 + 8 齿）
                        float rOut = 6f, rTeeth = 8.5f;
                        g.DrawEllipse(pen, cx - rOut, cy - rOut, rOut * 2, rOut * 2);
                        g.DrawEllipse(pen, cx - 2.5f, cy - 2.5f, 5, 5);
                        for (int k = 0; k < 8; k++)
                        {
                            float a = (float)(k * Math.PI / 4);
                            g.DrawLine(pen,
                                cx + rOut * (float)Math.Cos(a), cy + rOut * (float)Math.Sin(a),
                                cx + rTeeth * (float)Math.Cos(a), cy + rTeeth * (float)Math.Sin(a));
                        }
                        break;
                    case 4: // 更多：横三点
                        using (var b = new SolidBrush(Color.FromArgb(240, 240, 242)))
                        {
                            g.FillEllipse(b, cx - 8, cy - 2, 4, 4);
                            g.FillEllipse(b, cx - 2, cy - 2, 4, 4);
                            g.FillEllipse(b, cx + 4, cy - 2, 4, 4);
                        }
                        break;
                    case 5: // 拖动：暗灰方块 + 暗灰九宫格点（与其他白色图标区分）
                        using (var sq = new SolidBrush(Color.FromArgb(80, 80, 88)))
                        using (var dot = new SolidBrush(Color.FromArgb(150, 150, 158)))
                        {
                            g.FillPath(sq, RoundedRectPath(new Rectangle(cx - 6, cy - 6, 12, 12), 3));
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

        protected override void OnMouseDown(MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left)
            {
                pressedIndex = HitTest(e.Location);
                if (pressedIndex >= 0) Invalidate();
                dragMoved = false;   // 每次按下都重置，避免上次拖动残留导致后续点击被跳过
                if (pressedIndex == DragBtnIndex)
                {
                    dragging = true;
                    dragStart = e.Location;
                    if (tooltip != null) tooltip.HideAway();   // 拖动时不显示文字气泡
                }
                else dragging = false;
            }
            base.OnMouseDown(e);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
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
                    if (Ready) tooltip.ShowFor(RectangleToScreen(buttonRects[idx]), labels[idx]);
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

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            hoverIndex = -1;
            selectedIndex = -1;   // 离开工具栏即取消所有锁定（含点击选中）
            Invalidate();
            animTimer.Start();   // 指示器滑出
            tooltip.HideAway();
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
                    int hit = HitTest(e.Location);
                    if (hit == 0 && ToggleFullscreen != null)
                    {
                        bool fs = ToggleFullscreen();
                        UpdateState(fs);
                    }
                    else if (hit == 2 && ShowResMenu != null)
                    {
                        // 分辨率按钮：由主窗体弹出下拉，指示器停在分辨率按钮上
                        ShowResMenu(RectangleToScreen(buttonRects[2]));
                        selectedIndex = hit;
                        Invalidate();
                        animTimer.Start();
                    }
                    else if (hit > 0 && hit != DragBtnIndex)
                    {
                        selectedIndex = hit;   // 预留工具：点击锁定选中（指示器停在该位置）
                        Invalidate();
                        animTimer.Start();   // 指示器滑向新选中的工具
                    }
                    // 拖动按钮（DragBtnIndex）点击不触发命令，只作拖动把手
                    // hit>0 的预留按钮暂无动作，仅表现选中态
                }
                if (pressedIndex >= 0) { pressedIndex = -1; Invalidate(); }
            }
            base.OnMouseUp(e);
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
            using (var p = RoundedRectPath(new Rectangle(0, 0, Width, Height), 9))
                Region = new Region(p);
        }
    }

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
