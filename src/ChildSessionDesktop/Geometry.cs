using System;
using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("RayDesktopTests")]

namespace ChildSessionDesktop
{
    // 无 UI/COM 依赖的纯几何与换算函数，独立于窗体与 ActiveX，可直接单元测试。
    // P2-10：为这些纯函数建立回归基线，改动此文件内的任何计算前先跑 tests。
    internal static class Geometry
    {
        // WM_SIZING 拖动方向常量（Win32 标准值，注意不是顺时针顺序：WMSZ_BOTTOM=6 插在 TOPLEFT(4)/TOPRIGHT(5) 之间）。
        internal const int WmszLeft = 1, WmszRight = 2, WmszTop = 3, WmszTopLeft = 4,
            WmszTopRight = 5, WmszBottom = 6, WmszBottomLeft = 7, WmszBottomRight = 8;

        // 像素 → 毫米（RDP UpdateSessionDisplaySettings 需要；dpi<=0 按 96 兜底，最小 10mm）
        internal static uint ToMillimeters(int pixels, float dpi)
        {
            if (dpi <= 0) dpi = 96;
            return (uint)Math.Max(10, Math.Round(pixels * 25.4 / dpi));
        }

        // Letterbox：给定客户区与会话分辨率，算画面（不放大、放不下等比缩小）的逻辑尺寸与居中偏移。
        // ActiveX 永不放大（只 1:1，放不下才等比缩小）→ 客户区比画面大时画面居中、四周露纯窗口底；
        // 客户区比画面小时等比缩小铺满（ActiveX 缩小，无灰）。
        internal static void ComputeLetterbox(int clientW, int clientH, int sessW, int sessH,
            out int dw, out int dh, out int dx, out int dy)
        {
            if (clientW <= 0 || clientH <= 0) { dw = clientW; dh = clientH; dx = 0; dy = 0; return; }
            if (sessW > 0 && sessH > 0)
            {
                double s = Math.Min((double)clientW / sessW, (double)clientH / sessH);
                if (s > 1.0) s = 1.0;
                dw = (int)(sessW * s + 0.5);
                dh = (int)(sessH * s + 0.5);
            }
            else { dw = clientW; dh = clientH; }
            dx = (clientW - dw) / 2;
            dy = (clientH - dh) / 2;
        }

        // WM_SIZING 宽高比锁定：给定拖动矩形、窗口实际四边（锚）、边框+标题栏尺寸、会话宽高比与拖动方向，
        // 计算锁定宽高比后的新窗口矩形并锚定实际四边（只移动被拖的边，防止 Aero Snap 吸附到屏幕顶部/边缘）。
        // 方向 dir 取 Win32 WMSZ_* 值（WmszLeft..WmszBottomRight），非法值按右下处理。
        // 返回 false 表示拖动矩形过小（cw<=160 或 ch<=120），不锁定。
        internal static bool LockAspectRect(Rect drag, Rect anchor, int bw, int bh, double aspect, int dir, out Rect result)
        {
            int cw = (drag.Right - drag.Left) - bw;
            int ch = (drag.Bottom - drag.Top) - bh;
            result = default(Rect);
            if (!(cw > 160 && ch > 120)) return false;

            int ncw, nch;
            if ((double)cw / ch > aspect) { ncw = (int)(ch * aspect + 0.5); nch = ch; }
            else { ncw = cw; nch = (int)(cw / aspect + 0.5); }
            int W = bw + ncw;
            int H = bh + nch;

            int L = anchor.Left, T = anchor.Top, R = anchor.Right, B = anchor.Bottom;
            result = new Rect { Left = L, Top = T, Right = R, Bottom = B };
            switch (dir)
            {
                case WmszLeft:        result.Left = R - W; result.Right = R; result.Top = T; result.Bottom = T + H; break; // 1 左
                case WmszRight:       result.Right = L + W; result.Left = L; result.Top = T; result.Bottom = T + H; break; // 2 右
                case WmszTop:         result.Top = B - H; result.Bottom = B; result.Left = L; result.Right = L + W; break; // 3 上
                case WmszTopLeft:     result.Left = R - W; result.Right = R; result.Top = B - H; result.Bottom = B; break; // 4 左上
                case WmszTopRight:    result.Right = L + W; result.Left = L; result.Top = B - H; result.Bottom = B; break; // 5 右上
                case WmszBottom:      result.Bottom = T + H; result.Top = T; result.Left = L; result.Right = L + W; break; // 6 下
                case WmszBottomLeft:  result.Left = R - W; result.Right = R; result.Bottom = T + H; result.Top = T; break; // 7 左下
                case WmszBottomRight: result.Right = L + W; result.Left = L; result.Bottom = T + H; result.Top = T; break; // 8 右下
                default:              result.Right = L + W; result.Left = L; result.Bottom = T + H; result.Top = T; break;
            }
            return true;
        }
    }

    // 纯几何矩形（int 四边），无 Win32/WinForms 依赖，供 Geometry 与测试使用。
    internal struct Rect
    {
        public int Left, Top, Right, Bottom;
    }
}
