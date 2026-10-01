# Dynamic Portrait

FFXIV Dalamud 插件，在独立小窗中显示跟随角色骨骼的镜头。可以选择角色与骨骼，调整角度、距离、FOV、平滑和窗口大小。目前仍处于实验阶段。

## 安装

在 `/xlsettings` → **Experimental** → **Custom Plugin Repositories** 中添加：

```text
https://raw.githubusercontent.com/zysilm/DynamicPortrait/main/pluginmaster.json
```

保存后，在 `/xlplugins` 中搜索 **Dynamic Portrait** 并安装。

## 使用

输入 `/dportrait` 打开设置，勾选 **Render portrait** 开启。每次加载插件后渲染默认关闭。

默认跟随自身的 **Chest**（`j_sebo_c`），输出尺寸默认 1024，上限 4096；默认窗口大小为 340 × 380。诊断模式只复制主画面，相机控制在该模式下禁用。

- `/dportrait on` / `off`：开启 / 停止渲染。
- `/dportrait toggle`：显示 / 隐藏小窗。
- `/dportrait reset`：重置小窗。
- `/dportrait resetall` 或 **Reset all settings**：恢复全部默认设置、清除锁定角色并停止渲染。

## 构建与发布

需要 .NET 10 和 Dalamud API 15。

```powershell
dotnet build DynamicPortrait/DynamicPortrait.csproj -c Release
```

DLL 位于 `DynamicPortrait/bin/Release/DynamicPortrait.dll`。Windows 默认使用 `%APPDATA%/XIVLauncher/addon/Hooks/dev`；其他位置可设置 `DALAMUD_HOME`。

与 CombatSimulator 一样，推送 `main` 自动构建并发布 `v版本号`，更新自定义仓库索引；发布新版本前同步更新 csproj 与插件清单中的版本号。

## 许可

[AGPL-3.0-or-later](LICENSE)。渲染实现参考 [FFXIV VR](https://github.com/WesleyLuk90/ffxiv-vr)，相机与 CI 参考 [CombatSimulator](https://github.com/zysilm/FFXIV-CombatSimulator)。详细说明见 [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md)。
