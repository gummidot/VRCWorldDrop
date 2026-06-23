// Inspector for WdsSyncedSettings. Explanations are shown INLINE (HelpBox), not just as tooltips,
// because people do not hover tooltips. It counts the WorldDropSynced objects on this avatar (found via
// the VRCAvatarDescriptor it sits under) and shows this avatar's exact synced-bit cost + reveal estimate,
// updating with the selected mode.
using UnityEditor;
using UnityEngine;
using VRC.SDK3.Avatars.Components;

namespace VRCWorldDrop.Synced {
    [CustomEditor(typeof(WdsSyncedSettings))]
    public class WdsSyncedSettingsEditor : Editor {
        public override void OnInspectorGUI() {
            var s = (WdsSyncedSettings)target;
            var desc = s.GetComponentInParent<VRCAvatarDescriptor>();
            int count = desc != null ? desc.GetComponentsInChildren<WdsSyncedDrop>(true).Length : 0;
            int n = Mathf.Min(count, WdsBuildHook.MaxSlots);   // only up to MaxSlots actually sync

            if (desc == null) {
                EditorGUILayout.HelpBox("Put this on your avatar (under the GameObject with the VRCAvatarDescriptor). " +
                    "It then applies to every WorldDropSynced object on that avatar.", MessageType.Warning);
            } else {
                EditorGUILayout.HelpBox("This avatar has " + count + " WorldDropSynced object" + (count == 1 ? "" : "s") + ".", MessageType.Info);
                if (count > WdsBuildHook.MaxSlots)
                    EditorGUILayout.HelpBox("Only " + WdsBuildHook.MaxSlots + " are supported. Remove " +
                        (count - WdsBuildHook.MaxSlots) + " or the upload will be blocked.", MessageType.Error);
                if (desc.GetComponentsInChildren<WdsSyncedSettings>(true).Length > 1)
                    EditorGUILayout.HelpBox("More than one WorldDropSynced Settings on this avatar - only one is used.", MessageType.Warning);
            }

            serializedObject.Update();

            // --- Mode (reveal speed) ---
            var speed = serializedObject.FindProperty("revealSpeed");
            EditorGUILayout.PropertyField(speed, new GUIContent("Mode"));
            bool fast = speed.enumValueIndex == (int)WdsRevealSpeed.Fast;
            string modeInfo;
            if (n > 0) {
                // Backend width is sized to the object count (AutoLanes), so cost and the Slow->Fast
                // delta are count-dependent, not a flat 40/72/+32.
                int backend = WdsMuxGenerator.BackendBits(WdsMuxGenerator.AutoLanes(n, fast));
                int bits = backend + 3 * n;
                int reveal = WdsMuxGenerator.EstimateRevealSeconds(n, fast);
                int fastDelta = WdsMuxGenerator.BackendBits(WdsMuxGenerator.AutoLanes(n, true)) - WdsMuxGenerator.BackendBits(WdsMuxGenerator.AutoLanes(n, false));
                modeInfo = (fast ? "Fast" : "Slow (default)") + ": ~" + bits + " synced bits on this avatar (" +
                    backend + " + 3 x " + n + "). With all " + n + " dropped at once, the slowest appears in about " +
                    reveal + "s" + (fast ? "." : " - switch to Fast to roughly halve that, for +" + fastDelta + " bits.");
            } else {
                modeInfo = (fast
                    ? "Fast: ~2x quicker reveal, wider backend (40-72 synced bits depending on object count)."
                    : "Slow (default): the fewest synced bits, sized to your object count (24-40 bits).") +
                    " Add WorldDropSynced objects to see this avatar's exact cost.";
            }
            EditorGUILayout.HelpBox(modeInfo, MessageType.None);

            EditorGUILayout.Space();

            // --- Anti-Cull Protection (off-screen reveal) ---
            var off = serializedObject.FindProperty("offScreenReveal");
            EditorGUILayout.PropertyField(off, new GUIContent("Anti-Cull Protection"));
            if (off.boolValue) {
                EditorGUILayout.HelpBox(
                    "On. Extends your avatar's invisible bounds so remotes keep animating it even when you are " +
                    "off their screen, revealing drops to people who are not looking at you. This forces your " +
                    "performance rank to Very Poor, so only enable it if you need off-screen reveal.",
                    MessageType.Warning);
                var distance = serializedObject.FindProperty("offScreenDistance");
                EditorGUILayout.PropertyField(distance, new GUIContent("Distance (m)"));
                EditorGUILayout.HelpBox("Invisible bounds: about a " + Mathf.Max(1f, distance.floatValue * 2f).ToString("0") +
                    "m box (distance x 2).", MessageType.None);
            } else {
                EditorGUILayout.HelpBox(
                    "Off (default). After you hide and reshow a dropped object, remotes may not see it until " +
                    "they look at your avatar (animator culling). Enable to always reveal drops, at the cost " +
                    "of a Very Poor rank.",
                    MessageType.None);
            }

            serializedObject.ApplyModifiedProperties();
        }
    }
}
