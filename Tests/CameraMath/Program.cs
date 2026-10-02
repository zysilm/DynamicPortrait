// SPDX-License-Identifier: AGPL-3.0-or-later
using DynamicPortrait;
using DynamicPortrait.Camera;
using System.Numerics;

var passed = 0;
void Check(string name, bool condition)
{
    if (!condition) throw new Exception(name);
    Console.WriteLine($"PASS {name}");
    passed++;
}
bool Near(float a, float b, float epsilon = 0.0001f) => MathF.Abs(a - b) < epsilon;

var config = new Configuration { LockBone = false, Offset = Vector3.Zero, Pitch = 0, Smoothing = 0, Distance = 2 };
var bone = new BonePose(new(10, 2, 5), Quaternion.Identity, 0);
var solver = new PortraitCamera();
var pose = solver.Solve(bone, config, 0.1f);
Check("Front camera faces character from +Z", Vector3.Distance(pose.Eye, new(10, 2, 7)) < 0.001f);
var viewTarget = Vector3.Transform(pose.Target, PortraitCamera.View(pose));
Check("Look-at target projects onto view center in front of camera", Near(viewTarget.X, 0) && Near(viewTarget.Y, 0) && viewTarget.Z < 0);

pose = solver.Solve(bone with { CharacterYaw = MathF.PI / 2 }, config, 0.1f);
Check("Character facing rotates orbit", Vector3.Distance(pose.Eye, new(12, 2, 5)) < 0.001f);
config.Orientation = OrientationMode.World;
pose = solver.Solve(bone with { CharacterYaw = MathF.PI }, config, 0.1f);
Check("World orientation is independent of actor yaw", Vector3.Distance(pose.Eye, new(10, 2, 7)) < 0.001f);
config.Pitch = 30;
pose = solver.Solve(bone, config, 0.1f);
Check("Positive pitch lifts camera", pose.Eye.Y > pose.Target.Y);
config.Orientation = OrientationMode.Bone;
config.Pitch = 0;
pose = solver.Solve(bone with { Rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI / 2) }, config, 0.1f);
Check("Bone orientation controls orbit", Vector3.Distance(pose.Eye, new(12, 2, 5)) < 0.001f);

foreach (var dimensions in new[] { (1920, 1080, 512, 1f), (3840, 2160, 1024, 2f), (800, 600, 1024, 0.5f) })
{
    var (w, h, res, aspect) = dimensions;
    var (cw, ch) = PortraitCamera.Crop(w, h, res, aspect);
    Check($"Crop fits source {dimensions}", cw <= w && ch <= h && Math.Max(cw, ch) <= res && cw > 0 && ch > 0);
    var projection = PortraitCamera.Projection(pose, w, h, cw, ch);
    var near = Vector4.Transform(new Vector4(0, 0, -pose.Near, 1), projection);
    var far = Vector4.Transform(new Vector4(0, 0, -10000, 1), projection);
    Check($"Reversed Z maps near to 1 and far to 0 {dimensions}", Near(near.Z / near.W, 1) && far.Z / far.W < 0.0001f);
    var edge = Vector4.Transform(new Vector4(0, MathF.Tan(pose.Fov / 2) * 2, -2, 1), projection);
    Check($"Portrait vertical FOV survives centered crop {dimensions}", Near(edge.Y / edge.W * h / ch, 1));
    var right = Vector4.Transform(new Vector4(MathF.Tan(pose.Fov / 2) * 2 * cw / ch, 0, -2, 1), projection);
    Check($"Portrait horizontal proportions survive centered crop {dimensions}", Near(right.X / right.W * w / cw, 1));
}

config = new Configuration { LockBone = false, Offset = Vector3.Zero, Pitch = 0, Smoothing = 0.1f };
solver.Reset();
solver.Solve(bone, config, 0.1f);
var shifted = solver.Solve(bone with { Position = bone.Position + Vector3.UnitX }, config, 0.1f * MathF.Log(2));
Check("Exponential smoothing has correct half-life", Near(shifted.Target.X, bone.Position.X + 0.5f));
var teleported = solver.Solve(bone with { Position = bone.Position + Vector3.UnitX * 100 }, config, 0.01f);
Check("Teleport snaps instead of sweeping across world", Near(teleported.Target.X, bone.Position.X + 100));

