// Feature-detects the box + face-proximity contact support the synced rig depends on (VRChat SDK
// 3.10.4+). Version-agnostic: it reflects the actual contact API (the shapeType enum's Box member and
// the useFaceProximity field) rather than parsing a version string, so it stays correct regardless of
// SDK packaging. Used by the build hook (abort the upload) and the component inspector (error box).
#if UNITY_EDITOR
using System;
using System.Linq;
using System.Reflection;
using VRC.SDK3.Dynamics.Contact.Components;

namespace VRCWorldDrop.Synced {
    public static class WdsSdkSupport {
        public const string MinSdk = "3.10.4";

        static int _cached = -1; // -1 = not checked, 0 = unsupported, 1 = supported

        /// <summary>True if the installed SDK exposes box contacts with Use Face Proximity, which the
        /// synced rig requires to read a signed per-axis world coordinate. False on older SDKs.</summary>
        public static bool HasBoxFaceProximity {
            get { if (_cached < 0) _cached = Detect() ? 1 : 0; return _cached == 1; }
        }

        static bool Detect() {
            // Reflect off VRCContactReceiver (already referenced by the rig): the inherited `shapeType`
            // enum must expose a Box member, and the `useFaceProximity` field must exist. Both shipped
            // together in SDK 3.10.4; checking the members (not a version string) is layout-agnostic.
            var recv = typeof(VRCContactReceiver);
            var shape = Member(recv, "shapeType");
            var shapeType = shape is FieldInfo sf ? sf.FieldType : (shape as PropertyInfo)?.PropertyType;
            bool hasBox = shapeType != null && shapeType.IsEnum && Enum.GetNames(shapeType).Contains("Box");
            bool hasFace = Member(recv, "useFaceProximity") != null;
            return hasBox && hasFace;
        }

        static MemberInfo Member(Type t, string name) {
            const BindingFlags F = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly;
            for (; t != null && t != typeof(object); t = t.BaseType) {
                MemberInfo m = (MemberInfo)t.GetField(name, F) ?? t.GetProperty(name, F);
                if (m != null) return m;
            }
            return null;
        }
    }
}
#endif
