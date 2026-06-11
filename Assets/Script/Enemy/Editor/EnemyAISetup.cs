#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.Animations.Rigging;

/// <summary>
/// One-click Enemy AI setup tool.
///   Tools → Enemy AI → Setup as Shooting Enemy
///   Tools → Enemy AI → Setup as Melee Enemy
///
/// Select the Survivalist root GameObject in the scene (or prefab stage),
/// then run either menu item. All components are created and wired automatically.
/// A full checklist is printed to the Console on completion.
/// </summary>
public static class EnemyAISetup
{
    // ─── Menu Items ───────────────────────────────────────────────────────────

    [MenuItem("Tools/Enemy AI/Setup as Shooting Enemy")]
    private static void SetupShooting() => RunSetup(EnemyAI.CombatType.Shooting);

    [MenuItem("Tools/Enemy AI/Setup as Shooting Enemy", true)]
    private static bool ValidateShooting() => Selection.activeGameObject != null;

    [MenuItem("Tools/Enemy AI/Setup as Melee Enemy")]
    private static void SetupMelee() => RunSetup(EnemyAI.CombatType.Melee);

    [MenuItem("Tools/Enemy AI/Setup as Melee Enemy", true)]
    private static bool ValidateMelee() => Selection.activeGameObject != null;

    // ─── Core Setup ───────────────────────────────────────────────────────────

