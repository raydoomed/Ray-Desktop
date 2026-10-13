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
        private static void TestLockAspectRect()
        {
            Rect anchor = new Rect { Left = 100, Top = 100, Right = 600, Bottom = 400 };
            Rect result;
            const double aspect = 16.0 / 9.0;

            // 右拖：cw/ch>aspect → 锁高。ncw=476,W=484,H=300 → Right=584。
            Check(Geometry.LockAspectRect(new Rect { Left = 100, Top = 100, Right = 700, Bottom = 400 }, anchor, 8, 32, aspect, 2, out result),
                "Lock right returns true");
            Check(result.Left == 100 && result.Right == 584 && result.Top == 100 && result.Bottom == 400,
                "Lock right anchored: 100,584,100,400");

            // 下拖：cw/ch<aspect → 锁宽。ncw=492,nch=277,W=500,H=309 → Bottom=409。
            Check(Geometry.LockAspectRect(new Rect { Left = 100, Top = 100, Right = 600, Bottom = 500 }, anchor, 8, 32, aspect, 4, out result),
                "Lock bottom returns true");
            Check(result.Left == 100 && result.Right == 600 && result.Top == 100 && result.Bottom == 409,
                "Lock bottom anchored: 100,600,100,409");

            // 左拖：锁高 ncw=476,W=484 → Left=600-484=116。
            Check(Geometry.LockAspectRect(new Rect { Left = 50, Top = 100, Right = 600, Bottom = 400 }, anchor, 8, 32, aspect, 1, out result),
                "Lock left returns true");
            Check(result.Left == 116 && result.Right == 600 && result.Top == 100 && result.Bottom == 400,
                "Lock left anchored: 116,600,100,400");

            // 左上拖：cw/ch<aspect → 锁宽。ncw=542,nch=305,W=550,H=337 → Left=50,Top=63。
            Check(Geometry.LockAspectRect(new Rect { Left = 50, Top = 60, Right = 600, Bottom = 400 }, anchor, 8, 32, aspect, 5, out result),
                "Lock topleft returns true");
            Check(result.Left == 50 && result.Right == 600 && result.Top == 63 && result.Bottom == 400,
                "Lock topleft anchored: 50,600,63,400");

            // 右下拖：锁宽。ncw=592,nch=333,W=600,H=365 → Right=700,Bottom=465。
            Check(Geometry.LockAspectRect(new Rect { Left = 100, Top = 100, Right = 700, Bottom = 500 }, anchor, 8, 32, aspect, 8, out result),
                "Lock bottomright returns true");
            Check(result.Left == 100 && result.Right == 700 && result.Top == 100 && result.Bottom == 465,
                "Lock bottomright anchored: 100,700,100,465");

            // 默认方向（非法 dir）按右下处理。
            Check(Geometry.LockAspectRect(new Rect { Left = 100, Top = 100, Right = 700, Bottom = 500 }, anchor, 8, 32, aspect, 99, out result),
                "Lock default dir returns true");
            Check(result.Left == 100 && result.Right == 700 && result.Top == 100 && result.Bottom == 465,
                "Lock default dir = bottomright");

            // 拖动矩形过小 → 不锁定。
            Check(!Geometry.LockAspectRect(new Rect { Left = 100, Top = 100, Right = 250, Bottom = 300 }, anchor, 8, 32, aspect, 2, out result),
                "Lock too-small drag returns false");
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

        private static int Main()
        {
            TestToMillimeters();
            TestComputeLetterbox();
            TestLockAspectRect();
            TestClipboardParse();
            Console.WriteLine("RayDesktopTests: " + _passed + " passed, " + _failed + " failed.");
            return _failed == 0 ? 0 : 1;
        }
    }
}
