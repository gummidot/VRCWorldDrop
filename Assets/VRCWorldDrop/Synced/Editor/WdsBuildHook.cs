// Multi-object shared-mux build hook.
//
// Runs as an avatar build hook BEFORE VRCFury (callbackOrder -20000; VRCFury's avatar merge is at
// -10000). It generates the synced rig as standalone assets - one shared-mux FX controller, the synced
// params and a per-object menu - and hands them to VRCFury through its public API
// (FuryComponents.CreateFullController). VRCFury then merges them into the avatar's FX, expression params
// and menu at its own pass, and owns menu pagination and parameter type coercion.
//
// Running before VRCFury is what keeps the generated IsLocal and host-drive conditions correct. VRCFury
// floats IsLocal whenever a local-only/remote-only action is present on the avatar, and coerces every
// bool-mode condition to match its parameter's final type. A hook that appends layers AFTER that coercion
// leaves its own IsLocal conditions type-mismatched and dead, which silently breaks remote sync. Handing
// the controller over before the merge lets that same coercion fix our conditions too. The WDM* parameter
// namespace (and any host drive param) is forced global so VRCFury preserves those names verbatim - the
// position contacts reference them by raw name.
//
// What it does: enumerate WdsSyncedDrop markers (IEditorOnly, stripped later in the build),
// assign dense slot ids, rewrite each instance's contact tags and params per slot, run
// WdsMuxGenerator.Emit to build the standalone controller/params/menu, then register them with VRCFury. It
// only adds a VRCFury Full Controller component and reads the standard SDK avatar descriptor; it never
// writes the avatar's controller, menu or parameter assets directly.
#if UNITY_EDITOR
using System.Collections.Generic;
using System.Linq;
using System.Text;
using com.vrcfury.api;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using VRC.SDK3.Avatars.Components;
using VRC.SDK3.Avatars.ScriptableObjects;
using VRC.SDK3.Dynamics.Contact.Components;
using VRC.SDKBase.Editor.BuildPipeline;

namespace VRCWorldDrop.Synced {
    public class WdsBuildHook : IVRCSDKPreprocessAvatarCallback, IVRCSDKPostprocessAvatarCallback {
        // -20000: before VRCFury's -10000 avatar-preprocess pass, so VRCFury owns the merge and coerces
        // our conditions to their final parameter types (see header). Any value below -10000 works. A
        // separate read-only hook (WdsBuildVerifyHook, below) runs after VRCFury and logs diagnostic
        // warnings if the merge changed the rig.
        public int callbackOrder => -20000;

        // Prefix on this build hook's Unity console messages, so its logs are easy to find and filter.
        const string LogPrefix = "[WorldDropSynced]";

        // Most droppable objects one avatar may share on the single mux. The generator and its
        // broadcast ring scale to any count (the Idx packing stays lossless up to 25 slots), so this is
        // not a code limit. The practical bound is VRChat's upload cap of 256 contacts per avatar: each
        // drop uses 15 (Y-only) or 20 (full-rotation) localOnly contacts, and localOnly contacts are
        // exempt from the performance rank but still count toward that upload cap, so roughly 17 Y-only
        // or 12-13 full-rotation objects fit before counting the avatar's own contacts. 16 is one flat
        // cap under that. It does not account for rotation family, so an avatar of full-rotation drops
        // can hit the SDK's contact cap below 16 objects; the SDK's own contact check usually rejects
        // such an upload before this hook's abort even runs.
        //
        // All 16 work in-game, even dropped in a tight cluster. That depends on the Rig layer in
        // WdsMuxGenerator gating each object's measurement contacts to its ~1s encode window: every
        // rig anchors its contacts near the world origin, and each VRChat contact shape considers at
        // most 32 overlapping shapes (tag matching runs only on those), so with every object's
        // contacts left always-on a receiver's own sender can be crowded out of its nearest-32 set
        // and drops would stop measuring reliably. What still grows per
        // object: the avatar's constraint count (~12 per object, so 16 objects is ~192, within the
        // Good performance band) and the reveal cycle, which steps through every currently-dropped
        // object (~12-14s for all 16 at the default 4 lanes, about 0.8s per object; a wider
        // WdsMuxGenerator.Lanes shortens it). Exceeding the cap aborts the upload with an error dialog
        // so the user removes the extras; extras are never silently ignored or uploaded half-synced.
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

