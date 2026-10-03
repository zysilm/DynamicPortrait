// SPDX-License-Identifier: AGPL-3.0-or-later
using Dalamud.Configuration;
using System.Numerics;

namespace DynamicPortrait;

public enum SubjectMode { Self, CurrentTarget, Locked }
public enum OrientationMode { Character, Bone, World }
public enum RenderBackend { FullTick, RenderOnly }

[Serializable]
public sealed class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 2;
    public bool ShowPortrait = true;
    public bool LockWindow;
    public bool Borderless;
    public bool ClickThrough;
    public Vector2 WindowPosition = new(3141, 142);
    public Vector2 WindowSize = new(340, 380);
    public SubjectMode Subject;
    public OrientationMode Orientation;
    public bool LockBone;
    public string BoneName = "j_sebo_c";
    public float Yaw;
    public float Pitch = -5.6f;
    public float Roll;
    public float Distance = 0.91f;
    public float FieldOfView = 30;
    public float NearClip = 0.03f;
    public Vector3 Offset = new(0, 0.12f, 0);
    public float Smoothing = 0.08f;
    public int RefreshRate = 60;
    public int Resolution = 1024;
    public RenderBackend Backend;

    public void ResetDefaults()
    {
        var defaults = new Configuration();
        Version = defaults.Version;
        ShowPortrait = defaults.ShowPortrait;
        LockWindow = defaults.LockWindow;
        Borderless = defaults.Borderless;
        ClickThrough = defaults.ClickThrough;
        // Reset settings without disturbing the user's portrait layout.
        // Geometry is reset only by the dedicated Reset window action.
        Subject = defaults.Subject;
        Orientation = defaults.Orientation;
        LockBone = defaults.LockBone;
        BoneName = defaults.BoneName;
        Yaw = defaults.Yaw;
        Pitch = defaults.Pitch;
        Roll = defaults.Roll;
        Distance = defaults.Distance;
        FieldOfView = defaults.FieldOfView;
        NearClip = defaults.NearClip;
        Offset = defaults.Offset;
        Smoothing = defaults.Smoothing;
        RefreshRate = defaults.RefreshRate;
        Resolution = defaults.Resolution;
        Backend = defaults.Backend;
    }

    // Rendering deliberately requires a fresh /dportrait on each plugin load.
    // Persisting the UI and camera never arms native hooks on login.
    public void Normalize()
    {
        // The first bone-lock preview enabled it implicitly. Make that preview
        // opt-in once on upgrade; subsequent explicit choices remain persisted.
        if (Version < 2) { LockBone = false; Version = 2; }
        if (!Enum.IsDefined(Subject)) Subject = SubjectMode.Self;
        if (!Enum.IsDefined(Orientation)) Orientation = OrientationMode.Character;
        if (!Enum.IsDefined(Backend)) Backend = RenderBackend.FullTick;
        BoneName = string.IsNullOrWhiteSpace(BoneName) ? "j_sebo_c" : BoneName[..Math.Min(128, BoneName.Length)];
        Yaw = Finite(Yaw, 0, -180, 180);
        Pitch = Finite(Pitch, -5.6f, -85, 85);
        Roll = Finite(Roll, 0, -180, 180);
        Distance = Finite(Distance, 0.91f, 0.1f, 20);
        FieldOfView = Finite(FieldOfView, 30, 5, 100);
        NearClip = Finite(NearClip, 0.03f, 0.005f, 0.5f);
        Smoothing = Finite(Smoothing, 0.08f, 0, 2);
        Offset = new(Finite(Offset.X, 0, -10, 10), Finite(Offset.Y, 0.12f, -10, 10), Finite(Offset.Z, 0, -10, 10));
        RefreshRate = Math.Clamp(RefreshRate, 1, 60);
        Resolution = Math.Clamp(Resolution, 128, 4096);
        WindowSize = new(Finite(WindowSize.X, 340, 120, 4096), Finite(WindowSize.Y, 380, 120, 4096));
        WindowPosition = new(Finite(WindowPosition.X, 3141, -8192, 16384), Finite(WindowPosition.Y, 142, -8192, 16384));
    }

    private static float Finite(float v, float fallback, float min, float max) => float.IsFinite(v) ? Math.Clamp(v, min, max) : fallback;
}