solver.Reset();
solver.Solve(bone, config, 0.1f);
config.Yaw = 180;
var reversed = solver.Solve(bone, config, 0.1f * MathF.Log(2));
var reversalView = PortraitCamera.View(reversed);
Check("180-degree orbit reversal cannot create a singular view", float.IsFinite(reversalView.M11 + reversalView.M22 + reversalView.M33)
    && Vector3.DistanceSquared(reversed.Eye, reversed.Target) >= 0.0001f);

// A bone is stabilized only if all its rigid landmarks, not just its origin,
// retain the same screen coordinates under arbitrary animated rigid motion.
var landmarks = new[] { Vector3.Zero, new Vector3(-0.1f, 0.1f, 0.08f), new Vector3(0.1f, 0.1f, 0.08f), new Vector3(0, -0.15f, 0.05f) };
Vector3 ScreenPoint(Vector3 point, BonePose animated, CameraPose solved)
{
    var world = animated.Position + Vector3.Transform(point, animated.Rotation);
    var clip = Vector4.Transform(new Vector4(world, 1), PortraitCamera.View(solved)
        * PortraitCamera.Projection(solved, 1920, 1080, 512, 512));
    return new Vector3(clip.X, clip.Y, clip.Z) / clip.W;
}
var legacyConfig = System.Text.Json.JsonSerializer.Deserialize<Configuration>("{\"BoneName\":\"j_kao\",\"Smoothing\":0.08}",
    new System.Text.Json.JsonSerializerOptions { IncludeFields = true });
Check("Bone locking is opt-in for fresh and legacy configurations", !new Configuration().LockBone && legacyConfig is { LockBone: false, BoneName: "j_kao" });
var previewConfig = new Configuration { Version = 1, LockBone = true, BoneName = "j_kao", Roll = 17, Resolution = 2048 };
previewConfig.Normalize();
Check("Upgrade disables the old implicit lock without resetting other settings", previewConfig.Version == 2 && !previewConfig.LockBone
    && previewConfig.BoneName == "j_kao" && Near(previewConfig.Roll, 17) && previewConfig.Resolution == 2048);
previewConfig.LockBone = true;
previewConfig.Normalize();
Check("Explicit lock selection remains saved after migration", previewConfig.LockBone);
foreach (var pitch in new[] { -85f, -5.6f, 85f })
{
    config = new Configuration { LockBone = true, Offset = new(0.03f, 0.12f, -0.02f), Pitch = pitch, Yaw = 35, Roll = 23, Smoothing = 2 };
    solver.Reset();
    var neutral = new BonePose(Vector3.Zero, Quaternion.Identity, 0);
    var neutralCamera = solver.Solve(neutral, config, 0.01f);
    var expected = landmarks.Select(point => ScreenPoint(point, neutral, neutralCamera)).ToArray();
    var stable = true;
    for (var frame = 0; frame < 120; frame++)
    {
        var t = frame * 0.1f;
        var animated = new BonePose(new(t * 2, MathF.Sin(t * 3) * 0.2f, MathF.Cos(t)),
            Quaternion.CreateFromYawPitchRoll(t, MathF.Sin(t) * 1.5f, MathF.Cos(t * 2)), -t);
        // Alternating quaternion signs encode the same orientation. Frame time
        // and unlocked orientation settings must not affect a strict lock.
        if (frame % 2 == 0) animated = animated with { Rotation = -animated.Rotation };
        config.Orientation = (OrientationMode)(frame % 3);
        var lockedCamera = solver.Solve(animated, config, frame % 2 == 0 ? 0 : 1f / 15);
        stable &= landmarks.Select((point, i) => Vector3.Distance(ScreenPoint(point, animated, lockedCamera), expected[i]) < 0.0002f).All(value => value);
    }
    Check($"Rigid head landmarks stay fixed during animated translation/rotation at pitch {pitch}", stable);
}
config = new Configuration { LockBone = true, Offset = Vector3.Zero, Pitch = 0, Smoothing = 2 };
solver.Reset();
var headPose = new BonePose(Vector3.Zero, Quaternion.Identity, 0);
var heldCamera = solver.Solve(headPose, config, 0);
Check("Other joints remain free to move relative to the locked head", Vector3.Distance(
    ScreenPoint(new(-0.2f, -0.3f, 0), headPose, heldCamera), ScreenPoint(new(0.2f, -0.3f, 0), headPose, heldCamera)) > 0.1f);
