// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Numerics;

namespace DynamicPortrait.Camera;

public readonly record struct BonePose(Vector3 Position, Quaternion Rotation, float CharacterYaw);
public readonly record struct CameraPose(Vector3 Eye, Vector3 Target, Vector3 Up, float Fov, float Near);

public sealed class PortraitCamera
{
    private CameraPose? previous;
    public void Reset() => previous = null;

    public CameraPose Solve(BonePose bone, Configuration config, float elapsed)
    {
        // Strict locking keeps the entire camera rig in bone space. World-space
        // smoothing would introduce tracking lag during running and animation.
        var reference = config.LockBone ? bone.Rotation : config.Orientation switch
        {
            OrientationMode.Bone => bone.Rotation,
            OrientationMode.World => Quaternion.Identity,
            _ => Quaternion.CreateFromAxisAngle(Vector3.UnitY, bone.CharacterYaw),
        };
        var orbit = Quaternion.CreateFromYawPitchRoll(Radians(config.Yaw), -Radians(config.Pitch), 0);
        // FFXIV characters face +Z at rotation zero. Positive pitch raises the camera.
        var localDirection = Vector3.Transform(Vector3.UnitZ, orbit);
        var direction = Vector3.Transform(localDirection, reference);
        var target = bone.Position + Vector3.Transform(config.Offset, reference);
        var eye = target + direction * config.Distance;
        var up = Vector3.Transform(Vector3.UnitY, reference);
        if (Math.Abs(Vector3.Dot(Vector3.Normalize(direction), up)) > 0.98f)
            up = Vector3.Transform(Vector3.Transform(Vector3.UnitY, orbit), reference);
        up = Vector3.Transform(up, Quaternion.CreateFromAxisAngle(Vector3.Normalize(target - eye), Radians(config.Roll)));
        if (!config.LockBone && previous is { } p && config.Smoothing > 0 && Vector3.Distance(p.Target, target) < 5)
        {
            var amount = 1 - MathF.Exp(-Math.Clamp(elapsed, 0, 1) / config.Smoothing);
            eye = Vector3.Lerp(p.Eye, eye, amount);
            target = Vector3.Lerp(p.Target, target, amount);
            var blendedUp = Vector3.Lerp(p.Up, up, amount);
            if (blendedUp.LengthSquared() > 0.01f) up = Vector3.Normalize(blendedUp);
        }
        // Opposite orbit positions can interpolate exactly through the look-at point.
        // Never submit a singular look-at matrix to the native renderer.
        if (Vector3.DistanceSquared(eye, target) < 0.0001f)
            eye = target + direction * config.Distance;
        var forward = Vector3.Normalize(target - eye);
        if (Vector3.Cross(forward, up).LengthSquared() < 0.0001f)
            up = Math.Abs(forward.Y) < 0.95f ? Vector3.UnitY : Vector3.UnitX;
        return (previous = new CameraPose(eye, target, up, Radians(config.FieldOfView), config.NearClip)).Value;
    }

    public static Matrix4x4 View(CameraPose pose) => Matrix4x4.CreateLookAt(pose.Eye, pose.Target, pose.Up);

    // Render at the game's resolution, keeping the requested FOV inside a centered crop.
    // Infinite-far, right-handed reversed Z, matching the FFXIV VR projection convention.
    public static Matrix4x4 Projection(CameraPose pose, int sourceWidth, int sourceHeight, int cropWidth, int cropHeight)
    {
        var tan = MathF.Tan(pose.Fov / 2);
        var yScale = cropHeight / (sourceHeight * tan);
        var xScale = cropHeight / (sourceWidth * tan);
        return new Matrix4x4(xScale, 0, 0, 0, 0, yScale, 0, 0, 0, 0, 0, -1, 0, 0, pose.Near, 0);
    }

    public static (int Width, int Height) Crop(int sourceWidth, int sourceHeight, int resolution, float aspect)
    {
        aspect = float.IsFinite(aspect) ? Math.Clamp(aspect, 0.25f, 4) : 1;
        var w = aspect >= 1 ? resolution : resolution * aspect;
        var h = aspect >= 1 ? resolution / aspect : resolution;
        var scale = Math.Min(1, Math.Min(sourceWidth / w, sourceHeight / h));
        return (Math.Max(1, (int)(w * scale)), Math.Max(1, (int)(h * scale)));
    }

    private static float Radians(float degrees) => degrees * MathF.PI / 180;
}
