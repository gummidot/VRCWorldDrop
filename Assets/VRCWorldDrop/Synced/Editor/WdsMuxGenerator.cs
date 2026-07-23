// Generates the multi-object synced-drop animator. It produces N independently droppable world-drop
// "slots" that share one small set of synced parameters (the "mux"): the broadcast lanes
// WDM/Lane0..Lane(Lanes-1) plus a turn counter WDM/Idx, instead of paying a full synced pose per
// object. Slots take turns broadcasting: each sends its pose channels over the shared lanes a few at
// a time, then hands the mux to the next slot, around a ring. The measurement cascade and rotation
// decode geometry already live in each WorldDropSynced prefab's contact rig; this file retags every
// instance's contacts to its slot (no geometry is duplicated) and generates the per-slot animator
// layers, parameters and menu on top.
//
// WDM/Idx says whose turn it is and which step of it, packed as slot*MAXSTEP + step. The bare value
// slot*MAXSTEP is the handoff token: no receiver's MuxLatch layer keys on it (only the owning slot's
// WaitTurn state reacts), so landing there passes the mux without latching anything. Send steps are
// token+1..token+S, and every Idx value receivers DO latch on is written by a Send state that writes
// the matching lanes in the same frame, so Idx can never advance ahead of the lane contents it
// describes and a receiver never latches lanes from the wrong step. One lane width (Lanes) serves
// both rotation families; MAXSTEP leaves headroom over the token plus the larger family's step count.
//
// Emit(...) is the entry point: it populates an existing controller + expression params + menu for a
// set of slots (each with an avatar-relative base path). WdsBuildHook calls it at build time against
// the real marker instances.
#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using VRC.SDK3.Avatars.ScriptableObjects;
using VRC.SDK3.Dynamics.Constraint.Components;
using VRC.SDK3.Dynamics.Contact.Components;
using VRC.SDKBase;

namespace VRCWorldDrop.Synced {

    public static class WdsMuxGenerator {
        // Cascade constants must match the WorldDropSynced prefab's baked contact geometry; the decode bias depends on them.
        const float RSuper = 4096f, R = 64f, SenderRadius = 0.01f, FineHalf = 3f, ForwardBoxHalf = 1.25f;
        // The fine stage only ever resolves the coarse-cell residual: the coarse cell is 2R/256 = 0.5m wide, so
        // the true point sits within +-0.25m of the decoded cell center (measured in playmode the fine offset
        // peaks at ~0.24m, ~8% of the prefab's +-FineHalf box, at any range), so its box is ~12x oversized.
        // FineBoxHalf shrinks ONLY the fine box (its receiver size.z) + its decode; super/coarse stay compression-
        // sized by FineHalf. Same 8-bit fine channel over a smaller range = finer precision, zero extra synced bits.
        // Why 0.5m (2x the 0.25m residual) and not a tight ~0.25m fit: the residual is not exactly 0.25m - sender
        // bias (SenderRadius), contact noise, jitter, and coarse-boundary uncertainty stack on top, and if the fine
        // read reaches the box FACE the proximity pins to 0/1 and the position silently clamps (a wrong decode that
        // looks valid). 0.5m keeps the measured 0.24m peak at ~48% of the box (2x headroom); 0.25m would sit at
        // ~96%, on the clip edge. Cost is tiny: Precision = 2*FineBoxHalf/256, so 0.5m -> 0.4cm vs 0.25m -> 0.2cm,
        // a 0.2cm tax to never saturate.
        const float FineBoxHalf = 0.5f;
        // Channels broadcast per step; wider = fewer steps/object = faster reveal, at +8 synced bits/lane.
        // Set per build (by the build hook) from AutoLanes(objectCount, fast), then read by Emit. The
        // build hook sizes it to the object count so a low-count avatar pays a narrower backend; the
        // value lands in [2, 8]. Default 4 here is a safe standalone fallback.
        public static int Lanes = 4;
        // Optional fixed-width override the build hook honors (null = use AutoLanes); it applies
        // LanesOverride ?? AutoLanes(...). Set it to pin the lane width to a specific value.
        public static int? LanesOverride = null;
        public const int MAXSTEP = 10;   // 1 token + max steps. Narrowest L=2 -> full-rot ceil(15/2)=8 steps; ring needs steps < MAXSTEP, 8 < 10 (1 token spare). Idx max at n=16 = 15*10+8 = 158 < 256.
        const float StepDwell = 0.3f;

        static readonly string[] Axes = { "X", "Y", "Z" };
        // Every generated animator/expression parameter starts with this root: the shared mux params
        // are "WDM/..." (MUX) and each slot's params are "WDM<slot>/..." (P). The build hook forces the
        // whole "WDM*" namespace global so VRCFury preserves these names verbatim through its merge -
        // otherwise it uniquifies them with a per-build "VF<n>_" prefix. VRCFury renames the contact
        // receivers together with the controller, so decode would keep working; the cost of a non-global
        // rename is name stability, which resets every saved (persist) drop and breaks host and OSC
        // integrations that reference the generated names.
        public const string ParamRoot = "WDM";
        public const string MUX = ParamRoot + "/";   // shared mux param prefix

        // Per-slot naming. Both derive from ParamRoot so the namespace the build hook forces global
        // (ParamRoot + "*") and the verify hook checks always covers the names emitted here.
        static string P(int slot) => ParamRoot + slot + "/";
        static string CP(int slot) => ParamRoot + slot + "_";

        /// <summary>One world-drop slot: a unique id, rotation family, the avatar-relative path to
        /// its root GameObject (where Container / _Sync live), the menu path for its Show/Drop
        /// toggles ("N" in the path is replaced with the object number; blank falls back to
        /// "WorldDrops/Object N (Synced)"), whether it starts visible (defaultShown), and optional
        /// author-named drive params (dropAlias/showAlias, null when unset; setting either suppresses the
        /// built-in menu and lets a host drive this object's Drop/Show from its own UI).</summary>
        /// <summary>persist: save the drop across sessions - the latched pose channels + a DropSaved
        /// bool become saved LOCAL expression params, and the owner gains a reconstruct-on-load path that
        /// re-broadcasts them. All persist machinery is generated only when set, so a persist=false slot's
        /// output is identical to a build without the feature.</summary>
        /// <summary>saveAlias (persist slots only): optional author-named Save preference param. When set,
        /// a BridgeSave mirror copies it onto the slot's AutoSave, the author's param becomes the saved
        /// store (AutoSave is registered unsaved), and any built-in Save control binds the alias. Unlike
        /// dropAlias/showAlias it does not suppress the built-in menu.</summary>
        public struct Slot { public int id; public bool fullRot; public string basePath; public string menuPath; public bool defaultShown; public bool persist; public string dropAlias; public string showAlias; public string saveAlias; }

        public static int Steps(bool fullRot) => (ChannelCount(fullRot) + Lanes - 1) / Lanes;
        static int ChannelCount(bool fullRot) => fullRot ? 15 : 11;

        // Auto-size the broadcast width to the object count. Few objects reveal fast even on a narrow
        // channel, so a low-count avatar need not pay for the full 4/8-lane backend; the build hook,
        // the marker inspector and the settings inspector all size cost through here so the displayed
        // and shipped backends never drift. Sized against the worst-case (full-rotation, 15-channel)
        // reveal, so a Y-only or mixed avatar only ever reveals faster. Slow picks the narrowest width;
        // Fast is ~2x wider (the reveal-speed dial), capped so a high-count avatar gets the full 4/8 widths.
        //   objects   Slow lanes (backend bits)   Fast lanes (backend bits)
        //    1-2          2 (24)                      4 (40)
        //    3-5          3 (32)                      6 (56)
        //    6-16         4 (40)                      8 (72)
        // Floored at 2 lanes, never 1 (one lane = too many steps/object: slow reveal + more missed-packet exposure).
        public static int AutoLanes(int objectCount, bool fast) {
            int slow = objectCount <= 2 ? 2 : objectCount <= 5 ? 3 : 4;
            return fast ? Math.Min(8, slow * 2) : slow;
        }

        // Synced backend bits for a lane width: L lane ints + the Idx int, 8 bits each. Per-object cost
        // (3 bits: Drop/Show/Live) is added by the caller.
        public static int BackendBits(int lanes) => 8 * lanes + 8;

        // Rough reveal estimate (seconds) for the slowest object when all are dropped at once, for the
        // inspectors. Two effects combine: (a) narrower lanes run more steps per object, so a low
        // object count - which gets narrow auto-lanes - has a HIGHER per-object reveal floor (a 1-2
        // object avatar is 2 lanes / ~8 steps and reveals ~3s, not faster); (b) dropping more at once
        // lengthens the broadcast ring. Take the larger of the one-drop floor (steps * ~0.4s, steps =
        // ceil(channels / lanes) at the worst-case full-rotation channel count) and the all-dropped
        // growth (~0.9 s/object Slow, ~0.45 Fast). Rotation family is ignored (uses the 15-channel
        // worst case, so a Y-only avatar over-estimates slightly, the safe side); a ballpark, not exact.
        public static int EstimateRevealSeconds(int objectCount, bool fast) {
            int steps = Mathf.CeilToInt(15f / AutoLanes(objectCount, fast));
            int oneDropFloor = Mathf.RoundToInt(steps * 0.4f);              // reveal for one drop at this lane width
            int allDroppedGrowth = Mathf.RoundToInt(objectCount * (fast ? 0.45f : 0.9f));
            return Mathf.Max(2, Mathf.Max(oneDropFloor, allDroppedGrowth));
        }

        struct Chan { public string name; public bool isInt; }
        static Chan[] Channels(int slot, bool fullRot) {
            var l = new List<Chan>();
            foreach (var a in Axes) l.Add(new Chan { name = P(slot) + "Super" + a, isInt = true });
            foreach (var a in Axes) l.Add(new Chan { name = P(slot) + "Cell" + a, isInt = true });
            foreach (var a in Axes) l.Add(new Chan { name = P(slot) + "Fine" + a, isInt = false });
            l.Add(new Chan { name = P(slot) + "ForwardX", isInt = false });
            l.Add(new Chan { name = P(slot) + "ForwardZ", isInt = false });
            if (fullRot) {
                l.Add(new Chan { name = P(slot) + "ForwardY", isInt = false });
                foreach (var a in Axes) l.Add(new Chan { name = P(slot) + "Up" + a, isInt = false });
            }
            return l.ToArray();
        }

