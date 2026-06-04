using System.Collections.Generic;
using Unity.XR.CoreUtils;
using UnityEngine;

namespace SpiderMan
{
    /// Moves the XR Origin with manual pendulum physics.
    /// Uses CharacterController.Move() for ground-collision-aware movement.
    /// Suppresses the XR Interaction Toolkit GravityProvider and DynamicMoveProvider
    /// while a web is held so their Move() calls cannot interfere.
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

        [Header("Release Boost  (fires when a swing web is released)")]
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

        [Tooltip("Layers that count as ground. Default = Everything.")]
        [SerializeField] LayerMask groundLayers = ~0;

        [Header("Dual-Web Slingshot")]
        [Tooltip("Maximum distance (m) between both anchor points for slingshot mode. " +
                 "When both anchors are within this spread, auto-launch and grapple are suppressed — " +
                 "SelfPropulsion's pull-back gesture fires the launch instead.")]
        [SerializeField] float slingshotAnchorSpread = 5f;

        [Header("Airborne Rotation")]
        [Tooltip("Camera must face more than this many degrees away from the XR Origin forward " +
                 "before auto-rotation kicks in. Acts as a look dead zone while airborne.")]
        [SerializeField, Range(0f, 60f)] float rotationDeadZone = 30f;

        [Tooltip("Speed (degrees/s) at which the XR Origin rotates to follow the camera once " +
                 "the dead zone is exceeded.")]
        [SerializeField] float rotationSpeed = 60f;

        [Header("References")]
        [Tooltip("WebShooter component on the Left Controller.")]
        [SerializeField] WebShooter leftShooter;

        [Tooltip("WebShooter component on the Right Controller.")]
        [SerializeField] WebShooter rightShooter;

        [Tooltip("WallClimber component on this XR Origin. Optional — leave empty if not using wall climbing.")]
        [SerializeField] WallClimber wallClimber;

        [Tooltip("WebTether component on this XR Origin. Optional — leave empty if not using web tethering.")]
        [SerializeField] WebTether webTether;

        // ── Public API ───────────────────────────────────────────────────────
        /// Current player velocity in world space.
        public Vector3 Velocity { get; private set; }

        /// True when XR Origin is within groundRadius of a ground surface.
        public bool IsGrounded { get; private set; }

        /// True when at least one web is in swing mode (high + far anchor).
        public bool IsSwinging => _leftSwing || _rightSwing;

        /// True when both webs are active and their anchor points are within slingshotAnchorSpread.
        /// SelfPropulsion reads this to decide whether to use the gesture-based launch.
        public bool IsSlingshotArmed { get; private set; }

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
            // ── Wall climbing (highest priority — suppresses all other movement) ──
            if (wallClimber != null && wallClimber.IsClimbing)
            {
                Velocity = Vector3.zero;
                IsSlingshotArmed = false;

                // Keep XRIT disabled while climbing so GravityProvider doesn't fight us
                foreach (var mb in _xrGravityBehaviours)
                    if (mb != null) mb.enabled = false;

                IsGrounded = Physics.CheckSphere(
                    transform.position + Vector3.down * 0.05f,
                    groundRadius, groundLayers, QueryTriggerInteraction.Ignore);

                Vector3 climbDelta = wallClimber.ConsumeClimbDelta();
                if (_cc != null && _cc.enabled)
                    _cc.Move(climbDelta);
                else
                    transform.position += climbDelta;

                ApplyAirborneRotation();
                return;
            }

            // Compute slingshot state first — ProcessWebAttachments and CheckDualWebCases both read it.
            bool lActive = leftShooter  != null && leftShooter.IsWebActive;
            bool rActive = rightShooter != null && rightShooter.IsWebActive;

            bool anchorsClose = lActive && rActive
                && Vector3.Distance(leftShooter.AnchorPoint, rightShooter.AnchorPoint) <= slingshotAnchorSpread;

