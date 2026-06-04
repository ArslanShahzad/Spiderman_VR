using System.Collections.Generic;
using Unity.XR.CoreUtils;
using UnityEngine;

namespace SpiderMan
{
    /// Moves the XR Origin with manual pendulum physics.
    /// Suppresses the XR Interaction Toolkit GravityProvider and DynamicMoveProvider
    /// while a web is held so their CharacterController.Move() calls cannot fight us.
    [RequireComponent(typeof(XROrigin))]
    [AddComponentMenu("SpiderMan/Swing Physics")]
    public class SwingPhysics : MonoBehaviour
    {
        // ── Inspector ────────────────────────────────────────────────────────

        [Header("Swing Feel")]
        [Tooltip("Downward acceleration (m/s²) applied when airborne.")]
        [SerializeField] float gravity = 12f;

        [Tooltip("Maximum player speed (m/s) at any time during a swing.")]
        [SerializeField] float maxSpeed = 22f;

        [Tooltip("Fractional velocity loss per second — simulates air resistance. Keep below 0.03.")]
        [SerializeField, Range(0f, 0.1f)] float airDrag = 0.008f;

        [Tooltip("Scales the TANGENTIAL gravity applied while swinging. " +
                 "Only the perpendicular-to-rope component is used so the player never loses altitude during a swing. " +
                 "Higher = stronger pendulum arc. 0 = flat circular arc with no speed change.")]
        [SerializeField, Range(0f, 1f)] float swingGravityScale = 1f;

        [Header("Swing Conditions")]
        [Tooltip("Anchor must be at least this many metres ABOVE the player to qualify as a swing anchor.")]
        [SerializeField] float swingMinHeight = 1.5f;

        [Tooltip("Anchor must be at least this many metres away to qualify as a swing anchor.")]
        [SerializeField] float swingMinDist = 3f;

        [Header("Auto Launch (fires once when swing web attaches)")]
        [Tooltip("Horizontal speed (m/s) added in the camera's facing direction on web attach. " +
                 "This is the tangential velocity that creates the pendulum arc.")]
        [SerializeField] float launchForward = 12f;

        [Tooltip("Upward speed (m/s) added on web attach to lift the player into the arc.")]
        [SerializeField] float launchUpward = 8f;

        [Tooltip("Minimum upward speed (m/s) guaranteed the moment a swing web is released. " +
                 "Prevents an immediate drop and gives the Spider-Man momentum-carry feel. " +
                 "If the player is already moving faster upward, this is ignored.")]
        [SerializeField] float releaseUpBoost = 4f;

        [Header("Long Jump  (both webs near feet)")]
        [Tooltip("Upward launch speed (m/s) applied when both webs are fired at the ground near the player's feet.")]
        [SerializeField] float groundJumpForce = 18f;

        [Tooltip("An anchor is counted as a 'ground anchor' only when it is within this many metres above the player.")]
        [SerializeField] float groundJumpMaxHeight = 1.5f;

        [Tooltip("An anchor is counted as a 'ground anchor' only when it is within this horizontal distance from the player.")]
        [SerializeField] float groundJumpMaxDist = 6f;

        [Header("Grapple  (both webs → same IMMOVABLE object)")]
        [Tooltip("Speed (m/s) at which the player is pulled toward a shared immovable anchor.")]
        [SerializeField] float grappleSpeed = 14f;

        [Header("Dual Pull  (both webs → same Rigidbody)")]
        [Tooltip("Speed (m/s) at which a dual-webbed moveable object flies toward the player.")]
        [SerializeField] float dualPullSpeed = 16f;

        [Tooltip("Distance (m) at which a dual-pulled object snaps into the player's hands.")]
        [SerializeField] float dualGrabDistance = 1.2f;

        [Header("Ground Detection")]
        [Tooltip("Radius of the sphere used to detect the ground beneath the XR Origin.")]
        [SerializeField] float groundRadius = 0.15f;

        [Tooltip("Maximum height (m) above the ground at which the launch impulse still fires on web attach. " +
                 "Above this height the web attaches silently into a swing without the boost.")]
        [SerializeField] float launchGroundThreshold = 1f;

