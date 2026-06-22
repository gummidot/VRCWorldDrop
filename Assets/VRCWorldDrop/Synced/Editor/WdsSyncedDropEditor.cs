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
                "Synced World Drop. Put your object under Container > Item. You can add up to " +
                WdsBuildHook.MaxSlots + " of these per avatar. Copies are synced together " +
                "automatically when you upload.\n\nTo move it, move the Reset Target object. To hide it " +
                "in the Scene, disable the Container > Item object (not Container).",
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
                    int bits = (fast ? 72 : 40) + 3 * n;
                    int reveal = Mathf.Max(2, Mathf.RoundToInt(n * (fast ? 0.45f : 0.8f)));
                    EditorGUILayout.HelpBox(
                        n + " synced object" + (n == 1 ? "" : "s") + " on this avatar. " + bits + " synced bits (" +
                        (fast ? "Fast" : "Slow") + " mode). With all " + n + " dropped at once, the slowest appears " +
                        "in about " + reveal + "s. Set the mode on the World Drop Synced Settings object.",
                        MessageType.Info);
                }
            }

            // The basic prefab is hidden by disabling Container; the synced rig needs Container enabled
            // (it is auto-re-enabled on upload, and the object starts hidden via Show anyway). Nudge users
            // who carried that habit over from the basic prefab.
            var container = m.transform.Find("Container");
            if (container != null && !container.gameObject.activeSelf)
                EditorGUILayout.HelpBox(
                    "Container is disabled. The synced variant needs it enabled (unlike the basic prefab); " +
                    "it will be re-enabled on upload. The object already starts hidden - toggle Show to reveal it. " +
                    "To hide it in the Scene, disable Container > Item or its renderer instead.",
                    MessageType.Warning);

            using (new EditorGUI.DisabledScope(true))
                EditorGUILayout.LabelField("Rotation", m.fullRotation ? "Full" : "Y only");

            serializedObject.Update();
            var menu = serializedObject.FindProperty("menuPath");
            EditorGUILayout.PropertyField(menu, new GUIContent("Menu Path", menu.tooltip));
            serializedObject.ApplyModifiedProperties();
        }
    }
}
