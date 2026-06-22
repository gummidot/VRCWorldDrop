// Marker for one world-drop object on the shared mux. The build-time WdsBuildHook enumerates these,
// assigns a dense slot id, and generates the shared-mux controller. Implements IEditorOnly so the
// VRChat SDK strips it from the upload (at callbackOrder -1024, after the build hook reads it at
// -1025). This is a RUNTIME script (not in an Editor/ folder) so prefab references survive into
// builds before the strip.
using UnityEngine;
using VRC.SDKBase;

namespace VRCWorldDrop.Synced {
    [AddComponentMenu("VRCWorldDrop/World Drop Synced")]
    [DisallowMultipleComponent]
    public class WdsSyncedDrop : MonoBehaviour, IEditorOnly {
        [Tooltip("Full XYZ rotation. Off = Y-axis only.")]
        public bool fullRotation = false;

        [Tooltip("Menu location for this object's Show/Drop toggles, e.g. \"Props/Tent\". Use \"/\" " +
                 "to nest. N is automatically replaced with a number (1, 2, ...).")]
        public string menuPath = "WorldDrops/Object N (Synced)";

        // The prefab keeps the original source tags/params; the build hook rewrites them per slot
        // based on `fullRotation`. These name the source prefixes to rewrite from.
        public string SrcParamPrefix => fullRotation ? "WDSFB/" : "WDSB/";
        public string SrcTag => fullRotation ? "WDSFB_" : "WDSB_";
    }
}
