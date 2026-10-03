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
        Program.Check("Portrait changes render camera while preserving gameplay scene camera", Bytes(&scene, sizeof(SceneCamera)).AsSpan().SequenceEqual(sceneBefore)
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
        // Scene.Camera.UpdateRender writes a new view, then passes that same
        // field by address into Render.Camera.SetMatrices. Restoring an older
        // scene snapshot here must never destroy the pending main-view input.
        state.Apply(&scene, pose, 640, 480, 256, 256);
        var updatedView = Matrix4x4.CreateLookAt(new(34, 42, 51), new(31, 40, 49), Vector3.UnitY);
        scene.ViewMatrix = *(FFXIVClientStructs.FFXIV.Common.Math.Matrix4x4*)&updatedView;
        scene.Position = new(34, 42, 51);
        scene.LookAtVector = new(31, 40, 49);
        var nextMainScene = Bytes(&scene, sizeof(SceneCamera));
        var setMatricesArgument = &scene.ViewMatrix;
        state.Restore();
        Program.Check("Restoring portrait preserves the freshly computed aliased main-view input",
            *(Matrix4x4*)setMatricesArgument == updatedView
            && Bytes(&scene, sizeof(SceneCamera)).AsSpan().SequenceEqual(nextMainScene));

        // Reproduce native camera constant updates: they copy the portrait view
        // into the previous-view slot and advance the projection sample state.
        var sequence = stackalloc byte[24];
        new Span<byte>(sequence, 24).Clear();
        var cameraBytes = (byte*)(&render);
        *(nint*)(cameraBytes + 0x278) = (nint)sequence;
        *(uint*)(sequence + 0x10) = 17;
        *(float*)(sequence + 8) = 0.001f;
        *(float*)(sequence + 12) = -0.002f;
        *(Matrix4x4*)(cameraBytes + 0x120) = updatedView;
        var cameraHistoryBefore = Bytes(cameraBytes + 0xA0, 256);
        var sequenceBefore = Bytes(sequence, 24);
        state.Apply(&scene, pose, 640, 480, 256, 256);
        *(Matrix4x4*)(cameraBytes + 0x120) = *(Matrix4x4*)(cameraBytes + 0x10);
        *(Matrix4x4*)(cameraBytes + 0x160) = *(Matrix4x4*)(cameraBytes + 0x50);
        *(uint*)(sequence + 0x10) += 1;
        *(float*)(sequence + 8) = 0.003f;
        *(float*)(sequence + 12) = 0.004f;
        *(ulong*)(cameraBytes + 0x280) = 123;
        *(ulong*)(cameraBytes + 0x288) = 456;
        // A native resource replacement must survive restoration.
        *(nint*)(cameraBytes + 0x270) = 789;
        state.Restore();
        Program.Check("Native portrait updates cannot replace main camera history",
            Bytes(cameraBytes + 0xA0, 256).AsSpan().SequenceEqual(cameraHistoryBefore));
        Program.Check("Native portrait updates cannot advance the main projection sequence",
            Bytes(sequence, 24).AsSpan().SequenceEqual(sequenceBefore));
        Program.Check("Restoration isolates projection offsets without restoring native resource pointers",
            *(ulong*)(cameraBytes + 0x280) == 0 && *(ulong*)(cameraBytes + 0x288) == 0
            && *(nint*)(cameraBytes + 0x270) == 789 && *(nint*)(cameraBytes + 0x278) == (nint)sequence);
        var replacement = stackalloc byte[24];
        new Span<byte>(replacement, 24).Fill(7);
        var replacementBefore = Bytes(replacement, 24);
        state.Apply(&scene, pose, 640, 480, 256, 256);
        *(nint*)(cameraBytes + 0x278) = (nint)replacement;
        *(uint*)(sequence + 0x10) = 99;
        state.Restore();
        Program.Check("A replaced native sequence is preserved without touching the old allocation",
            *(nint*)(cameraBytes + 0x278) == (nint)replacement
            && Bytes(replacement, 24).AsSpan().SequenceEqual(replacementBefore)
            && *(uint*)(sequence + 0x10) == 99);
    }

    private static byte[] Bytes(void* value, int size) => new ReadOnlySpan<byte>(value, size).ToArray();
}
