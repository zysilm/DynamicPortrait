# DynamicPortrait 实现计划

## 0.1.0 实验实现状态（2026-09-29）

现已实现可编译的 API 15 插件、骨骼相机、小窗与设置、GPU 场景捕获、配置和发布工作流。当前选择 FFXIV VR 的双 Framework tick 路径作为实验后端：肖像 tick 捕获纹理并跳过 Present，随后正常 tick 显示主画面。它尚未达到下述原计划中“逻辑只更新一次”的交付门槛，也没有完成时域历史隔离或游戏内双视角验收。不能把工程完成和编译通过等同于这些门槛通过。

当前具体行为见根目录 README，游戏内验收项见 `docs/RUNTIME_VALIDATION.md`。下文保留作为后续优化与稳定版验收依据。

## 目标与范围

在游戏内显示一个可移动、缩放的 Dalamud 小窗，实时显示同一场景中另一台相机的画面，默认从角色正面拍摄脸部。主游戏视角和操作保持正常。

首版只做一个画面窗口：自己 / 当前目标 / 锁定角色、骨骼选择、相机角度与距离、构图偏移、FOV、平滑、窗口尺寸、画质与刷新率、配置保存。默认保留场景背景。透明背景、独立灯光、多窗口、录制和自动演出暂不纳入。

“小窗”按游戏内 ImGui 窗口理解；独立操作系统窗口不是首版目标。

## 已检查的参考实现

调研日期：2026-09-29。当前 DynamicPortrait 仓库为空，尚无插件工程。

### xivr-Ex

仓库：https://github.com/ProjectMimer/xivr-Ex

本地参考：`C:/project/xivr-Ex`，提交 `3e183e9b6007ac445226eee1cd7a6bee3fd765a1`。

- `xivr-Ex/xivr_hooks.cs:2934`：`FrameworkTickFn` 为左右眼分别调用一次原始 Framework tick。不能把它理解成现成的独立 RenderToTexture API。
- 同文件 `RunGameTasksFn`：遍历游戏任务，在尾部任务之前插入用于区分眼睛的渲染命令。
- `RenderThreadSetRenderTargetFn`：渲染线程消费该标记，调用 native `SetThreadedEye`；说明跨线程视角状态需要随命令流传递。
- `xivr_main/dllmain.cpp:822`：`SetThreadedEye` 使用 D3D11 `CopyResource` 保存场景颜色与深度；`RenderVR` 轮换缓冲。
- `CalculateViewMatrix2Fn` / `MakeProjectionMatrix2Fn`：分别干预观察矩阵和投影矩阵。
- README 指向 Dawntrail / Dalamud 10，并明确说明性能问题和暂停开发。旧签名、任务索引、内存偏移不能直接用于当前客户端。

结论：可参考多视角调度、命令流标记、GPU 纹理捕获；不能直接照搬双 tick、VR 依赖或旧版偏移。第二路纯渲染入口尚未验证。

### 后续维护的 FFXIV VR（补充调研）

仓库：https://github.com/WesleyLuk90/ffxiv-vr

本地参考：`C:/project/ffxiv-vr`，提交 `bd5b4a9f6be472018411520c8fb4aae7cf43731e`，提交时间 2026-09-25，版本 0.0.76，Dalamud SDK / API 15。README 声明支持 Dawntrail 7.x。它是当前优先参考的 VR 项目；未找到足够证据将其描述为 ProjectMimer 官方迁仓。

- `FfxivVr/game/GameHooks.cs`：`FrameworkTickDetour` 仍调用两次原始 tick；`SetMatricesDetour` 在原函数后更新当前视角矩阵。
- `FfxivVr/vr/VRCamera.cs`：分别写入 view、projection、projection2，显式处理 reverse-Z，可参考当前矩阵注入位置。
- `FfxivVr/vr/VRSession.cs`：左右视角阶段切换，在 UI 渲染前排入纹理捕获命令。
- `FfxivVr/vr/Resources.cs`：两个 `SceneRenderTargets`，包含可采样的 shader resource view。
- `FfxivVr/vr/Renderer.cs`：`CopyTexture` 将对应视角的游戏画面复制到独立 GPU 纹理。
- `FfxivVr/vr/RenderManager.cs`：跳过左眼桌面 Present，以右眼作为桌面画面；不是一个额外的普通主相机视角。

判断：已有实际双视角和独立纹理实现，因此改造成主视角加自拍窗口有明确源码基础。窗口展示层可由 DynamicPortrait 的 ImGui UI 实现；原项目本身不提供开箱即用的独立自拍窗口。将两眼替换为普通主视角和骨骼相机、去除 OpenXR 会话依赖、隔离状态并处理任意大角度下的剔除仍需开发验证。双 tick 本身不能证明游戏速度必然翻倍，也不能证明逻辑只更新一次，需通过调用计数和实际时序测量判断影响。

据此调整优先级：P0 先沿此项目当前的矩阵注入、纹理捕获和 Present 调度定位入口，再验证额外纯渲染路径。允许将双 tick 链路作为受控调研参照，但不将其直接认定为满足本计划逻辑单次更新的交付实现。

