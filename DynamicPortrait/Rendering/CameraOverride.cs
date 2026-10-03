// SPDX-License-Identifier: AGPL-3.0-or-later
using DynamicPortrait.Camera;
using SceneCamera = FFXIVClientStructs.FFXIV.Client.Graphics.Scene.Camera;
using RenderCamera = FFXIVClientStructs.FFXIV.Client.Graphics.Render.Camera;
using Matrix = FFXIVClientStructs.FFXIV.Common.Math.Matrix4x4;
using Vector = FFXIVClientStructs.FFXIV.Common.Math.Vector3;

namespace DynamicPortrait.Rendering;

internal sealed unsafe class CameraOverride
{
    private SceneCamera* scene;
    private RenderCamera* render;
    private Matrix renderView, projection, projection2;
    private Vector origin;
    private float fov, aspect, near;
    private Matrix historyView, historyProjection, previousView, previousProjection;
    private ulong currentOffsets, previousOffsets, sequenceOffsets;
    private uint sequenceCounter;
    private nint sequence;

    public bool IsActive => render != null;
    public bool IsSceneView(nint address) => scene != null && address == (nint)(&scene->ViewMatrix);

    public bool Matches(nint renderCamera) => render != null && (nint)render == renderCamera;

    public void Apply(SceneCamera* camera, CameraPose pose, int sourceWidth, int sourceHeight, int cropWidth, int cropHeight)
    {
        // Scene.Camera belongs to gameplay camera tracking. Only override its
        // render camera, so the next native scene update starts from game state.
        if (scene != null) throw new InvalidOperationException("Unrestored camera override");
        scene = camera;
        render = camera->RenderCamera;
        renderView = render->ViewMatrix;
        projection = render->ProjectionMatrix;
        projection2 = render->ProjectionMatrix2;
        origin = render->Origin;
        fov = render->FoV;
        aspect = render->AspectRatio;
        near = render->NearPlane;
        // Native camera constant updates retain previous views and projection
        // samples. Preserve only verified value fields, never resource pointers.
        var data = (byte*)render;
        historyView = *(Matrix*)(data + 0xA0);
        historyProjection = *(Matrix*)(data + 0xE0);
        previousView = *(Matrix*)(data + 0x120);
        previousProjection = *(Matrix*)(data + 0x160);
        currentOffsets = *(ulong*)(data + 0x280);
        previousOffsets = *(ulong*)(data + 0x288);
        sequence = *(nint*)(data + 0x278);
        if (sequence != 0)
        {
            sequenceOffsets = *(ulong*)(sequence + 8);
            sequenceCounter = *(uint*)(sequence + 0x10);
        }
        var vm = PortraitCamera.View(pose);
        var pm = PortraitCamera.Projection(pose, sourceWidth, sourceHeight, cropWidth, cropHeight);
        render->ViewMatrix = *(Matrix*)&vm;
        render->ProjectionMatrix = render->ProjectionMatrix2 = *(Matrix*)&pm;
        render->Origin = pose.Eye;
        render->FoV = 2 * MathF.Atan(MathF.Tan(pose.Fov / 2) * sourceHeight / cropHeight);
        render->AspectRatio = (float)sourceWidth / sourceHeight;
        render->NearPlane = pose.Near;
    }

    public void Restore()
    {
        if (scene == null) return;
        // SetMatrices receives a pointer to the freshly computed scene view.
        // Never restore Scene.Camera here: that would overwrite its input and
        // roll the next main camera back to the preceding portrait snapshot.
        render->ViewMatrix = renderView;
        render->ProjectionMatrix = projection;
        render->ProjectionMatrix2 = projection2;
        render->Origin = origin;
        render->FoV = fov;
        render->AspectRatio = aspect;
        render->NearPlane = near;
        var data = (byte*)render;
        *(Matrix*)(data + 0xA0) = historyView;
        *(Matrix*)(data + 0xE0) = historyProjection;
        *(Matrix*)(data + 0x120) = previousView;
        *(Matrix*)(data + 0x160) = previousProjection;
        *(ulong*)(data + 0x280) = currentOffsets;
        *(ulong*)(data + 0x288) = previousOffsets;
        // A native replacement owns new state; do not dereference the old one.
        if (sequence != 0 && *(nint*)(data + 0x278) == sequence)
        {
            *(ulong*)(sequence + 8) = sequenceOffsets;
            *(uint*)(sequence + 0x10) = sequenceCounter;
        }
        sequence = 0;
        scene = null;
        render = null;
    }
}
