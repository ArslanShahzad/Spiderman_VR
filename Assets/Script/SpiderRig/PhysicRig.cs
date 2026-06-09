using UnityEngine;

public class PhysicRig : MonoBehaviour
{
    public Transform playerHead;
    public Transform LeftController;
    public Transform RightController;

    public ConfigurableJoint RightArmJoint;
    public ConfigurableJoint HeadJoint;
    public ConfigurableJoint LeftArmJoint;
    public CapsuleCollider BodyCollider;

    public float heightOffset = 0.5f;
    public float Heightmax = 2.0f;
    public float Heightmin = 0.5f;

    [Header("Wall Climb")]
    [Tooltip("XR Origin root transform that physically moves the player. Assign in Inspector.")]
    public Transform playerRoot;

    [Tooltip("Layers treated as climbable. Exclude moveable/physics objects.")]
    public LayerMask climbLayers = ~0;

    [Tooltip("Sphere radius (m) around each controller for auto-grab detection.")]
    public float autoGrabReach = 0.12f;

    [Tooltip("How far (m) the controller must be pulled from its stick point before releasing.")]
    public float detachDistance = 0.4f;

    /// True while at least one hand is stuck to a wall.
    public bool IsClimbing => _lStuck || _rStuck;

    // ── private state ────────────────────────────────────────────────────────
    CharacterController _cc;
    bool    _lStuck,   _rStuck;
    Vector3 _lStickPt, _rStickPt;
    readonly Collider[] _overlapBuffer = new Collider[8];

    // ── Unity lifecycle ──────────────────────────────────────────────────────
    void Start()
    {
        if (playerRoot != null)
            _cc = playerRoot.GetComponent<CharacterController>();
    }

    void Update() { }

    void FixedUpdate()
    {
        BodyCollider.height = Mathf.Clamp(playerHead.localPosition.y + heightOffset, Heightmin, Heightmax);
        BodyCollider.center = new Vector3(playerHead.localPosition.x, BodyCollider.height / 2, playerHead.localPosition.z);

        LeftArmJoint.targetPosition = LeftController.localPosition;
        LeftArmJoint.targetRotation = LeftController.localRotation;

        RightArmJoint.targetPosition = RightController.localPosition;
        RightArmJoint.targetRotation = RightController.localRotation;

        HeadJoint.targetPosition = playerHead.localPosition;

        UpdateClimb();
    }

    // ── Wall climb ───────────────────────────────────────────────────────────

    void UpdateClimb()
    {
        if (playerRoot == null) return;

        TryAutoGrab(LeftController,  ref _lStuck, ref _lStickPt);
        TryAutoGrab(RightController, ref _rStuck, ref _rStickPt);
        CheckDetach(LeftController,  ref _lStuck,  _lStickPt);
        CheckDetach(RightController, ref _rStuck,  _rStickPt);

        if (!_lStuck && !_rStuck) return;

        // delta = stickPoint – controllerPos
        // Controller pulled down → delta Y is positive → player root moves up.
        Vector3 sum   = Vector3.zero;
        int     count = 0;

        if (_lStuck && LeftController  != null) { sum += _lStickPt - LeftController.position;  count++; }
        if (_rStuck && RightController != null) { sum += _rStickPt - RightController.position; count++; }

        Vector3 delta = sum / count;

        if (_cc != null && _cc.enabled) _cc.Move(delta);
        else                            playerRoot.position += delta;
    }

    // Attach if a static surface is within autoGrabReach and the hand isn't already stuck.
    void TryAutoGrab(Transform ctrl, ref bool stuck, ref Vector3 stickPt)
    {
        if (stuck || ctrl == null) return;

        int hitCount = Physics.OverlapSphereNonAlloc(ctrl.position, autoGrabReach,
            _overlapBuffer, climbLayers, QueryTriggerInteraction.Ignore);

        float    bestDist = float.MaxValue;
        Collider bestCol  = null;

        for (int i = 0; i < hitCount; i++)
        {
            var col = _overlapBuffer[i];
            // Ignore anything on a rigidbody — only static geometry is climbable.
            if (col.GetComponentInParent<Rigidbody>() != null) continue;

            Vector3 closest = col.ClosestPoint(ctrl.position);
            float   d       = Vector3.Distance(ctrl.position, closest);
            if (d < bestDist) { bestDist = d; bestCol = col; }
        }

        if (bestCol == null) return;

        stickPt = bestCol.ClosestPoint(ctrl.position);
        stuck   = true;
    }

    // Release the grab once the controller is pulled more than detachDistance from its stick point.
    void CheckDetach(Transform ctrl, ref bool stuck, Vector3 stickPt)
    {
        if (!stuck || ctrl == null) return;
        if (Vector3.Distance(ctrl.position, stickPt) > detachDistance)
            stuck = false;
    }
}