        // The measurement-rig contact GameObjects (leaf nodes, one contact each), relative to a slot's
        // base path. These paths must match the node names in the shipped WorldDropSynced (*) prefabs;
        // renaming a node there means updating the matching string here. Gated active only during the
        // encode window so idle and already-dropped objects carry no live contacts; contacts left
        // always-on would pile up at the shared world-origin anchor and stop registering (see the Rig
        // layer note in SlotLayers). Frames (CoarseFrame/FineFrame/ForwardFrame/DecodeFrame) are deliberately excluded -
        // they stay live for decode/display on every client.
        static string[] ContactGOs(bool fullRot) {
            var l = new List<string> {
                "Container/_SyncSenders/FineSender", "Container/_SyncSenders/ForwardSender",
                "_Sync/Anchor/SuperSender", "_Sync/Anchor/CoarseSender",
                "_Sync/Anchor/RcvSuperX", "_Sync/Anchor/RcvSuperY", "_Sync/Anchor/RcvSuperZ",
                "_Sync/Anchor/CoarseFrame/RcvCoarseX", "_Sync/Anchor/CoarseFrame/RcvCoarseY", "_Sync/Anchor/CoarseFrame/RcvCoarseZ",
                "_Sync/Anchor/FineFrame/RcvFineX", "_Sync/Anchor/FineFrame/RcvFineY", "_Sync/Anchor/FineFrame/RcvFineZ",
                "_Sync/Anchor/ForwardFrame/RcvForwardX", "_Sync/Anchor/ForwardFrame/RcvForwardZ",
            };
            if (fullRot) {
                l.Add("Container/_SyncSenders/UpSender");
                l.Add("_Sync/Anchor/ForwardFrame/RcvForwardY");
                l.Add("_Sync/Anchor/ForwardFrame/RcvUpX"); l.Add("_Sync/Anchor/ForwardFrame/RcvUpY"); l.Add("_Sync/Anchor/ForwardFrame/RcvUpZ");
            }
            return l.ToArray();
        }

        // ============================ reusable core ============================

        /// <summary>Populate an existing controller + expression params + menu for these slots.
        /// Clip/submenu assets are written under <paramref name="assetDir"/>. Called by the
        /// build-time hook (WdsBuildHook).</summary>
        public static void Emit(AnimatorController ctrl, VRCExpressionParameters prms, VRCExpressionsMenu menu,
                                Slot[] slots, string assetDir, StringBuilder log) {
            var cl = BuildClips(slots, assetDir, log);
            EmitController(ctrl, slots, cl, log);
            if (prms != null) EmitParams(prms, slots);
            if (menu != null) EmitMenu(menu, slots, assetDir);
        }

        /// <summary>Rewrite a slot instance's placeholder contact tags + receiver params to its
        /// unique slot id. Geometry is otherwise untouched.</summary>
        public static void RetagSlot(GameObject slotRoot, int slotId, string srcParamPrefix, string srcTag) {
            foreach (var s in slotRoot.GetComponentsInChildren<VRCContactSender>(true))
                s.collisionTags = s.collisionTags.Select(t => t.Replace(srcTag, CP(slotId))).ToList();
            foreach (var r in slotRoot.GetComponentsInChildren<VRCContactReceiver>(true)) {
                r.collisionTags = r.collisionTags.Select(t => t.Replace(srcTag, CP(slotId))).ToList();
                // Fine receivers (RawFX/Y/Z, not RawFwd*) measure the per-axis residual via FACE proximity along
                // the box's local +Z. The prefab fine boxes are 6m cubes whose size.z encodes the +-FineHalf
                // range; shrink ONLY size.z to +-FineBoxHalf so the same 8-bit fine channel resolves a smaller
                // range = finer precision. size.x/y (lateral) stay 6m and amply cover the ~0.25m lateral residual.
                // Super/coarse receivers (RawS*/RawC*) are untouched. NB: a Box shape is sized by `size` (Vector3);
                // `radius`/`height` are sphere/capsule fields and have no effect on a box.
                bool isFine = !string.IsNullOrEmpty(r.parameter) && r.parameter.StartsWith(srcParamPrefix + "RawF") && !r.parameter.Contains("Fwd");
                if (!string.IsNullOrEmpty(r.parameter) && r.parameter.StartsWith(srcParamPrefix))
                    r.parameter = P(slotId) + r.parameter.Substring(srcParamPrefix.Length);
                if (isFine) { var sz = r.size; sz.z *= FineBoxHalf / FineHalf; r.size = sz; }
            }
        }

        // ============================ clips ============================

        class SlotClips {
            public AnimationClip Buffer1F, Buffer005, Buffer03;
            public AnimationClip LocalIdle, LocalDropped, RemoteIdle, RemoteDropped, RemoteFrozen, ShowOn, ShowOff;
            public AnimationClip RestoreDwell;   // persist only: decode-follow display with a real dwell length
            public AnimationClip RigOn, RigOff;   // gate the measurement contacts to the encode window
            public AnimationClip[] SuperMin = new AnimationClip[3], SuperMax = new AnimationClip[3];
            public AnimationClip[] CellMin = new AnimationClip[3], CellMax = new AnimationClip[3];
            public AnimationClip[] FineMin = new AnimationClip[3], FineMax = new AnimationClip[3];
            public AnimationClip ForwardXMin, ForwardXMax, ForwardZMin, ForwardZMax, ForwardYMin, ForwardYMax;
            public AnimationClip[] UpMin = new AnimationClip[3], UpMax = new AnimationClip[3];
        }

        static readonly List<AnimationClip> Pending = new List<AnimationClip>();
        static AnimationClip NewClip(string name) { var c = new AnimationClip { name = name }; Pending.Add(c); return c; }
        static void AddCurve(AnimationClip clip, string path, Type type, string attr, float value, float len) {
            AnimationUtility.SetEditorCurve(clip, EditorCurveBinding.FloatCurve(path, type, attr), new AnimationCurve(new Keyframe(0, value), new Keyframe(len, value)));
        }

        static SlotClips[] BuildClips(Slot[] slots, string dir, StringBuilder log) {
            Pending.Clear();
            var all = new SlotClips[slots.Length];
            float superBias = SenderRadius * RSuper / FineHalf;
            const float fr = 1f / 60f;
            for (int si = 0; si < slots.Length; si++) {
                var slot = slots[si];
                string B = slot.basePath, cp = CP(slot.id);
                var c = new SlotClips();
                // Keepalive is an always-on dummy node; setting m_IsActive=1 is a no-op whose only purpose is to
                // give the clip a real length. The timing states dwell via exitTime, which needs a non-zero-length clip.
                AnimationClip Buffer(string name, float len) { var clip = NewClip(name); AddCurve(clip, B + "/_Sync/Keepalive", typeof(GameObject), "m_IsActive", 1, len); return clip; }
                c.Buffer1F = Buffer(cp + "Buffer1F", fr);
                c.Buffer005 = Buffer(cp + "Buffer005", 0.05f);
                c.Buffer03 = Buffer(cp + "Buffer03", StepDwell);

                AnimationClip Display(string name, float freeze, float w0, float w1, float len) {
                    var clip = NewClip(name);
                    AddCurve(clip, B + "/Container", typeof(VRCParentConstraint), "FreezeToWorld", freeze, len);
                    AddCurve(clip, B + "/Container", typeof(VRCParentConstraint), "Sources.source0.Weight", w0, len);
                    AddCurve(clip, B + "/Container", typeof(VRCParentConstraint), "Sources.source1.Weight", w1, len);
                    return clip;
                }
                c.LocalIdle = Display(cp + "LocalIdle", 0, 1, 0, fr);
                c.LocalDropped = Display(cp + "LocalDropped", 1, 1, 0, fr);
                c.RemoteIdle = Display(cp + "RemoteIdle", 0, 1, 0, fr);
                // Remote reveal = follow the live reconstruction (RemoteDropped, source1), then once it has
                // converged freeze it (RemoteFrozen, FreezeToWorld=1) into a static capture like the owner's
                // LocalDropped. The "converged" gate is a Seen count in the Display layer (see SlotLayers), not a
                // clip dwell: on redrop the reconstruction must re-converge to the new pose before the freeze
                // re-captures it, otherwise the freeze latches the previous (stale) DecodeFrame and gates itself.
                c.RemoteDropped = Display(cp + "RemoteDropped", 0, 0, 1, fr);
                c.RemoteFrozen = Display(cp + "RemoteFrozen", 1, 0, 1, fr);
                // Persist: the owner's restore states dwell on the decode-follow pose before freezing, and an
                // exitTime dwell needs a clip with real length (the 1-frame RemoteDropped would exit ~instantly).
                if (slot.persist) c.RestoreDwell = Display(cp + "RestoreDwell", 0, 0, 1, StepDwell);
                c.ShowOn = NewClip(cp + "ShowOn"); AddCurve(c.ShowOn, B + "/Container/Item", typeof(GameObject), "m_IsActive", 1, fr);
                c.ShowOff = NewClip(cp + "ShowOff"); AddCurve(c.ShowOff, B + "/Container/Item", typeof(GameObject), "m_IsActive", 0, fr);
                c.RigOn = NewClip(cp + "RigOn"); c.RigOff = NewClip(cp + "RigOff");
                foreach (var go in ContactGOs(slot.fullRot)) {
                    AddCurve(c.RigOn, B + "/" + go, typeof(GameObject), "m_IsActive", 1, fr);
                    AddCurve(c.RigOff, B + "/" + go, typeof(GameObject), "m_IsActive", 0, fr);
                }

                for (int a = 0; a < 3; a++) {
                    string ax = Axes[a].ToLower();
                    c.SuperMin[a] = NewClip(cp + "Super" + Axes[a] + "Min");
                    c.SuperMax[a] = NewClip(cp + "Super" + Axes[a] + "Max");
                    foreach (var tgt in new[] { B + "/_Sync/Anchor/CoarseFrame", B + "/_Sync/Anchor/FineFrame", B + "/_Sync/Anchor/DecodeFrame" }) {
                        AddCurve(c.SuperMin[a], tgt, typeof(VRCPositionConstraint), "PositionOffset." + ax, -RSuper - superBias, fr);
                        AddCurve(c.SuperMax[a], tgt, typeof(VRCPositionConstraint), "PositionOffset." + ax, RSuper - superBias, fr);
                    }
                    c.CellMin[a] = NewClip(cp + "Cell" + Axes[a] + "Min");
                    AddCurve(c.CellMin[a], B + "/_Sync/Anchor/FineFrame", typeof(VRCPositionConstraint), "PositionOffset." + ax, -R, fr);
                    AddCurve(c.CellMin[a], B + "/_Sync/Anchor/DecodeFrame", typeof(VRCPositionConstraint), "PositionOffset." + ax, -R, fr);
                    c.CellMax[a] = NewClip(cp + "Cell" + Axes[a] + "Max");
                    AddCurve(c.CellMax[a], B + "/_Sync/Anchor/FineFrame", typeof(VRCPositionConstraint), "PositionOffset." + ax, R, fr);
                    AddCurve(c.CellMax[a], B + "/_Sync/Anchor/DecodeFrame", typeof(VRCPositionConstraint), "PositionOffset." + ax, R, fr);
                    c.FineMin[a] = NewClip(cp + "Fine" + Axes[a] + "Min");
                    AddCurve(c.FineMin[a], B + "/_Sync/Anchor/DecodeFrame", typeof(VRCPositionConstraint), "PositionOffset." + ax, -FineBoxHalf - SenderRadius, fr);
                    c.FineMax[a] = NewClip(cp + "Fine" + Axes[a] + "Max");
                    AddCurve(c.FineMax[a], B + "/_Sync/Anchor/DecodeFrame", typeof(VRCPositionConstraint), "PositionOffset." + ax, FineBoxHalf - SenderRadius, fr);
                }
                AnimationClip OffsetClip(string name, string target, string ax, float value) {
                    var clip = NewClip(name);
                    AddCurve(clip, B + "/_Sync/Anchor/DecodeFrame/" + target, typeof(VRCPositionConstraint), "PositionOffset." + ax, value, fr);
                    return clip;
                }
                c.ForwardXMin = OffsetClip(cp + "ForwardXMin", "ForwardTarget", "x", -ForwardBoxHalf - SenderRadius);
                c.ForwardXMax = OffsetClip(cp + "ForwardXMax", "ForwardTarget", "x", ForwardBoxHalf - SenderRadius);
                c.ForwardZMin = OffsetClip(cp + "ForwardZMin", "ForwardTarget", "z", -ForwardBoxHalf - SenderRadius);
                c.ForwardZMax = OffsetClip(cp + "ForwardZMax", "ForwardTarget", "z", ForwardBoxHalf - SenderRadius);
                if (slot.fullRot) {
                    c.ForwardYMin = OffsetClip(cp + "ForwardYMin", "ForwardTarget", "y", -ForwardBoxHalf - SenderRadius);
                    c.ForwardYMax = OffsetClip(cp + "ForwardYMax", "ForwardTarget", "y", ForwardBoxHalf - SenderRadius);
                    for (int a = 0; a < 3; a++) {
                        string ax = Axes[a].ToLower();
                        c.UpMin[a] = OffsetClip(cp + "Up" + Axes[a] + "Min", "UpTarget", ax, -ForwardBoxHalf - SenderRadius);
                        c.UpMax[a] = OffsetClip(cp + "Up" + Axes[a] + "Max", "UpTarget", ax, ForwardBoxHalf - SenderRadius);
                    }
                }
                all[si] = c;
            }
            foreach (var clip in Pending) AssetDatabase.CreateAsset(clip, dir + "/" + clip.name + ".anim");
            Pending.Clear();
            log.AppendLine("Clips built (" + slots.Length + " slots)");
            return all;
        }