            Rigidbody lRb = lActive ? leftShooter.AnchorRigidbody  : null;
            Rigidbody rRb = rActive ? rightShooter.AnchorRigidbody : null;

            // Arm when both webs hit the same moveable Rigidbody (pull-object gesture).
            bool sameMoveable = lRb != null && lRb == rRb && !lRb.isKinematic;

            // Tether case: one web on a static surface, one on a DIFFERENT Rigidbody.
            // WebTether owns this scenario — suppress slingshot so the gesture doesn't misfire.
            bool lStatic  = lActive && (lRb == null || lRb.isKinematic);
            bool rStatic  = rActive && (rRb == null || rRb.isKinematic);
            bool lMovable = lActive && lRb != null && !lRb.isKinematic;
            bool rMovable = rActive && rRb != null && !rRb.isKinematic;
            bool isTetherCase = (lStatic && rMovable) || (lMovable && rStatic);

            IsSlingshotArmed = !isTetherCase && (anchorsClose || sameMoveable);

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
            ApplyAirborneRotation();
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
            }

            if (r && !_rightPrev)
            {
                _rightLen   = Vector3.Distance(transform.position, rightShooter.AnchorPoint);
                _rightSwing = IsSwingAnchor(rightShooter.AnchorPoint);
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

        // ── Special dual-web cases ────────────────────────────────────────────
        void CheckDualWebCases()
        {
            bool l = leftShooter  != null && leftShooter.IsWebActive;
            bool r = rightShooter != null && rightShooter.IsWebActive;
            if (!l || !r) return;

            // Slingshot mode: both anchors close together.
            // SelfPropulsion owns the launch — suppress every auto-behavior here.
            if (IsSlingshotArmed) return;

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

            // Use CharacterController.Move() so Unity's collision resolution stops the
            // player at ground and wall surfaces during a swing arc.
            // GravityProvider and DynamicMoveProvider are already disabled while webHeld,
            // so they cannot issue competing Move() calls.
            // EnforceSwingConstraints() writes transform.position directly after this;
            // the CC picks up the corrected position automatically on the next Move() call.
            if (_cc != null && _cc.enabled)
                _cc.Move(Velocity * Time.deltaTime);
            else
                transform.position += Velocity * Time.deltaTime;
        }

        // ── Airborne rotation ────────────────────────────────────────────────
        // While not grounded, gradually rotates the XR Origin so it faces the camera's
        // look direction once the player has turned past rotationDeadZone degrees.
        // Rotates around the camera world position so the player's view stays centred.
        void ApplyAirborneRotation()
        {
            if (IsGrounded) return;

            Vector3 camFlat = new Vector3(_xrCamera.forward.x, 0f, _xrCamera.forward.z);
            Vector3 fwdFlat = new Vector3(transform.forward.x,  0f, transform.forward.z);
            if (camFlat.sqrMagnitude < 0.01f || fwdFlat.sqrMagnitude < 0.01f) return;

            float angle = Vector3.SignedAngle(fwdFlat.normalized, camFlat.normalized, Vector3.up);
            if (Mathf.Abs(angle) <= rotationDeadZone) return;

            // Rotate at a fixed speed, capped so we never overshoot the dead-zone edge
            float maxStep = Mathf.Abs(angle) - rotationDeadZone;
            float step    = Mathf.Min(rotationSpeed * Time.deltaTime, maxStep) * Mathf.Sign(angle);

            transform.RotateAround(_xrCamera.position, Vector3.up, step);
        }

        // ── Public API ───────────────────────────────────────────────────────
        /// Apply an instantaneous velocity change (called by SelfPropulsion).
        public void AddImpulse(Vector3 impulse) => Velocity += impulse;

        /// Start pulling a Rigidbody toward the player (called by SelfPropulsion on slingshot gesture).
        /// TickDualPull() will move it each frame and snap it once it is within dualGrabDistance.
        public void BeginDualPull(Rigidbody target) => _dualPullTarget = target;
    }
}
