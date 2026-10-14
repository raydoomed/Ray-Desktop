using System;
using System.IO;
using System.Text;
using ChildSessionDesktop;

namespace RayDesktopTests
{
    // P2-10 回归基线：核心纯函数（Geometry / ClipboardProtocol）的轻量单测。
    // 目标机无 dotnet SDK/NuGet，故用 .NET Framework csc 编译、断言失败打印并返回非零退出码。
    internal static class Program
    {
        private static int _passed;
        private static int _failed;

        private static void Check(bool cond, string name)
        {
            if (cond) { _passed++; }
            else { _failed++; Console.WriteLine("FAIL: " + name); }
        }

        // ---------- Geometry.ToMillimeters ----------
        private static void TestToMillimeters()
        {
            Check(Geometry.ToMillimeters(1920, 96) == 508, "ToMillimeters(1920,96)=508");
            Check(Geometry.ToMillimeters(100, 96) == 26, "ToMillimeters(100,96)=26");
            Check(Geometry.ToMillimeters(1920, 144) == 339, "ToMillimeters(1920,144)=339");
            Check(Geometry.ToMillimeters(0, 96) == 10, "ToMillimeters(0,96)=10 (floor 10)");
            Check(Geometry.ToMillimeters(100, 0) == 26, "ToMillimeters(100,0) fallback dpi=96");
            Check(Geometry.ToMillimeters(100, -1) == 26, "ToMillimeters(100,-1) fallback dpi=96");
        }

        // ---------- Geometry.ComputeLetterbox ----------
        private static void TestComputeLetterbox()
        {
            int dw, dh, dx, dy;
            // 客户区 == 画面：s=1，无偏移。
            Geometry.ComputeLetterbox(1920, 1080, 1920, 1080, out dw, out dh, out dx, out dy);
            Check(dw == 1920 && dh == 1080 && dx == 0 && dy == 0, "Letterbox same size");

            // 客户区比画面大：不放大，居中。
            Geometry.ComputeLetterbox(2560, 1440, 1920, 1080, out dw, out dh, out dx, out dy);
            Check(dw == 1920 && dh == 1080 && dx == 320 && dy == 180, "Letterbox bigger client centered");

            // 客户区比画面小：等比缩小铺满。
            Geometry.ComputeLetterbox(960, 1080, 1920, 1080, out dw, out dh, out dx, out dy);
            Check(dw == 960 && dh == 540 && dx == 0 && dy == 270, "Letterbox scale down");

            // 16:9 会话 2x：缩小到客户区。
            Geometry.ComputeLetterbox(1280, 720, 2560, 1440, out dw, out dh, out dx, out dy);
            Check(dw == 1280 && dh == 720 && dx == 0 && dy == 0, "Letterbox half-res 1:1");

            // 会话未设：铺满客户区。
            Geometry.ComputeLetterbox(800, 600, 0, 0, out dw, out dh, out dx, out dy);
            Check(dw == 800 && dh == 600 && dx == 0 && dy == 0, "Letterbox no session size");
        }

        // ---------- Geometry.LockAspectRect (WM_SIZING 8 方向) ----------
        // 共同前提：aspect=16/9≈1.7778, bw=8, bh=32, 锚定窗口 L=100,T=100,R=600,B=400（500x300）。
        // 方向值必须用 Win32 标准 WMSZ_*（非顺时针：WMSZ_TOPLEFT=4, WMSZ_TOPRIGHT=5, WMSZ_BOTTOM=6）。
        private static bool RectEq(Rect r, int l, int t, int rt, int b)
        {
            return r.Left == l && r.Top == t && r.Right == rt && r.Bottom == b;
        }