        [Tooltip("Layers that count as ground. Default = Everything.")]
        [SerializeField] LayerMask groundLayers = ~0;

        [Header("References")]
        [Tooltip("WebShooter component on the Left Controller.")]
        [SerializeField] WebShooter leftShooter;

        [Tooltip("WebShooter component on the Right Controller.")]
        [SerializeField] WebShooter rightShooter;

        // ── Public API ───────────────────────────────────────────────────────
        /// Current player velocity in world space.
        public Vector3 Velocity { get; private set; }

        /// True when XR Origin is within groundRadius of a ground surface.
        public bool IsGrounded { get; private set; }

        /// True when at least one web is in swing mode (high + far anchor).
        public bool IsSwinging => _leftSwing || _rightSwing;

        // True as long as EITHER trigger is held — used to suppress XR gravity.
        bool AnyWebHeld =>
            (leftShooter  != null && leftShooter.IsWebActive) ||
            (rightShooter != null && rightShooter.IsWebActive);

        // ── Private ──────────────────────────────────────────────────────────
        float _leftLen,  _rightLen;
        bool  _leftPrev, _rightPrev;
        bool  _leftSwing, _rightSwing;

        bool      _groundJumpReady = true;
        Rigidbody _dualPullTarget;

        Transform           _xrCamera;
        CharacterController _cc;

        // XR IT components that apply gravity via CC.Move() — disabled while web
        // is held so they cannot fight SwingPhysics position writes.
        MonoBehaviour[] _xrGravityBehaviours;

        // ── Unity lifecycle ──────────────────────────────────────────────────
        void Start()
        {
            _xrCamera = Camera.main != null ? Camera.main.transform : transform;
            _cc       = GetComponent<CharacterController>();

            var found = new List<MonoBehaviour>();
            foreach (MonoBehaviour mb in GetComponentsInChildren<MonoBehaviour>(true))
            {
                string t = mb.GetType().Name;
                if (t == "GravityProvider" || t == "DynamicMoveProvider")
                    found.Add(mb);
            }
            _xrGravityBehaviours = found.ToArray();
        }

        // LateUpdate runs AFTER all XR IT locomotion provider Update() calls,
        // so our final position write always wins.
        void LateUpdate()
        {
            bool webHeld = AnyWebHeld;

            // XR IT components are only allowed while the player is grounded AND no web is held.
            // Re-enabling them while airborne causes GravityProvider to fire CC.Move() with
            // accumulated velocity, which negates the release boost and makes the player drop instantly.
            bool xritActive = !webHeld && IsGrounded;
            foreach (var mb in _xrGravityBehaviours)
                if (mb != null) mb.enabled = xritActive;

            IsGrounded = Physics.CheckSphere(
                transform.position + Vector3.down * 0.05f,
                groundRadius, groundLayers, QueryTriggerInteraction.Ignore);

            ProcessWebAttachments();
            CheckDualWebCases();
            ApplyGravity();
            ApplyDragAndClamp();
            MovePlayer();
            EnforceSwingConstraints(); // after move so constraint acts on the real new position
            TickDualPull();
        }

        // ── Web attachment processing ─────────────────────────────────────────
        void ProcessWebAttachments()
        {
            bool l = leftShooter  != null && leftShooter.IsWebActive;
            bool r = rightShooter != null && rightShooter.IsWebActive;

            if (l && !_leftPrev)
            {
                // Use the full attachment distance as constraint length.
                // Constraint only fires when player swings past this radius — no first-frame snap.
                _leftLen   = Vector3.Distance(transform.position, leftShooter.AnchorPoint);
                _leftSwing = IsSwingAnchor(leftShooter.AnchorPoint);
                if (_leftSwing) Launch();
            }

            if (r && !_rightPrev)
            {
                _rightLen   = Vector3.Distance(transform.position, rightShooter.AnchorPoint);
                _rightSwing = IsSwingAnchor(rightShooter.AnchorPoint);
                if (_rightSwing) Launch();
            }

            // On the frame a swinging web is released, guarantee a minimum upward speed
            // so the player carries momentum upward instead of dropping immediately.
            if (_leftPrev  && !l && _leftSwing)  ApplyReleaseBoost();
            if (_rightPrev && !r && _rightSwing) ApplyReleaseBoost();

            if (!l) _leftSwing  = false;
            if (!r) _rightSwing = false;

            if (!l || !r)
            {
                _groundJumpReady = true;
                if (!l && !r) _dualPullTarget = null;
            }

            _leftPrev  = l;
            _rightPrev = r;
        }