        // Validates the author-named drive params (dropParam/showParam/saveParam) across all markers.
        // Returns null when everything is valid, else a user-facing error message. Rules:
        //   - no alias may start with the generated parameter namespace root or equal IsLocal;
        //   - showParam without dropParam is rejected (either alias suppresses the built-in menu, so
        //     the object would have no Drop control anywhere and could never be dropped);
        //   - a saveParam may not equal any dropParam/showParam of any object (cross-role collision:
        //     dropping one object would silently flip another's Save preference);
        //   - the SAME saveParam on several objects is allowed (one Save toggle drives them all), as
        //     is sharing a dropParam/showParam (those objects drop or show together by design).
        // saveParam is only considered on persist markers; on others the hook ignores it (logged).
        public static string ValidateHostParams(IList<WdsSyncedDrop> markers) {
            var dropShow = new List<(string name, string role, string obj)>();
            var saves = new List<(string name, string obj)>();
            foreach (var mk in markers) {
                string drop = mk.DropParamOrNull, show = mk.ShowParamOrNull;
                string save = mk.persist ? mk.SaveParamOrNull : null;
                if (show != null && drop == null)
                    return $"'{mk.name}' sets a Show Param without a Drop Param. Setting either hides the " +
                        "built-in menu, so the object would have no Drop control and could never be dropped. " +
                        "Set a Drop Param too, or clear the Show Param. Upload aborted.";
                foreach (var (role, name) in new[] { ("Drop Param", drop), ("Show Param", show), ("Save Param", save) }) {
                    if (name == null) continue;
                    if (name.StartsWith(WdsMuxGenerator.ParamRoot))
                        return $"'{mk.name}' {role} '{name}' starts with '{WdsMuxGenerator.ParamRoot}', which is " +
                            "reserved for the generated synced-drop parameters. Rename the parameter. Upload aborted.";
                    if (name == "IsLocal")
                        return $"'{mk.name}' {role} 'IsLocal' is a reserved VRChat parameter and cannot drive a " +
                            "drop. Use a different parameter. Upload aborted.";
                }
                if (drop != null) dropShow.Add((drop, "Drop Param", mk.name));
                if (show != null) dropShow.Add((show, "Show Param", mk.name));
                if (save != null) saves.Add((save, mk.name));
            }
            foreach (var sv in saves) {
                var hit = dropShow.FirstOrDefault(ds => ds.name == sv.name);
                if (hit.name != null)
                    return $"'{sv.obj}' Save Param '{sv.name}' is also the {hit.role} of '{hit.obj}'. A Save " +
                        "preference cannot share a parameter with a Drop or Show control. Use a different " +
                        "Save Param. Upload aborted.";
            }
            return null;
        }

