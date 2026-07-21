// Marker for one world-drop object on the shared mux. The build-time WdsBuildHook enumerates these,
// assigns a dense slot id, and generates the shared-mux controller. Implements IEditorOnly so the
// avatar build strips it from the upload, after the build hook at -20000 and the -1025 diagnostic
// pass have read it. This is a RUNTIME script (not in an Editor/ folder) so prefab references
// survive into builds before the strip.
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

        [Tooltip("Save a dropped object's position across world rejoins and avatar switches. Adds a Save " +
                 "toggle (on by default); turning it off is remembered across sessions. Costs no extra synced " +
                 "parameters. In a different world, turn Drop off before showing the object, or it may appear " +
                 "somewhere totally unexpected.")]
        public bool persist = false;

        [Tooltip("The avatar parameter your own toggle uses to drop this object. Bool, float or int. " +
                 "Keep it local (the drop is synced internally).")]
        public string dropParam = "";

        [Tooltip("The avatar parameter your own toggle uses to show or hide this object. Bool, float or int. " +
                 "Keep it local (the drop is synced internally).")]
        public string showParam = "";

        [Tooltip("The avatar parameter your own toggle uses as this object's Save preference, instead of the " +
                 "built-in Save toggle. Only used with Saved Across Sessions on. Must be a saved parameter.")]
        public string saveParam = "";

        // The author-named drive params, normalized (blank -> null = no host binding, use the built-in toggle).
        public string DropParamOrNull => string.IsNullOrWhiteSpace(dropParam) ? null : dropParam.Trim();
        public string ShowParamOrNull => string.IsNullOrWhiteSpace(showParam) ? null : showParam.Trim();
        public string SaveParamOrNull => string.IsNullOrWhiteSpace(saveParam) ? null : saveParam.Trim();

        // The prefab keeps the original source tags/params; the build hook rewrites them per slot
        // based on `fullRotation`. These name the source prefixes to rewrite from.
        public string SrcParamPrefix => fullRotation ? "WDSFB/" : "WDSB/";
        public string SrcTag => fullRotation ? "WDSFB_" : "WDSB_";
    }
}