        private static void TestLockAspectRect()
        {
            Rect anchor = new Rect { Left = 100, Top = 100, Right = 600, Bottom = 400 };
            Rect result;
            const double aspect = 16.0 / 9.0;

            // 锁高组：drag {100,100,700,300} → cw=592, ch=168, ratio 3.52>aspect → ncw=299, W=307, H=200。
            Rect lockHigh = new Rect { Left = 100, Top = 100, Right = 700, Bottom = 300 };
            Check(Geometry.LockAspectRect(lockHigh, anchor, 8, 32, aspect, Geometry.WmszLeft, out result) && RectEq(result, 293, 100, 600, 300), "WmszLeft anchors left");
            Check(Geometry.LockAspectRect(lockHigh, anchor, 8, 32, aspect, Geometry.WmszRight, out result) && RectEq(result, 100, 100, 407, 300), "WmszRight anchors right");
            Check(Geometry.LockAspectRect(lockHigh, anchor, 8, 32, aspect, Geometry.WmszTop, out result) && RectEq(result, 100, 200, 407, 400), "WmszTop anchors top");
            Check(Geometry.LockAspectRect(lockHigh, anchor, 8, 32, aspect, Geometry.WmszTopLeft, out result) && RectEq(result, 293, 200, 600, 400), "WmszTopLeft anchors topleft");
            Check(Geometry.LockAspectRect(lockHigh, anchor, 8, 32, aspect, Geometry.WmszTopRight, out result) && RectEq(result, 100, 200, 407, 400), "WmszTopRight anchors topright");
            Check(Geometry.LockAspectRect(lockHigh, anchor, 8, 32, aspect, Geometry.WmszBottom, out result) && RectEq(result, 100, 100, 407, 300), "WmszBottom anchors bottom");
            Check(Geometry.LockAspectRect(lockHigh, anchor, 8, 32, aspect, Geometry.WmszBottomLeft, out result) && RectEq(result, 293, 100, 600, 300), "WmszBottomLeft anchors bottomleft");
            Check(Geometry.LockAspectRect(lockHigh, anchor, 8, 32, aspect, Geometry.WmszBottomRight, out result) && RectEq(result, 100, 100, 407, 300), "WmszBottomRight anchors bottomright");

            // 非法方向按右下处理。
            Check(Geometry.LockAspectRect(lockHigh, anchor, 8, 32, aspect, 99, out result) && RectEq(result, 100, 100, 407, 300), "default dir = bottomright");

            // 锁宽组：drag {100,100,600,500} → cw=492, ch=368, ratio 1.337<aspect → nch=277, W=500, H=309。
            Rect lockWide = new Rect { Left = 100, Top = 100, Right = 600, Bottom = 500 };
            Check(Geometry.LockAspectRect(lockWide, anchor, 8, 32, aspect, Geometry.WmszBottom, out result) && RectEq(result, 100, 100, 600, 409), "WmszBottom lock-wide");

            // 拖动矩形过小 → 不锁定。
            Check(!Geometry.LockAspectRect(new Rect { Left = 100, Top = 100, Right = 250, Bottom = 300 }, anchor, 8, 32, aspect, Geometry.WmszRight, out result), "Lock too-small drag returns false");
        }

