// Multi-object shared-mux generator. Produces N independently-droppable world-drop "slots" that
// share ONE synced mux (WDM/Lane0..Lane3 + WDM/Idx) via a cross-slot broadcast RING. The cascade and
// rotation decode geometry already lives in each WorldDropSynced prefab's contact rig; this file
// retags every instance per slot (no geometry is duplicated) and adds the slot parameterization and
// the ring on top.
//
// Idx packs (slot*MAXSTEP + step). base = i*MAXSTEP is a TOKEN the handoff lands on (no MuxLatch
// keys on it; only the owner's WaitTurn reacts); steps are base+1..base+S. Keeping every
// MuxLatch-keyed value owned by a Send state (which writes the lanes the same frame) prevents a torn
// latch when Idx advances ahead of the lanes. One mux width (Lanes) serves both rotation families;
// MAXSTEP leaves headroom over the token plus the per-family step count.
//
// Emit(...) is the reusable core: it populates an existing controller + expression params + menu for
// a set of slots (each with an avatar-relative base path). WdsBuildHook wraps it at build time
// against the real marker instances.
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
        // Set per build (by the build hook) from the avatar's reveal-speed setting, then read
        // by Emit. Default 4 (Slow, fewest bits); the settings object's Fast picks 8.
        public static int Lanes = 4;
        // Optional override of the lane width (null = use the avatar's reveal-speed setting); the build
        // hook applies LanesOverride ?? lanes. Set it to force a fixed width.
        public static int? LanesOverride = null;
        public const int MAXSTEP = 10;   // 1 token + max steps; full-rot ceil(15/L) <= 4 at L>=4, so 10 is ample headroom
        const float StepDwell = 0.3f;

        static readonly string[] Axes = { "X", "Y", "Z" };
        public const string MUX = "WDM/";   // shared mux param prefix

        // Per-slot naming.
        static string P(int slot) => "WDM" + slot + "/";
        static string CP(int slot) => "WDM" + slot + "_";

        /// <summary>One world-drop slot: a unique id, rotation family, the avatar-relative path to
        /// its root GameObject (where Container / _Sync live), and the menu path for its Show/Drop
        /// toggles ("N" in the path is replaced with the object number; blank falls back to
        /// "WorldDrops/Object N (Synced)").</summary>
        public struct Slot { public int id; public bool fullRot; public string basePath; public string menuPath; }

        public static int Steps(bool fullRot) => (ChannelCount(fullRot) + Lanes - 1) / Lanes;
        static int ChannelCount(bool fullRot) => fullRot ? 15 : 11;

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
        // encode window so idle/dropped objects carry no live contacts and the broadphase never
        // saturates. Frames (CoarseFrame/FineFrame/ForwardFrame/DecodeFrame) are deliberately excluded -
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

        static void EnsureBool(AnimatorController c, string n) { if (!c.parameters.Any(p => p.name == n)) c.AddParameter(new AnimatorControllerParameter { name = n, type = AnimatorControllerParameterType.Bool, defaultBool = false }); }
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
            EnsureBool(ctrl, "IsLocal"); EnsureFloat(ctrl, MUX + "One", 1f);
            EnsureInt(ctrl, MUX + "Lane0"); EnsureInt(ctrl, MUX + "Lane1"); EnsureInt(ctrl, MUX + "Idx"); EnsureBool(ctrl, MUX + "AnyDrop");
            foreach (var slot in slots) {
                string p = P(slot.id);
                EnsureBool(ctrl, p + "Drop"); EnsureBool(ctrl, p + "Show"); EnsureBool(ctrl, p + "Live"); EnsureInt(ctrl, p + "Seen"); EnsureBool(ctrl, p + "RigOn"); EnsureBool(ctrl, p + "Frozen");
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
                Cond(on.AddTransition(off), slots.Select(s => IfNot(P(s.id) + "Drop")).ToArray());
            }

            for (int si = 0; si < n; si++) { SlotLayers(ctrl, slots[si], n, cl[si], added); }

            var layers = ctrl.layers;
            for (int k = 0; k < layers.Length; k++) if (added.Contains(layers[k].name)) layers[k].defaultWeight = 1f;
            ctrl.layers = layers;
            EditorUtility.SetDirty(ctrl);
            log.AppendLine("Controller layers added: " + added.Count);
        }

        static void SlotLayers(AnimatorController ctrl, Slot slot, int n, SlotClips c, HashSet<string> added) {
            int i = slot.id; bool fullRot = slot.fullRot; int steps = Steps(fullRot);
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
                Any(sm, off, IfNot(p + "Drop"));
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
                AddDriver(ctrl, ss, true, Set(p + "RigOn", 1));    // contacts on for the settle/latch cascade
                AddDriver(ctrl, wait, true, Set(p + "RigOn", 0));  // pose latched; contacts off (broadcast reads latched params)
                AddDriver(ctrl, lsup, true, Axes.Select(a => Copy(p + "RawS" + a, p + "Super" + a, true, 0, 1, 0, 255)).ToArray());
                AddDriver(ctrl, lc, true, Axes.Select(a => Copy(p + "RawC" + a, p + "Cell" + a, true, 0, 1, 0, 255)).ToArray());
                var fine = Axes.Select(a => Copy(p + "RawF" + a, p + "Fine" + a, true, 0, 1, -1, 1)).Concat(new[] { Copy(p + "RawFwdX", p + "ForwardX", true, 0, 1, -1, 1), Copy(p + "RawFwdZ", p + "ForwardZ", true, 0, 1, -1, 1) });
                if (fullRot) fine = fine.Append(Copy(p + "RawFwdY", p + "ForwardY", true, 0, 1, -1, 1)).Concat(Axes.Select(a => Copy(p + "RawU" + a, p + "Up" + a, true, 0, 1, -1, 1)));
                AddDriver(ctrl, lf, true, fine.ToArray());
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
                // value -> permanent stall, Live never re-arms, remotes never re-reveal (verified in
                // playmode). A range match re-broadcasts cleanly from Send0 (which resets
                // Idx=base+1) regardless of where it froze; ranges are disjoint (base_j..base_j+MAXSTEP)
                // so this only ever fires on this slot's turn. Together with the undropped-slot skips
                // the ring self-heals from any frozen Idx.
                Cond(wait.AddTransition(send0), Gt(MUX + "Idx", baseIdx - 1), Lt(MUX + "Idx", baseIdx + MAXSTEP));
                ExitT(prev, handoff); ExitT(handoff, wait);
                Any(sm, idle, If("IsLocal"), IfNot(p + "Drop"));
            }

            // Rig (all clients): gate the measurement contacts to the owner's encode window. RigOn is
            // set only by the owner-only Encode layer, so on remotes it stays 0 and the contacts stay
            // off (they are localOnly anyway). Idle and already-dropped objects carry no live contacts,
            // so the broadphase never saturates from clustered or numerous objects.
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
                Any(sm, lIdle, If("IsLocal"), IfNot(p + "Drop"));
                Any(sm, lDrop, If("IsLocal"), If(p + "Drop"));
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
                // PositionOffset just holds (verified: gating off then perturbing a decode input does not move the
                // frame), and it is unread either way - a frozen Container ignores source1, an idle one follows
                // source0 - so gating it off cannot disturb the displayed pose. Verified in playmode: freeze +
                // gate-off holds the frozen pose (~1.5cm vs owner), and a redrop re-enables decode and
                // reconstructs the new pose (~0.9cm).
                var off = St(sm, "Off", null, new Vector3(0, 0, 0));
                var decode = St(sm, "Decode", root, new Vector3(200, 0, 0));
                sm.defaultState = off;
                Cond(off.AddTransition(decode), If(p + "Drop"), IfNot(p + "Frozen"));
                Any(sm, off, IfNot(p + "Drop"));
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
                tr(off, on, new[] { If(p + "Show"), IfNot(p + "Drop") });
                tr(off, on, new[] { If(p + "Show"), If(p + "Live"), Gt(p + "Seen", steps - 1) });
                tr(on, off, new[] { IfNot(p + "Show") });
                tr(on, off, new[] { IfNot("IsLocal"), If(p + "Drop"), IfNot(p + "Live") });
                tr(on, off, new[] { IfNot("IsLocal"), If(p + "Drop"), Lt(p + "Seen", steps) });
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
                }
                for (int k = 0; k < steps; k++) Cond(ring[k].AddTransition(ring[(k + 1) % steps]), Eq(MUX + "Idx", baseIdx + 1 + ((k + 1) % steps)));
                Any(sm, wait, IfNot(p + "Drop"));
            }
        }

        // ============================ params + menu ============================

        static void EmitParams(VRCExpressionParameters prms, Slot[] slots) {
            var list = prms.parameters != null ? prms.parameters.ToList() : new List<VRCExpressionParameters.Parameter>();
            void AddSynced(string name, VRCExpressionParameters.ValueType vt) { if (!list.Any(x => x.name == name)) list.Add(new VRCExpressionParameters.Parameter { name = name, valueType = vt, networkSynced = true, saved = false }); }
            for (int s = 0; s < Lanes; s++) AddSynced(MUX + "Lane" + s, VRCExpressionParameters.ValueType.Int);
            AddSynced(MUX + "Idx", VRCExpressionParameters.ValueType.Int);
            foreach (var slot in slots) { AddSynced(P(slot.id) + "Drop", VRCExpressionParameters.ValueType.Bool); AddSynced(P(slot.id) + "Show", VRCExpressionParameters.ValueType.Bool); AddSynced(P(slot.id) + "Live", VRCExpressionParameters.ValueType.Bool); }
            prms.parameters = list.ToArray();
            EditorUtility.SetDirty(prms);
        }

        // VRCFury's FinalizeMenuService normalizes every control to non-null parameter + subParameters
        // (null crashes GestureManager / Av3Emulator / the SDK3->CCK converter; VRChat itself tolerates it).
        // That pass runs in VRCFury's builder BEFORE this hook, so it never sees the controls we add here -
        // normalize ours the same way so the tools don't choke on the generated synced menu.
        static VRCExpressionsMenu.Control Norm(VRCExpressionsMenu.Control c) {
            if (c.parameter == null) c.parameter = new VRCExpressionsMenu.Control.Parameter { name = "" };
            if (c.subParameters == null) c.subParameters = new VRCExpressionsMenu.Control.Parameter[0];
            return c;
        }

        static void EmitMenu(VRCExpressionsMenu menu, Slot[] slots, string dir) {
            if (menu.controls == null) menu.controls = new List<VRCExpressionsMenu.Control>();
            // Each object's Show/Drop toggles go in a submenu at its configured menuPath. "N" in the
            // path is a token for the object number (1-based); blank falls back to the same default.
            // Build the nested submenu tree, reusing submenus we created for shared path prefixes
            // so multiple objects can share folders (e.g. all under one "World Drop"), AND merging into
            // a same-named folder that already exists on the menu (e.g. a "WorldDrops" submenu VRCFury
            // merged in for the unsynced toggles) instead of creating a duplicate. Every generated
            // submenu is saved as an asset (VRChat drops in-memory menus on upload).
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
                // Merge into an existing same-named submenu (e.g. VRCFury's "WorldDrops") instead of
                // adding a duplicate folder. Copy-on-write: clone that submenu and repoint the control
                // at the clone, so we extend our own asset and never mutate VRCFury's (the build hook's
                // root Instantiate does not clone nested submenu assets). Then treat the clone as ours.
                var hit = parentMenu.controls.FirstOrDefault(c => c != null && c.type == VRCExpressionsMenu.Control.ControlType.SubMenu && c.name == name);
                if (hit != null) {
                    var merged = hit.subMenu != null ? UnityEngine.Object.Instantiate(hit.subMenu) : ScriptableObject.CreateInstance<VRCExpressionsMenu>();
                    if (merged.controls == null) merged.controls = new List<VRCExpressionsMenu.Control>();
                    owned.Add(merged);
                    hit.subMenu = merged;
                    created[key] = merged;
                    return merged;
                }
                var sub = ScriptableObject.CreateInstance<VRCExpressionsMenu>();
                sub.controls = new List<VRCExpressionsMenu.Control>();
                owned.Add(sub);
                parentMenu.controls.Add(Norm(new VRCExpressionsMenu.Control { name = name, type = VRCExpressionsMenu.Control.ControlType.SubMenu, subMenu = sub }));
                created[key] = sub;
                return sub;
            };
            foreach (var slot in slots) {
                string raw = string.IsNullOrWhiteSpace(slot.menuPath) ? "WorldDrops/Object N (Synced)" : slot.menuPath.Trim();
                // "N" stands for this object's number, so the shared default "Object N" resolves to
                // Object 1, Object 2, ... per slot (word-boundary match leaves names like "Neon" alone).
                string path = System.Text.RegularExpressions.Regex.Replace(raw, @"\bN\b", (slot.id + 1).ToString());
                var segs = path.Split('/').Select(s => s.Trim()).Where(s => s.Length > 0).ToArray();
                if (segs.Length == 0) segs = new[] { "WorldDrops", "Object " + (slot.id + 1) + " (Synced)" };
                VRCExpressionsMenu cur = menu; string keyPrefix = "";
                foreach (var seg in segs) { cur = folder(cur, keyPrefix, seg); keyPrefix = keyPrefix.Length == 0 ? seg : keyPrefix + "/" + seg; }
                cur.controls.Add(Norm(new VRCExpressionsMenu.Control { name = "Show", type = VRCExpressionsMenu.Control.ControlType.Toggle, parameter = new VRCExpressionsMenu.Control.Parameter { name = P(slot.id) + "Show" } }));
                cur.controls.Add(Norm(new VRCExpressionsMenu.Control { name = "Drop", type = VRCExpressionsMenu.Control.ControlType.Toggle, parameter = new VRCExpressionsMenu.Control.Parameter { name = P(slot.id) + "Drop" } }));
            }
            // Auto-paginate: VRChat shows at most 8 controls per menu and has no native pagination, so any
            // menu WE own (plus the root) that overflowed gets its tail peeled into a chained "Next" subpage -
            // the same scheme as VRCFury's MenuSplitter, ported here because VRCFury finalizes and splits the
            // menu in its main builder (hook -10000) BEFORE this hook (-1025) appends our controls, so it never
            // re-splits our overflow. Pages are created here, before the persist walk, so they persist as
            // owned assets (never CreateAsset-then-mutate). Keep 7 originals + a "Next" link per full page.
            const int maxControls = 8;
            void Paginate(VRCExpressionsMenu m) {
                var page = m;
                while (page.controls.Count > maxControls) {
                    var next = ScriptableObject.CreateInstance<VRCExpressionsMenu>();
                    next.controls = new List<VRCExpressionsMenu.Control>();
                    owned.Add(next);
                    while (page.controls.Count > maxControls - 1) {
                        next.controls.Insert(0, page.controls[page.controls.Count - 1]);
                        page.controls.RemoveAt(page.controls.Count - 1);
                    }
                    page.controls.Add(Norm(new VRCExpressionsMenu.Control { name = "Next", type = VRCExpressionsMenu.Control.ControlType.SubMenu, subMenu = next }));
                    page = next; // a large overflow chains: re-check the new page and spill again
                }
            }
            Paginate(menu);
            foreach (var f in created.Values.ToList()) Paginate(f);

            // Persist pass: walk the finished tree depth-first and CreateAsset every submenu WE own, children
            // before parents, so each parent's subMenu reference already points at an on-disk asset when it is
            // serialized. Writing complete assets this way (never CreateAsset-then-mutate) is what survives the
            // avatar build's batched asset editing, which silently drops post-create mutations. A post-order
            // walk (not reverse creation order) is required because pagination can move a child-folder control
            // into a "Next" page, so a parent may reference a submenu created after it.
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
