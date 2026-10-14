using System;

namespace ChildSessionDesktop
{
    // v1.4.0 补测试基线：自动重连的纯决策逻辑（无 UI/COM 依赖，可单测）。
    // 守护连接断开的自动重连：触发条件、尝试计数、上限放弃、恢复/手动复位。
    // DesktopForm 把重连决策委托给本类，timer 启停等 UI 动作仍由 DesktopForm 执行。
    // 与 Geometry.cs / ClipboardProtocol.cs 一致走 internal + InternalsVisibleTo("RayDesktopTests")，测试经 InternalsVisibleTo 访问。
    internal sealed class ReconnectPolicy
    {
        // 连续尝试上限（超限停止并提示手动重连），与 v1.1.1 P0-3 既有行为一致。
        public const int MaxAttempts = 6;

        private int _attempts;

        public bool InProgress { get; private set; }
        public int Attempts { get { return _attempts; } }
        public bool GaveUp { get; private set; }

        // 本地主动断开（reason==1）不自动重连；其余（远端断开/致命/登录错误/超时）进入。
        public static bool ShouldAutoReconnect(int disconnectReason)
        {
            return disconnectReason != 1;
        }

        // 进入自动重连状态（幂等：已在重连则保留当前计数，不重置）。
        public void Enter()
        {
            if (InProgress) return;
            InProgress = true;
            _attempts = 0;
            GaveUp = false;
        }

        // 每次重连尝试：计数递增；超过上限则放弃并退出重连。
        public void OnAttempt()
        {
            if (!InProgress) return;
            _attempts++;
            if (_attempts > MaxAttempts)
            {
                GaveUp = true;
                InProgress = false;
            }
        }

        // 连接已恢复：退出重连、清零（恢复 750ms 状态轮询）。
        public void OnReconnected()
        {
            InProgress = false;
            _attempts = 0;
            GaveUp = false;
        }

        // 手动重连：退出重连、清零（立即重新连接，不再探测）。
        public void OnManualReconnect()
        {
            InProgress = false;
            _attempts = 0;
            GaveUp = false;
        }
    }
}