    private static void RunSetup(EnemyAI.CombatType combatType)
    {
        GameObject root = Selection.activeGameObject;
        if (root == null)
        {
            EditorUtility.DisplayDialog("Enemy AI Setup",
                "No GameObject selected.\nSelect the enemy root in the scene or prefab stage.", "OK");
            return;
        }

        Undo.SetCurrentGroupName("Enemy AI Setup");
        int undoGroup = Undo.GetCurrentGroup();

        // ── 1. Physics / Navigation ───────────────────────────────────────────
        EnsureCapsuleCollider(root);

        NavMeshAgent agent = EnsureComponent<NavMeshAgent>(root);
        agent.height                = 1.8f;
        agent.radius                = 0.35f;
        agent.speed                 = 3f;
        agent.angularSpeed          = 240f;
        agent.acceleration          = 10f;
        agent.stoppingDistance      = 0.5f;
        agent.obstacleAvoidanceType = ObstacleAvoidanceType.LowQualityObstacleAvoidance;

        // ── 2. Audio Source ───────────────────────────────────────────────────
        AudioSource audio  = EnsureComponent<AudioSource>(root);
        audio.spatialBlend = 1f;
        audio.minDistance  = 2f;
        audio.maxDistance  = 20f;
        audio.rolloffMode  = AudioRolloffMode.Linear;

        // ── 3. Animation Rigging (Shooting only) ──────────────────────────────
        bool spineFound = false;
        Rig rig = null;
        Transform aimTargetTransform = null;

        if (combatType == EnemyAI.CombatType.Shooting)
        {
            RigBuilder rigBuilder = EnsureComponent<RigBuilder>(root);

            GameObject rigLayerGO = FindOrCreateChild(root, "AimRigLayer");
            rig = EnsureComponent<Rig>(rigLayerGO);
            RegisterRigLayer(rigBuilder, rig);

            Transform spineBone = FindBoneRecursive(root.transform, "spine_02")
                               ?? FindBoneRecursive(root.transform, "spine_01")
                               ?? FindBoneRecursive(root.transform, "Spine");

            // World-space aim target
            string aimName     = root.name + "_AimTarget";
            GameObject aimGO   = GameObject.Find(aimName) ?? new GameObject(aimName);
            Undo.RegisterCreatedObjectUndo(aimGO, "Create AimTarget");
            aimGO.transform.position = root.transform.position + root.transform.forward * 5f + Vector3.up * 1.5f;
            aimTargetTransform = aimGO.transform;

            if (spineBone != null)
            {
                spineFound = true;
                GameObject aimConsGO = FindOrCreateChild(rigLayerGO, "UpperBodyAim");
                MultiAimConstraint mac = EnsureComponent<MultiAimConstraint>(aimConsGO);
                ConfigureMultiAimConstraint(mac, spineBone, aimTargetTransform);
            }
            else
            {
                Debug.LogWarning("[EnemyAISetup] spine_02 bone not found — add MultiAimConstraint manually.");
            }
        }

        // ── 4. Eye point (head bone) ──────────────────────────────────────────
        Transform headBone = FindBoneRecursive(root.transform, "head");
        GameObject eyeGO   = FindOrCreateChild(headBone != null ? headBone.gameObject : root, "EyePoint");
        eyeGO.transform.localPosition = new Vector3(0f, 0.1f, 0.15f);
        eyeGO.transform.localRotation = Quaternion.identity;

        // ── 5. Gun root (hand_r → GunRoot child) ─────────────────────────────
        Transform handR      = FindBoneRecursive(root.transform, "hand_r")
                            ?? FindBoneRecursive(root.transform, "RightHand");
        GameObject gunRootGO = FindOrCreateChild(handR != null ? handR.gameObject : root, "GunRoot");
        gunRootGO.transform.localPosition = Vector3.zero;
        gunRootGO.transform.localRotation = Quaternion.identity;
        // Shooting enemies: show gun. Melee: hide.
        gunRootGO.SetActive(combatType == EnemyAI.CombatType.Shooting);

        // ── 6. Muzzle point (child of GunRoot) ───────────────────────────────
        GameObject muzzleGO = null;
        if (combatType == EnemyAI.CombatType.Shooting)
        {
            muzzleGO = FindOrCreateChild(gunRootGO, "MuzzlePoint");
            muzzleGO.transform.localPosition = new Vector3(0f, 0f, 0.35f);
            muzzleGO.transform.localRotation = Quaternion.identity;
        }

        // ── 7. Bullet trail (Shooting only) ───────────────────────────────────
        LineRenderer lr = null;
        if (combatType == EnemyAI.CombatType.Shooting && muzzleGO != null)
        {
            GameObject trailGO = FindOrCreateChild(muzzleGO, "BulletTrail");
            lr = EnsureComponent<LineRenderer>(trailGO);
            lr.positionCount     = 2;
            lr.useWorldSpace     = true;
            lr.startWidth        = 0.015f;
            lr.endWidth          = 0.004f;
            lr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            lr.receiveShadows    = false;
            lr.enabled           = false;

            const string matPath = "Assets/Script/Enemy/BulletTrailMat.mat";
            if (lr.sharedMaterial == null)
            {
                Material mat = AssetDatabase.LoadAssetAtPath<Material>(matPath)
                            ?? new Material(Shader.Find("Sprites/Default")) { name = "BulletTrailMat" };
                if (AssetDatabase.GetAssetPath(mat).Length == 0)
                    AssetDatabase.CreateAsset(mat, matPath);
                lr.sharedMaterial = mat;
            }
        }

        // ── 8. EnemyHealth ────────────────────────────────────────────────────
        EnemyHealth health = EnsureComponent<EnemyHealth>(root);
        if (health.ragdollRoot == null)
        {
            Transform pelvis = FindBoneRecursive(root.transform, "pelvis");
            if (pelvis != null) health.ragdollRoot = pelvis;
        }
        if (health.hitRenderers == null || health.hitRenderers.Length == 0)
            health.hitRenderers = root.GetComponentsInChildren<SkinnedMeshRenderer>();

        // ── 9. EnemyAI ────────────────────────────────────────────────────────
        EnemyAI ai             = EnsureComponent<EnemyAI>(root);
        ai.combatType          = combatType;
        ai.eyeTransform        = eyeGO.transform;
        ai.audioSource         = audio;
        ai.obstacleMask        = LayerMask.GetMask("Default");
        ai.bulletHitMask       = LayerMask.GetMask("Default");
        ai.gun.gunRoot         = gunRootGO;

        if (combatType == EnemyAI.CombatType.Shooting)
        {
            ai.muzzlePoint         = muzzleGO != null ? muzzleGO.transform : null;
            ai.bulletTrailRenderer = lr;
            ai.aimRig              = rig;
            ai.aimTarget           = aimTargetTransform;
        }
        else
        {
            // Melee defaults: tighter combat range
            ai.combatRange    = 2.5f;
            ai.melee.attackRange = 1.8f;
        }

        // ── 10. Tag ───────────────────────────────────────────────────────────
        if (TagExists("Enemy") && !root.CompareTag("Enemy"))
            root.tag = "Enemy";

        EditorUtility.SetDirty(root);
        Undo.CollapseUndoOperations(undoGroup);
        AssetDatabase.SaveAssets();

        // ── Print checklist ───────────────────────────────────────────────────
        string type = combatType == EnemyAI.CombatType.Shooting ? "Shooting" : "Melee";
        string log =
            $"[EnemyAISetup] ✓ '{root.name}' set up as {type} enemy.\n\n" +
            "ADDED:\n" +
            "  ✅ CapsuleCollider + NavMeshAgent\n" +
            "  ✅ AudioSource (3D, linear rolloff)\n" +
            (combatType == EnemyAI.CombatType.Shooting
                ? $"  ✅ RigBuilder + AimRigLayer\n" +
                  (spineFound ? "  ✅ MultiAimConstraint → spine_02\n"
                              : "  ⚠️  MultiAimConstraint — spine_02 not found, set manually\n") +
                  "  ✅ GunRoot (hand_r) + MuzzlePoint + BulletTrail\n"
                : "  ✅ GunRoot hidden (Melee)\n") +
            "  ✅ EyePoint → head bone\n" +
            "  ✅ EnemyHealth + EnemyAI\n\n" +
            "MANUAL STEPS:\n" +
            "  1. Assign Animator Controller — required states (no parameters needed):\n" +
            "       Looping : Idle, Walk, Run, AimIdle, FireContinuous, MeleeIdle\n" +
            "       One-shot: Shoot, Alert, Die, MeleeAttack_0, MeleeAttack_1\n" +
            "       (State names must match Animation Profile fields in EnemyAI Inspector)\n" +
            "  2. Bake NavMesh → Window → AI → Navigation → Bake\n" +
            "  3. Tag the Player as 'Player'\n" +
            "  4. Set ObstacleMask & BulletHitMask layers in EnemyAI Inspector\n" +
            (combatType == EnemyAI.CombatType.Shooting
                ? "  5. Add your gun mesh as a child of GunRoot (already positioned at hand_r)\n" +
                  "  6. Tune gun.positionOffset / gun.rotationOffset in EnemyAI Inspector\n"
                : "  5. For ragdoll: add Rigidbody + Collider to each bone under pelvis\n") +
            "  6. Press 'Update Animation Rigging' button in the RigBuilder component";

        Debug.Log(log);
        EditorUtility.DisplayDialog("Enemy AI Setup",
            $"Setup complete as {type} enemy!\nSee Console for the full checklist.", "Done");
    }

