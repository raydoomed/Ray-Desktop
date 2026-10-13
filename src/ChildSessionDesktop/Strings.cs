using System;
using System.Collections.Generic;
using System.Globalization;

namespace ChildSessionDesktop
{
    // 多语言：所有用户可见文案集中在此，按系统语言自动选择（中文系统→中文，其它→英文），
    // 也可用启动参数 --lang=zh / --lang=en 覆盖（见 Program.Main 解析）。
    internal static class Strings
    {
        private static bool _en;

        public static void SetLanguage(string lang)
        {
            if (string.Equals(lang, "zh", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(lang, "zh-cn", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(lang, "zh-hans", StringComparison.OrdinalIgnoreCase))
                _en = false;
            else if (string.Equals(lang, "en", StringComparison.OrdinalIgnoreCase))
                _en = true;
            else
                _en = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName != "zh";
        }

        private static readonly Dictionary<string, string> zh = new Dictionary<string, string>
        {
            // 入口 / 通用
            { "err_title", "子会话桌面错误" },
            { "startup_fail_title", "子会话桌面启动失败" },
            // 显示模式菜单
            { "menu_fixed", "固定分辨率" },
            { "menu_dynamic", "动态分辨率" },
            // 更多菜单 / 关于
            { "menu_about", "关于 Ray Desktop" },
            { "menu_open_log", "打开日志文件夹" },
            { "menu_check_update", "检查更新…" },
            { "about_body", "在现有 Windows 中开启一个独立子会话虚拟桌面，不影响主桌面。\n注意：子会话与主桌面共享全部本地磁盘（可完整读写）；剪贴板（含文件路径）经 Ray Desktop 桥接在两会话间中继。\n开源：github.com/raydoomed/Ray-Desktop\n作者：raydoomed" },
            // 分辨率
            { "res_your_screen", "{0} × {1}（你的屏幕，推荐）" },
            { "res_confirm_title", "改分辨率" },
            { "res_confirm_body", "立即把虚拟桌面改成 {0} × {1} 吗？\n\n• 虚拟桌面里已打开的程序会保留，不会关闭\n• 但正在运行的全屏游戏可能会退出" },
            { "close_confirm_title", "关闭虚拟桌面" },
            { "close_confirm_body", "虚拟桌面仍已连接，正在运行的程序可能还有未保存的内容。确定关闭吗？" },
            { "res_not_connected", "尚未连接，无法改分辨率。" },
            { "res_fail", "改分辨率失败：" },
            // 连接状态（SetStatus 标题栏文案）
            { "status_connected", "已连接" },
            { "status_connecting", "正在连接本机子会话…" },
            { "status_connecting_win", "正在连接本机 Windows 子会话…" },
            { "status_waiting", "等待 Windows 建立子会话…" },
            { "status_not_connected", "尚未连接" },
            { "status_timeout", "连接超时。请确认此电脑已启用子会话，并在启用后重新登录 Windows。" },
            { "status_disabled_missing_helper", "此电脑未启用 Windows 子会话；缺少管理员启用程序 EnableChildSessions.exe。" },
            { "status_requesting", "正在请求管理员启用子会话。启用后请注销并重新登录；若出现凭据提示，请取消，不要输入空密码。" },
            { "status_elevation_canceled", "已取消管理员授权；Child Sessions 尚未启用。" },
            { "status_launch_helper_fail", "无法启动管理员启用程序：" },
            { "status_disconnected_local", "本地断开了 RDP 连接（0x00000001）；这是状态码，不是错误码。" },
            { "menu_reconnect", "重新连接" },
            { "status_reconnect_failed", "自动重连多次失败，可在「更多」菜单点击「重新连接」重试。" },
            { "status_disconnected", "子会话已断开，错误码 0x{0:X8}" },
            { "status_disconnected_acct", "（账户受限）。请检查 Windows 账户状态和登录策略；若刚启用 Child Sessions，请注销并重新登录后再试。" },
            { "status_fatal", "RDP 致命错误 0x{0:X8}" },
            { "status_logon_error", "子会话登录错误 0x{0:X8}" },
            { "status_continuing", "Windows 正在继续建立子会话…" },
            { "status_state", "RDP 控件状态：" },
            { "status_read_fail", "读取连接状态失败：" },
            { "msg_connect_fail", "连接失败：" },
            { "msg_check_fail", "无法检查 Windows 子会话状态，系统错误码 {0}。请确认此 Windows 版本支持远程桌面子会话。" },
            // 工具栏按钮 / 提示
            { "btn_fullscreen", "全屏" }, { "btn_display", "显示" }, { "btn_resolution", "分辨率" },
            { "btn_exit", "结束" }, { "btn_more", "更多" }, { "btn_collapse", "收起" }, { "btn_drag", "拖动" },
            { "tip_display_dynamic", "显示 · 动态" }, { "tip_display_fixed", "显示 · 固定" },
            { "tip_expand", "展开" }, { "tip_collapse", "收起" },
        };

        private static readonly Dictionary<string, string> en = new Dictionary<string, string>
        {
            { "err_title", "Ray Desktop error" },
            { "startup_fail_title", "Ray Desktop failed to start" },
            { "menu_fixed", "Fixed resolution" },
            { "menu_dynamic", "Dynamic resolution" },
            { "menu_about", "About Ray Desktop" },
            { "menu_open_log", "Open log folder" },
            { "menu_check_update", "Check for updates…" },
            { "about_body", "Opens a separate Child Session virtual desktop on top of the current Windows session, without affecting the main desktop.\nNote: the child session shares all local disks with the main desktop (read-write); the clipboard (including file paths) is relayed between the two sessions by the Ray Desktop bridge.\nOpen source: github.com/raydoomed/Ray-Desktop\nAuthor: raydoomed" },
            { "res_your_screen", "{0} × {1} (your screen, recommended)" },
            { "res_confirm_title", "Change resolution" },
            { "res_confirm_body", "Change the virtual desktop to {0} × {1} now?\n\n• Programs already open in the virtual desktop are kept, not closed\n• But any running fullscreen game may exit" },
            { "close_confirm_title", "Close virtual desktop" },
            { "close_confirm_body", "The virtual desktop is still connected and running programs may have unsaved work. Close anyway?" },
            { "res_not_connected", "Not connected yet; cannot change resolution." },
            { "res_fail", "Failed to change resolution: " },
            { "status_connected", "Connected" },
            { "status_connecting", "Connecting to the local Child Session…" },
            { "status_connecting_win", "Connecting to the local Windows Child Session…" },
            { "status_waiting", "Waiting for Windows to create the Child Session…" },
            { "status_not_connected", "Not connected" },
            { "status_timeout", "Connection timed out. Make sure Child Sessions are enabled on this computer, then sign out and back in." },
            { "status_disabled_missing_helper", "Child Sessions are not enabled on this computer; the elevation helper EnableChildSessions.exe is missing." },
            { "status_requesting", "Requesting administrator to enable Child Sessions. Sign out and back in after enabling; if a credentials prompt appears, cancel it and do not enter an empty password." },
            { "status_elevation_canceled", "Administrator authorization was canceled; Child Sessions are still not enabled." },
            { "status_launch_helper_fail", "Could not launch the elevation helper: " },
            { "status_disconnected_local", "The RDP connection was disconnected locally (0x00000001); this is a status code, not an error code." },
            { "menu_reconnect", "Reconnect" },
            { "status_reconnect_failed", "Auto-reconnect failed several times; use \"Reconnect\" in the More menu to retry." },
            { "status_disconnected", "Child Session disconnected, error code 0x{0:X8}" },
            { "status_disconnected_acct", " (account restricted). Check the Windows account status and sign-in policy; if you just enabled Child Sessions, sign out and back in and try again." },
            { "status_fatal", "Fatal RDP error 0x{0:X8}" },
            { "status_logon_error", "Child Session logon error 0x{0:X8}" },
            { "status_continuing", "Windows is continuing to build the Child Session…" },
            { "status_state", "RDP control state: " },
            { "status_read_fail", "Failed to read connection state: " },
            { "msg_connect_fail", "Connection failed: " },
            { "msg_check_fail", "Could not check the Windows Child Session state, system error code {0}. Make sure this Windows version supports remote desktop Child Sessions." },
            { "btn_fullscreen", "Fullscreen" }, { "btn_display", "Display" }, { "btn_resolution", "Resolution" },
            { "btn_exit", "Exit" }, { "btn_more", "More" }, { "btn_collapse", "Collapse" }, { "btn_drag", "Drag" },
            { "tip_display_dynamic", "Display · Dynamic" }, { "tip_display_fixed", "Display · Fixed" },
            { "tip_expand", "Expand" }, { "tip_collapse", "Collapse" },
        };

        public static string T(string key)
        {
            var table = _en ? en : zh;
            string s;
            return table.TryGetValue(key, out s) ? s : key;
        }

        public static string T(string key, params object[] args)
        {
            return string.Format(T(key), args);
        }
    }
}
