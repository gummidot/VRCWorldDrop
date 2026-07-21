// Inspector for the WdsSyncedDrop marker. Surfaces the user-editable menu path cleanly, shows
// the rotation family read-only (it is baked into the prefab's contact geometry, so toggling it
// would desync the rig), and mirrors the Settings inspector's live cost (object count, synced
// bits, reveal estimate) so it is visible from a drop object too.
using UnityEditor;
using UnityEngine;
using VRC.SDK3.Avatars.Components;

namespace VRCWorldDrop.Synced {
    [CustomEditor(typeof(WdsSyncedDrop))]
    [CanEditMultipleObjects]
    public class WdsSyncedDropEditor : Editor {
        static bool _showAdvanced;

        public override void OnInspectorGUI() {
            var m = (WdsSyncedDrop)target;

            // Hard requirement: box contacts with Use Face Proximity (SDK 3.10.4+). Flag it here at edit
            // time too, not only at upload, so the user can fix it before building.
            if (!WdsSdkSupport.HasBoxFaceProximity) {
                EditorGUILayout.HelpBox(
                    "Requires VRChat SDK " + WdsSdkSupport.MinSdk + " or newer. Your project's SDK is older, " +
                    "so this synced drop will not work until you update it.",
                    MessageType.Error);
                EditorGUILayout.Space();
            }

            EditorGUILayout.HelpBox(
                "Synced World Drop. Put your object under Container/Item. You can add up to " +
                WdsBuildHook.MaxSlots + " of these per avatar. Duplicate this prefab for each object you " +
                "want to drop: every copy is wired up automatically when you upload and syncs on its own." +
                "\n\nTo move it, move the Reset Target object. Use Default " +
                "Shown to choose whether it starts visible. To hide it in the Scene view, disable Container/Item.",
                MessageType.Info);

            // Warn at edit time if this avatar already exceeds the supported count, so the user sees
            // it before upload. The build also blocks the upload, so it never ships with inert extras.
            var desc = m.GetComponentInParent<VRCAvatarDescriptor>();
            if (desc != null) {
                int count = desc.GetComponentsInChildren<WdsSyncedDrop>(true).Length;
                if (count > WdsBuildHook.MaxSlots) {
                    EditorGUILayout.HelpBox(
                        "This avatar has " + count + " Synced World Drop objects. Only " + WdsBuildHook.MaxSlots +
                        " are supported. Remove " + (count - WdsBuildHook.MaxSlots) +
                        " or the upload will be blocked.",
                        MessageType.Error);
                } else {
                    // Live cost for this avatar, mirrored from the Settings inspector so it is visible from a drop
                    // object too. Mode (Slow/Fast) lives on the World Drop Synced Settings object; default is Slow.
                    int n = count;
                    var settings = desc.GetComponentInChildren<WdsSyncedSettings>(true);
                    bool fast = settings != null && settings.revealSpeed == WdsRevealSpeed.Fast;
                    // Backend width is sized to the object count (AutoLanes), so cost is count-dependent.
                    int bits = WdsMuxGenerator.BackendBits(WdsMuxGenerator.AutoLanes(n, fast)) + 3 * n;
                    int reveal = WdsMuxGenerator.EstimateRevealSeconds(n, fast);
                    EditorGUILayout.HelpBox(
                        n + " synced object" + (n == 1 ? "" : "s") + " on this avatar. " + bits + " synced bits (" +
                        (fast ? "Fast" : "Slow") + " mode). With all " + n + " dropped at once, the slowest appears " +
                        "in about " + reveal + "s. Set the mode on the World Drop Synced Settings object.",
                        MessageType.Info);
                }
            }

            using (new EditorGUI.DisabledScope(true))
                EditorGUILayout.LabelField("Rotation", m.fullRotation ? "Full" : "Y only");

            serializedObject.Update();
            var menuPath = serializedObject.FindProperty("menuPath");
            var defaultShown = serializedObject.FindProperty("defaultShown");
            var persist = serializedObject.FindProperty("persist");
            var dropParam = serializedObject.FindProperty("dropParam");
            var showParam = serializedObject.FindProperty("showParam");
            var saveParam = serializedObject.FindProperty("saveParam");

            // Common controls. Menu Path drives the built-in Show/Drop submenu; greyed out when a Drop/Show
            // Param is set (a host drives it, so no built-in menu is authored).
            using (new EditorGUI.DisabledScope(!string.IsNullOrWhiteSpace(dropParam.stringValue) || !string.IsNullOrWhiteSpace(showParam.stringValue)))
                EditorGUILayout.PropertyField(menuPath, new GUIContent("Menu Path", menuPath.tooltip));
            EditorGUILayout.PropertyField(defaultShown, new GUIContent("Default Shown", defaultShown.tooltip));
            EditorGUILayout.PropertyField(persist, new GUIContent("Saved Across Sessions", persist.tooltip));

            // Integration controls, tucked away so a normal drop stays a one-field setup. Open this to drive
            // the object from your own menu instead of the built-in toggles.
            EditorGUILayout.Space();
            _showAdvanced = EditorGUILayout.Foldout(_showAdvanced, "Use your own menu (optional)", true);
            if (_showAdvanced) {
                using (new EditorGUI.IndentLevelScope()) {
                    EditorGUILayout.HelpBox(
                        "Drive this object from your own menu instead of the built-in Show/Drop toggles. Enter the " +
                        "avatar parameters your toggles use (this hides the built-in menu); keep them local. A Save " +
                        "Param drives the Save preference from your own toggle. If using VRCFury, mark the " +
                        "parameters global.",
                        MessageType.None);
                    EditorGUILayout.PropertyField(dropParam, new GUIContent("Drop Param", dropParam.tooltip));
                    EditorGUILayout.PropertyField(showParam, new GUIContent("Show Param", showParam.tooltip));
                    // The Save preference only exists on a persist slot, so the alias field only shows then.
                    if (persist.boolValue)
                        EditorGUILayout.PropertyField(saveParam, new GUIContent("Save Param", saveParam.tooltip));
                }
            }

            // Misconfiguration warnings, outside the foldout so they are visible even when it is closed.
            bool hasDrop = !string.IsNullOrWhiteSpace(dropParam.stringValue);
            bool hasShow = !string.IsNullOrWhiteSpace(showParam.stringValue);
            bool hasSave = !string.IsNullOrWhiteSpace(saveParam.stringValue);
            if (hasShow && !hasDrop)
                EditorGUILayout.HelpBox(
                    "Show Param is set without a Drop Param. Setting a Show Param hides the built-in menu, so " +
                    "this object would have no Drop control at all and could never be dropped. Set a Drop Param " +
                    "too, or clear the Show Param. The upload is blocked until this is fixed.",
                    MessageType.Error);
            if (hasSave && !persist.boolValue)
                EditorGUILayout.HelpBox(
                    "Save Param is set but Saved Across Sessions is off, so it does nothing. Turn " +
                    "Saved Across Sessions on, or clear the Save Param.",
                    MessageType.Warning);
            serializedObject.ApplyModifiedProperties();
        }
    }
}
