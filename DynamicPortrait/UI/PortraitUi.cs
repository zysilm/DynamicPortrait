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
    private bool resetSettingsWindow;
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
        resetWindow = true;
        resetSettingsWindow = true;
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
        if (resetSettingsWindow)
        {
            ImGui.SetNextWindowPos(new(430, 100), ImGuiCond.Always);
            ImGui.SetNextWindowSize(new(510, 720), ImGuiCond.Always);
            ImGui.SetNextWindowCollapsed(false, ImGuiCond.Always);
            resetSettingsWindow = false;
        }
        ImGui.SetNextWindowSize(new(510, 720), ImGuiCond.FirstUseEver);
        if (!ImGui.Begin("Dynamic Portrait settings###DynamicPortrait.Settings", ref settingsOpen)) { ImGui.End(); return; }
        try
        {
            ImGui.TextWrapped("Experimental dual-view renderer. Each portrait update runs an additional game tick. Runtime compatibility and performance require in-game testing.");
            var enabled = renderer.Enabled;
            ImGui.BeginDisabled(!renderer.Ready);
            if (ImGui.Checkbox("Render portrait", ref enabled)) renderer.SetEnabled(enabled);
            ImGui.EndDisabled();
            ImGui.BeginDisabled(renderer.Enabled);
            var mainViewOnly = renderer.MainViewOnly;
            if (ImGui.Checkbox("Diagnostic: capture main view only", ref mainViewOnly)) renderer.SetMainViewOnly(mainViewOnly);
            ImGui.EndDisabled();
            ImGui.TextWrapped(mainViewOnly
                ? "Diagnostic mode: copies the final main display before Present, including game UI. No second camera or extra tick."
                : "Portrait camera: follows the selected bone. Runtime behavior still requires in-game verification.");
            if (ImGui.Button("Reset all settings")) ResetAll();
            ImGui.TextWrapped("Reset restores all defaults, clears the locked subject and stops rendering.");
            dirty |= ImGui.Checkbox("Show portrait window", ref config.ShowPortrait);
            ImGui.TextWrapped(renderer.Status);
            if (renderer.Fault != null) ImGui.TextWrapped($"Error: {renderer.Fault}");
            ImGui.Separator();

            ImGui.BeginDisabled(mainViewOnly);
            var subject = (int)config.Subject;
            if (ImGui.Combo("Subject", ref subject, "Self\0Current target\0Locked character\0"))
            { config.Subject = (SubjectMode)subject; dirty = true; }
            if (ImGui.Button("Lock current target") && subjects.LockTarget())
            { config.Subject = SubjectMode.Locked; dirty = true; }
            ImGui.SameLine(); ImGui.TextUnformatted(subjects.LockedName);
            dirty |= ImGui.InputText("Bone name", ref config.BoneName, 128);
            if (ImGui.BeginCombo("Available bones", config.BoneName))
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
            var orientation = (int)config.Orientation;
            if (ImGui.Combo("Orientation", ref orientation, "Character facing\0Bone rotation\0World fixed\0"))
            { config.Orientation = (OrientationMode)orientation; dirty = true; }
            dirty |= ImGui.SliderFloat("Yaw", ref config.Yaw, -180, 180, "%.1f deg");
            dirty |= ImGui.SliderFloat("Pitch", ref config.Pitch, -85, 85, "%.1f deg");
            dirty |= ImGui.SliderFloat("Roll", ref config.Roll, -180, 180, "%.1f deg");
            dirty |= ImGui.SliderFloat("Distance", ref config.Distance, 0.1f, 10, "%.2f");
            dirty |= ImGui.SliderFloat("Vertical FOV", ref config.FieldOfView, 5, 100, "%.1f deg");
            dirty |= ImGui.DragFloat3("Look-at offset", ref config.Offset, 0.01f, -10, 10);
            dirty |= ImGui.SliderFloat("Near clip", ref config.NearClip, 0.005f, 0.5f, "%.3f");
            dirty |= ImGui.SliderFloat("Smoothing", ref config.Smoothing, 0, 1, "%.2f s");
            ImGui.EndDisabled();
            ImGui.Separator();
            dirty |= ImGui.SliderInt("Refresh limit", ref config.RefreshRate, 1, 60, "%d FPS");
            dirty |= ImGui.SliderInt("Output long edge", ref config.Resolution, 128, 4096, "%d px");
            ImGui.TextWrapped("Output is a centered crop. The additional scene render still uses the game's resolution.");
            dirty |= ImGui.Checkbox("Lock position and size", ref config.LockWindow);
            dirty |= ImGui.Checkbox("Hide title bar", ref config.Borderless);
            dirty |= ImGui.Checkbox("Click through", ref config.ClickThrough);
            if (ImGui.Button("Reset window")) ResetWindow();
            ImGui.TextWrapped("Use /dportrait to reopen settings; /dportrait off stops rendering.");
            if (ImGui.CollapsingHeader("Diagnostics"))
            {
                var texture = renderer.Texture;
                ImGui.TextUnformatted($"Normal ticks: {renderer.NormalTicks} | Portrait ticks: {renderer.PortraitTicks}");
                ImGui.TextUnformatted($"Captures: {renderer.CapturedFrames} | Suppressed presents: {renderer.SkippedPresents}");
                ImGui.TextUnformatted($"UI draws: {renderer.UiDraws} | During portrait submission: {renderer.UiDrawsDuringPortrait}");
                ImGui.TextUnformatted($"Main-chain presents: {renderer.MainPresents}");
                ImGui.TextUnformatted($"Last portrait tick: {renderer.LastPortraitMilliseconds:F2} ms (CPU wall time)");
                ImGui.TextUnformatted($"Texture: {texture.Width} x {texture.Height}");
                ImGui.TextWrapped($"Scene pixels: {renderer.PixelProbe}");
                ImGui.TextWrapped("Reference: WesleyLuk90/ffxiv-vr. License: AGPL-3.0-or-later.");
            }
        }
        finally { ImGui.End(); }
        if (dirty) config.Normalize();
    }
}
