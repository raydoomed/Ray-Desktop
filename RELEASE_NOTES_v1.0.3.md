## Ray Desktop v1.0.3

分辨率机制改进 + 稳定性修复。

### 主要变更
- **固定分辨率 Letterbox**：会话分辨率在启动时固定为主机完整分辨率，窗口任意缩放只做等比缩放，办公与游戏画面都不变形、鼠标对齐（默认模式）
- **可选 `--dynamic`**：分辨率随窗口实时变化、画面铺满无黑边，适合纯办公场景（`Raydesktop.exe --dynamic`）
- **修复最小尺寸比例冲突**：窗口缩到最小也保持主机比例、无黑边
- **清理冗余代码与无用资产**：移除失效引用、旧图标与旧版本发布包
- **README 同步更新**：分辨率机制与窗口最小尺寸说明

### 发布内容
- Raydesktop.exe 主程序
- EnableChildSessions.exe 启用 Child Sessions 辅助程序
- MSTSCLib.dll / AxInterop.MSTSCLib.dll RDP 互操作程序集

### 使用
1. 系统需开启远程桌面（设置 → 系统 → 远程桌面）
2. 解压后双击 `Raydesktop.exe`
3. 首次按 UAC 启用 Child Sessions，注销/重启一次后即可使用

面向 Windows 11；首次运行需在系统设置开启远程桌面，并按 README 完成首次启用流程。
