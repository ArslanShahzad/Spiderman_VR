// Editor-only utility.
// Menu: SpiderMan → Setup Spider-Man Controller
// Run this once after opening BasicScene.
// It finds XR Origin / Left Controller / Right Controller,
// creates the hand-tip transforms, attaches every SpiderMan
// component, and wires all cross-references automatically.

using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using SpiderMan;

namespace SpiderMan.Editor
{
    public static class SpiderManSetup
    {
        [MenuItem("SpiderMan/Setup Spider-Man Controller")]
        static void Run()
        {
            // ── 1. Locate required GameObjects ───────────────────────────────
            var xrOrigin = FindByName("XR Origin (XR Rig)")
                        ?? FindByName("XR Origin");

            if (xrOrigin == null)
            {
                EditorUtility.DisplayDialog("SpiderMan Setup",
                    "Could not find 'XR Origin (XR Rig)' in the scene.\n\n" +
                    "Make sure BasicScene is open and the XR Rig is present.",
                    "OK");
                return;
            }

            var leftCtrl  = FindChildRecursive(xrOrigin, "Left Controller");
            var rightCtrl = FindChildRecursive(xrOrigin, "Right Controller");

            if (leftCtrl == null || rightCtrl == null)
            {
                EditorUtility.DisplayDialog("SpiderMan Setup",
                    "Could not find 'Left Controller' or 'Right Controller' " +
                    "under XR Origin.\n\nCheck your hierarchy names.",
                    "OK");
                return;
            }

            // ── 2. Create hand-tip origin transforms ─────────────────────────
            Transform webOriginLeft  = GetOrCreateChild(leftCtrl,  "WebOrigin_Left",
                                           new Vector3(0f, 0f, 0.05f));
            Transform webOriginRight = GetOrCreateChild(rightCtrl, "WebOrigin_Right",
                                           new Vector3(0f, 0f, 0.05f));

            // ── 3. Add / fetch components ────────────────────────────────────
            var leftShooter  = GetOrAdd<WebShooter>(leftCtrl);
            var rightShooter = GetOrAdd<WebShooter>(rightCtrl);
            var leftPull     = GetOrAdd<ObjectPullGrab>(leftCtrl);
            var rightPull    = GetOrAdd<ObjectPullGrab>(rightCtrl);
            var swing        = GetOrAdd<SwingPhysics>(xrOrigin);
            var propulsion   = GetOrAdd<SelfPropulsion>(xrOrigin);
            var climber      = GetOrAdd<WallClimber>(xrOrigin);
            var tether       = GetOrAdd<WebTether>(xrOrigin);

            // ── 4. Configure WebShooter – Left ───────────────────────────────
            var soLeft = new SerializedObject(leftShooter);
            soLeft.FindProperty("isLeftHand").boolValue      = true;
            soLeft.FindProperty("maxWebDistance").floatValue = 40f;
            soLeft.FindProperty("webSag").floatValue         = 0.04f;
            soLeft.FindProperty("webOriginPoint").objectReferenceValue = webOriginLeft;
            soLeft.ApplyModifiedProperties();

            // ── 5. Configure WebShooter – Right ──────────────────────────────
            var soRight = new SerializedObject(rightShooter);
            soRight.FindProperty("isLeftHand").boolValue      = false;
            soRight.FindProperty("maxWebDistance").floatValue = 40f;
            soRight.FindProperty("webSag").floatValue         = 0.04f;
            soRight.FindProperty("webOriginPoint").objectReferenceValue = webOriginRight;
            soRight.ApplyModifiedProperties();

            // ── 6. Configure ObjectPullGrab – Left ───────────────────────────
            var soPleft = new SerializedObject(leftPull);
            soPleft.FindProperty("isLeftHand").boolValue    = true;
            soPleft.FindProperty("pullRange").floatValue    = 20f;
            soPleft.FindProperty("pullSpeed").floatValue    = 14f;
            soPleft.FindProperty("snapDistance").floatValue = 0.4f;
            soPleft.FindProperty("handAnchor").objectReferenceValue = webOriginLeft;
            soPleft.ApplyModifiedProperties();

            // ── 7. Configure ObjectPullGrab – Right ──────────────────────────
            var soPright = new SerializedObject(rightPull);
            soPright.FindProperty("isLeftHand").boolValue    = false;
            soPright.FindProperty("pullRange").floatValue    = 20f;
            soPright.FindProperty("pullSpeed").floatValue    = 14f;
            soPright.FindProperty("snapDistance").floatValue = 0.4f;
            soPright.FindProperty("handAnchor").objectReferenceValue = webOriginRight;
            soPright.ApplyModifiedProperties();

            // ── 8. Configure SwingPhysics ────────────────────────────────────
            var soSwing = new SerializedObject(swing);
            soSwing.FindProperty("gravity").floatValue          = 12f;
            soSwing.FindProperty("maxSpeed").floatValue         = 22f;
            soSwing.FindProperty("airDrag").floatValue          = 0.008f;
            soSwing.FindProperty("swingMinHeight").floatValue   = 1.5f;
            soSwing.FindProperty("swingMinDist").floatValue     = 3f;
            soSwing.FindProperty("releaseUpBoost").floatValue   = 4f;
            soSwing.FindProperty("groundJumpForce").floatValue  = 18f;
            soSwing.FindProperty("groundJumpMaxHeight").floatValue = 1.5f;
            soSwing.FindProperty("groundJumpMaxDist").floatValue   = 6f;
            soSwing.FindProperty("grappleSpeed").floatValue     = 14f;
            soSwing.FindProperty("dualPullSpeed").floatValue    = 16f;
            soSwing.FindProperty("dualGrabDistance").floatValue = 1.2f;
            soSwing.FindProperty("groundRadius").floatValue     = 0.15f;
            soSwing.FindProperty("leftShooter").objectReferenceValue  = leftShooter;
            soSwing.FindProperty("rightShooter").objectReferenceValue = rightShooter;
            soSwing.FindProperty("wallClimber").objectReferenceValue  = climber;
            soSwing.FindProperty("webTether").objectReferenceValue    = tether;
            soSwing.ApplyModifiedProperties();

            // ── 9b. Configure WallClimber ────────────────────────────────────
            var soClimb = new SerializedObject(climber);
            soClimb.FindProperty("leftController").objectReferenceValue  = leftCtrl.transform;
            soClimb.FindProperty("rightController").objectReferenceValue = rightCtrl.transform;
            soClimb.ApplyModifiedProperties();

            // ── 9c. Configure WebTether ──────────────────────────────────────
            var soTether = new SerializedObject(tether);
            soTether.FindProperty("leftShooter").objectReferenceValue  = leftShooter;
            soTether.FindProperty("rightShooter").objectReferenceValue = rightShooter;
            soTether.ApplyModifiedProperties();

            // ── 9. Configure SelfPropulsion ──────────────────────────────────
            var soProp = new SerializedObject(propulsion);
            soProp.FindProperty("pushForce").floatValue        = 10f;
            soProp.FindProperty("cooldown").floatValue         = 0.6f;
            soProp.FindProperty("upwardBias").floatValue       = 0.25f;
            soProp.FindProperty("maxAnchorSpread").floatValue  = 5f;
            soProp.FindProperty("pullBackThreshold").floatValue = 1.5f;
            soProp.FindProperty("slingshotForce").floatValue   = 16f;
            soProp.FindProperty("slingshotUpward").floatValue  = 6f;
            soProp.FindProperty("leftShooter").objectReferenceValue   = leftShooter;
            soProp.FindProperty("rightShooter").objectReferenceValue  = rightShooter;
            soProp.FindProperty("swingPhysics").objectReferenceValue  = swing;
            soProp.ApplyModifiedProperties();

            // ── 10. Mark scene dirty and save ────────────────────────────────
            EditorSceneManager.MarkSceneDirty(
                UnityEngine.SceneManagement.SceneManager.GetActiveScene());
            EditorSceneManager.SaveOpenScenes();

            Debug.Log("[SpiderMan] Setup complete! All components attached and wired.");

            EditorUtility.DisplayDialog("SpiderMan Setup",
                "Setup complete!\n\n" +
                "Components attached to:\n" +
                $"  • {xrOrigin.name}  →  SwingPhysics, SelfPropulsion, WallClimber\n" +
                $"  • {leftCtrl.name}  →  WebShooter (Left), ObjectPullGrab (Left)\n" +
                $"  • {rightCtrl.name} →  WebShooter (Right), ObjectPullGrab (Right)\n\n" +
                "Child transforms created:\n" +
                "  • WebOrigin_Left  (under Left Controller)\n" +
                "  • WebOrigin_Right (under Right Controller)\n\n" +
                "All cross-references wired automatically.",
                "Done");
        }

        // ── Helpers ──────────────────────────────────────────────────────────

        static GameObject FindByName(string name)
        {
            return GameObject.Find(name);
        }

        static Transform FindChildRecursive(GameObject root, string name)
        {
            foreach (Transform t in root.GetComponentsInChildren<Transform>(true))
                if (t.name == name) return t;
            return null;
        }

        static Transform GetOrCreateChild(Transform parent, string name, Vector3 localPos)
        {
            var existing = parent.Find(name);
            if (existing != null) return existing;

            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            go.transform.localPosition = localPos;
            go.transform.localRotation = Quaternion.identity;
            go.transform.localScale    = Vector3.one;
            return go.transform;
        }

        static T GetOrAdd<T>(GameObject go) where T : Component
        {
            return go.GetComponent<T>() ?? go.AddComponent<T>();
        }

        static T GetOrAdd<T>(Transform t) where T : Component
            => GetOrAdd<T>(t.gameObject);
    }
}
