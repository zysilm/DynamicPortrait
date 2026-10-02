// SPDX-License-Identifier: AGPL-3.0-or-later
using Dalamud.Bindings.ImGui;
using DynamicPortrait.Camera;
using DynamicPortrait.Rendering;
using System.Numerics;

namespace DynamicPortrait.UI;

internal sealed class PortraitUi(Configuration config, SubjectResolver subjects, PortraitRenderer renderer, Action save)
{
    private bool settingsOpen;
    private bool resetWindow = true;
    private bool dirty;
    private long lastSaved;
    private string boneFilter = "";

    public void OpenSettings() => settingsOpen = true;
    public void ResetAll()
    {
        renderer.SetEnabled(false);
        config.ResetDefaults();
        subjects.Clear();
        renderer.ResetSession();
        boneFilter = "";
        settingsOpen = true;
        dirty = false;
        save();
    }
    public void ResetWindow()
    {
        var defaults = new Configuration();
        config.WindowPosition = defaults.WindowPosition;
        config.WindowSize = defaults.WindowSize;
        config.ShowPortrait = true;
        config.LockWindow = config.Borderless = config.ClickThrough = false;
        resetWindow = true;
        save();
    }

    public void Draw()
    {
        // UiBuilder.Draw owns the ImGui frame. CPU portrait submission is not a
        // display-frame boundary: skipping here makes both windows disappear.
        renderer.RecordUiDraw();
        if (config.ShowPortrait) DrawPortrait();
        if (settingsOpen) DrawSettings();
        if (dirty && Environment.TickCount64 - lastSaved > 1000)
        {
            save(); dirty = false; lastSaved = Environment.TickCount64;
        }
    }

    private void DrawPortrait()
    {
        var flags = ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse;
        if (config.LockWindow) flags |= ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoResize;
        if (config.Borderless) flags |= ImGuiWindowFlags.NoTitleBar;
        if (config.ClickThrough) flags |= ImGuiWindowFlags.NoInputs;
        if (resetWindow)
        {
            ImGui.SetNextWindowPos(config.WindowPosition, ImGuiCond.Always);
            ImGui.SetNextWindowSize(config.WindowSize, ImGuiCond.Always);
            ImGui.SetNextWindowCollapsed(false, ImGuiCond.Always);
            resetWindow = false;
        }
        ImGui.SetNextWindowSizeConstraints(new(120, 120), new(4096, 4096));
        var shown = config.ShowPortrait;
        var visible = ImGui.Begin("Dynamic Portrait###DynamicPortrait.View", ref shown, flags);
        try
        {
            if (shown != config.ShowPortrait) { config.ShowPortrait = shown; dirty = true; }
            var pos = ImGui.GetWindowPos();
            var size = ImGui.GetWindowSize();
            if (pos != config.WindowPosition || size != config.WindowSize)
            { config.WindowPosition = pos; config.WindowSize = size; dirty = true; }
            if (!visible) return;
            var area = ImGui.GetContentRegionAvail();
            if (area.X < 1 || area.Y < 1) return;
            renderer.MarkVisible(area.X / area.Y);
            var texture = renderer.Texture;
            if (renderer.Enabled && renderer.SubjectReady && texture.Handle != 0)
            {
                var scale = Math.Min(area.X / texture.Width, area.Y / texture.Height);
                var imageSize = new Vector2(texture.Width * scale, texture.Height * scale);
                ImGui.SetCursorPos(ImGui.GetCursorPos() + (area - imageSize) / 2);
                ImGui.Image(new ImTextureID(texture.Handle), imageSize);
            }
            else
            {
                ImGui.TextWrapped(renderer.Status);
                if (!config.ClickThrough && ImGui.Button("Settings")) settingsOpen = true;
            }
            if (!config.ClickThrough && ImGui.BeginPopupContextWindow("portrait-context"))
            {
                if (ImGui.MenuItem("Settings")) settingsOpen = true;
                if (ImGui.MenuItem("Stop rendering")) renderer.SetEnabled(false);
                ImGui.EndPopup();
            }
        }
        finally { ImGui.End(); }
    }

