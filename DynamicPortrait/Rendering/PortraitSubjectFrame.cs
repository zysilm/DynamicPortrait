// SPDX-License-Identifier: AGPL-3.0-or-later
using DynamicPortrait.Camera;

namespace DynamicPortrait.Rendering;

// A redraw can replace the model between tick preparation and matrix submission.
// Once invalidated, this frame must drain or cancel; only a new frame can retry.
internal sealed class PortraitSubjectFrame
{
    private SubjectIdentity expected;
    public bool Unavailable { get; private set; }

    public void Begin(SubjectIdentity identity) { expected = identity; Unavailable = false; }

    public bool Accept(bool ready, SubjectIdentity identity)
    {
        if (!ready || identity != expected) Unavailable = true;
        return !Unavailable;
    }
}
