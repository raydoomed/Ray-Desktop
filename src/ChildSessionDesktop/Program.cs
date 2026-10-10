using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
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
                bool agentMode = args != null && args.Length > 0 &&
                    string.Equals(args[0], "--clipboard-agent", StringComparison.OrdinalIgnoreCase);
                bool dynamicMode = args != null && Array.IndexOf(args, "--dynamic") >= 0;
                if (!agentMode)
                {
                    bool createdNew;
                    singleInstance = new Mutex(true, @"Local\RayDesktop_SingleInstance", out createdNew);
                    if (!createdNew)
                    {
                        Log("Another Ray Desktop instance is already running; this instance will exit.");
                        return;
                    }
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

        private readonly bool dynamicMode;
        private double aspect;
        private bool fullscreen;
        private Rectangle prevBounds;
        private FormBorderStyle prevBorder;
        private const int WM_SIZING = 0x0214;
        private const int WM_HOTKEY = 0x0312;
        private const int WMSZ_LEFT = 1, WMSZ_RIGHT = 2, WMSZ_TOP = 3, WMSZ_TOPLEFT = 4,
            WMSZ_TOPRIGHT = 5, WMSZ_BOTTOM = 6, WMSZ_BOTTOMLEFT = 7, WMSZ_BOTTOMRIGHT = 8;
        private const uint VK_F11 = 0x7A;

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT { public int Left, Top, Right, Bottom; }

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
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_HOTKEY && m.WParam.ToInt32() == 1)
            {
                ToggleFullscreen();
                return;
            }

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
                    switch (m.WParam.ToInt32())
                    {
                        case WMSZ_LEFT:        r.Left  = r.Right  - W; break;
                        case WMSZ_RIGHT:       r.Right = r.Left   + W; break;
                        case WMSZ_TOP:         r.Top   = r.Bottom - H; break;
                        case WMSZ_BOTTOM:      r.Bottom= r.Top    + H; break;
                        case WMSZ_TOPLEFT:     r.Left  = r.Right  - W; r.Top    = r.Bottom - H; break;
                        case WMSZ_TOPRIGHT:    r.Right = r.Left   + W; r.Top    = r.Bottom - H; break;
                        case WMSZ_BOTTOMLEFT:  r.Left  = r.Right  - W; r.Bottom = r.Top    + H; break;
                        case WMSZ_BOTTOMRIGHT: r.Right = r.Left   + W; r.Bottom = r.Top    + H; break;
                        default:               r.Right = r.Left   + W; r.Bottom = r.Top    + H; break;
                    }
                    Marshal.StructureToPtr(r, m.LParam, false);
                    m.Result = (IntPtr)1;
                    return;
                }
            }

            if (m.Msg == ClipboardFileRelay.WmClipboardUpdate)
            {
                if (clipboardRelay != null) clipboardRelay.OnClipboardChanged();
            }
            base.WndProc(ref m);
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
                else                          dh = (int)(cw / aspect + 0.5);  // 太高 → 定宽算高，上下黑边
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

        private void ToggleFullscreen()
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
        }

        private void SetStatus(string message)
        {
            Text = message + " — Ray Desktop";
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
}
