# 0.1.0.2 UI 频闪修正候选

游戏内反馈：开启渲染后，设置窗口和肖像窗口一起频闪。

旧版在 `PortraitUi.Draw` 中读取 CPU 端 `inPortrait`，为 true 时跳过整个
ImGui 窗口提交。这个标志只表示额外 Framework tick 尚未返回，不表示当前
Dalamud UI 帧应该被丢弃。跳过提交会让两个窗口在对应 UI 帧消失，也会跳过
肖像窗口的可见性刷新。

本版移除这个条件，每次 UiBuilder.Draw 均按窗口开关正常提交窗口。
捕获仍位于游戏 UI 命令前；Present 仍由渲染线程捕获标记驱动。本次没有
改变第二次游戏 tick、相机覆盖或纹理源，因此不宣称这些部分已在游戏内验证。

Diagnostics 新增 UI draws、During portrait submission、Main-chain presents。
第二项记录旧代码会跳过 UI 的次数；它增长可说明旧条件确实会在当前环境触发，
但不能单独证明 Present 或相机渲染正确。计数只在插件重载时清零。

游戏内需要确认：

- 开启/关闭渲染时两个窗口保持显示，拖动和调整大小正常。
- Captures 和 Suppressed presents 持续增长，稳定后相差通常为 0 或 1；
  Main-chain presents 也持续增长。计数不是原子快照，不能只凭瞬时差值判错。
- 肖像实际显示游戏中的目标角色；改变 Yaw/Pitch 只改变肖像视角。
- 若窗口稳定但肖像错误，继续检查游戏相机和捕获时序，不视为完成。

离线 WARP 立方体测试只覆盖独立 D3D11 复制、裁剪、相机数学和模拟调度，
不是 FFXIV 截图，不能验证本次 UI 频闪修正的游戏内效果。