        // ============================ animator helpers ============================

        static VRC_AvatarParameterDriver.Parameter Copy(string src, string dst, bool conv = false, float sMin = 0, float sMax = 0, float dMin = 0, float dMax = 0) =>
            new VRC_AvatarParameterDriver.Parameter { type = VRC_AvatarParameterDriver.ChangeType.Copy, source = src, name = dst, convertRange = conv, sourceMin = sMin, sourceMax = sMax, destMin = dMin, destMax = dMax };
        static VRC_AvatarParameterDriver.Parameter Set(string dst, float v) => new VRC_AvatarParameterDriver.Parameter { type = VRC_AvatarParameterDriver.ChangeType.Set, name = dst, value = v };
        static VRC_AvatarParameterDriver.Parameter AddTo(string dst, float v) => new VRC_AvatarParameterDriver.Parameter { type = VRC_AvatarParameterDriver.ChangeType.Add, name = dst, value = v };

        static void EnsureBool(AnimatorController c, string n, bool def = false) { if (!c.parameters.Any(p => p.name == n)) c.AddParameter(new AnimatorControllerParameter { name = n, type = AnimatorControllerParameterType.Bool, defaultBool = def }); }
        static void EnsureInt(AnimatorController c, string n) { if (!c.parameters.Any(p => p.name == n)) c.AddParameter(new AnimatorControllerParameter { name = n, type = AnimatorControllerParameterType.Int, defaultInt = 0 }); }
        static void EnsureFloat(AnimatorController c, string n, float d = 0) { if (!c.parameters.Any(p => p.name == n)) c.AddParameter(new AnimatorControllerParameter { name = n, type = AnimatorControllerParameterType.Float, defaultFloat = d }); }

        static void AddDriver(AnimatorController ctrl, AnimatorState state, bool localOnly, params VRC_AvatarParameterDriver.Parameter[] ps) {
            var d = ScriptableObject.CreateInstance<VRC.SDK3.Avatars.Components.VRCAvatarParameterDriver>();
            d.name = "WdsMuxDriver"; d.hideFlags = HideFlags.HideInHierarchy;
            AssetDatabase.AddObjectToAsset(d, ctrl);
            d.localOnly = localOnly; d.parameters = ps.ToList();
            state.behaviours = state.behaviours.Concat(new StateMachineBehaviour[] { d }).ToArray();
        }
        static AnimatorState St(AnimatorStateMachine sm, string name, Motion m, Vector3 pos) { var s = sm.AddState(name, pos); s.writeDefaultValues = true; s.motion = m; return s; }
        static AnimatorStateTransition ExitT(AnimatorState from, AnimatorState to) { var t = from.AddTransition(to); t.hasExitTime = true; t.exitTime = 1f; t.duration = 0; t.hasFixedDuration = true; return t; }
        static void Cond(AnimatorStateTransition t, params AnimatorCondition[] cs) { t.hasExitTime = false; t.duration = 0; t.hasFixedDuration = true; t.conditions = cs; }
        static AnimatorStateTransition Any(AnimatorStateMachine sm, AnimatorState to, params AnimatorCondition[] cs) { var t = sm.AddAnyStateTransition(to); t.hasExitTime = false; t.duration = 0; t.hasFixedDuration = true; t.canTransitionToSelf = false; t.conditions = cs; return t; }
        static AnimatorCondition If(string p) => new AnimatorCondition { mode = AnimatorConditionMode.If, parameter = p };
        static AnimatorCondition IfNot(string p) => new AnimatorCondition { mode = AnimatorConditionMode.IfNot, parameter = p };
        static AnimatorCondition Eq(string p, float v) => new AnimatorCondition { mode = AnimatorConditionMode.Equals, parameter = p, threshold = v };
        static AnimatorCondition Gt(string p, float v) => new AnimatorCondition { mode = AnimatorConditionMode.Greater, parameter = p, threshold = v };
        static AnimatorCondition Lt(string p, float v) => new AnimatorCondition { mode = AnimatorConditionMode.Less, parameter = p, threshold = v };

        static BlendTree Tree1D(AnimatorController ctrl, string name, string bp, Motion lo, Motion hi, float tLo, float tHi) {
            var bt = new BlendTree { name = name, blendType = BlendTreeType.Simple1D, blendParameter = bp, useAutomaticThresholds = false, hideFlags = HideFlags.HideInHierarchy };
            AssetDatabase.AddObjectToAsset(bt, ctrl);
            bt.children = new[] { new ChildMotion { motion = lo, threshold = tLo, timeScale = 1 }, new ChildMotion { motion = hi, threshold = tHi, timeScale = 1 } };
            return bt;
        }

        // ============================ controller ============================

