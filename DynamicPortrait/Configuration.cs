// SPDX-License-Identifier: AGPL-3.0-or-later
using Dalamud.Configuration;
using System.Numerics;

namespace DynamicPortrait;

public enum SubjectMode { Self, CurrentTarget, Locked }
public enum OrientationMode { Character, Bone, World }

[Serializable]
public sealed class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 1;
    public bool ShowPortrait = true;
    public bool LockWindow;
    public bool Borderless;
    public bool ClickThrough;
    public Vector2 WindowPosition = new(60, 100);
    public Vector2 WindowSize = new(340, 380);
    public SubjectMode Subject;
    public OrientationMode Orientation;
    public string BoneName = "j_kao";
    public float Yaw;
    public float Pitch = 5;
    public float Roll;
    public float Distance = 1.2f;
    public float FieldOfView = 30;
    public float NearClip = 0.03f;
    public Vector3 Offset = new(0, 0.05f, 0);
    public float Smoothing = 0.08f;
    public int RefreshRate = 30;
    public int Resolution = 512;

    // Rendering deliberately requires a fresh /dportrait on each plugin load.
    // Persisting the UI and camera never arms native hooks on login.
    public void Normalize()
    {
        if (!Enum.IsDefined(Subject)) Subject = SubjectMode.Self;
        if (!Enum.IsDefined(Orientation)) Orientation = OrientationMode.Character;
        BoneName = string.IsNullOrWhiteSpace(BoneName) ? "j_kao" : BoneName[..Math.Min(128, BoneName.Length)];
        Yaw = Finite(Yaw, 0, -180, 180);
        Pitch = Finite(Pitch, 5, -85, 85);
        Roll = Finite(Roll, 0, -180, 180);
        Distance = Finite(Distance, 1.2f, 0.1f, 20);
        FieldOfView = Finite(FieldOfView, 30, 5, 100);
        NearClip = Finite(NearClip, 0.03f, 0.005f, 0.5f);
        Smoothing = Finite(Smoothing, 0.08f, 0, 2);
        Offset = new(Finite(Offset.X, 0, -10, 10), Finite(Offset.Y, 0.05f, -10, 10), Finite(Offset.Z, 0, -10, 10));
        RefreshRate = Math.Clamp(RefreshRate, 1, 60);
        Resolution = Math.Clamp(Resolution, 128, 1024);
        WindowSize = new(Finite(WindowSize.X, 340, 120, 4096), Finite(WindowSize.Y, 380, 120, 4096));
        WindowPosition = new(Finite(WindowPosition.X, 60, -8192, 16384), Finite(WindowPosition.Y, 100, -8192, 16384));
    }

    private static float Finite(float v, float fallback, float min, float max) => float.IsFinite(v) ? Math.Clamp(v, min, max) : fallback;
}