        // ---------- ClipboardProtocol.Parse ----------
        private static byte[] BuildMessage(Guid id, string text, string[] paths)
        {
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms, Encoding.UTF8))
            {
                w.Write(ClipboardProtocol.Magic);
                w.Write(ClipboardProtocol.Version);
                w.Write(id.ToByteArray());
                w.Write(text);
                w.Write(paths.Length);
                foreach (var p in paths) w.Write(p);
                return ms.ToArray();
            }
        }

        private static void TestClipboardParse()
        {
            Guid id = new Guid("10203040506070809000a0b0c0d0e0f0");
            string[] paths = new[] { @"C:\a.txt", @"C:\b dir\x.txt" };

            // 合法消息。
            byte[] ok = BuildMessage(id, "hello", paths);
            using (var ms = new MemoryStream(ok))
            using (var r = new BinaryReader(ms, Encoding.UTF8))
            {
                Guid outId; string[] outPaths; string outText;
                Check(ClipboardProtocol.Parse(r, out outId, out outPaths, out outText), "Parse valid message");
                Check(outId == id, "Parse id round-trip");
                Check(outText == "hello", "Parse text");
                Check(outPaths.Length == 2 && outPaths[0] == paths[0] && outPaths[1] == paths[1], "Parse paths");
            }

            // 错误 magic → false。
            using (var ms = new MemoryStream(ok))
            using (var r = new BinaryReader(ms, Encoding.UTF8))
            {
                r.ReadInt32(); r.ReadInt32(); r.ReadBytes(16); r.ReadString(); int n = r.ReadInt32();
                // 重建一条但 magic 错。
                byte[] bad = BuildMessage(id, "hello", new string[0]);
                bad[0] = 0x00; // 改 magic 低字节
                using (var ms2 = new MemoryStream(bad))
                using (var r2 = new BinaryReader(ms2, Encoding.UTF8))
                {
                    Guid oi; string[] op; string ot;
                    Check(!ClipboardProtocol.Parse(r2, out oi, out op, out ot), "Parse bad magic rejected");
                }
            }

            // 错误 version → false。
            {
                byte[] bad = BuildMessage(id, "t", new string[0]);
                bad[4] = 0x7F; // version 改成非 2
                using (var ms = new MemoryStream(bad))
                using (var r = new BinaryReader(ms, Encoding.UTF8))
                {
                    Guid oi; string[] op; string ot;
                    Check(!ClipboardProtocol.Parse(r, out oi, out op, out ot), "Parse bad version rejected");
                }
            }

            // count 越界（负 / 超大）→ false。
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms, Encoding.UTF8))
            {
                w.Write(ClipboardProtocol.Magic); w.Write(ClipboardProtocol.Version);
                w.Write(id.ToByteArray()); w.Write("t"); w.Write(-1);
                byte[] bad = ms.ToArray();
                using (var ms2 = new MemoryStream(bad))
                using (var r = new BinaryReader(ms2, Encoding.UTF8))
                {
                    Guid oi; string[] op; string ot;
                    Check(!ClipboardProtocol.Parse(r, out oi, out op, out ot), "Parse negative count rejected");
                }
            }
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms, Encoding.UTF8))
            {
                w.Write(ClipboardProtocol.Magic); w.Write(ClipboardProtocol.Version);
                w.Write(id.ToByteArray()); w.Write("t"); w.Write(99999);
                byte[] bad = ms.ToArray();
                using (var ms2 = new MemoryStream(bad))
                using (var r = new BinaryReader(ms2, Encoding.UTF8))
                {
                    Guid oi; string[] op; string ot;
                    Check(!ClipboardProtocol.Parse(r, out oi, out op, out ot), "Parse oversized count rejected");
                }
            }

            // id 不足 16B → false。
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms, Encoding.UTF8))
            {
                w.Write(ClipboardProtocol.Magic); w.Write(ClipboardProtocol.Version);
                w.Write(new byte[8]); w.Write("t"); w.Write(0);
                byte[] bad = ms.ToArray();
                using (var ms2 = new MemoryStream(bad))
                using (var r = new BinaryReader(ms2, Encoding.UTF8))
                {
                    Guid oi; string[] op; string ot;
                    Check(!ClipboardProtocol.Parse(r, out oi, out op, out ot), "Parse short id rejected");
                }
            }
        }

        // ---------- ReconnectPolicy（v1.4.0 补测试基线：连接状态机决策） ----------
        private static void TestReconnectPolicy()
        {
            // 触发条件：本地主动断开（reason==1）不自动重连，其余（远端/致命/登录/超时）进入。
            Check(!ReconnectPolicy.ShouldAutoReconnect(1), "local disconnect(1) no auto-reconnect");
            Check(ReconnectPolicy.ShouldAutoReconnect(0xC07), "remote disconnect auto-reconnect");
            Check(ReconnectPolicy.ShouldAutoReconnect(0), "fatal/other reason auto-reconnect");

            // Enter 首次进入：进行中、计数 0、未放弃。
            var p = new ReconnectPolicy();
            p.Enter();
            Check(p.InProgress && p.Attempts == 0 && !p.GaveUp, "enter starts in-progress, attempts 0");

            // Enter 幂等：已在重连时再次 Enter 不重置计数。
            p.OnAttempt();   // attempts=1
            p.Enter();
            Check(p.Attempts == 1, "enter is idempotent (keeps attempts)");

            // 先清零再测上限：6 次内继续，第 7 次触发放弃。
            p.OnReconnected();
            p.Enter();   // attempts 归 0
            Check(p.InProgress && p.Attempts == 0, "fresh enter attempts 0");
            for (int i = 1; i <= 6; i++) p.OnAttempt();
            Check(p.Attempts == 6 && p.InProgress && !p.GaveUp, "attempt 6 still in progress, not gave up");
            p.OnAttempt();   // 第 7 次 > MaxAttempts
            Check(p.Attempts == 7 && p.GaveUp && !p.InProgress, "attempt 7 gave up, exits in-progress");

            // OnAttempt 在非进行中无效（放弃后不再递增）。
            int after = p.Attempts;
            p.OnAttempt();
            Check(p.Attempts == after, "on-attempt after gaveup is no-op");

            // 连接恢复：退出重连、清零。
            p.Enter();
            p.OnAttempt();
            p.OnReconnected();
            Check(!p.InProgress && p.Attempts == 0 && !p.GaveUp, "reconnected resets");

            // 手动重连：退出重连、清零。
            p.Enter();
            p.OnAttempt();
            p.OnManualReconnect();
            Check(!p.InProgress && p.Attempts == 0 && !p.GaveUp, "manual reconnect resets");
        }

        private static int Main()
        {
            TestToMillimeters();
            TestComputeLetterbox();
            TestLockAspectRect();
            TestClipboardParse();
            TestReconnectPolicy();
            Console.WriteLine("RayDesktopTests: " + _passed + " passed, " + _failed + " failed.");
            return _failed == 0 ? 0 : 1;
        }
    }
}