        static void EmitController(AnimatorController ctrl, Slot[] slots, SlotClips[] cl, StringBuilder log) {
            int n = slots.Length;
            var added = new HashSet<string>();

            // shared params
            EnsureBool(ctrl, "IsLocal"); EnsureBool(ctrl, "IsAnimatorEnabled", true); EnsureFloat(ctrl, MUX + "One", 1f);
            for (int s = 0; s < Lanes; s++) EnsureInt(ctrl, MUX + "Lane" + s);   // declare every lane the width uses
            EnsureInt(ctrl, MUX + "Idx"); EnsureBool(ctrl, MUX + "AnyDrop");
            foreach (var slot in slots) {
                string p = P(slot.id);
                EnsureBool(ctrl, p + "Drop"); EnsureBool(ctrl, p + "Show", slot.defaultShown); EnsureBool(ctrl, p + "Live"); EnsureInt(ctrl, p + "Seen"); EnsureBool(ctrl, p + "RigOn"); EnsureBool(ctrl, p + "Frozen");
                // Persist: DropSaved is the internal saved "a drop is stored / arm restore" flag; AutoSave is
                // the sticky user preference (saved, menu-driven) that gates whether a drop saves - its animator
                // default MUST be true so an init-skew frame cannot fire the forget watcher and wipe a legit
                // saved drop before restore reads it. Latched marks "the cascade ran this session" (animator-only).
                if (slot.persist) { EnsureBool(ctrl, p + "DropSaved"); EnsureBool(ctrl, p + "Latched"); EnsureBool(ctrl, p + "AutoSave", true); }
                // Author-named drive params (local; the bridge layer mirrors them onto WDM<slot>/Drop|Show on the owner).
                if (slot.dropAlias != null) EnsureBool(ctrl, slot.dropAlias);
                if (slot.showAlias != null) EnsureBool(ctrl, slot.showAlias, slot.defaultShown);
                // Save alias default ON is best-effort only: the host's own declaration usually wins the
                // merged animator default, so the Save watcher's Live/Latched gate (below) is what makes a
                // hostile default non-destructive, not this.
                if (slot.saveAlias != null) EnsureBool(ctrl, slot.saveAlias, true);
                foreach (var a in Axes) { EnsureInt(ctrl, p + "Super" + a); EnsureFloat(ctrl, p + "Super" + a + "F"); EnsureInt(ctrl, p + "Cell" + a); EnsureFloat(ctrl, p + "Cell" + a + "F"); EnsureFloat(ctrl, p + "Fine" + a); EnsureFloat(ctrl, p + "RawS" + a); EnsureFloat(ctrl, p + "RawC" + a); EnsureFloat(ctrl, p + "RawF" + a); }
                EnsureFloat(ctrl, p + "ForwardX"); EnsureFloat(ctrl, p + "ForwardZ"); EnsureFloat(ctrl, p + "RawFwdX"); EnsureFloat(ctrl, p + "RawFwdZ");
                if (slot.fullRot) { EnsureFloat(ctrl, p + "ForwardY"); EnsureFloat(ctrl, p + "RawFwdY"); foreach (var a in Axes) { EnsureFloat(ctrl, p + "Up" + a); EnsureFloat(ctrl, p + "RawU" + a); } }
            }

            // AnyDrop OR layer (one per controller; suffix-less name is unique enough for our appends)
            ctrl.AddLayer("WDM_AnyDrop"); added.Add("WDM_AnyDrop");
            {
                var sm = ctrl.layers[ctrl.layers.Length - 1].stateMachine;
                var off = St(sm, "Off", null, new Vector3(200, 0, 0));
                var on = St(sm, "On", null, new Vector3(200, 100, 0));
                sm.defaultState = off;
                AddDriver(ctrl, off, false, Set(MUX + "AnyDrop", 0));
                AddDriver(ctrl, on, false, Set(MUX + "AnyDrop", 1));
                foreach (var slot in slots) Any(sm, on, If(P(slot.id) + "Drop"));
                // Persist: a restored slot broadcasts with Drop=0, and the ring's idle-slot Skip states only
                // advance the token while AnyDrop is set - without this the ring stalls at n>1 and a restored
                // drop never reaches Live/remotes. DropSaved counts as "dropped" for the ring.
                foreach (var slot in slots) if (slot.persist) Any(sm, on, If(P(slot.id) + "DropSaved"));
                Cond(on.AddTransition(off), slots.Select(s => IfNot(P(s.id) + "Drop")).Concat(slots.Where(s => s.persist).Select(s => IfNot(P(s.id) + "DropSaved"))).ToArray());
            }

            for (int si = 0; si < n; si++) { SlotLayers(ctrl, slots[si], n, cl[si], added); }

            // Re-sync after an animator un-cull (remote only). VRChat sets IsAnimatorEnabled false on the
            // last frame it evaluates before disabling a hidden avatar's animator, and true once it
            // re-enables. Arming is a plain state TRANSITION on that final frame: a transition commits
            // within the frame it fires, while a parameter-driver write can be applied after evaluation
            // and is lost when no evaluated frame follows. So the reset itself runs on the first
            // re-enabled frame instead, when evaluation is guaranteed to continue and the write always
            // lands. It clears each slot's Seen AND Frozen: Seen < steps re-arms the reveal, and clearing
            // Frozen re-enables the Decode and lets the Display re-capture. Frozen must be cleared here,
            // not left to the RemoteIdle cascade: after a redrop while this client was paused, RemoteIdle
            // does not re-fire, so a still-latched Frozen would gate the decode off and pin the object to
            // its rest pose. The object then re-reveals from the live broadcast, re-converging on whatever
            // pose was dropped while this client had the animator paused. The cost is a hidden re-reveal
            // (a few seconds) after every un-cull, drop moved or not. A client that predates
            // IsAnimatorEnabled never sees it false (declared default true) and keeps the old behavior.
            ctrl.AddLayer("WDM_UncullResync"); added.Add("WDM_UncullResync");
            {
                var sm = ctrl.layers[ctrl.layers.Length - 1].stateMachine;
                var noop = St(sm, "NoOp", cl[0].Buffer1F, new Vector3(200, 0, 0));
                var armed = St(sm, "Armed", cl[0].Buffer1F, new Vector3(200, 100, 0));
                var reset = St(sm, "Reset", cl[0].Buffer1F, new Vector3(200, 200, 0));
                sm.defaultState = noop;
                AddDriver(ctrl, reset, false, slots.SelectMany(s => new[] { Set(P(s.id) + "Seen", 0), Set(P(s.id) + "Frozen", 0) }).ToArray());
                Cond(noop.AddTransition(armed), IfNot("IsLocal"), IfNot("IsAnimatorEnabled"));
                Cond(armed.AddTransition(reset), If("IsAnimatorEnabled"));
                ExitT(reset, noop);
            }

            var layers = ctrl.layers;
            for (int k = 0; k < layers.Length; k++) if (added.Contains(layers[k].name)) layers[k].defaultWeight = 1f;
            ctrl.layers = layers;
            EditorUtility.SetDirty(ctrl);
            log.AppendLine("Controller layers added: " + added.Count);
            AuditConditionParams(ctrl, added, log);
        }

        // Every condition we emit must reference a parameter we declared on this controller. A condition on
        // an undeclared parameter is not an error Unity reports: after VRCFury's merge its RemoveWrongParamTypes
        // replaces it with an always-false condition, so the layer sits inert forever. Fail loudly at build
        // instead. Condition MODES are deliberately NOT checked: this controller is handed to VRCFury before
        // its merge, which coerces every condition to its parameter's final merged type (so IsLocal floated by
        // a local-only action, or a host drive param that ends up an int/float, has its bool-mode conditions
        // rewritten automatically during UpgradeWrongParamTypes).
        static void AuditConditionParams(AnimatorController ctrl, HashSet<string> added, StringBuilder log) {
            var names = new HashSet<string>(ctrl.parameters.Select(p => p.name));
            foreach (var layer in ctrl.layers.Where(l => added.Contains(l.name))) {
                foreach (var sm in AllStateMachines(layer.stateMachine)) {
                    var transitions = sm.anyStateTransitions.Cast<AnimatorTransitionBase>()
                        .Concat(sm.states.SelectMany(s => s.state.transitions.Cast<AnimatorTransitionBase>()));
                    foreach (var t in transitions)
                        foreach (var cond in t.conditions)
                            if (!names.Contains(cond.parameter)) {
                                string msg = "[WorldDropSynced] something went wrong setting up the synced drops on this avatar, " +
                                    "so one of them may not work in-game. Please report it and include this line: layer '" +
                                    layer.name + "', parameter '" + cond.parameter + "', condition " + cond.mode + ".";
                                log.AppendLine(msg);
                                Debug.LogError(msg);
                            }
                }
            }
        }

        static IEnumerable<AnimatorStateMachine> AllStateMachines(AnimatorStateMachine sm) {
            yield return sm;
            foreach (var child in sm.stateMachines)
                foreach (var nested in AllStateMachines(child.stateMachine))
                    yield return nested;
        }

