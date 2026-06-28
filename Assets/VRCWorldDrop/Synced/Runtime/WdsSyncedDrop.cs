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
                 "to nest. N is automatically replaced with a number (1, 2, ...). Ignored when you set a Drop/Show Param.")]
        public string menuPath = "WorldDrops/Object N (Synced)";

        [Tooltip("Start visible instead of hidden. Off = object is hidden until Shown (default). " +
                 "On = object is visible from the start. Any Show toggle reflects this initial state.")]
        public bool defaultShown = false;

        [Tooltip("The avatar bool your own toggle uses to drop this object. " +
                 "Should be local (synced internally).")]
        public string dropParam = "";

        [Tooltip("The avatar bool your own toggle uses to show or hide this object. " +
                 "Should be local (synced internally).")]
        public string showParam = "";

        // The author-named drive params, normalized (blank -> null = no host binding, use the built-in toggle).
        public string DropParamOrNull => string.IsNullOrWhiteSpace(dropParam) ? null : dropParam.Trim();
        public string ShowParamOrNull => string.IsNullOrWhiteSpace(showParam) ? null : showParam.Trim();

        // The prefab keeps the original source tags/params; the build hook rewrites them per slot
        // based on `fullRotation`. These name the source prefixes to rewrite from.
        public string SrcParamPrefix => fullRotation ? "WDSFB/" : "WDSB/";
        public string SrcTag => fullRotation ? "WDSFB_" : "WDSB_";
    }
}