    // ─── Animation Rigging Helpers ────────────────────────────────────────────

    private static void RegisterRigLayer(RigBuilder rigBuilder, Rig rig)
    {
        using var rbSO = new SerializedObject(rigBuilder);
        SerializedProperty layers = rbSO.FindProperty("m_RigLayers");
        for (int i = 0; i < layers.arraySize; i++)
            if (layers.GetArrayElementAtIndex(i).FindPropertyRelative("m_Rig").objectReferenceValue == rig)
                return;

        layers.arraySize++;
        SerializedProperty last = layers.GetArrayElementAtIndex(layers.arraySize - 1);
        last.FindPropertyRelative("m_Rig").objectReferenceValue = rig;
        last.FindPropertyRelative("m_Active").boolValue         = true;
        rbSO.ApplyModifiedProperties();
    }

    private static void ConfigureMultiAimConstraint(MultiAimConstraint mac,
                                                     Transform constrained,
                                                     Transform aimTarget)
    {
        using var so = new SerializedObject(mac);

        SetPropSafe(so, "m_Data.m_ConstrainedObject", constrained);

        SerializedProperty arr = so.FindProperty("m_Data.m_SourceObjects.m_Array");
        if (arr != null)
        {
            arr.arraySize = 1;
            SerializedProperty elem = arr.GetArrayElementAtIndex(0);
            SetPropSafe(elem, "transform", aimTarget);
            SerializedProperty w = elem.FindPropertyRelative("weight");
            if (w != null) w.floatValue = 1f;
        }

        // Constrain X (pitch) only — Y rotation is handled by NavMesh/FaceTarget
        SetBoolSafe(so, "m_Data.m_Settings.m_ConstrainedXAxis", true);
        SetBoolSafe(so, "m_Data.m_Settings.m_ConstrainedYAxis", false);
        SetBoolSafe(so, "m_Data.m_Settings.m_ConstrainedZAxis", false);

        so.ApplyModifiedProperties();
    }

    // ─── Generic Helpers ──────────────────────────────────────────────────────

    private static T EnsureComponent<T>(GameObject go) where T : Component
    {
        T comp = go.GetComponent<T>();
        return comp != null ? comp : Undo.AddComponent<T>(go);
    }

    private static void EnsureCapsuleCollider(GameObject go)
    {
        if (go.GetComponent<Collider>() != null) return;
        CapsuleCollider col = Undo.AddComponent<CapsuleCollider>(go);
        col.height = 1.8f;
        col.radius = 0.35f;
        col.center = new Vector3(0f, 0.9f, 0f);
    }

    private static GameObject FindOrCreateChild(GameObject parent, string childName)
    {
        Transform t = parent.transform.Find(childName);
        if (t != null) return t.gameObject;

        GameObject child = new GameObject(childName);
        Undo.RegisterCreatedObjectUndo(child, $"Create {childName}");
        Undo.SetTransformParent(child.transform, parent.transform, $"Parent {childName}");
        child.transform.SetLocalPositionAndRotation(Vector3.zero, Quaternion.identity);
        child.transform.localScale = Vector3.one;
        return child;
    }

    private static Transform FindBoneRecursive(Transform root, string boneName)
    {
        if (root.name.Equals(boneName, System.StringComparison.OrdinalIgnoreCase)) return root;
        foreach (Transform c in root)
        {
            Transform r = FindBoneRecursive(c, boneName);
            if (r != null) return r;
        }
        return null;
    }

    private static bool TagExists(string tag)
    {
        try
        {
            return !string.IsNullOrEmpty(tag)
                && System.Array.IndexOf(UnityEditorInternal.InternalEditorUtility.tags, tag) >= 0;
        }
        catch { return false; }
    }

    private static void SetPropSafe(SerializedObject so, string path, Object val)
    {
        var p = so.FindProperty(path);
        if (p != null) p.objectReferenceValue = val;
        else Debug.LogWarning($"[EnemyAISetup] Property '{path}' not found — set manually in Inspector.");
    }

    private static void SetPropSafe(SerializedProperty parent, string rel, Object val)
    {
        var p = parent.FindPropertyRelative(rel);
        if (p != null) p.objectReferenceValue = val;
    }

    private static void SetBoolSafe(SerializedObject so, string path, bool val)
    {
        var p = so.FindProperty(path);
        if (p != null) p.boolValue = val;
    }
}
#endif