        // Rejects a WorldDropSynced object nested inside another: the outer slot's contact retag pass
        // rewrites every receiver under it, including the inner object's, binding the inner object's
        // contacts to the outer slot's parameters - both objects would then misread. Returns null when
        // no marker contains another, else a user-facing error message.
        public static string ValidateMarkerNesting(IList<WdsSyncedDrop> markers) {
            foreach (var mk in markers) {
                var inner = markers.FirstOrDefault(o => o != mk && o.transform.IsChildOf(mk.transform));
                if (inner != null)
                    return $"'{inner.name}' is inside '{mk.name}'. WorldDropSynced objects cannot be nested; " +
                        "move the inner one to its own branch of the avatar. Upload aborted.";
            }
            return null;
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

            // Author-named drive params are validated up front, and a bad setup is a hard abort, not a
            // warning: each rejected case ships an object that cannot work (a namespace or cross-role
            // collision silently cross-wires unrelated controls; a Show-only binding hides the built-in
            // menu while providing no Drop control, leaving the object permanently undroppable).
            string aliasErr = ValidateHostParams(used);
            if (aliasErr != null) {
                Debug.LogError($"{LogPrefix} {aliasErr}");
                EditorUtility.DisplayDialog("WorldDropSynced - check your menu parameters", aliasErr, "OK");
                return false; // cancel the upload rather than ship a rig that cannot work
            }
            string nestErr = ValidateMarkerNesting(used);
            if (nestErr != null) {
                Debug.LogError($"{LogPrefix} {nestErr}");
                EditorUtility.DisplayDialog("WorldDropSynced - nested objects", nestErr, "OK");
                return false; // a nested object's contacts would bind to the outer slot's parameters
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
                    Debug.Log($"{LogPrefix} '{mk.name}': Container was disabled, so it was turned back on for this upload - it has to stay active for the drop to work. To keep the object hidden in your scene, disable Container/Item instead.");
                }
                var item = container != null ? container.Find("Item") : null;
                if (item != null) item.gameObject.SetActive(false);
                string basePath = AnimationUtility.CalculateTransformPath(mk.transform, avatar.transform);
                // The Save preference only exists on persist slots, so a saveParam on a non-persist
                // marker has nothing to bind; drop it with a log so the author sees why.
                if (!mk.persist && mk.SaveParamOrNull != null)
                    Debug.LogWarning($"{LogPrefix} '{mk.name}' sets a Save Param but Saved Across Sessions is off; the Save Param is ignored.");
                slots[i] = new WdsMuxGenerator.Slot { id = i, fullRot = mk.fullRotation, basePath = basePath, menuPath = mk.menuPath, defaultShown = mk.defaultShown, persist = mk.persist, dropAlias = mk.DropParamOrNull, showAlias = mk.ShowParamOrNull, saveAlias = mk.persist ? mk.SaveParamOrNull : null };
                WdsMuxGenerator.RetagSlot(mk.gameObject, i, mk.SrcParamPrefix, mk.SrcTag);
            }

            // Generate the synced rig as STANDALONE assets: a fresh FX controller, empty expression params
            // and an empty menu, saved under Temp (VRChat's uploader ignores an in-memory menu / params with
            // no asset path, and AddObjectToAsset needs the controller on disk). VRCFury merges these into
            // the avatar's own FX / params / menu at its -10000 pass, so this hook never reads or writes the
            // avatar's existing controller, menu or parameters. It also does not need the avatar to already
            // have an FX playable layer: VRCFury's Full Controller merge creates one if missing.
            string targetPath = tempDir + "/WdsMuxFX.controller";
            var target = AnimatorController.CreateAnimatorControllerAtPath(targetPath);
            var prms = ScriptableObject.CreateInstance<VRCExpressionParameters>();
            prms.parameters = new VRCExpressionParameters.Parameter[0];
            AssetDatabase.CreateAsset(prms, tempDir + "/WdsMuxParams.asset");
            var menu = ScriptableObject.CreateInstance<VRCExpressionsMenu>();
            menu.controls = new List<VRCExpressionsMenu.Control>();
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
                var boundsPath = syncedDir + "/Support/WDS_BoundsMesh.asset";
                var boundsMesh = AssetDatabase.LoadAssetAtPath<Mesh>(boundsPath);
                if (boundsMesh != null) {
                    var bounds = new GameObject("WDS_OffScreenBounds");
                    bounds.transform.SetParent(avatar.transform, false);
                    float sz = Mathf.Max(1f, settings.offScreenDistance * 2f);
                    bounds.transform.localScale = new Vector3(sz, sz, sz);
                    bounds.AddComponent<MeshFilter>().sharedMesh = boundsMesh;
                    bounds.AddComponent<MeshRenderer>().sharedMaterials = new Material[0];
                } else {
                    Debug.LogWarning($"{LogPrefix} Anti-Cull Protection is on, but part of the VRCWorldDrop package is missing, so it was skipped. Other players may not see your drops appear while you are off their screen. Reinstall VRCWorldDrop and upload again. Missing file: {boundsPath}");
                }
            }

            var log = new StringBuilder();
            log.AppendLine($"{LogPrefix} wiring {slots.Length} slot(s) at {WdsMuxGenerator.Lanes} lanes:");
            foreach (var s in slots) log.AppendLine($"  slot {s.id} fullRot={s.fullRot} path='{s.basePath}'");
            WdsMuxGenerator.Emit(target, prms, menu, slots, tempDir, log); // adds layers to target, params to prms, submenu assets + controls to menu

