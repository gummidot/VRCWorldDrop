// Optional, avatar-scoped settings for WorldDropSynced. Add one per avatar to change the avatar-wide
// choices - broadcast reveal speed and off-screen reveal (anti-cull) - so the drop prefabs stay
// independent drag-drop objects with nothing baked in. Implements IEditorOnly so the VRChat SDK strips
// it from the upload (after the build hook reads it at -1025). RUNTIME script (not in Editor/) so the
// bounds-child reference survives into builds before the strip.
using UnityEngine;
using VRC.SDKBase;

namespace VRCWorldDrop.Synced {
    public enum WdsRevealSpeed { Slow, Fast }

    [AddComponentMenu("VRCWorldDrop/World Drop Synced Settings")]
    [DisallowMultipleComponent]
    public class WdsSyncedSettings : MonoBehaviour, IEditorOnly {
        // Reveal speed -> broadcast lane width: Slow=4 lanes (default, fewest synced bits), Fast=8 (+32 bits,
        // ~2x quicker reveal). The inspector (WdsSyncedSettingsEditor) explains the live cost inline.
        public WdsRevealSpeed revealSpeed = WdsRevealSpeed.Slow;

        // Off-screen reveal (anti-cull): when on, the build hook adds oversized invisible bounds so VRChat
        // keeps animating the avatar off a remote's screen (forces Very Poor). Inspector explains the tradeoff.
        public bool offScreenReveal = false;

        // Anti-cull distance in metres; the invisible box spans about twice this across.
        public float offScreenDistance = 100f;
    }
}
