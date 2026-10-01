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

var config = new Configuration { Offset = Vector3.Zero, Pitch = 0, Smoothing = 0, Distance = 2 };
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

config = new Configuration { Offset = Vector3.Zero, Pitch = 0, Smoothing = 0.1f };
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

config.Distance = float.NaN;
config.FieldOfView = float.PositiveInfinity;
config.RefreshRate = 0;
config.BoneName = "";
config.WindowSize = new(float.NaN, -50);
config.Normalize();
Check("Invalid persisted configuration is repaired", float.IsFinite(config.Distance) && float.IsFinite(config.FieldOfView)
    && config.RefreshRate == 1 && config.BoneName == "j_kao" && config.WindowSize.X >= 120 && config.WindowSize.Y >= 120);
Console.WriteLine($"{passed} camera/configuration checks passed.");