该工程声明 `AGPL-3.0-or-later`；如直接复用实现，需将许可证选择纳入工程决策。

### CombatSimulator

本地参考：`C:/project/FFXIV-CombatSimulator`，HEAD `1dc1fff6466ff02af1e2cde8d854cbc8fb1c9eb2`。本地有未提交修改，本次调研没有修改该仓库。

- `CombatSimulator/Camera/ActiveCameraController.cs`：骨骼名称查找、同步 model-space pose、骨骼世界位置、近距离角色可见性处理。这里改变的是主相机，不是第二路渲染。
- `CombatSimulator/Animation/BoneTransformService.cs`：`GetBoneNames`、`GetBoneWorldPos`、`GetBoneWorldTransform` 可参考；世界变换实现仍需补充验证缩放及不同 partial skeleton。
- `CombatSimulator/Camera/GameCameraView.cs`：直接读取实际 view / projection，避免凭经验猜测矩阵方向和 FOV 约定。
- `GameCameraUpdateHook.cs`：相机更新时机和主相机识别。
- 工程使用 `Dalamud.NET.Sdk/15.0.0`，workflow 使用 .NET 10。作为工程起点，实际开发时再核对目标客户端及安装的 Dalamud，而非认定所有客户端都适用。
- `.github/workflows/build.yaml`、`release.yaml`：构建、SDK 打包、GitHub Release、生成并更新 `pluginmaster.json` 的完整链路。

CombatSimulator 源文件有 MPL-2.0 标头；如复用代码，保留相关标头并履行相应要求。xivr-Ex 本次未检出 LICENSE 文件，先参考机制独立实现，不直接移植大段代码。

## 核心技术路线

### 1. 先验证额外场景渲染

第一优先级是找出当前客户端的场景渲染任务边界、相机状态来源、资源绑定及提交时序。期望每帧逻辑只更新一次，按小窗刷新预算额外提交一次场景渲染；游戏 UI 和 Present 保持正常执行次数。

期望的数据流（具体 pass 顺序由原型验证）：

```text
游戏逻辑与动画更新一次
  → 在安全时机读取目标骨骼，形成不可变相机快照
  → 正常场景 pass + 到期时的 portrait 场景 pass
  → portrait 完成纹理交给 ImGui
  → 正常游戏 UI / Dalamud UI / Present
```

优先调查引擎是否已有适合额外 view 的场景提交路径；若没有，再验证限定范围的渲染任务重入。不能通过重放主视角的最终 draw commands 假定得到任意角度画面，因为可见集、LOD、常量、阴影等可能已依赖原视角。

如果必须暂时借用引擎全局相机或中间目标：明确列出读写字段，限定作用范围，恢复 CPU 状态，并确认延迟执行的 GPU / 渲染线程命令拥有正确的数据生命周期。仅在 CPU 返回时恢复矩阵不足以保证正确。

禁止把完整 Framework tick 调用两次作为交付方案。如果无法分离额外场景提交，记录阻塞入口和实验结果，重新评估引擎的离屏角色预览路径；后者可能丢失实时世界背景和当前角色状态，不能静默当作同等实现。

### 2. 纹理与渲染隔离

- 使用同一 D3D11 device 创建 / 管理 GPU 纹理，通过当前 Dalamud 支持的纹理接口提供给 ImGui；具体包装 API 在工程落地时核对。
- 原型允许先使用引擎原尺寸中间目标，再缩小输出。独立低分辨率场景目标是后续性能验证项，不能仅凭小窗尺寸承诺低开销。
- `CopyResource` 不负责缩放；尺寸或格式不匹配时使用合适的 resolve / shader blit / 色彩转换路径。
- portrait 至少需要独立的最终输出；深度、G-buffer、阴影、曝光、运动矢量、TAA / 升频历史等逐项确认是否可共享、需隔离或应禁用。
- 第二相机重新计算或正确扩展可见集，验证主相机背后的角色仍能出现、动画不因主视角剔除而停止。
- 使用带 frame/pass 标识的命令或等价机制传递状态，不能只用游戏线程上的全局布尔值判断渲染线程所在视角。
- 双缓冲或环形输出纹理，保证 ImGui 采样、GPU 写入和纹理销毁的顺序；允许显示最近完成的一帧。
- Resize 做合并 / 防抖与尺寸上限；卸载前停止提交，等待必要的在途使用结束，再释放资源。

### 3. 骨骼跟随与构图