        static void SlotLayers(AnimatorController ctrl, Slot slot, int n, SlotClips c, HashSet<string> added) {
            int i = slot.id; bool fullRot = slot.fullRot; bool persist = slot.persist; int steps = Steps(fullRot);
            string p = P(i), cp = CP(i);
            // Slot ids are dense 0..n-1, so the next slot's base is just (i+1)%n.
            int baseIdx = i * MAXSTEP, next = ((i + 1) % n) * MAXSTEP;
            var chans = Channels(i, fullRot);

            void AddLayer(string name) { ctrl.AddLayer(name); added.Add(name); }

            // Mirror: int cells/supers -> float for the Decode blend trees. Gated to the reconstruction window
            // (Drop && !Frozen, same gate as Decode below) instead of always-on. The driver-set *F params persist
            // when gated off (Write Defaults does not revert driver writes), so a frozen object's last decoded
            // floats are held - harmless, since Decode is gated off too and nothing reads them while frozen.
            AddLayer(cp + "Mirror");
            {
                var sm = ctrl.layers[ctrl.layers.Length - 1].stateMachine;
                var off = St(sm, "Off", null, new Vector3(0, 0, 0));
                var m0 = St(sm, "M0", c.Buffer1F, new Vector3(200, 0, 0));
                var m1 = St(sm, "M1", c.Buffer1F, new Vector3(200, 80, 0));
                sm.defaultState = off;
                Func<VRC_AvatarParameterDriver.Parameter[]> copies = () => Axes.Select(a => Copy(p + "Super" + a, p + "Super" + a + "F")).Concat(Axes.Select(a => Copy(p + "Cell" + a, p + "Cell" + a + "F"))).ToArray();
                AddDriver(ctrl, m0, false, copies()); AddDriver(ctrl, m1, false, copies());
                ExitT(m0, m1); ExitT(m1, m0);
                Cond(off.AddTransition(m0), If(p + "Drop"), IfNot(p + "Frozen"));
                if (persist) {
                    // A restored drop decodes with Drop=0: on a remote the signal is Live (the owner's restored
                    // broadcast), on the owner it is DropSaved (available immediately, BEFORE Live - the ring
                    // takes a cycle to set Live, and the owner's restore display must not freeze an un-decoded
                    // pose while waiting for it).
                    Cond(off.AddTransition(m0), If(p + "Live"), IfNot(p + "Frozen"));
                    Cond(off.AddTransition(m0), If(p + "DropSaved"), IfNot(p + "Frozen"));
                    Any(sm, off, IfNot(p + "Drop"), IfNot(p + "Live"), IfNot(p + "DropSaved"));
                } else {
                    Any(sm, off, IfNot(p + "Drop"));
                }
                Any(sm, off, If(p + "Frozen"));
            }

            // Encode (owner): latch + cross-slot broadcast ring
            AddLayer(cp + "Encode");
            {
                var sm = ctrl.layers[ctrl.layers.Length - 1].stateMachine;
                var idle = St(sm, "Idle", c.Buffer1F, new Vector3(200, 0, 0));
                var ss = St(sm, "SettleSuper", c.Buffer03, new Vector3(200, 100, 0));
                var lsup = St(sm, "LatchSuper", c.Buffer005, new Vector3(200, 200, 0));
                var sc = St(sm, "SettleCoarse", c.Buffer03, new Vector3(200, 300, 0));
                var lc = St(sm, "LatchCoarse", c.Buffer005, new Vector3(200, 400, 0));
                var sf = St(sm, "SettleFine", c.Buffer03, new Vector3(200, 500, 0));
                var lf = St(sm, "LatchFine", c.Buffer005, new Vector3(200, 600, 0));
                var wait = St(sm, "WaitTurn", c.Buffer1F, new Vector3(450, 0, 0));
                var skip = St(sm, "Skip", c.Buffer1F, new Vector3(-50, 0, 0));
                var handoff = St(sm, "Handoff", c.Buffer1F, new Vector3(700, 0, 0));
                sm.defaultState = idle;

                AddDriver(ctrl, idle, true, Set(p + "Live", 0), Set(p + "RigOn", 0));
                // Persist: Latched is set on CASCADE ENTRY (not at latch completion) - the redrop-from-restore
                // AnyState below keys on !Latched, and setting it any later would let that AnyState re-fire from
                // every subsequent cascade state (canTransitionToSelf only blocks self), looping the cascade
                // forever. DropSaved (the internal "a drop is stored" flag) is CLEARED here at cascade entry
                // and re-set at LatchFine as Copy(AutoSave -> DropSaved) - so mid-cascade DropSaved=0 (quitting in
                // the ~1s cascade window then leaves no torn half-overwritten pose to restore), and the completed
                // drop is saved only if the AutoSave preference is on. Idle deliberately does NOT touch
                // DropSaved: its entry driver fires at session init, before the restore path could read the saved
                // value. WaitTurn does not write DropSaved: it re-enters every ring cycle, so the set belongs
                // on the once-per-drop LatchFine instead. Full clear on a real undrop still happens in the Clear state below.
                // Set(Live,0) on cascade entry: redundant after Idle (which already zeroed it), but a REDROP
                // from a restored broadcast enters here directly without passing Idle - the Live dip is what
                // sends remotes back to RemoteIdle (clear Frozen, reset Seen) so they re-reveal the NEW pose
                // instead of staying frozen on the old one.
                if (persist) AddDriver(ctrl, ss, true, Set(p + "RigOn", 1), Set(p + "Latched", 1), Set(p + "Live", 0), Set(p + "DropSaved", 0));
                else AddDriver(ctrl, ss, true, Set(p + "RigOn", 1));    // contacts on for the settle/latch cascade
                AddDriver(ctrl, wait, true, Set(p + "RigOn", 0));  // pose latched; contacts off (broadcast reads latched params)
                AddDriver(ctrl, lsup, true, Axes.Select(a => Copy(p + "RawS" + a, p + "Super" + a, true, 0, 1, 0, 255)).ToArray());
                AddDriver(ctrl, lc, true, Axes.Select(a => Copy(p + "RawC" + a, p + "Cell" + a, true, 0, 1, 0, 255)).ToArray());
                var fine = Axes.Select(a => Copy(p + "RawF" + a, p + "Fine" + a, true, 0, 1, -1, 1)).Concat(new[] { Copy(p + "RawFwdX", p + "ForwardX", true, 0, 1, -1, 1), Copy(p + "RawFwdZ", p + "ForwardZ", true, 0, 1, -1, 1) });
                if (fullRot) fine = fine.Append(Copy(p + "RawFwdY", p + "ForwardY", true, 0, 1, -1, 1)).Concat(Axes.Select(a => Copy(p + "RawU" + a, p + "Up" + a, true, 0, 1, -1, 1)));
                // Persist: commit the save as the LAST op - every channel above is written first, then DropSaved is
                // set from the AutoSave preference within this one atomic driver evaluation (torn-free). AutoSave
                // off -> Copy writes 0 -> the drop is placed this session but not saved for the next.
                var lfOps = fine.ToList();
                if (persist) lfOps.Add(Copy(p + "AutoSave", p + "DropSaved"));
                AddDriver(ctrl, lf, true, lfOps.ToArray());
                AddDriver(ctrl, skip, true, Set(MUX + "Idx", next));
                AddDriver(ctrl, handoff, true, Set(MUX + "Idx", next));

                Cond(idle.AddTransition(ss), If("IsLocal"), If(p + "Drop"));
                Cond(idle.AddTransition(skip), If("IsLocal"), If(MUX + "AnyDrop"), IfNot(p + "Drop"), Gt(MUX + "Idx", baseIdx - 1), Lt(MUX + "Idx", baseIdx + MAXSTEP));
                ExitT(skip, idle);
                ExitT(ss, lsup); ExitT(lsup, sc); ExitT(sc, lc); ExitT(lc, sf); ExitT(sf, lf); ExitT(lf, wait);

                AnimatorState prev = wait, send0 = null;
                for (int k = 0; k < steps; k++) {
                    var send = St(sm, "Send" + k, c.Buffer03, new Vector3(450, 100 + k * 70, 0));
                    if (k == 0) send0 = send;
                    var ops = new List<VRC_AvatarParameterDriver.Parameter>();
                    for (int s = 0; s < Lanes; s++) { int ci = k * Lanes + s; if (ci >= chans.Length) break; ops.Add(chans[ci].isInt ? Copy(chans[ci].name, MUX + "Lane" + s) : Copy(chans[ci].name, MUX + "Lane" + s, true, -1, 1, 0, 255)); }
                    ops.Add(Set(MUX + "Idx", baseIdx + 1 + k));
                    if (k == steps - 1) ops.Add(Set(p + "Live", 1));
                    AddDriver(ctrl, send, true, ops.ToArray());
                    if (k > 0) ExitT(prev, send);
                    prev = send;
                }
                // Claim the mux for ANY Idx in this slot's range, not just the exact token. The token
                // can freeze mid-range: undropping a slot mid-broadcast yanks Encode to idle (AnyState
                // below) leaving Idx at a Send value (base+1+k); if every slot then undrops, AnyDrop=0
                // so no idle->skip can advance it, and Idx stays frozen there. Re-dropping that slot
                // returns it to `wait`, where an exact `Eq(Idx,base)` would never match the frozen
                // value: a permanent stall where Live never re-arms and remotes never re-reveal.
                // A range match re-broadcasts cleanly from Send0 (which resets
                // Idx=base+1) regardless of where it froze; ranges are disjoint (base_j..base_j+MAXSTEP)
                // so this only ever fires on this slot's turn. Together with the undropped-slot skips
                // the ring self-heals from any frozen Idx.
                Cond(wait.AddTransition(send0), Gt(MUX + "Idx", baseIdx - 1), Lt(MUX + "Idx", baseIdx + MAXSTEP));
                ExitT(prev, handoff); ExitT(handoff, wait);
                if (persist) {
                    // Restored broadcast: the saved channels ARE the pose, so enter the ring directly - never
                    // re-measure on restore (re-latching would re-encode with fresh contact noise every session,
                    // a random-walk drift, and would race the display's own convergence).
                    Cond(idle.AddTransition(wait), If("IsLocal"), IfNot(p + "Drop"), If(p + "DropSaved"));
                    // Redrop while broadcasting restored channels: the ring would keep sending the OLD pose
                    // (nothing re-latches), so route back through the cascade. Only fires when this session
                    // never latched (restored broadcast); a normal drop sets Latched at SettleSuper entry.
                    Any(sm, ss, If("IsLocal"), If(p + "Drop"), IfNot(p + "Latched"));
                    // Undrop after a real drop this session clears the save; "Saved" toggled off during a
                    // restored broadcast (never latched) just stops it. Order matters: Clear first - a
                    // mid-cascade undrop can have Latched=1 with DropSaved still 0/1, and Clear also resets
                    // Live/RigOn/Latched before handing back to Idle.
                    var clear = St(sm, "Clear", c.Buffer005, new Vector3(-50, 100, 0));
                    AddDriver(ctrl, clear, true, Set(p + "DropSaved", 0), Set(p + "Live", 0), Set(p + "RigOn", 0), Set(p + "Latched", 0));
                    ExitT(clear, idle);
                    Any(sm, clear, If("IsLocal"), IfNot(p + "Drop"), If(p + "Latched"));
                    Any(sm, idle, If("IsLocal"), IfNot(p + "Drop"), IfNot(p + "DropSaved"));
                } else {
                    Any(sm, idle, If("IsLocal"), IfNot(p + "Drop"));
                }
            }

            // Save (owner-only watcher; persist only): keep DropSaved (the internal "a drop is stored" flag) in
            // sync with the sticky AutoSave preference WITHOUT touching Encode/Display - an AnyState there would
            // yank the broadcast ring or the display state. Watch is the default and carries NO driver: its entry
            // fires at session init, before VRChat applies the saved expression values, so a driver there would
            // wipe a stored drop before the restore path reads it. Both edges self-disarm
            // (their driver clears the condition that triggered them) and are mutually exclusive on AutoSave, so no flicker.
            if (persist) {
                AddLayer(cp + "Save");
                var sm = ctrl.layers[ctrl.layers.Length - 1].stateMachine;
                var watch = St(sm, "Watch", c.Buffer1F, new Vector3(200, 0, 0));
                var forget = St(sm, "Forget", c.Buffer005, new Vector3(450, 0, 0));
                var resave = St(sm, "Resave", c.Buffer005, new Vector3(450, 120, 0));
                sm.defaultState = watch;
                AddDriver(ctrl, forget, true, Set(p + "DropSaved", 0));
                AddDriver(ctrl, resave, true, Set(p + "DropSaved", 1));
                // Save toggled OFF -> forget the stored save. The object stays placed (on a restored slot the
                // menu-sync drive holds Drop = 1, built-in or aliased) and only the cross-session save is
                // forgotten - recall is then a Drop tap-off.
                // The forget only fires on positive evidence that this session's rig actually ran: Live (raised
                // by the owner's Send-last, for both fresh drops and restores) or Latched (raised at cascade
                // entry, and at the restore freeze). Both are 0 at avatar init, so no
                // init-ordering skew - e.g. a host Save alias whose animator default is false being read before
                // VRChat applies the saved expression values - can wipe a stored drop before the restore path
                // consumes it. Any real DropSaved=1 raises one of the two within a ring cycle, so a Save-off is
                // honored at most a beat late, never lost.
                Cond(watch.AddTransition(forget), If("IsLocal"), IfNot(p + "AutoSave"), If(p + "DropSaved"), If(p + "Live"));
                Cond(watch.AddTransition(forget), If("IsLocal"), IfNot(p + "AutoSave"), If(p + "DropSaved"), If(p + "Latched"));
                // Save toggled ON during a live drop -> (re)store the current pose. Gate on Live (raised only by
                // Send-last, after a full latch + broadcast cycle), NOT Latched (= "cascade started") which would
                // fire mid-cascade and persist a torn pose.
                Cond(watch.AddTransition(resave), If("IsLocal"), If(p + "AutoSave"), If(p + "Drop"), If(p + "Live"), IfNot(p + "DropSaved"));
                ExitT(forget, watch);
                ExitT(resave, watch);
            }

            // Rig (all clients): gate the measurement contacts to the owner's encode window. RigOn is
            // set only by the owner-only Encode layer, so on remotes it stays 0 and the contacts stay
            // off (they are localOnly anyway). Idle and already-dropped objects therefore carry no live
            // contacts. That matters because every slot's measurement contacts anchor near the world
            // origin, and each VRChat contact shape considers only its 32 nearest overlapping shapes,
            // with tag matching run after that cut: many objects' contacts left live at the same spot
            // can crowd a receiver's own sender out of its nearest-32 set, and drops would stop
            // measuring reliably (even one drop among many).
            AddLayer(cp + "Rig");
            {
                var sm = ctrl.layers[ctrl.layers.Length - 1].stateMachine;
                var off = St(sm, "RigOff", c.RigOff, new Vector3(200, 0, 0));
                var on = St(sm, "RigOn", c.RigOn, new Vector3(200, 100, 0));
                sm.defaultState = off;
                Cond(off.AddTransition(on), If(p + "RigOn"));
                Cond(on.AddTransition(off), IfNot(p + "RigOn"));
            }

            // Display (all clients)
            AddLayer(cp + "Display");
            {
                var sm = ctrl.layers[ctrl.layers.Length - 1].stateMachine;
                var rIdle = St(sm, "RemoteIdle", c.RemoteIdle, new Vector3(200, 0, 0));
                var rDrop = St(sm, "RemoteDropped", c.RemoteDropped, new Vector3(200, 100, 0));
                var rFrozen = St(sm, "RemoteFrozen", c.RemoteFrozen, new Vector3(450, 100, 0));
                var lIdle = St(sm, "LocalIdle", c.LocalIdle, new Vector3(200, 200, 0));
                var lDrop = St(sm, "LocalDropped", c.LocalDropped, new Vector3(200, 300, 0));
                sm.defaultState = rIdle;
                // Remote reveal: idle -> dropped (live reconstruction) -> frozen (static capture).
                // Reveal once all `steps` of this slot's steps have been observed (every channel latched). No +1:
                // by the time Seen reaches steps, all distinct steps were seen. Then show the LIVE reconstruction
                // for one more full broadcast cycle (Seen in [steps, 2*steps)) so it converges to THIS drop, and
                // only then freeze it (Seen >= 2*steps) into a static FreezeToWorld capture - like the owner's
                // LocalDropped. The Seen gate (not a clip dwell) is what makes redrop correct: undrop resets Seen
                // to 0 (MuxReveal), so on redrop the freeze waits for the new reconstruction to re-converge instead
                // of latching the previous frame's stale pose. The local `Frozen` gate keeps the AnyState
                // transitions from re-entering and re-capturing once frozen; RemoteIdle clears it so a re-drop
                // re-arms the whole cycle.
                AddDriver(ctrl, rIdle, false, Set(p + "Frozen", 0));
                AddDriver(ctrl, rFrozen, false, Set(p + "Frozen", 1));
                Any(sm, rIdle, IfNot("IsLocal"), IfNot(p + "Live"));
                Any(sm, rIdle, IfNot("IsLocal"), Lt(p + "Seen", steps));
                Any(sm, rDrop, IfNot("IsLocal"), If(p + "Live"), Gt(p + "Seen", steps - 1), Lt(p + "Seen", 2 * steps), IfNot(p + "Frozen"));
                Any(sm, rFrozen, IfNot("IsLocal"), If(p + "Live"), Gt(p + "Seen", 2 * steps - 1), IfNot(p + "Frozen"));
                if (persist) {
                    // Owner restore: with a saved drop pending (DropSaved, no menu Drop yet), follow the decode
                    // (the channels were restored from disk) and freeze only once the owner's own broadcast ring
                    // has completed two full cycles (Seen >= 2*steps - the SAME convergence gate the remote
                    // freeze uses; the persist MuxReveal counts Seen on Live, and the restored owner is Live).
                    // A fixed time dwell can fire before the reconstruction has caught up at low frame rates,
                    // capturing a mid-slide pose tens of cm / over a hundred degrees off - gate on convergence
                    // (Seen), never on a dwell. Frozen blocks
                    // re-entry after the capture; canTransitionToSelf=false blocks re-entry during the wait.
                    // Because the owner now sets Frozen, LocalIdle and LocalDropped must clear it: a redrop with
                    // Frozen stuck 1 would keep the Decode/Mirror gates off and the cascade would measure garbage.
                    AddDriver(ctrl, lIdle, false, Set(p + "Frozen", 0));
                    AddDriver(ctrl, lDrop, false, Set(p + "Frozen", 0));
                    var lRestore = St(sm, "LocalRestore", c.RestoreDwell, new Vector3(450, 200, 0));
                    var lRestoreFrozen = St(sm, "LocalRestoreFrozen", c.RemoteFrozen, new Vector3(450, 300, 0));
                    AddDriver(ctrl, lRestoreFrozen, false, Set(p + "Frozen", 1));
                    // Menu-sync: the param the user's Drop toggle binds (the synced WDM<slot>/Drop for the
                    // built-in menu, the host's drive param on an alias slot) is not a saved param and so
                    // rejoins at 0 - it would read OFF over a visibly restored object, and the first tap would
                    // be a redrop-at-body instead of the intuitive undrop. Once the restore has converged and
                    // frozen, drive it to 1 so the toggle reflects the placed object; a single tap OFF then
                    // rides the existing Clear path below (keyed on !Drop && Latched) to recall + forget with
                    // no teleport. Latched=1 goes in the SAME driver so Encode never sees Drop=1 with Latched=0
                    // and fires the redrop cascade at SettleSuper. An alias slot sets the host param and the
                    // internal Drop together, also in that one driver: the bridge copies the host param onto
                    // Drop a frame later, so driving only the host param would leave a frame of !Drop && Latched,
                    // which the Clear path reads as an undrop and it wipes the save mid-restore. Driving Drop=1
                    // while Frozen=1 parks the machine in this state (no Display state fires on that pair -
                    // LocalDropped is gated on !Frozen) until the user taps the toggle off. The driven value 1
                    // reads as on for a Bool, Int, or Float host param (menu toggles set 1 for all three).
                    // localOnly: Drop is owner-authoritative, host drive params are local by contract, and this
                    // state is only reached by the owner.
                    if (slot.dropAlias == null) AddDriver(ctrl, lRestoreFrozen, true, Set(p + "Drop", 1), Set(p + "Latched", 1));
                    else AddDriver(ctrl, lRestoreFrozen, true, Set(slot.dropAlias, 1), Set(p + "Drop", 1), Set(p + "Latched", 1));
                    Cond(lRestore.AddTransition(lRestoreFrozen), Gt(p + "Seen", 2 * steps - 1));
                    Any(sm, lIdle, If("IsLocal"), IfNot(p + "Drop"), IfNot(p + "DropSaved"));
                    Any(sm, lRestore, If("IsLocal"), IfNot(p + "Drop"), If(p + "DropSaved"), IfNot(p + "Frozen"));
                    // Gating LocalDropped on !Frozen is behavior-preserving for normal drops (the non-persist owner never sets Frozen).
                    Any(sm, lDrop, If("IsLocal"), If(p + "Drop"), IfNot(p + "Frozen"));
                } else {
                    Any(sm, lIdle, If("IsLocal"), IfNot(p + "Drop"));
                    Any(sm, lDrop, If("IsLocal"), If(p + "Drop"));
                }
            }

            // Decode (direct blend tree, gated to the reconstruction window)
            AddLayer(cp + "Decode");
            {
                var sm = ctrl.layers[ctrl.layers.Length - 1].stateMachine;
                var root = new BlendTree { name = cp + "DecodeRoot", blendType = BlendTreeType.Direct, hideFlags = HideFlags.HideInHierarchy };
                AssetDatabase.AddObjectToAsset(root, ctrl);
                var ch = new List<ChildMotion>();
                for (int a = 0; a < 3; a++) {
                    ch.Add(new ChildMotion { motion = Tree1D(ctrl, cp + "SuperT" + Axes[a], p + "Super" + Axes[a] + "F", c.SuperMin[a], c.SuperMax[a], 0f, 255f), directBlendParameter = MUX + "One", timeScale = 1 });
                    ch.Add(new ChildMotion { motion = Tree1D(ctrl, cp + "CellT" + Axes[a], p + "Cell" + Axes[a] + "F", c.CellMin[a], c.CellMax[a], 0f, 255f), directBlendParameter = MUX + "One", timeScale = 1 });
                    ch.Add(new ChildMotion { motion = Tree1D(ctrl, cp + "FineT" + Axes[a], p + "Fine" + Axes[a], c.FineMin[a], c.FineMax[a], -1f, 1f), directBlendParameter = MUX + "One", timeScale = 1 });
                }
                ch.Add(new ChildMotion { motion = Tree1D(ctrl, cp + "ForwardTX", p + "ForwardX", c.ForwardXMin, c.ForwardXMax, -1f, 1f), directBlendParameter = MUX + "One", timeScale = 1 });
                ch.Add(new ChildMotion { motion = Tree1D(ctrl, cp + "ForwardTZ", p + "ForwardZ", c.ForwardZMin, c.ForwardZMax, -1f, 1f), directBlendParameter = MUX + "One", timeScale = 1 });
                if (fullRot) {
                    ch.Add(new ChildMotion { motion = Tree1D(ctrl, cp + "ForwardTY", p + "ForwardY", c.ForwardYMin, c.ForwardYMax, -1f, 1f), directBlendParameter = MUX + "One", timeScale = 1 });
                    for (int a = 0; a < 3; a++) ch.Add(new ChildMotion { motion = Tree1D(ctrl, cp + "UpT" + Axes[a], p + "Up" + Axes[a], c.UpMin[a], c.UpMax[a], -1f, 1f), directBlendParameter = MUX + "One", timeScale = 1 });
                }
                root.children = ch.ToArray();
                // Gate the direct blend tree: it only needs to write the cascade frames' PositionOffset while a
                // slot is actively reconstructing - dropped and not yet frozen (remote), or during the owner's
                // encode (the owner never sets Frozen, so this stays on for the owner's whole drop, which the
                // encode cascade needs). Once a remote freezes, the Container is a static FreezeToWorld capture
                // that ignores the decoded frame; idle/undropped objects never read it. So steady-state decode
                // cost falls to ~0 except for objects mid-reveal. In the Off state the cascade frames' last
                // PositionOffset just holds (gating off and then changing a decode input does not move the
                // frame), and it is unread either way - a frozen Container ignores source1, an idle one follows
                // source0 - so gating it off cannot disturb the displayed pose. In practice a frozen pose holds
                // within ~1.5cm of the owner's, and a redrop re-enables decode and reconstructs the new pose
                // to about a centimeter.
                var off = St(sm, "Off", null, new Vector3(0, 0, 0));
                var decode = St(sm, "Decode", root, new Vector3(200, 0, 0));
                sm.defaultState = off;
                Cond(off.AddTransition(decode), If(p + "Drop"), IfNot(p + "Frozen"));
                if (persist) {
                    // A restored drop reconstructs with Drop=0: Live gates the remote's decode window, DropSaved
                    // the owner's (available before Live - the ring takes a cycle to raise it, and the owner's
                    // restore display dwells must run against a live decode). Note the persist owner DOES set
                    // Frozen (LocalRestoreFrozen), unlike the non-persist owner; LocalIdle/LocalDropped clear it.
                    Cond(off.AddTransition(decode), If(p + "Live"), IfNot(p + "Frozen"));
                    Cond(off.AddTransition(decode), If(p + "DropSaved"), IfNot(p + "Frozen"));
                    Any(sm, off, IfNot(p + "Drop"), IfNot(p + "Live"), IfNot(p + "DropSaved"));
                } else {
                    Any(sm, off, IfNot(p + "Drop"));
                }
                Any(sm, off, If(p + "Frozen"));
            }

            // Show (all clients)
            AddLayer(cp + "Show");
            {
                var sm = ctrl.layers[ctrl.layers.Length - 1].stateMachine;
                var off = St(sm, "ShowOff", c.ShowOff, new Vector3(200, 0, 0));
                var on = St(sm, "ShowOn", c.ShowOn, new Vector3(200, 100, 0));
                sm.defaultState = off;
                Action<AnimatorState, AnimatorState, AnimatorCondition[]> tr = (f, t, cs) => Cond(f.AddTransition(t), cs);
                tr(off, on, new[] { If(p + "Show"), If("IsLocal") });
                // Persist: the "shown while undropped" path (rest-follow) must not fire for a restored drop
                // (Drop=0 but Live=1) - a late-joining remote would show the object following the avatar for
                // the whole reveal window and then teleport it, exactly the artifact reveal gating prevents.
                if (persist) tr(off, on, new[] { If(p + "Show"), IfNot(p + "Drop"), IfNot(p + "Live") });
                else tr(off, on, new[] { If(p + "Show"), IfNot(p + "Drop") });
                tr(off, on, new[] { If(p + "Show"), If(p + "Live"), Gt(p + "Seen", steps - 1) });
                tr(on, off, new[] { IfNot(p + "Show") });
                tr(on, off, new[] { IfNot("IsLocal"), If(p + "Drop"), IfNot(p + "Live") });
                tr(on, off, new[] { IfNot("IsLocal"), If(p + "Drop"), Lt(p + "Seen", steps) });
                // Persist: hide a mid-session restore (owner re-arms "Saved") while it reveals on remotes.
                if (persist) tr(on, off, new[] { IfNot("IsLocal"), IfNot(p + "Drop"), If(p + "Live"), Lt(p + "Seen", steps) });
            }

            // MuxLatch (remote): copy lanes -> channels when Idx == baseIdx+1+k
            AddLayer(cp + "MuxLatch");
            {
                var sm = ctrl.layers[ctrl.layers.Length - 1].stateMachine;
                var wait = St(sm, "Wait", c.Buffer1F, new Vector3(200, 0, 0));
                sm.defaultState = wait;
                for (int k = 0; k < steps; k++) {
                    var st = St(sm, "L" + k, c.Buffer1F, new Vector3(450, k * 55, 0));
                    var ops = new List<VRC_AvatarParameterDriver.Parameter>();
                    for (int s = 0; s < Lanes; s++) { int ci = k * Lanes + s; if (ci >= chans.Length) break; ops.Add(chans[ci].isInt ? Copy(MUX + "Lane" + s, chans[ci].name) : Copy(MUX + "Lane" + s, chans[ci].name, true, 0, 255, -1, 1)); }
                    AddDriver(ctrl, st, false, ops.ToArray());
                    Any(sm, st, IfNot("IsLocal"), Eq(MUX + "Idx", baseIdx + 1 + k));
                }
            }

            // MuxReveal (remote): ring counting Seen over this slot's steps
            AddLayer(cp + "MuxReveal");
            {
                var sm = ctrl.layers[ctrl.layers.Length - 1].stateMachine;
                var wait = St(sm, "Wait", c.Buffer1F, new Vector3(200, 0, 0));
                AddDriver(ctrl, wait, false, Set(p + "Seen", 0));
                sm.defaultState = wait;
                var ring = new AnimatorState[steps];
                for (int k = 0; k < steps; k++) {
                    ring[k] = St(sm, "R" + k, c.Buffer1F, new Vector3(450, k * 55, 0));
                    AddDriver(ctrl, ring[k], false, AddTo(p + "Seen", 1));
                    Cond(wait.AddTransition(ring[k]), If(p + "Drop"), Eq(MUX + "Idx", baseIdx + 1 + k));
                    // Persist: a restored drop broadcasts with Drop=0, so remotes must also count Seen on Live
                    // (the synced flag the restored owner raises) or they never reveal it.
                    if (persist) Cond(wait.AddTransition(ring[k]), If(p + "Live"), Eq(MUX + "Idx", baseIdx + 1 + k));
                }
                for (int k = 0; k < steps; k++) Cond(ring[k].AddTransition(ring[(k + 1) % steps]), Eq(MUX + "Idx", baseIdx + 1 + ((k + 1) % steps)));
                // Persist: reset purely on !Live. Live is the broadcast-validity signal (owner drivers only:
                // Idle, the cascade-entry dip, Send-last), so any Live=0 window means "channels in flight are
                // not a complete pose - restart the count". Keying the reset on Drop misses the redrop-from-
                // restore dip (Drop is already 1), leaving Seen stale >= 2*steps, and the remote freeze then
                // fires the instant Live returns - capturing a mid-flight garbage pose, which leaves the
                // object frozen at the rest-follow spot.
                if (persist) Any(sm, wait, IfNot(p + "Live"));
                else Any(sm, wait, IfNot(p + "Drop"));
            }

            // Bridge (owner): when this object names an author drive param, mirror that local param onto the
            // synced WDM<slot>/Drop|Show. The drivers are localOnly, so only the owner writes the synced param
            // (which then propagates to remotes); on a remote the param sits at its local default and the no-op
            // driver leaves the synced value untouched. This makes the bridge the single writer of the synced
            // param - the menu binds the author param, not WDM - so the host's UI and our menu can both drive
            // the object without two writers fighting over it. Drop and Show are independent: either may exist.
            void Mirror(string layer, string src, string target, bool defOn) {
                AddLayer(layer);
                var sm = ctrl.layers[ctrl.layers.Length - 1].stateMachine;
                var off = St(sm, "Off", c.Buffer1F, new Vector3(200, 0, 0));
                var on = St(sm, "On", c.Buffer1F, new Vector3(200, 100, 0));
                // Start in the state matching the source's default, so no startup transition flickers the param.
                sm.defaultState = defOn ? on : off;
                AddDriver(ctrl, off, true, Set(target, 0));
                AddDriver(ctrl, on, true, Set(target, 1));
                // The host owns `src` and picks its type; we declare it Bool and read it with If/IfNot.
                // This controller is handed to VRCFury before its merge, so if the host's param ends up a
                // float or int (e.g. a toggle that also drives an FX float), VRCFury rewrites these bool-mode
                // conditions to the matching comparison during UpgradeWrongParamTypes. No type check here.
                Any(sm, on, If(src));
                Any(sm, off, IfNot(src));
            }
            if (slot.dropAlias != null) Mirror(cp + "BridgeDrop", slot.dropAlias, p + "Drop", false);
            if (slot.showAlias != null) Mirror(cp + "BridgeShow", slot.showAlias, p + "Show", slot.defaultShown);
            // Save bridge (persist only): the host's Save param becomes the durable store and this mirror
            // makes the internal AutoSave follow it, so the Save watcher and LatchFine keep reading a Bool
            // whatever type the host param ends up as. The bridge is AutoSave's single writer on an aliased
            // slot (the built-in Save control binds the alias, never AutoSave). defOn keeps a dead bridge's
            // failure mode "Save stuck on" (annoying) instead of "stuck off" (data loss).
            if (persist && slot.saveAlias != null) Mirror(cp + "BridgeSave", slot.saveAlias, p + "AutoSave", true);
        }

