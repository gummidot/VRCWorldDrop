// Optional, avatar-scoped settings for WorldDropSynced. Add one per avatar to change the avatar-wide
// choices - broadcast reveal speed and off-screen reveal (anti-cull) - so the drop prefabs stay
// independent drag-drop objects with nothing baked in. Implements IEditorOnly so the avatar build
// strips it from the upload, after the build hook at -20000 reads it. RUNTIME script (not in
// Editor/) so the bounds-child reference survives into builds before the strip.
using UnityEngine;
using VRC.SDKBase;

namespace VRCWorldDrop.Synced {
    public enum WdsRevealSpeed { Slow, Fast }

    [AddComponentMenu("VRCWorldDrop/World Drop Synced Settings")]
    [DisallowMultipleComponent]
    public class WdsSyncedSettings : MonoBehaviour, IEditorOnly {
        // Reveal speed -> broadcast lane width, sized to the object count by WdsMuxGenerator.AutoLanes:
        // Slow is 2-4 lanes (default, fewest synced bits), Fast is 4-8 (~2x quicker reveal, 16-32 more bits
        // depending on object count). The inspector (WdsSyncedSettingsEditor) explains the live cost inline.
        public WdsRevealSpeed revealSpeed = WdsRevealSpeed.Slow;

        // Off-screen reveal (anti-cull): when on, the build hook adds oversized invisible bounds so VRChat
        // keeps animating the avatar off a remote's screen (forces Very Poor). Inspector explains the tradeoff.
        public bool offScreenReveal = false;

        // Anti-cull distance in metres; the invisible box spans about twice this across.
        public float offScreenDistance = 100f;
    }
}