- 默认 `j_kao`，提供头、颈、胸等常用项和实际骨架名称列表；不承诺每个模型都有同名骨骼。
- 读取动画更新后的 model-space pose，结合 skeleton 的平移、旋转和缩放得到世界位置。验证 partial skeleton、骑乘、变身和特殊模型。
- 位置锚点与角度参考分开：默认追踪头骨位置、相对角色身体朝向拍摄，避免头部轻微动作带动整个镜头旋转；可切换跟随骨骼旋转或固定世界方向。
- 相机参数：yaw / pitch / roll、distance、局部 look-at 偏移、FOV、near clip、位置 / 朝向平滑。近裁剪和坐标约定以实际引擎投影为准。
- 索引缓存随角色实例、DrawObject 或骨架变化失效。跨线程传数值快照，不长期缓存 pose 指针；锁定目标需校验身份，避免地址复用。
- 骨骼缺失时显示明确状态，必要时回退到已验证的角色锚点。切图 / 目标消失时停止采样和额外提交。
- 近距离隐藏、碰撞等调整限定于 portrait pass 和目标对象，不复用参考插件中全局强制可见的行为。

## 工程划分

保持一个 C# 插件工程，优先使用 unsafe C# 与合适的 D3D11 绑定；只有明确需要 native 时再增加辅助 DLL，不引入 OpenVR。

| 模块 | 职责 |
| --- | --- |
| Plugin / Configuration | 服务接入、命令、配置版本和生命周期 |
| TargetResolver / BonePoseReader | 目标有效性、骨架枚举和当前姿态快照 |
| PortraitCameraSolver | 纯数学构图、平滑、投影参数 |
| PortraitRenderBackend | 引擎入口、pass 调度、状态隔离 |
| PortraitTextureBridge | GPU 输出、Dalamud 纹理包装、尺寸与释放 |
| PortraitWindow / SettingsWindow | 图像窗口和参数 UI |
| Compatibility / Diagnostics | 签名校验、兼容状态、帧耗时与资源统计 |

渲染 backend 与 UI 解耦，便于替换实验路径；不预先搭建通用多相机框架。

## 实施顺序与验收门槛

### P0：工程与渲染探针

建立最小插件、命令和诊断窗口，记录目标游戏 / Dalamud / FFXIVClientStructs 版本。定位渲染任务和线程边界；先验证 GPU 场景纹理能在小窗显示。此时复制主画面仅用来验证纹理链路，不算实现第二相机。

产出：关键入口及签名记录、每帧执行计数、纹理来源 / 格式 / 尺寸、候选第二 pass 入口。

### P1：真正第二视角（最大不确定性）

固定自拍角度或静态第二相机，额外生成场景画面并显示到固定大小窗口。先处理正确性，再做低分辨率优化。

必须通过：主画面和小窗同时保持不同角度；移动、输入、动画时序正常；逻辑无重复更新；主视角外的目标正确显示；UI 不递归捕获；关闭 / 卸载恢复正常。验证不同抗锯齿 / 升频设置下的主画面和小窗历史污染。

未通过此门槛前，不投入完整设置 UI 或发布自动化。

### P2：可用首版

加入目标选择、头骨默认构图、骨骼下拉、角度 / 距离 / FOV / 偏移 / 平滑、窗口拖动缩放、锁定与配置保存。参数通过不可变快照交给渲染端。

建议起始配置：约 320×320 显示、512×512 渲染目标（若 backend 支持）、30 FPS 目标刷新率。刷新预算按时间累计，不通过每隔固定游戏帧实现。窗口关闭 / 隐藏时停止额外渲染。

### P3：性能与兼容性

测量关闭、开启但不刷新、15 / 30 / 60 FPS 和不同输出尺寸的 CPU / GPU 耗时。确认瓶颈后再裁减 portrait 的后处理、阴影或场景内容。

覆盖切图、传送、目标销毁、换装、骨架重建、骑乘、死亡、窗口与游戏分辨率变化、插件重载；验证与其他相机插件共存时不会覆盖主视角状态。过场、GPose、登录界面首版默认暂停，后续按验证结果开放。

签名失配或初始化失败时关闭 backend 并显示诊断。托管异常捕获不能保证原生内存访问安全，关键保护必须在访问前的版本、对象与生命周期校验中完成。

### P4：CI/CD 与首个测试发布

参考 CombatSimulator 的 SDK 打包与自定义源流程，去掉其私有模块和业务专属检查。

- PR / 分支：restore、Release build、纯数学和配置迁移的必要测试、上传构建 artifact。
- 发布：建议通过版本 tag 触发，核对程序集 / manifest / tag 版本及 API level，从 SDK 产物生成安装 ZIP，发布 GitHub Release，再更新 `pluginmaster.json`。
- 使用不可变版本下载链接；发布完成后才更新安装源，避免指向不存在的 ZIP。索引更新保持幂等并控制并发。
- 固定 SDK 和依赖版本；发布记录所用 Dalamud 分发版本，额外做最新分发兼容检查。C# 工程可沿用 Ubuntu runner；若加入 C++ DLL，再使用 Windows runner 构建并检查打包完整性。
- GitHub Actions 能验证编译和打包，不能代替 FFXIV 进程内的双视角、资源生命周期及性能测试。

## 首轮开发结论

功能规模小，主要工作量集中在 P0 / P1 的渲染边界与状态隔离。先交付可证明独立第二视角的实验版，再扩展骨骼和界面；在原型通过前不承诺性能数字或当前客户端已兼容。
