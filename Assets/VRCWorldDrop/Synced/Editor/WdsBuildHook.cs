// Multi-object shared-mux build hook.
//
// Runs as an avatar build hook AFTER VRCFury. VRCFury merges the avatar's FX controller,
// expression menu and parameters into their final form at its own hook (callbackOrder -10000).
// This hook appends its generated mux layers, synced params and per-object menu onto that
// already-merged output, so it has to run later or the merge would clobber what it added. It also
// has to run before order -1024, where the VRC SDK strips IEditorOnly components: the
// WdsSyncedDrop markers it enumerates are IEditorOnly and only still exist between -10000 and
// -1024. callbackOrder -1025 sits in that window.
//
// What it does: enumerate WdsSyncedDrop markers, assign dense slot ids, rewrite each instance's
// contact tags and params per slot, then run WdsMuxGenerator.Emit to build one shared-mux controller
// appended to the merged FX, plus the synced params and per-object menu. It only touches the
// standard SDK avatar descriptor, never VRCFury or NDMF directly.
#if UNITY_EDITOR
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using VRC.SDK3.Avatars.Components;
using VRC.SDK3.Avatars.ScriptableObjects;
using VRC.SDKBase.Editor.BuildPipeline;

namespace VRCWorldDrop.Synced {
    public class WdsBuildHook : IVRCSDKPreprocessAvatarCallback, IVRCSDKPostprocessAvatarCallback {
        // -1025: after VRCFury's -10000 merge, before the SDK's -1024 IEditorOnly strip (see header).
        public int callbackOrder => -1025;

        // Prefix on this build hook's Unity console messages, so its logs are easy to find and filter.
        const string LogPrefix = "[WorldDropSynced]";

        // Most independently-droppable objects one avatar may share on the single mux. The generator
        // and ring scale to any N (Idx packs slot*MAXSTEP+step, lossless to 25 slots), so this is not a
        // code limit. The real external bound is VRChat's 256-contact-per-avatar upload cap: each drop is
        // 15 (Y-only) or 20 (full-rotation) localOnly contacts - localOnly is exempt from the perf RANK
        // but NOT from this upload limit - so ~17 Y-only / ~12-13 full-rotation fit before the avatar's
        // own contacts. 16 is a single flat cap under that (full-rotation can exceed 256 below 16; a
        // rotation-aware cap is a follow-up). Over-cap uploads are usually rejected by the SDK's contact
        // check before this hook's own >16 abort even runs. 16 is verified in-game: all 16 dropped even in
        // a tight cluster reveal correctly. That relies on the transient measurement rig (the Rig layer in
        // WdsMuxGenerator gates each object's contacts to its ~1s encode window); without it, every
        // object's contacts stay live and cluster at the shared world-origin anchor, saturating
        // VRChat's contact broadphase so even one drop among many stalls.
        // What still grows per object: the avatar's constraint count (~12 each -> 16 objects ~= 192,
        // the Good perf band) and the reveal cycle, which steps through every CURRENTLY-DROPPED object
        // (~12-14s for all 16 at L=4 - measured 2026-06-20, ~0.8s/object; widen WdsMuxGenerator.Lanes to shorten it). Going over
        // aborts the upload (error + dialog) so the user removes the extras, never a silent or half-synced upload.
        public const int MaxSlots = 16;

        // Resolve this package's "Synced" folder from this script's own location, so the build-time
        // scratch folder (<Synced>/_Temp) is found whether the package is embedded under Assets/ or
        // installed under Packages/ (VPM). A hardcoded "Assets/..." path would not exist for a VPM
        // consumer, whose copy lives under Packages/.
        static string SyncedDir() {
            foreach (var guid in AssetDatabase.FindAssets("WdsBuildHook t:MonoScript")) {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                if (path.EndsWith("/WdsBuildHook.cs")) {
                    var editorDir = path.Substring(0, path.LastIndexOf('/'));   // <Synced>/Editor
                    return editorDir.Substring(0, editorDir.LastIndexOf('/'));  // <Synced>
                }
            }
            return "Assets/VRCWorldDrop/Synced"; // fallback if this script was moved
        }

