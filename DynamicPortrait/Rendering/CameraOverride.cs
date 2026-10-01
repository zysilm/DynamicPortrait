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
    private Matrix view, renderView, projection, projection2;
    private Vector position, lookAt, origin;
    private float fov, aspect, near;

    public bool Matches(nint renderCamera) => render != null && (nint)render == renderCamera;

    public void Apply(SceneCamera* camera, CameraPose pose, int sourceWidth, int sourceHeight, int cropWidth, int cropHeight)
    {
        // Restore before the original SetMatrices call, so nested camera updates cannot snapshot our override.
        if (scene != null) throw new InvalidOperationException("Unrestored camera override");
        scene = camera;
        render = camera->RenderCamera;
        view = scene->ViewMatrix;
        position = scene->Position;
        lookAt = scene->LookAtVector;
        renderView = render->ViewMatrix;
        projection = render->ProjectionMatrix;
        projection2 = render->ProjectionMatrix2;
        origin = render->Origin;
        fov = render->FoV;
        aspect = render->AspectRatio;
        near = render->NearPlane;
        var vm = PortraitCamera.View(pose);
        var pm = PortraitCamera.Projection(pose, sourceWidth, sourceHeight, cropWidth, cropHeight);
        scene->ViewMatrix = render->ViewMatrix = *(Matrix*)&vm;
        render->ProjectionMatrix = render->ProjectionMatrix2 = *(Matrix*)&pm;
        scene->Position = render->Origin = pose.Eye;
        scene->LookAtVector = pose.Target;
        render->FoV = 2 * MathF.Atan(MathF.Tan(pose.Fov / 2) * sourceHeight / cropHeight);
        render->AspectRatio = (float)sourceWidth / sourceHeight;
        render->NearPlane = pose.Near;
    }

    public void Restore()
    {
        if (scene == null) return;
        // Valid only within the synchronous Framework tick; never retained across ticks.
        scene->ViewMatrix = view;
        scene->Position = position;
        scene->LookAtVector = lookAt;
        render->ViewMatrix = renderView;
        render->ProjectionMatrix = projection;
        render->ProjectionMatrix2 = projection2;
        render->Origin = origin;
        render->FoV = fov;
        render->AspectRatio = aspect;
        render->NearPlane = near;
        scene = null;
        render = null;
    }
}