    private void DrawSettings()
    {
        ImGui.SetNextWindowSize(new(560, 640), ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowSizeConstraints(new(460, 400), new(4096, 4096));
        if (!ImGui.Begin("Dynamic Portrait settings###DynamicPortrait.Settings", ref settingsOpen,
            ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse)) { ImGui.End(); return; }
        try
        {
            ImGui.BeginDisabled(!renderer.Ready);
            if (ImGui.Button(renderer.Enabled ? "Stop rendering" : "Start rendering"))
            {
                if (!renderer.Enabled) { config.ShowPortrait = true; dirty = true; }
                renderer.SetEnabled(!renderer.Enabled);
            }
            ImGui.EndDisabled();
            ImGui.SameLine();
            dirty |= ImGui.Checkbox("Show portrait window", ref config.ShowPortrait);
            ImGui.TextWrapped(renderer.Status);
            if (renderer.Fault != null) ImGui.TextWrapped($"Error: {renderer.Fault}");
            if (renderer.MainViewOnly) ImGui.TextWrapped("Main-view diagnostic mode is active. Camera controls are disabled.");
            ImGui.Separator();
            if (ImGui.BeginTabBar("portrait-settings-tabs"))
            {
                try
                {
                    DrawSettingsTab("Camera", DrawCameraSettings);
                    DrawSettingsTab("Output", DrawOutputSettings);
                    DrawSettingsTab("Window", DrawWindowSettings);
                    DrawSettingsTab("Diagnostics", DrawDiagnostics);
                }
                finally { ImGui.EndTabBar(); }
            }
            ImGui.Separator();
            if (ImGui.Button("Reset all settings")) ResetAll();
            Help("Restore settings, clear the locked subject, and stop rendering. Keep both windows' positions and sizes.");
        }
        finally { ImGui.End(); }
        if (dirty) config.Normalize();
    }

    private static void DrawSettingsTab(string label, Action draw)
    {
        if (!ImGui.BeginTabItem(label)) return;
        try
        {
            // Only the content scrolls. The tab bar, toolbar and reset action
            // belong to the non-scrolling parent window.
            var visible = ImGui.BeginChild($"settings-content-{label}",
                new Vector2(0, -ImGui.GetFrameHeightWithSpacing() - ImGui.GetStyle().ItemSpacing.Y));
            try { if (visible) draw(); }
            finally { ImGui.EndChild(); }
        }
        finally { ImGui.EndTabItem(); }
    }

    private void DrawCameraSettings()
    {
        ImGui.BeginDisabled(renderer.MainViewOnly);
        ImGui.PushItemWidth(-160);
        try
        {
            ImGui.TextUnformatted("Subject and bone");
            var subject = (int)config.Subject;
            if (ImGui.Combo("Subject", ref subject, "Self\0Current target\0Locked character\0"))
            { config.Subject = (SubjectMode)subject; dirty = true; }
            if (ImGui.Button("Lock current target") && subjects.LockTarget())
            { config.Subject = SubjectMode.Locked; dirty = true; }
            ImGui.SameLine(); ImGui.TextUnformatted(subjects.LockedName);
            if (ImGui.BeginCombo("Bone", config.BoneName))
            {
                ImGui.InputText("Filter", ref boneFilter, 128);
                foreach (var bone in subjects.Bones)
                    if (bone.Contains(boneFilter, StringComparison.OrdinalIgnoreCase) && ImGui.Selectable(bone, bone == config.BoneName))
                    { config.BoneName = bone; dirty = true; }
                ImGui.EndCombo();
            }
            if (ImGui.Button("Head")) { config.BoneName = "j_kao"; dirty = true; }
            ImGui.SameLine();
            if (ImGui.Button("Neck")) { config.BoneName = "j_kubi"; dirty = true; }
            ImGui.SameLine();
            if (ImGui.Button("Chest")) { config.BoneName = "j_sebo_c"; dirty = true; }
            ImGui.Spacing();
            ImGui.Separator();
            ImGui.TextUnformatted("Tracking");
            dirty |= ImGui.Checkbox("Lock bone in frame", ref config.LockBone);
            Help("Calibrate against the selected orientation, then follow bone motion immediately. Smoothing and orientation controls apply only when unlocked.");
            ImGui.BeginDisabled(config.LockBone);
            var orientation = (int)config.Orientation;
            if (ImGui.Combo("Orientation", ref orientation, "Character facing\0Bone rotation\0World fixed\0"))
            { config.Orientation = (OrientationMode)orientation; dirty = true; }
            dirty |= ImGui.SliderFloat("Smoothing", ref config.Smoothing, 0, 1, "%.2f s");
            ImGui.EndDisabled();
            ImGui.Spacing();
            ImGui.Separator();
            ImGui.TextUnformatted("Framing");
            dirty |= ImGui.SliderFloat("Yaw", ref config.Yaw, -180, 180, "%.1f deg");
            dirty |= ImGui.SliderFloat("Pitch", ref config.Pitch, -85, 85, "%.1f deg");
            dirty |= ImGui.SliderFloat("Roll", ref config.Roll, -180, 180, "%.1f deg");
            dirty |= ImGui.SliderFloat("Distance", ref config.Distance, 0.1f, 10, "%.2f");
            dirty |= ImGui.SliderFloat("Vertical FOV", ref config.FieldOfView, 5, 100, "%.1f deg");
            dirty |= ImGui.DragFloat3("Look-at offset", ref config.Offset, 0.01f, -10, 10);
            Help("Offsets use bone space while locked. Distance, angles, and FOV remain adjustable.");
            if (ImGui.CollapsingHeader("Advanced camera settings"))
            {
                dirty |= ImGui.InputText("Bone name", ref config.BoneName, 128);
                dirty |= ImGui.SliderFloat("Near clip", ref config.NearClip, 0.005f, 0.5f, "%.3f");
            }
        }
        finally { ImGui.PopItemWidth(); ImGui.EndDisabled(); }
    }

    private void DrawOutputSettings()
    {
        ImGui.PushItemWidth(-160);
        try
        {
            dirty |= ImGui.SliderInt("Refresh limit", ref config.RefreshRate, 1, 60, "%d FPS");
            dirty |= ImGui.SliderInt("Output long edge", ref config.Resolution, 128, 4096, "%d px");
            ImGui.TextWrapped("Output is a centered crop. The additional scene render still uses the game's resolution.");
        }
        finally { ImGui.PopItemWidth(); }
    }

    private void DrawWindowSettings()
    {
        dirty |= ImGui.Checkbox("Lock position and size", ref config.LockWindow);
        dirty |= ImGui.Checkbox("Hide title bar", ref config.Borderless);
        dirty |= ImGui.Checkbox("Click through", ref config.ClickThrough);
        ImGui.TextUnformatted($"Size: {config.WindowSize.X:F0} × {config.WindowSize.Y:F0}");
        ImGui.TextWrapped("Drag the portrait window to move it; drag an edge or corner to resize it.");
        if (ImGui.Button("Reset window")) ResetWindow();
        ImGui.TextWrapped("Use /dportrait to reopen settings; /dportrait off stops rendering.");
    }

    private void DrawDiagnostics()
    {
        ImGui.BeginDisabled(renderer.Enabled);
        var mainViewOnly = renderer.MainViewOnly;
        if (ImGui.Checkbox("Capture main view only", ref mainViewOnly)) renderer.SetMainViewOnly(mainViewOnly);
        ImGui.EndDisabled();
        ImGui.TextWrapped("Stop rendering before changing modes. Main-view capture includes game UI and uses no second camera or extra tick.");
        ImGui.Separator();
        var texture = renderer.Texture;
        if (ImGui.BeginTable("capture-statistics", 2, ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchProp))
        {
            try
            {
                Stat("Normal ticks", renderer.NormalTicks.ToString());
                Stat("Portrait ticks", renderer.PortraitTicks.ToString());
                Stat("Captures", renderer.CapturedFrames.ToString());
                Stat("Suppressed presents", renderer.SkippedPresents.ToString());
                Stat("UI draws", renderer.UiDraws.ToString());
                Stat("UI draws during portrait", renderer.UiDrawsDuringPortrait.ToString());
                Stat("Main-chain presents", renderer.MainPresents.ToString());
                Stat("Portrait tick (CPU)", $"{renderer.LastPortraitMilliseconds:F2} ms");
                Stat("Texture", $"{texture.Width} x {texture.Height}");
            }
            finally { ImGui.EndTable(); }
        }
        ImGui.TextWrapped($"Scene pixels: {renderer.PixelProbe}");
        ImGui.Separator();
        ImGui.TextWrapped("Experimental dual-view renderer. Each portrait update runs an additional game tick. Runtime compatibility and performance require in-game testing.");
        ImGui.TextWrapped("Reference: WesleyLuk90/ffxiv-vr. License: AGPL-3.0-or-later.");
    }

    private static void Help(string text)
    {
        if (ImGui.IsItemHovered()) ImGui.SetTooltip(text);
    }

    private static void Stat(string name, string value)
    {
        ImGui.TableNextRow();
        ImGui.TableSetColumnIndex(0);
        ImGui.TextUnformatted(name);
        ImGui.TableSetColumnIndex(1);
        ImGui.TextUnformatted(value);
    }
}