        bool IsSwingAnchor(Vector3 anchor)
        {
            float dY   = anchor.y - transform.position.y;
            float dist = Vector3.Distance(transform.position, anchor);
            return dY >= swingMinHeight && dist >= swingMinDist;
        }

        // Fires once when a qualifying web attaches.
        // Forward is ALWAYS added — creates the tangential velocity for the pendulum arc.
        // Upward is only added when near the ground so the player doesn't rocket higher
        // when they attach a new web while already airborne mid-swing.
        void Launch()
        {
            Vector3 forward = new Vector3(_xrCamera.forward.x, 0f, _xrCamera.forward.z);
            if (forward.sqrMagnitude < 0.01f)
                forward = new Vector3(transform.forward.x, 0f, transform.forward.z);
            forward.Normalize();

            Velocity += forward * launchForward;
            if (IsNearGround())
                Velocity += Vector3.up * launchUpward;
        }

        // ── Special dual-web cases ────────────────────────────────────────────
        void CheckDualWebCases()
        {
            bool l = leftShooter  != null && leftShooter.IsWebActive;
            bool r = rightShooter != null && rightShooter.IsWebActive;
            if (!l || !r) return;

            // Long jump — both anchors on the ground near the player's feet
            if (_groundJumpReady
                && IsGroundAnchor(leftShooter.AnchorPoint)
                && IsGroundAnchor(rightShooter.AnchorPoint))
            {
                _groundJumpReady = false;
                Velocity = Vector3.up * groundJumpForce;
                leftShooter.ForceDetach();
                rightShooter.ForceDetach();
                return;
            }

            if (leftShooter.AnchorObject == null
                || leftShooter.AnchorObject != rightShooter.AnchorObject) return;

            var rb = leftShooter.AnchorRigidbody;

            if (rb != null && !rb.isKinematic)
            {
                // Moveable — start dual-pulling toward the player
                if (_dualPullTarget == null) _dualPullTarget = rb;
            }
            else
            {
                // Immovable — grapple player toward the midpoint
                Vector3 mid = (leftShooter.AnchorPoint + rightShooter.AnchorPoint) * 0.5f;
                Velocity    = (mid - transform.position).normalized * grappleSpeed;
                _leftSwing  = false;
                _rightSwing = false;
            }
        }

        bool IsGroundAnchor(Vector3 anchor)
        {
            float dY     = anchor.y - transform.position.y;
            float horizD = Vector3.Distance(
                new Vector3(anchor.x, transform.position.y, anchor.z), transform.position);
            return dY < groundJumpMaxHeight && horizD < groundJumpMaxDist;
        }

        // True when the player is on the ground or there is ground directly below within
        // launchGroundThreshold metres. Uses a downward raycast so only floor below counts
        // (walls beside the player at height do NOT trigger this).
        bool IsNearGround() =>
            IsGrounded ||
            Physics.Raycast(transform.position, Vector3.down,
                            launchGroundThreshold, groundLayers,
                            QueryTriggerInteraction.Ignore);

        // Called the frame a swinging web is released.
        // Lifts the upward velocity to at least releaseUpBoost so the player arcs
        // upward briefly before gravity takes over — the Spider-Man momentum carry.
        void ApplyReleaseBoost()
        {
            if (Velocity.y < releaseUpBoost)
                Velocity = new Vector3(Velocity.x, releaseUpBoost, Velocity.z);
        }

