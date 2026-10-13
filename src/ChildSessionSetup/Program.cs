using System;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace ChildSessionSetup
{
    internal static class Program
    {
        [DllImport("wtsapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool WTSEnableChildSessions([MarshalAs(UnmanagedType.Bool)] bool enable);

        [DllImport("wtsapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool WTSIsChildSessionsEnabled([MarshalAs(UnmanagedType.Bool)] out bool enabled);

        // Lightweight localization: zh for Chinese systems, English otherwise (P2-9).
        private static string T(string zh, string en)
        {
            return CultureInfo.CurrentUICulture.Name.StartsWith("zh", StringComparison.OrdinalIgnoreCase) ? zh : en;
        }

        [STAThread]
        private static int Main()
        {
            try
            {
                if (!WTSEnableChildSessions(true))
                {
                    int error = Marshal.GetLastWin32Error();
                    throw new InvalidOperationException(T(
                        "WTSEnableChildSessions 失败，Windows 错误码 " + error + "。",
                        "WTSEnableChildSessions failed, Windows error code " + error + "."));
                }

                bool enabled;
                if (!WTSIsChildSessionsEnabled(out enabled) || !enabled)
                {
                    int error = Marshal.GetLastWin32Error();
                    throw new InvalidOperationException(T(
                        "Windows 未确认 Child Sessions 已启用，错误码 " + error + "。",
                        "Windows did not confirm Child Sessions are enabled, error code " + error + "."));
                }

                MessageBox.Show(
                    T(
                        "Child Sessions 已启用。请保存工作并注销 Windows，然后重新登录再使用独立桌面。\r\n\r\n如果现在连接时出现 Windows 凭据提示，请取消提示，不要尝试输入空密码；注销并重新登录后，子会话会使用当前登录状态自动连接。",
                        "Child Sessions are enabled. Save your work, sign out of Windows, then sign back in to use the isolated desktop.\r\n\r\nIf a Windows credential prompt appears when connecting now, cancel it and do not enter a blank password; after signing out and back in, the child session will connect automatically using your current sign-in."),
                    T("独立 Windows 桌面设置", "Isolated Windows Desktop Setup"),
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
                return 0;
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    ex.Message + Environment.NewLine + Environment.NewLine + T(
                        "请确认此 Windows 版本支持 Child Sessions，并允许管理员操作。",
                        "Verify this Windows version supports Child Sessions and that administrator action is allowed."),
                    T("无法启用 Child Sessions", "Could Not Enable Child Sessions"),
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
                return 1;
            }
        }
    }
}