            // Persist the generated assets so VRCFury (and the upload bundle) can read them.
            EditorUtility.SetDirty(target); EditorUtility.SetDirty(prms); EditorUtility.SetDirty(menu);
            AssetDatabase.SaveAssets();

            // Hand the rig to VRCFury: it merges the controller as an FX layer set, the params and the menu at
            // its -10000 pass. Force the whole WDM* namespace global so VRCFury keeps those names verbatim
            // through the merge - otherwise it uniquifies them with a per-build "VF<n>_" prefix that changes
            // whenever the user adds or removes any VRCFury component. VRCFury renames the contact receivers
            // together with the controller, so the rig itself would keep working; what a non-global rename
            // costs is name stability, which resets every saved (persist) drop and breaks host and OSC
            // integrations that reference the generated names. Force every host drive param global too, so
            // the host's own declaration and our bridge reference unify by name instead of being renamed apart.
            var fc = FuryComponents.CreateFullController(avatar);
            fc.AddController(target, VRCAvatarDescriptor.AnimLayerType.FX);
            fc.AddParams(prms);
            fc.AddMenu(menu);
            fc.AddGlobalParam(WdsMuxGenerator.ParamRoot + "*");
            foreach (var slot in slots) {
                if (slot.dropAlias != null) fc.AddGlobalParam(slot.dropAlias);
                if (slot.showAlias != null) fc.AddGlobalParam(slot.showAlias);
                // The Save alias must be global like the others: without this VRCFury renames the
                // host's declaration apart from our BridgeSave reference and the bridge is silently
                // dead, leaving the Save preference stuck on.
                if (slot.saveAlias != null) fc.AddGlobalParam(slot.saveAlias);
            }

            int cost = prms.CalcTotalCost();
            log.AppendLine($"{LogPrefix} done. Added synced cost {cost}/256 bits (VRCFury merges it into the avatar's params). menu='{AssetDatabase.GetAssetPath(menu)}' params='{AssetDatabase.GetAssetPath(prms)}' fx='{AssetDatabase.GetAssetPath(target)}'");
            if (cost > 256) Debug.LogWarning($"{LogPrefix} this avatar needs {cost} synced parameter bits, more than the 256 VRChat allows, so the upload will be rejected. Remove some of your other synced parameters, or use fewer WorldDropSynced objects.");
            Debug.Log(log.ToString());

