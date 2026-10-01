// SPDX-License-Identifier: AGPL-3.0-or-later
using DynamicPortrait.Camera;
using DynamicPortrait.Rendering;
using System.Numerics;
using SceneCamera = FFXIVClientStructs.FFXIV.Client.Graphics.Scene.Camera;
using RenderCamera = FFXIVClientStructs.FFXIV.Client.Graphics.Render.Camera;

internal static unsafe class CameraStateTest
{
    public static void Run()
    {
        var scene = new SceneCamera();
        var render = new RenderCamera();
        scene.RenderCamera = &render;
        scene.Position = new(2, 3, 4);
        scene.LookAtVector = new(5, 6, 7);
        render.FoV = 1.2f;
        render.NearPlane = 0.2f;
        render.AspectRatio = 1.7f;
        render.Origin = new(11, 12, 13);
        var sceneBefore = Bytes(&scene, sizeof(SceneCamera));
        var renderBefore = Bytes(&render, sizeof(RenderCamera));
        var pose = new CameraPose(new(10, 2, 5), new(10, 2, 3), Vector3.UnitY, 0.5f, 0.03f);
        var state = new CameraOverride();
        state.Apply(&scene, pose, 640, 480, 256, 256);
        Program.Check("Camera override changes scene and render matrices", !Bytes(&scene, sizeof(SceneCamera)).AsSpan().SequenceEqual(sceneBefore)
            && !Bytes(&render, sizeof(RenderCamera)).AsSpan().SequenceEqual(renderBefore));
        Program.Check("Camera snapshot identifies only its matching native render camera", state.Matches((nint)(&render)) && !state.Matches(123));
        state.Restore();
        Program.Check("Restoration preserves every byte of the main scene camera", Bytes(&scene, sizeof(SceneCamera)).AsSpan().SequenceEqual(sceneBefore));
        Program.Check("Restoration preserves every byte of the main render camera", Bytes(&render, sizeof(RenderCamera)).AsSpan().SequenceEqual(renderBefore));
        state.Restore();
        Program.Check("Completed camera snapshot cannot restore over a later frame", !state.Matches((nint)(&render)));
        render.FoV = 0.9f;
        scene.Position = new(30, 40, 50);
        sceneBefore = Bytes(&scene, sizeof(SceneCamera));
        renderBefore = Bytes(&render, sizeof(RenderCamera));
        state.Apply(&scene, pose, 640, 480, 256, 256);
        state.Restore();
        Program.Check("Next portrait snapshots the updated main camera", Bytes(&scene, sizeof(SceneCamera)).AsSpan().SequenceEqual(sceneBefore)
            && Bytes(&render, sizeof(RenderCamera)).AsSpan().SequenceEqual(renderBefore));
    }

    private static byte[] Bytes(void* value, int size) => new ReadOnlySpan<byte>(value, size).ToArray();
}