        // ── Dual-pull object toward the player ───────────────────────────────
        void TickDualPull()
        {
            if (_dualPullTarget == null) return;

            bool l = leftShooter  != null && leftShooter.IsWebActive;
            bool r = rightShooter != null && rightShooter.IsWebActive;

            if (!l || !r || leftShooter.AnchorObject != rightShooter.AnchorObject)
            {
                _dualPullTarget = null;
                return;
            }

            Vector3 delta = transform.position - _dualPullTarget.position;

            if (delta.magnitude <= dualGrabDistance)
            {
                _dualPullTarget.linearVelocity = Vector3.zero;
                _dualPullTarget.isKinematic    = true;
                _dualPullTarget.transform.SetParent(transform, true);
                _dualPullTarget = null;
                leftShooter.ForceDetach();
                rightShooter.ForceDetach();
                return;
            }

            _dualPullTarget.linearVelocity = delta.normalized * dualPullSpeed;
        }

        // ── Physics ──────────────────────────────────────────────────────────
        void ApplyGravity()
        {
            if (AnyWebHeld)
            {
                if (!IsGrounded && IsSwinging)
                {
                    // Apply only the tangential component of gravity (perpendicular to the rope).
                    // Tangential gravity drives the pendulum arc — player speeds up swinging down
                    // and slows down swinging up — without any net altitude loss, because the
                    // radial component (straight along the rope) is cancelled by rope tension anyway.
                    Vector3 arm = PrimaryArmNormalized();
                    if (arm != Vector3.zero)
                    {
                        Vector3 g     = Vector3.down * (gravity * swingGravityScale);
                        Vector3 gTang = g - Vector3.Dot(g, arm) * arm; // strip radial part
                        Velocity += gTang * Time.deltaTime;
                    }
                }
                return;
            }

            // No web — full gravity for natural free-fall after release.
            if (!IsGrounded)
                Velocity += Vector3.down * (gravity * Time.deltaTime);
        }

        // Returns the normalised arm vector of whichever swinging web is active.
        Vector3 PrimaryArmNormalized()
        {
            if (_leftSwing && leftShooter != null && leftShooter.IsWebActive)
            {
                Vector3 a = transform.position - leftShooter.AnchorPoint;
                if (a.sqrMagnitude > 0.001f) return a.normalized;
            }
            if (_rightSwing && rightShooter != null && rightShooter.IsWebActive)
            {
                Vector3 a = transform.position - rightShooter.AnchorPoint;
                if (a.sqrMagnitude > 0.001f) return a.normalized;
            }
            return Vector3.zero;
        }

        void EnforceSwingConstraints()
        {
            if (_leftSwing)  EnforceConstraint(leftShooter,  _leftLen);
            if (_rightSwing) EnforceConstraint(rightShooter, _rightLen);
        }

        void EnforceConstraint(WebShooter shooter, float constraintLen)
        {
            if (shooter == null || !shooter.IsWebActive) return;

            Vector3 arm  = transform.position - shooter.AnchorPoint;
            float   dist = arm.magnitude;
            if (dist <= constraintLen) return;

            transform.position = shooter.AnchorPoint + arm.normalized * constraintLen;

            // Strip only the outward (rope-stretching) velocity.
            // The surviving tangential component IS the pendulum arc.
            float radial = Vector3.Dot(Velocity, arm.normalized);
            if (radial > 0f)
                Velocity -= arm.normalized * radial;
        }

        void ApplyDragAndClamp()
        {
            Velocity *= 1f - airDrag * Time.deltaTime * 60f;
            Velocity  = Vector3.ClampMagnitude(Velocity, maxSpeed);
        }

        void MovePlayer()
        {
            bool webHeld = AnyWebHeld;

            if (!webHeld && IsGrounded)
            {
                Velocity = Vector3.zero;
                return;
            }

            // Briefly disable the CharacterController so our transform.position
            // write is not fought or overridden by the CC's internal resolution.
            bool ccWasEnabled = _cc != null && _cc.enabled;
            if (ccWasEnabled) _cc.enabled = false;

            transform.position += Velocity * Time.deltaTime;

            if (ccWasEnabled) _cc.enabled = true;
        }

        // ── Public API ───────────────────────────────────────────────────────
        /// Apply an instantaneous velocity change (called by SelfPropulsion).
        public void AddImpulse(Vector3 impulse) => Velocity += impulse;
    }
}
