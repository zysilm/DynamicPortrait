# 0.1.0.4：绕开空的中间纹理

## 游戏内确认（2026-10-01）

用户确认 0.1.0.4 默认诊断模式的小窗已经显示游戏画面。这验证了最终 backbuffer
到小窗的显示路径。用户同时确认相机相关 GUI 选项不影响画面，符合诊断模式
直接复制主画面的设计。第二相机尚未修复；不能把这次成功描述成独立肖像镜头可用。

## 修改与验证记录

0.1.0.3 的游戏内日志显示：主相机诊断模式下，采样的 ToneAdjustSource 裁剪
区域 Alpha 为 0..0、maxRGB 为 0。切换到第二相机仍报告相同结果。
用户确认主相机诊断模式不闪，第二相机会使主画面文字和 3D 闪烁，两者小窗均黑。
因此 Alpha 转不透明只解决了透明输出，不是黑屏根因的完整修复。

本次将默认诊断模式改为在主 SwapChain.Present 的原函数调用前，通过
IDXGISwapChain.GetBuffer(0, ID3D11Texture2D) 获取实际 backbuffer，再复制中心裁剪。
不再读取 ToneAdjustSource，也不在主相机诊断模式提交 pre-UI 队列标记。
它会包含游戏 UI，且可能包含其他覆盖层，不能被当作独立第二相机。

GetBuffer 的 COM 引用在每次复制后释放；不跨 resize 保存 backbuffer。
第一帧记录像素采样，原有五秒统计保留。第二相机路径未宣称修复；增加 tick
进入/退出、矩阵应用和每次 Present 的有限事件追踪，供后续分析时序。

只读检查了本机游戏 EXE 的 Present 实现：它从 SwapChain+0x68 取得 DXGI
对象，并调用 vtable+0x40（Present）。没有修改游戏文件或进程。

离线验证增加隐藏 Win32 窗口上的实际 WARP DXGI swap chain，覆盖 GetBuffer
颜色复制、裁剪、Alpha 转换以及释放引用后 ResizeBuffers、重新获取。
窗口未显示，也未访问游戏进程。47 项渲染相关离线检查通过；仍须游戏内确认。

测试时重载 0.1.0.4，保持 Diagnostic: capture main view only 勾选，开启约
5 秒并观察小窗。若看到游戏主画面的中心裁剪，说明最终画面复制路径成立，
但第二相机、pre-UI 捕获和引擎共享状态问题仍未解决。
