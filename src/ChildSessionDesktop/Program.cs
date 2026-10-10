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
        private bool fullscreen;
        private Rectangle prevBounds;
        private FormBorderStyle prevBorder;
        private ToolbarForm toolBar;                 // 独立悬浮工具栏（可拖动，含全屏/退出全屏）
        private const int WM_SIZING = 0x0214;
        private const int WM_EXITSIZEMOVE = 0x0232;
        private const int WM_HOTKEY = 0x0312;
        private const int WM_SYSCOMMAND = 0x0112;
        private const int SC_MAXIMIZE = 0xF030;
        private const int SW_SHOWNORMAL = 1;
        private const int WMSZ_LEFT = 1, WMSZ_RIGHT = 2, WMSZ_TOP = 3, WMSZ_TOPLEFT = 4,
            WMSZ_TOPRIGHT = 5, WMSZ_BOTTOM = 6, WMSZ_BOTTOMLEFT = 7, WMSZ_BOTTOMRIGHT = 8;
        private const uint VK_F11 = 0x7A;

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
            int initialW = (int)(screenW * 0.9);
            int initialH = (int)(initialW / aspect + 0.5);
            ClientSize = new Size(initialW, initialH);
            // 最小尺寸严格保持主机比例，缩到最小也不破比例、无黑边
            int minW = 640;
            MinimumSize = new Size(minW, (int)(minW / aspect + 0.5));

            rdp = new ChildSessionControl { Dock = dynamicMode ? DockStyle.Fill : DockStyle.None, BackColor = Color.Black };
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
            // 双击标题栏 / 点最大化 / 拖到顶部：不真正最大化（最大化会破坏宽高比留黑边），
            // 改成按会话比例放到当前工作区能容纳的最大尺寸——铺满、不变形、不挡任务栏。
            if (m.Msg == WM_SYSCOMMAND && (m.WParam.ToInt32() & 0xFFF0) == SC_MAXIMIZE)
            {
                FitToWorkArea();
                return;
            }

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

        // 等比铺满当前工作区：保持会话宽高比，居中，不变形、不挡任务栏。
        // 用 Windows 官方 SetWindowPlacement 把窗口设为"正常"并同步还原尺寸，
        // 这样双击/最大化后窗口是真正的 Normal 状态，移动不会还原到旧尺寸。
        private void FitToWorkArea()
        {
            var area = Screen.FromControl(this).WorkingArea;
            if (area.Width <= 0 || area.Height <= 0) area = Screen.FromControl(this).Bounds;
            int bw = Width - ClientSize.Width;
            int bh = Height - ClientSize.Height;
            int maxCw = area.Width - bw;
            int maxCh = area.Height - bh;
            if (maxCw <= 0 || maxCh <= 0) return;
            int cw, ch;
            if ((double)maxCw / maxCh > aspect) { ch = maxCh; cw = (int)(ch * aspect + 0.5); }
            else                                { cw = maxCw; ch = (int)(cw / aspect + 0.5); }
            int W = cw + bw;
            int H = ch + bh;
            int x = area.Left + (area.Width - W) / 2;
            int y = area.Top + (area.Height - H) / 2;

            var wp = new WINDOWPLACEMENT();
            wp.length = Marshal.SizeOf(typeof(WINDOWPLACEMENT));
            GetWindowPlacement(Handle, ref wp);
            wp.flags = 0;
            wp.showCmd = SW_SHOWNORMAL;
            wp.rcNormalPosition = new RECT { Left = x, Top = y, Right = x + W, Bottom = y + H };
            SetWindowPlacement(Handle, ref wp);
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
            int dw = cw, dh = ch;
            if (aspect > 0)
            {
                if ((double)cw / ch > aspect) dw = (int)(ch * aspect + 0.5);   // 太宽 → 定高算宽，左右黑边
                else                           dh = (int)(cw / aspect + 0.5);  // 太高 → 定宽算高，上下黑边
            }
            rdp.Location = new Point((cw - dw) / 2, (ch - dh) / 2);
            rdp.Size = new Size(dw, dh);
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
            if (fullscreen)
            {
                prevBounds = Bounds;
                prevBorder = FormBorderStyle;
                FormBorderStyle = FormBorderStyle.None;
                Bounds = Screen.FromControl(this).Bounds;   // 会话分辨率 = 屏幕原生 → 1:1
            }
            else
            {
                FormBorderStyle = prevBorder;
                Bounds = prevBounds;
            }
            ApplyLetterbox();
            if (toolBar != null) toolBar.UpdateState(fullscreen);
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
            toolBar.FollowHost(this);
            toolBar.Show();
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
                            if (dw > 0 && dh > 0)
                            {
                                aspect = (double)dw / (double)dh;
                                Program.Log("Session resolution readback " + dw + "x" + dh + "; aspect=" + aspect.ToString("0.0000"));
                            }
                        }
                        catch (Exception ex)
                        {
                            Program.Log("Could not read back session resolution: " + ex.GetBaseException().Message);
                        }
                        ApplyLetterbox();
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
        private readonly string[] labels = { "全屏", "显示", "设置", "更多" };
        private bool fullscreenState;
        private int hoverIndex = -1;
        private int pressedIndex = -1;
        private int selectedIndex = -1;
        private float indicatorX = -1f;
        private bool dragging;
        private Point dragStart;
        private bool dragMoved;
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
            int n = 4;
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

        internal void UpdateState(bool fs)
        {
            fullscreenState = fs;
            Invalidate();
        }

        // 跟随主窗体：标题栏下方、顶部居中；主窗体移动/缩放时保持相对位置
        internal void FollowHost(Form host)
        {
            Rectangle clientScreen = host.RectangleToScreen(host.ClientRectangle);
            int x = clientScreen.Left + Math.Max(0, (clientScreen.Width - Width) / 2);
            int y = clientScreen.Top + 34;
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
                    case 2: // 设置：齿轮（外圈 + 内孔 + 8 齿）
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
                    case 3: // 更多：横三点
                        using (var b = new SolidBrush(Color.FromArgb(240, 240, 242)))
                        {
                            g.FillEllipse(b, cx - 8, cy - 2, 4, 4);
                            g.FillEllipse(b, cx - 2, cy - 2, 4, 4);
                            g.FillEllipse(b, cx + 4, cy - 2, 4, 4);
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
                dragging = true;
                dragMoved = false;
                dragStart = e.Location;
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
                Location = new Point(Location.X + e.X - dragStart.X, Location.Y + e.Y - dragStart.Y);
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
                if (!dragMoved)
                {
                    int hit = HitTest(e.Location);
                    if (hit == 0 && ToggleFullscreen != null)
                    {
                        bool fs = ToggleFullscreen();
                        UpdateState(fs);
                    }
                    else if (hit > 0)
                    {
                        selectedIndex = hit;   // 预留工具：点击锁定选中（指示器停在该位置）
                        Invalidate();
                        animTimer.Start();   // 指示器滑向新选中的工具
                    }
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
