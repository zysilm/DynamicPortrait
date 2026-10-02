// SPDX-License-Identifier: AGPL-3.0-or-later
namespace DynamicPortrait.Camera;

// Addresses identify a model generation; they are never dereferenced by this value.
public readonly record struct SubjectIdentity(ulong Id, nint Address, nint DrawObject, nint Skeleton, nint Pose, nint Havok);
