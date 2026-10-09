using System;
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

        [STAThread]
        private static int Main()
        {
            try
            {
                if (!WTSEnableChildSessions(true))
                {
                    int error = Marshal.GetLastWin32Error();
                    throw new InvalidOperationException("WTSEnableChildSessions 失败，Windows 错误码 " + error + "。");
                }

                bool enabled;
                if (!WTSIsChildSessionsEnabled(out enabled) || !enabled)
                {
                    int error = Marshal.GetLastWin32Error();
                    throw new InvalidOperationException("Windows 未确认 Child Sessions 已启用，错误码 " + error + "。");
                }

                MessageBox.Show(
                    "Child Sessions 已启用。请保存工作并注销 Windows，然后重新登录再使用独立桌面。\r\n\r\n如果现在连接时出现 Windows 凭据提示，请取消提示，不要尝试输入空密码；注销并重新登录后，子会话会使用当前登录状态自动连接。",
                    "独立 Windows 桌面设置",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
                return 0;
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    ex.Message + Environment.NewLine + Environment.NewLine + "请确认此 Windows 版本支持 Child Sessions，并允许管理员操作。",
                    "无法启用 Child Sessions",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
                return 1;
            }
        }
    }
}