        // ============================ params + menu ============================

        static void EmitParams(VRCExpressionParameters prms, Slot[] slots) {
            var list = prms.parameters != null ? prms.parameters.ToList() : new List<VRCExpressionParameters.Parameter>();
            void Add(string name, VRCExpressionParameters.ValueType vt, bool synced, float def = 0, bool saved = false) { if (!list.Any(x => x.name == name)) list.Add(new VRCExpressionParameters.Parameter { name = name, valueType = vt, networkSynced = synced, saved = saved, defaultValue = def }); }
            void AddSynced(string name, VRCExpressionParameters.ValueType vt, float def = 0) => Add(name, vt, true, def);
            for (int s = 0; s < Lanes; s++) AddSynced(MUX + "Lane" + s, VRCExpressionParameters.ValueType.Int);
            AddSynced(MUX + "Idx", VRCExpressionParameters.ValueType.Int);
            foreach (var slot in slots) {
                AddSynced(P(slot.id) + "Drop", VRCExpressionParameters.ValueType.Bool);
                // Show defaults to the author's choice so the object spawns shown or hidden (and any Show toggle
                // reflects it). The synced default is what a remote/late-joiner reads before the owner's value arrives.
                AddSynced(P(slot.id) + "Show", VRCExpressionParameters.ValueType.Bool, slot.defaultShown ? 1f : 0f);
                AddSynced(P(slot.id) + "Live", VRCExpressionParameters.ValueType.Bool);
                // Author-named drive params are NOT declared here: the host owns them. The build hook runs
                // before VRCFury and forces each alias global, so the host's own declaration (whatever its
                // type - bool, int or float) is the single source, and the owner's bridge layer reads it and
                // copies it onto the synced WDM<slot> param. Declaring a Bool copy in this fresh pre-merge
                // params asset would double-declare it or clash with the host's type. The alias exists as a
                // Bool animator param (EnsureBool in EmitController) only so the bridge conditions are valid;
                // VRCFury coerces those conditions to the merged type. Drop and Show are independent.
                // Persist: the latched pose channels + the DropSaved flag + the AutoSave preference become saved
                // LOCAL expression params (VRChat writes them to the wearer's local storage and restores them next
                // session; zero synced bits). They stay animator params too - being expression params only adds the
                // persistence. The *F mirrors and Raw* contact reads are transient and stay animator-only.
                if (slot.persist) {
                    string pp = P(slot.id);
                    Add(pp + "DropSaved", VRCExpressionParameters.ValueType.Bool, false, 0, true);
                    // Sticky Save preference, default on. With a host Save alias the ALIAS is the single
                    // saved store (the author marks it saved on their own declaration; the BridgeSave
                    // mirror re-derives AutoSave from it every session), so AutoSave is registered
                    // unsaved there - two saved copies could disagree at load. The alias itself is never
                    // declared in these params: it is the host's, whatever type they chose.
                    Add(pp + "AutoSave", VRCExpressionParameters.ValueType.Bool, false, 1, slot.saveAlias == null);
                    foreach (var a in Axes) {
                        Add(pp + "Super" + a, VRCExpressionParameters.ValueType.Int, false, 0, true);
                        Add(pp + "Cell" + a, VRCExpressionParameters.ValueType.Int, false, 0, true);
                        Add(pp + "Fine" + a, VRCExpressionParameters.ValueType.Float, false, 0, true);
                    }
                    Add(pp + "ForwardX", VRCExpressionParameters.ValueType.Float, false, 0, true);
                    Add(pp + "ForwardZ", VRCExpressionParameters.ValueType.Float, false, 0, true);
                    if (slot.fullRot) {
                        Add(pp + "ForwardY", VRCExpressionParameters.ValueType.Float, false, 0, true);
                        foreach (var a in Axes) Add(pp + "Up" + a, VRCExpressionParameters.ValueType.Float, false, 0, true);
                    }
                }
            }
            prms.parameters = list.ToArray();
            EditorUtility.SetDirty(prms);
        }

