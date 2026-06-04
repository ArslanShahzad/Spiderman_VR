using UnityEngine;

namespace SpiderMan
{
    /// Web tether: when one web is attached to a STATIC surface and the other to a
    /// moveable RIGIDBODY, a constraining web line is drawn between the two anchor
    /// points and the Rigidbody is prevented from moving beyond the initial rope length.
    ///
    /// Condition to activate:
    ///   Left  web → static (no Rigidbody)   +   Right web → Rigidbody
    ///   OR
    ///   Left  web → Rigidbody               +   Right web → static (no Rigidbody)
    ///
    /// The tether releases automatically when either web is released.
    [AddComponentMenu("SpiderMan/Web Tether")]
    public class WebTether : MonoBehaviour
    {
        // ── Inspector ────────────────────────────────────────────────────────

        [Header("Rope Physics")]
        [Tooltip("Inward spring force (N/m of excess stretch) applied to the Rigidbody " +
                 "when it exceeds the rope length. Higher = stiffer rope.")]
        [SerializeField] float ropeSpringForce = 80f;

        [Tooltip("Damping applied to the outward velocity component when the rope is taut. " +
                 "Higher = rope snaps tight more quickly.")]
        [SerializeField] float ropeDamping = 12f;

        [Header("Visual")]
        [Tooltip("Width of the tether line drawn between the two anchor points.")]
        [SerializeField] float lineWidth = 0.014f;

        [Tooltip("Colour of the tether line.")]
        [SerializeField] Color tetherColor = new Color(0.88f, 0.95f, 1f);

        [Tooltip("Sag per metre of rope length (0 = taut, 0.05 = light droop).")]
        [SerializeField, Range(0f, 0.15f)] float ropeSag = 0.04f;

        [Header("References")]
        [Tooltip("WebShooter on the Left Controller.")]
        [SerializeField] WebShooter leftShooter;

        [Tooltip("WebShooter on the Right Controller.")]
        [SerializeField] WebShooter rightShooter;

        // ── Public API ───────────────────────────────────────────────────────

        /// True while a tether is active between a static anchor and a Rigidbody.
        public bool IsTethered { get; private set; }

        // ── Private ──────────────────────────────────────────────────────────
        Vector3   _staticAnchorPt; // world-space point on the static surface
        Rigidbody _targetRb;       // the Rigidbody being constrained
        float     _ropeLength;     // maximum allowed distance set at tether creation

        LineRenderer _line;

        // ── Unity lifecycle ──────────────────────────────────────────────────
        void Awake()  => BuildLineRenderer();

        // Physics constraint runs in FixedUpdate so AddForce timing is correct.
        void FixedUpdate()
        {
            if (!IsTethered || _targetRb == null) return;
            ApplyRopeConstraint();
        }

        // Condition detection and line rendering run in LateUpdate so
        // WebShooter.Update() has already processed trigger input this frame.
        void LateUpdate()
        {
            UpdateTetherState();
            if (IsTethered) UpdateLine();
        }

        // ── Tether state ─────────────────────────────────────────────────────
        void UpdateTetherState()
        {
            bool l = leftShooter  != null && leftShooter.IsWebActive;
            bool r = rightShooter != null && rightShooter.IsWebActive;

            if (!l || !r) { ReleaseTether(); return; }

            Rigidbody lRb = leftShooter.AnchorRigidbody;
            Rigidbody rRb = rightShooter.AnchorRigidbody;

            bool lStatic  = lRb == null;
            bool rStatic  = rRb == null;
            bool lMovable = lRb != null && !lRb.isKinematic;
            bool rMovable = rRb != null && !rRb.isKinematic;

            if (lStatic && rMovable)
                ArmTether(leftShooter.AnchorPoint, rRb);
            else if (rStatic && lMovable)
                ArmTether(rightShooter.AnchorPoint, lRb);
            else
                ReleaseTether();
        }

        void ArmTether(Vector3 staticPt, Rigidbody rb)
        {
            // Re-arm if this is a new target
            if (!IsTethered || _targetRb != rb)
            {
                _staticAnchorPt = staticPt;
                _targetRb       = rb;
                _ropeLength     = Vector3.Distance(staticPt, rb.position);
                IsTethered      = true;
                _line.enabled   = true;
            }
        }

        void ReleaseTether()
        {
            IsTethered    = false;
            _targetRb     = null;
            _line.enabled = false;
        }

        // ── Physics ──────────────────────────────────────────────────────────
        void ApplyRopeConstraint()
        {
            Vector3 offset = _targetRb.position - _staticAnchorPt;
            float   dist   = offset.magnitude;
            if (dist <= _ropeLength || dist < 0.001f) return;

            Vector3 dir    = offset / dist;
            float   excess = dist - _ropeLength;

            // Spring force pulling the Rigidbody back within rope length
            _targetRb.AddForce(-dir * (excess * ropeSpringForce), ForceMode.Force);

            // Damp the outward velocity so the rope doesn't keep oscillating
            float outSpeed = Vector3.Dot(_targetRb.linearVelocity, dir);
            if (outSpeed > 0f)
                _targetRb.AddForce(-dir * (outSpeed * ropeDamping), ForceMode.Force);
        }

        // ── Visual ───────────────────────────────────────────────────────────
        void UpdateLine()
        {
            if (_targetRb == null) return;

            Vector3 start = _staticAnchorPt;
            Vector3 end   = _targetRb.position;
            float   dist  = (end - start).magnitude;

            // 5-point smooth catenary between the two anchored points
            for (int i = 0; i < 5; i++)
            {
                float   t     = i / 4f;
                Vector3 pt    = Vector3.Lerp(start, end, t);
                float   droop = dist * ropeSag * Mathf.Sin(t * Mathf.PI);
                _line.SetPosition(i, pt + Vector3.down * droop);
            }
        }

        void BuildLineRenderer()
        {
            var go = new GameObject("TetherLine");
            go.transform.SetParent(transform, false);
            _line = go.AddComponent<LineRenderer>();

            _line.positionCount     = 5;
            _line.useWorldSpace     = true;
            _line.textureMode       = LineTextureMode.Tile;
            _line.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            _line.receiveShadows    = false;
            _line.numCapVertices    = 4;
            _line.startWidth        = lineWidth;
            _line.endWidth          = lineWidth;

            var shader = Shader.Find("Universal Render Pipeline/Unlit")
                      ?? Shader.Find("Unlit/Color");
            if (shader != null)
                _line.sharedMaterial = new Material(shader) { color = tetherColor };

            _line.enabled = false;
        }
    }
}