config.Yaw = 45;
config.FieldOfView = 55;
var reframed = solver.Solve(headPose, config, 0);
Check("Strict lock applies framing controls immediately", Vector3.Distance(reframed.Eye, heldCamera.Eye) > 0.1f
    && Near(reframed.Fov, 55 * MathF.PI / 180));
config.LockBone = false;
config.Orientation = OrientationMode.World;
config.Smoothing = 0.1f;
solver.Reset();
solver.Solve(headPose, config, 0.1f);
var followed = solver.Solve(headPose with { Position = Vector3.UnitX }, config, 0.1f * MathF.Log(2));
Check("Unlocking restores smooth tracking", Near(followed.Target.X, 0.5f));

foreach (var orientation in new[] { OrientationMode.Character, OrientationMode.World })
{
    config = new Configuration { LockBone = false, Orientation = orientation, Smoothing = 0, Yaw = 35, Pitch = -10, Roll = 17 };
    solver.Reset();
    var bindRotation = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI / 2);
    var offsetBone = new BonePose(new(1, 2, 3), bindRotation, 0.7f);
    var unlocked = solver.Solve(offsetBone, config, 0.1f);
    config.LockBone = true;
    var calibrated = solver.Solve(offsetBone, config, 0.1f);
    Check($"Enabling lock preserves {orientation} framing despite a 90-degree bone axis offset",
        Vector3.Distance(unlocked.Eye, calibrated.Eye) < 0.0001f && Vector3.Distance(unlocked.Target, calibrated.Target) < 0.0001f
        && Vector3.Distance(unlocked.Up, calibrated.Up) < 0.0001f);
    var expected = landmarks.Select(point => ScreenPoint(point, offsetBone, calibrated)).ToArray();
    var movedBone = offsetBone with { Position = new(-4, 3, -2), Rotation = Quaternion.CreateFromYawPitchRoll(0.8f, -0.5f, 0.3f) * bindRotation };
    var moved = solver.Solve(movedBone, config, 0);
    Check($"Calibrated {orientation} lock stabilizes all rigid landmarks after motion",
        landmarks.Select((point, i) => Vector3.Distance(ScreenPoint(point, movedBone, moved), expected[i]) < 0.0002f).All(value => value));
    solver.Reset();
    var replacement = new BonePose(new(5, 1, 2), Quaternion.CreateFromAxisAngle(Vector3.UnitX, -MathF.PI / 2), -0.4f);
    var resumed = solver.Solve(replacement, config, 0);
    config.LockBone = false;
    config.Smoothing = 0;
    var replacementUnlocked = new PortraitCamera().Solve(replacement, config, 0);
    Check($"Model replacement recalibrates {orientation} lock instead of retaining old bone axes",
        Vector3.Distance(resumed.Eye, replacementUnlocked.Eye) < 0.0001f && Vector3.Distance(resumed.Up, replacementUnlocked.Up) < 0.0001f);
}

config.Distance = float.NaN;
config.FieldOfView = float.PositiveInfinity;
config.RefreshRate = 0;
config.BoneName = "";
config.WindowSize = new(float.NaN, -50);
config.Normalize();
Check("Invalid persisted configuration is repaired", float.IsFinite(config.Distance) && float.IsFinite(config.FieldOfView)
    && config.RefreshRate == 1 && config.BoneName == "j_sebo_c" && config.WindowSize.X >= 120 && config.WindowSize.Y >= 120);
// Change every public setting, so adding a field without resetting it fails here.
var settingFields = typeof(Configuration).GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
foreach (var field in settingFields)
{
    object replacement = field.FieldType == typeof(bool) ? !(bool)field.GetValue(config)!
        : field.FieldType == typeof(float) ? 99f
        : field.FieldType == typeof(int) ? 99
        : field.FieldType == typeof(string) ? "changed"
        : field.FieldType == typeof(Vector2) ? new Vector2(999, 999)
        : field.FieldType == typeof(Vector3) ? new Vector3(999, 999, 999)
        : field.FieldType.IsEnum ? Enum.ToObject(field.FieldType, 2)
        : throw new Exception($"Add a non-default fixture for {field.Name}");
    field.SetValue(config, replacement);
}
config.Version = 99;
config.ResetDefaults();
var defaults = new Configuration();
Check("Reset all restores every public setting and schema version", config.Version == defaults.Version
    && settingFields.All(field => Equals(field.GetValue(config), field.GetValue(defaults))));
Console.WriteLine($"{passed} camera/configuration checks passed.");