            // Record exactly what was generated so the -1025 diagnostic pass can compare the merged
            // avatar against facts instead of name patterns (see WdsBuildRecord). The receiver
            // snapshot is taken after RetagSlot, so it holds the final slot-scoped parameter names.
            WdsBuildRecord.receivers = used
                .SelectMany(mk => mk.GetComponentsInChildren<VRCContactReceiver>(true))
                .Where(r => !string.IsNullOrEmpty(r.parameter) && r.parameter.StartsWith(WdsMuxGenerator.ParamRoot))
                .Select(r => (r, r.parameter)).ToList();
            WdsBuildRecord.parameters = prms.parameters
                .Select(pp => (pp.name, pp.valueType, pp.saved, pp.networkSynced, pp.defaultValue)).ToList();
            WdsBuildRecord.avatar = avatar;
            return true;
        }

        public void OnPostprocessAvatar() {
            // Leave Temp assets: they are the source VRCFury reads when it merges the Full Controller, and
            // they are overwritten on the next build (fixed names). Nothing to clean up.
        }
    }

    /// <summary>Handoff from WdsBuildHook (-20000) to WdsBuildVerifyHook (-1025) within one avatar
    /// build: the generator records exactly what it wrote - the receiver-to-parameter bindings it
    /// retagged and the expression parameters it registered - and the diagnostic pass compares the
    /// merged avatar against that record. Recorded facts, not name patterns: VRCFury renames a
    /// non-global parameter on the avatar's contact receivers together with the controller
    /// parameter, so a prefix filter would skip exactly the renamed receiver it needs to flag.
    /// Overwritten at the start of every build; the diagnostic pass requires the record to belong
    /// to its own avatar copy.</summary>
    public static class WdsBuildRecord {
        public static GameObject avatar;
        public static List<(VRCContactReceiver receiver, string parameter)> receivers;
        public static List<(string name, VRCExpressionParameters.ValueType type, bool saved, bool synced, float def)> parameters;
    }

    // Read-only diagnostic pass that runs AFTER VRCFury (order -1025; IEditorOnly components are
    // stripped later in the build, so the WdsSyncedDrop markers are present). It compares the merged avatar
    // against WdsBuildRecord - the exact receivers and expression parameters the -20000 hook
    // generated - and logs a console warning for anything that no longer matches: a removed or
    // renamed contact receiver binding, a generated parameter that vanished or changed type, or an
    // expression parameter whose merged contract differs from what was registered. Warnings are
    // diagnostics only; the upload always proceeds. When a synced drop misbehaves for other players
    // (reconstructs at the world origin, a toggle that does nothing), these warnings are the first
    // place to look in the build console. It also logs advisory notes when a persist slot's host
    // Save Param is missing, unsaved, or synced. It mutates nothing.
    public class WdsBuildVerifyHook : IVRCSDKPreprocessAvatarCallback {
        public int callbackOrder => -1025;
        const string LogPrefix = "[WorldDropSynced]";

        /// <summary>Diagnostics from the most recent Verify run (cleared on entry). Logged to the
        /// console; never blocks an upload.</summary>
        public static readonly List<string> Warnings = new List<string>();
        static void Warn(string msg) => Warnings.Add(msg);

        public bool OnPreprocessAvatar(GameObject avatar) {
            Verify(avatar);
            foreach (var w in Warnings) Debug.LogWarning($"{LogPrefix} {w}");
            return true;
        }

        /// <summary>Compares the merged avatar against the generation record and fills Warnings
        /// with every mismatch. Read-only; never blocks an upload.</summary>
        public static void Verify(GameObject avatar) {
            Warnings.Clear();
            var markers = avatar.GetComponentsInChildren<WdsSyncedDrop>(true);
            if (markers.Length == 0) return;
            var desc = avatar.GetComponent<VRCAvatarDescriptor>();
            if (desc == null) return; // the -20000 hook already aborted; nothing to check

            if (WdsBuildRecord.avatar != avatar || WdsBuildRecord.receivers == null) {
                Warn("the setup step that records what was generated did not run for this avatar build " +
                    "(or ran on a different copy), so the finished avatar was not checked");
                return;
            }

            var fx = desc.baseAnimationLayers
                .Where(l => l.type == VRCAvatarDescriptor.AnimLayerType.FX)
                .Select(l => l.animatorController as AnimatorController)
                .FirstOrDefault(c => c != null);
            if (fx == null) {
                Warn("the avatar has no FX controller after the build, so the drop setup could not be checked");
                return;
            }
            var types = new Dictionary<string, AnimatorControllerParameterType>();
            foreach (var p in fx.parameters) types[p.name] = p.type;   // tolerate a duplicate merged name rather than throwing

            // Every recorded receiver binding should be intact, and its parameter present in the merged
            // controller. The recorded snapshot was taken right after the retag pass, so any difference
            // here was made by a later hook.
            foreach (var (receiver, parameter) in WdsBuildRecord.receivers) {
                if (receiver == null) {
                    Warn($"part of the drop's position rig (the contact receiver for '{parameter}') was removed " +
                        "during the build, so this drop may not sync correctly");
                    continue;
                }
                if (receiver.parameter != parameter) {
                    // VRCFury rewrites receiver parameters together with controller contents, so a
                    // rename can leave the pair consistent; only claim breakage when the current
                    // name has no matching Float parameter in the merged controller.
                    if (types.TryGetValue(receiver.parameter, out var renamedType) && renamedType == AnimatorControllerParameterType.Float)
                        Warn($"another tool renamed the drop's parameter '{parameter}' to '{receiver.parameter}' " +
                            "during the build. The drop still works, but the name may change again on a future " +
                            "upload, which would reset saved drop positions and break custom menu toggles or OSC " +
                            "tools that use this name");
                    else
                        Warn($"another tool renamed the drop's parameter '{parameter}' to '{receiver.parameter}' " +
                            "during the build, and the avatar's animator has no matching entry for it, so other " +
                            "players would see this drop at the world origin");
                    continue;
                }
                if (!types.ContainsKey(parameter)) {
                    Warn($"the drop's parameter '{parameter}' went missing during the build, so other players " +
                        "would see this drop at the world origin");
                    continue;
                }
                // Contact receivers write proximity floats and the generator declares every receiver
                // channel as Float; any other merged type means another declaration took the name over.
                if (types[parameter] != AnimatorControllerParameterType.Float)
                    Warn($"the drop's parameter '{parameter}' ended up as {types[parameter]} instead of Float " +
                        "after the build (another component probably declares the same name), so other players " +
                        "would see this drop at the world origin");
            }

            // Every registered expression parameter should keep its exact contract through the merge.
            // VRCFury only rejects a same-name parameter on a TYPE conflict; saved, synced and default
            // are inherited from a pre-existing declaration unchecked, which would silently repurpose a
            // synced channel or the persist store. The same generated name must also survive as an
            // animator parameter of the matching type: VRChat bridges the expression value onto the
            // animator parameter by name, and the merged rig reads and drives it there.
            var eps = desc.expressionParameters;
            foreach (var (name, type, saved, synced, def) in WdsBuildRecord.parameters) {
                var want = type == VRCExpressionParameters.ValueType.Bool ? AnimatorControllerParameterType.Bool
                    : type == VRCExpressionParameters.ValueType.Int ? AnimatorControllerParameterType.Int
                    : AnimatorControllerParameterType.Float;
                if (!types.TryGetValue(name, out var animType))
                    Warn($"the drop's parameter '{name}' is missing from the avatar's FX controller after the " +
                        "build, so part of the drop's logic has nothing to read or drive");
                else if (animType != want)
                    Warn($"the drop's parameter '{name}' ended up as {animType} instead of {want} in the FX " +
                        "controller after the build, so part of the drop's logic may not respond");

                var ep = eps != null ? eps.FindParameter(name) : null;
                if (ep == null) {
                    Warn($"the avatar parameter '{name}' is missing after the build, so this drop may not sync, " +
                        "save, or show in the menu correctly");
                    continue;
                }
                if (ep.valueType != type || ep.saved != saved || ep.networkSynced != synced || Mathf.Abs(ep.defaultValue - def) > 1e-4f)
                    Warn($"the avatar parameter '{name}' was set up as ({Describe(type, saved, synced, def)}) but " +
                        $"ended up as ({Describe(ep.valueType, ep.saved, ep.networkSynced, ep.defaultValue)}) after " +
                        "the build. Another component on the avatar declares the same name differently, so this " +
                        "drop may not sync or save correctly");
            }

            // Host Save Params (persist slots): the author's Save param is the preference's only
            // store, and this rig never writes another author's parameters, so it must be author-marked
            // saved to survive sessions. Warn, never abort: an unsaved Save param only means the
            // preference resets to its default each session, which the generated Save watcher keeps
            // non-destructive. Read-only: look up the merged expression param and log.
            foreach (var mk in markers) {
                string save = mk.persist ? mk.SaveParamOrNull : null;
                if (save == null) continue;
                var ep = eps != null ? eps.FindParameter(save) : null;
                if (ep == null)
                    Debug.LogWarning($"{LogPrefix} Save Param '{save}' on '{mk.name}' is not among the avatar's " +
                        "expression parameters after VRCFury's merge, so the Save preference cannot persist across " +
                        "sessions. Declare it (VRCFury Toggle: \"Use a global parameter\", or add it to your parameters asset).");
                else {
                    if (!ep.saved)
                        Debug.LogWarning($"{LogPrefix} Save Param '{save}' on '{mk.name}' is not marked saved, so the " +
                            "Save preference resets every session. Mark the parameter saved (VRCFury Toggle: check " +
                            "Saved; expression parameters asset: check Saved).");
                    if (ep.networkSynced)
                        Debug.LogWarning($"{LogPrefix} Save Param '{save}' on '{mk.name}' is synced. The Save " +
                            "preference only needs to be local; syncing it spends synced bits for no benefit.");
                }
            }
        }

        static string Describe(VRCExpressionParameters.ValueType type, bool saved, bool synced, float def) =>
            $"{type}, saved={saved}, synced={synced}, default={def}";
    }
}
#endif