        public bool OnPreprocessAvatar(GameObject avatar) {
            var markers = avatar.GetComponentsInChildren<WdsSyncedDrop>(true);
            if (markers.Length == 0) return true;

            // Hard requirement: box contacts with Use Face Proximity (SDK 3.10.4+). Without them the rig
            // cannot read a world coordinate and would upload silently broken, so abort the build loudly.
            if (!WdsSdkSupport.HasBoxFaceProximity) {
                string msg = "WorldDropSynced needs VRChat SDK " + WdsSdkSupport.MinSdk + " or newer; this project's " +
                    "SDK is older, so the synced drop cannot work. Upload aborted. Update the VRChat SDK, then upload again.";
                Debug.LogError($"{LogPrefix} {msg}");
                EditorUtility.DisplayDialog("WorldDropSynced - update the VRChat SDK", msg, "OK");
                return false; // cancels the build instead of uploading a broken rig
            }

            var desc = avatar.GetComponent<VRCAvatarDescriptor>();
            if (desc == null) {
                string msg = "WorldDropSynced could not find a VRCAvatarDescriptor on this avatar, so it cannot add the synced rig. Upload aborted.";
                Debug.LogError($"{LogPrefix} {msg}");
                EditorUtility.DisplayDialog("WorldDropSynced - no avatar descriptor", msg, "OK");
                return false; // abort rather than upload a broken, unsynced rig
            }

            var used = markers.ToList();
            if (used.Count > MaxSlots) {
                string msg = $"Only {MaxSlots} WorldDropSynced objects are supported per avatar. You have {used.Count}. " +
                    $"Upload aborted. Remove {used.Count - MaxSlots}, then upload again.";
                Debug.LogError($"{LogPrefix} {msg}");
                EditorUtility.DisplayDialog("WorldDropSynced - too many objects", msg, "OK");
                return false; // cancel the upload rather than ship with inert, unsynced extras
            }

            // Prepare a fresh scratch dir, resolved relative to the package (see SyncedDir).
            string syncedDir = SyncedDir();
            string tempDir = syncedDir + "/_Temp";
            if (AssetDatabase.IsValidFolder(tempDir)) AssetDatabase.DeleteAsset(tempDir);
            AssetDatabase.CreateFolder(syncedDir, "_Temp");

            // Ensure each object root has an unambiguous hierarchy path. Two instances with the same
            // name under the same parent produce identical paths, and the generated animation clips
            // would then bind to the wrong one. Drag-drop usually uniquifies names already; this is
            // insurance. The appended " (slot N)" is build-local: stripped with the avatar copy,
            // never uploaded.
            for (int i = 0; i < used.Count; i++) {
                string path = AnimationUtility.CalculateTransformPath(used[i].transform, avatar.transform);
                if (used.Take(i).Any(o => AnimationUtility.CalculateTransformPath(o.transform, avatar.transform) == path))
                    used[i].gameObject.name += " (slot " + i + ")";
            }

            // Assign dense slot ids; rewrite each instance's contact tags/params; build slot specs.
            var slots = new WdsMuxGenerator.Slot[used.Count];
            for (int i = 0; i < used.Count; i++) {
                var mk = used[i];
                // Normalize the two visibility nodes on the build copy (never the scene), so the upload is
                // deterministic no matter what active states the user toggled. Container must stay active -
                // it hosts the parent constraint and the pose senders - so re-enable it if disabled.
                // Container/Item (the visible object) is forced off; from there the Show layer owns its
                // visibility every frame and Default Shown decides whether it starts revealed. This frees
                // users from the Container-vs-Item active-state distinction, a frequent source of disabling
                // the wrong node. The pose senders ride under Container/_SyncSenders (a sibling of Item) and
                // the decode chain under _Sync, so hiding Item never touches the rig.
                var container = mk.transform.Find("Container");
                if (container != null && !container.gameObject.activeSelf) {
                    container.gameObject.SetActive(true);
                    Debug.Log($"{LogPrefix} '{mk.name}/Container' was disabled; re-enabled it for the synced rig.");
                }
                var item = container != null ? container.Find("Item") : null;
                if (item != null) item.gameObject.SetActive(false);
                string basePath = AnimationUtility.CalculateTransformPath(mk.transform, avatar.transform);
                slots[i] = new WdsMuxGenerator.Slot { id = i, fullRot = mk.fullRotation, basePath = basePath, menuPath = mk.menuPath, defaultShown = mk.defaultShown, dropAlias = mk.DropParamOrNull, showAlias = mk.ShowParamOrNull };
                WdsMuxGenerator.RetagSlot(mk.gameObject, i, mk.SrcParamPrefix, mk.SrcTag);
            }

            // Target FX controller: clone VRCFury's merged FX into Temp (so AddObjectToAsset works
            // and we never mutate VRCFury's asset), or create a fresh one if the avatar has none.
            var layers = desc.baseAnimationLayers;
            int fxIdx = System.Array.FindIndex(layers, l => l.type == VRCAvatarDescriptor.AnimLayerType.FX);
            if (fxIdx < 0) {
                string msg = "WorldDropSynced needs an FX playable layer on the avatar to add the synced rig, but none was found. Upload aborted.";
                Debug.LogError($"{LogPrefix} {msg}");
                EditorUtility.DisplayDialog("WorldDropSynced - no FX layer", msg, "OK");
                return false; // abort rather than upload a broken, unsynced rig
            }
            var fx = layers[fxIdx].animatorController as AnimatorController;
            string fxPath = fx != null ? AssetDatabase.GetAssetPath(fx) : null;
            string targetPath = tempDir + "/WdsMuxFX.controller";
            AnimatorController target;
            if (fx != null && !string.IsNullOrEmpty(fxPath)) {
                AssetDatabase.CopyAsset(fxPath, targetPath);
                target = AssetDatabase.LoadAssetAtPath<AnimatorController>(targetPath);
            } else {
                if (fx != null) Debug.LogWarning($"{LogPrefix} merged FX '{fx.name}' has no asset path; creating a fresh controller");
                target = AnimatorController.CreateAnimatorControllerAtPath(targetPath);
            }

            // Clone the expression params + menu (VRCFury supplies these at build; null in edit mode)
            // and PERSIST them as project assets. This is load-bearing: VRChat's uploader ignores an
            // expression menu / parameters object that has no asset path (it shows up in Av3Emulator,
            // which reads the descriptor directly, but is dropped from the actual upload). Saving them
            // is what every avatar tool (VRCFury, MA, d4rk) does.
            var prms = desc.expressionParameters != null ? Object.Instantiate(desc.expressionParameters) : ScriptableObject.CreateInstance<VRCExpressionParameters>();
            if (prms.parameters == null) prms.parameters = new VRCExpressionParameters.Parameter[0];
            AssetDatabase.CreateAsset(prms, tempDir + "/WdsMuxParams.asset");
            var menu = desc.expressionsMenu != null ? Object.Instantiate(desc.expressionsMenu) : ScriptableObject.CreateInstance<VRCExpressionsMenu>();
            if (menu.controls == null) menu.controls = new System.Collections.Generic.List<VRCExpressionsMenu.Control>();
            AssetDatabase.CreateAsset(menu, tempDir + "/WdsMuxMenu.asset");

            // Avatar-wide settings (optional, one object). Reveal speed picks the broadcast width through
            // WdsMuxGenerator.AutoLanes, which sizes the width to the object count: a low-count avatar pays a
            // narrower backend (Slow 1-2 objects = 2 lanes / 24 bits, vs 4 lanes / 40 at 6+). Fast is ~2x
            // wider for a faster reveal. An explicit LanesOverride still wins.
            var settings = avatar.GetComponentInChildren<WdsSyncedSettings>(true);
            bool fast = settings != null && settings.revealSpeed == WdsRevealSpeed.Fast;
            WdsMuxGenerator.Lanes = WdsMuxGenerator.LanesOverride ?? WdsMuxGenerator.AutoLanes(used.Count, fast);

            // Off-screen reveal (anti-cull): when requested, add a material-less large-bounds renderer so
            // VRChat keeps animating the avatar while it is off a remote's screen (forces Very Poor). Built
            // here so the settings prefab stays a lightweight marker and nothing ships when it is off.
            if (settings != null && settings.offScreenReveal) {
                var boundsMesh = AssetDatabase.LoadAssetAtPath<Mesh>(syncedDir + "/Support/WDS_BoundsMesh.asset");
                if (boundsMesh != null) {
                    var bounds = new GameObject("WDS_OffScreenBounds");
                    bounds.transform.SetParent(avatar.transform, false);
                    float sz = Mathf.Max(1f, settings.offScreenDistance * 2f);
                    bounds.transform.localScale = new Vector3(sz, sz, sz);
                    bounds.AddComponent<MeshFilter>().sharedMesh = boundsMesh;
                    bounds.AddComponent<MeshRenderer>().sharedMaterials = new Material[0];
                } else {
                    Debug.LogWarning($"{LogPrefix} off-screen reveal is on but WDS_BoundsMesh was not found; anti-cull bounds skipped.");
                }
            }

            var log = new StringBuilder();
            log.AppendLine($"{LogPrefix} wiring {slots.Length} slot(s) at {WdsMuxGenerator.Lanes} lanes:");
            foreach (var s in slots) log.AppendLine($"  slot {s.id} fullRot={s.fullRot} path='{s.basePath}'");
            WdsMuxGenerator.Emit(target, prms, menu, slots, tempDir, log); // adds layers to target, params to prms, submenu assets + controls to menu

            // Persist all generated assets so the upload bundle includes them.
            EditorUtility.SetDirty(target); EditorUtility.SetDirty(prms); EditorUtility.SetDirty(menu);
            AssetDatabase.SaveAssets();

            // Reassign onto the descriptor (now all asset-backed).
            layers[fxIdx].isDefault = false;
            layers[fxIdx].animatorController = target;
            desc.baseAnimationLayers = layers;
            desc.expressionParameters = prms;
            desc.expressionsMenu = menu;

            int cost = prms.CalcTotalCost();
            log.AppendLine($"{LogPrefix} done. Synced cost {cost}/256 bits. menu='{AssetDatabase.GetAssetPath(menu)}' params='{AssetDatabase.GetAssetPath(prms)}' fx='{AssetDatabase.GetAssetPath(target)}'");
            if (cost > 256) Debug.LogWarning($"{LogPrefix} synced cost {cost} exceeds 256: VRChat will reject the upload. Remove other params or fewer WorldDropSynced objects.");
            Debug.Log(log.ToString());
            return true;
        }

        public void OnPostprocessAvatar() {
            // Leave Temp assets: the descriptor references them during playmode, and the upload
            // bundle has already captured them. They are overwritten on the next build (fixed names).
        }
    }
}
#endif