        // Normalize every control to a non-null parameter + subParameters (null crashes GestureManager /
        // Av3Emulator / the SDK3->CCK converter; VRChat itself tolerates it). VRCFury's FinalizeMenuService
        // does the same on the final merged menu after this hook hands the menu over, so this is defensive,
        // but it keeps the generated submenu assets well-formed on disk regardless.
        static VRCExpressionsMenu.Control Norm(VRCExpressionsMenu.Control c) {
            if (c.parameter == null) c.parameter = new VRCExpressionsMenu.Control.Parameter { name = "" };
            if (c.subParameters == null) c.subParameters = new VRCExpressionsMenu.Control.Parameter[0];
            return c;
        }

        static void EmitMenu(VRCExpressionsMenu menu, Slot[] slots, string dir) {
            if (menu.controls == null) menu.controls = new List<VRCExpressionsMenu.Control>();
            // Each object's Show/Drop toggles go in a submenu at its configured menuPath. "N" in the
            // path is a token for the object number (1-based); blank falls back to the same default.
            // Build the nested submenu tree, reusing submenus we created for shared path prefixes so
            // multiple objects can share folders (e.g. all under one "World Drop"). This menu is a fresh
            // standalone asset handed to VRCFury before its merge; VRCFury merges it into the avatar's menu
            // at its own pass, folding a same-named folder (e.g. a "WorldDrops" from the unsynced toggles)
            // into one - so no cross-asset dedup is needed here. Every generated submenu is saved as an
            // asset (VRChat drops in-memory menus on upload).
            var created = new Dictionary<string, VRCExpressionsMenu>();
            // Submenus are written to disk only AFTER their controls are fully populated (the persist pass
            // at the end), never created-then-mutated. During an avatar build Unity batches asset editing,
            // so CreateAsset-then-mutate-then-SaveAssets does NOT reliably re-serialize the mutation: some
            // generated submenus flush, some persist EMPTY, nondeterministically - menu items that appear
            // in-game with no Show/Drop toggles. Track every submenu WE create here; the persist pass at the
            // end walks the finished tree and CreateAssets each one once, children before parents.
            var owned = new HashSet<VRCExpressionsMenu>();
            Func<VRCExpressionsMenu, string, string, VRCExpressionsMenu> folder = (parentMenu, keyPrefix, name) => {
                string key = keyPrefix.Length == 0 ? name : keyPrefix + "/" + name;
                if (created.TryGetValue(key, out var existing)) return existing;
                if (parentMenu.controls == null) parentMenu.controls = new List<VRCExpressionsMenu.Control>();
                var sub = ScriptableObject.CreateInstance<VRCExpressionsMenu>();
                sub.controls = new List<VRCExpressionsMenu.Control>();
                owned.Add(sub);
                parentMenu.controls.Add(Norm(new VRCExpressionsMenu.Control { name = name, type = VRCExpressionsMenu.Control.ControlType.SubMenu, subMenu = sub }));
                created[key] = sub;
                return sub;
            };
            foreach (var slot in slots) {
                // A named Drop/Show Param means a host drives this object, so author no built-in menu for
                // it - skip entirely so no empty folder is left behind. No param = the built-in Show + Drop.
                if (slot.dropAlias != null || slot.showAlias != null) continue;
                string raw = string.IsNullOrWhiteSpace(slot.menuPath) ? "WorldDrops/Object N (Synced)" : slot.menuPath.Trim();
                // "N" stands for this object's number, so the shared default "Object N" resolves to
                // Object 1, Object 2, ... per slot (word-boundary match leaves names like "Neon" alone).
                string path = System.Text.RegularExpressions.Regex.Replace(raw, @"\bN\b", (slot.id + 1).ToString());
                var segs = path.Split('/').Select(s => s.Trim()).Where(s => s.Length > 0).ToArray();
                if (segs.Length == 0) segs = new[] { "WorldDrops", "Object " + (slot.id + 1) + " (Synced)" };
                VRCExpressionsMenu cur = menu; string keyPrefix = "";
                foreach (var seg in segs) { cur = folder(cur, keyPrefix, seg); keyPrefix = keyPrefix.Length == 0 ? seg : keyPrefix + "/" + seg; }
                // When the author named a drive param, the toggle binds that param (the bridge layer is the sole
                // writer of the synced WDM<slot> param), so our menu and a host's UI never both write the same
                // synced param. Without one, the toggle binds the WDM<slot> param directly (the default).
                string showParam = slot.showAlias ?? P(slot.id) + "Show";
                string dropParam = slot.dropAlias ?? P(slot.id) + "Drop";
                cur.controls.Add(Norm(new VRCExpressionsMenu.Control { name = "Show", type = VRCExpressionsMenu.Control.ControlType.Toggle, parameter = new VRCExpressionsMenu.Control.Parameter { name = showParam } }));
                cur.controls.Add(Norm(new VRCExpressionsMenu.Control { name = "Drop", type = VRCExpressionsMenu.Control.ControlType.Toggle, parameter = new VRCExpressionsMenu.Control.Parameter { name = dropParam } }));
                // Persist: the Save toggle is the sticky preference (bound to AutoSave, default on). ON = a drop
                // is saved across sessions; toggling OFF sticks (saved), so future drops do not save until
                // it is turned back on, and it forgets any currently-stored drop. A slot whose Save is host-driven
                // (a Save Param is set) gets NO built-in Save control: the host drives Save from its own toggle,
                // exactly as a Drop/Show Param suppresses those built-in controls. This also keeps our menu from
                // binding the host's param - VRCFury rejects a merged menu that references a parameter declared by
                // a separate component and not yet merged when it checks our menu. Slots whose Drop/Show is
                // host-driven skip this whole submenu already.
                if (slot.persist && slot.saveAlias == null)
                    cur.controls.Add(Norm(new VRCExpressionsMenu.Control { name = "Save", type = VRCExpressionsMenu.Control.ControlType.Toggle, parameter = new VRCExpressionsMenu.Control.Parameter { name = P(slot.id) + "AutoSave" } }));
            }
            // Pagination is left to VRCFury: it runs MenuSplitter on the final merged menu after this hook
            // hands the menu over, splitting any folder past VRChat's 8-control limit into chained subpages.
            // This hook does not paginate the menu itself, so nothing here interferes with that merge/split.

            // Persist pass: walk the finished tree depth-first and CreateAsset every submenu WE own, children
            // before parents, so each parent's subMenu reference already points at an on-disk asset when it is
            // serialized. Writing complete assets this way (never CreateAsset-then-mutate) is what survives the
            // avatar build's batched asset editing, which silently drops post-create mutations.
            int assetIdx = 0;
            void Persist(VRCExpressionsMenu m) {
                if (m.controls == null) return;
                foreach (var c in m.controls) {
                    if (c == null || c.type != VRCExpressionsMenu.Control.ControlType.SubMenu || c.subMenu == null) continue;
                    if (!owned.Contains(c.subMenu)) continue; // skip VRCFury's / the user's existing on-disk submenus
                    Persist(c.subMenu);
                    if (string.IsNullOrEmpty(AssetDatabase.GetAssetPath(c.subMenu)))
                        AssetDatabase.CreateAsset(c.subMenu, dir + "/WDM_Menu_" + (assetIdx++) + ".asset");
                }
            }
            Persist(menu);
            EditorUtility.SetDirty(menu);
            foreach (var sub in owned) EditorUtility.SetDirty(sub);
        }
    }
}
#endif
