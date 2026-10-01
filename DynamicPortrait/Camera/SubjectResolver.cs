// SPDX-License-Identifier: AGPL-3.0-or-later
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Graphics.Scene;
using System.Numerics;

namespace DynamicPortrait.Camera;

public sealed unsafe class SubjectResolver(IObjectTable objects, ITargetManager targets)
{
    private ulong? lockedId;
    private nint lockedAddress;
    public string LockedName { get; private set; } = "None";
    public string Status { get; private set; } = "No subject";
    public string[] Bones { get; private set; } = [];
    private nint lastSkeleton;
    private nint lastHavok;
    private ulong lastId;
    public nint CurrentAddress { get; private set; }
    public ulong CurrentId { get; private set; }

    public bool LockTarget()
    {
        if (targets.Target is not ICharacter target) return false;
        lockedId = target.GameObjectId;
        lockedAddress = target.Address;
        LockedName = target.Name.TextValue;
        return true;
    }

    public void Clear()
    {
        lockedId = null;
        lockedAddress = 0;
        LockedName = "None";
        CurrentAddress = 0;
        CurrentId = 0;
        lastSkeleton = lastHavok = 0;
        Bones = [];
    }

    public bool TryRead(Configuration config, out BonePose result)
    {
        result = default;
        ICharacter? actor = config.Subject switch
        {
            SubjectMode.Self => objects.LocalPlayer,
            SubjectMode.CurrentTarget => targets.Target as ICharacter,
            _ => lockedId.HasValue ? objects.FirstOrDefault(o => o.GameObjectId == lockedId && o.Address == lockedAddress) as ICharacter : null,
        };
        CurrentAddress = 0;
        if (actor == null)
        {
            if (config.Subject == SubjectMode.Locked) { lockedId = null; lockedAddress = 0; }
            Bones = [];
            lastSkeleton = lastHavok = 0;
            Status = "Subject unavailable";
            return false;
        }
        var obj = (FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject*)actor.Address;
        var draw = obj->DrawObject;
        if (draw == null || draw->GetObjectType() != ObjectType.CharacterBase)
        {
            Status = "Character model is not ready";
            return false;
        }
        var skeleton = ((CharacterBase*)draw)->Skeleton;
        if (skeleton == null || skeleton->PartialSkeletonCount == 0 || skeleton->PartialSkeletons == null)
        {
            Status = "Skeleton is not ready";
            return false;
        }
        // Body/root partial only: attached weapon/accessory partials need their own parent transforms.
        var pose = skeleton->PartialSkeletons[0].GetHavokPose(0);
        if (pose == null || pose->Skeleton == null) { Status = "Pose is not ready"; return false; }
        var havok = pose->Skeleton;
        if (havok->Bones.Length is <= 0 or > 4096 || havok->Bones.Data == null) return false;
        if (lastSkeleton != (nint)skeleton || lastHavok != (nint)havok || lastId != actor.GameObjectId || Bones.Length != havok->Bones.Length)
        {
            Bones = Enumerable.Range(0, havok->Bones.Length).Select(i => havok->Bones[i].Name.String ?? "").ToArray();
            lastSkeleton = (nint)skeleton;
            lastHavok = (nint)havok;
            lastId = actor.GameObjectId;
        }
        var index = Array.IndexOf(Bones, config.BoneName);
        if (index < 0) { Status = $"Bone '{config.BoneName}' not found"; return false; }
        var model = pose->GetSyncedPoseModelSpace();
        if (model == null || model->Data == null || index >= model->Length) return false;
        var t = model->Data[index];
        var root = skeleton->Transform;
        var rootRotation = new Quaternion(root.Rotation.X, root.Rotation.Y, root.Rotation.Z, root.Rotation.W);
        var local = new Vector3(t.Translation.X * root.Scale.X, t.Translation.Y * root.Scale.Y, t.Translation.Z * root.Scale.Z);
        var position = new Vector3(root.Position.X, root.Position.Y, root.Position.Z) + Vector3.Transform(local, rootRotation);
        var rotation = rootRotation * new Quaternion(t.Rotation.X, t.Rotation.Y, t.Rotation.Z, t.Rotation.W);
        if (!float.IsFinite(position.X + position.Y + position.Z) || !float.IsFinite(actor.Rotation)
            || !float.IsFinite(rotation.LengthSquared()) || rotation.LengthSquared() < 0.01f)
            return false;
        result = new BonePose(position, Quaternion.Normalize(rotation), actor.Rotation);
        CurrentAddress = actor.Address;
        CurrentId = actor.GameObjectId;
        Status = $"{actor.Name.TextValue} / {config.BoneName}";
        return true;
    }
}
