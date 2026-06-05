using System.Collections.Generic;
using Unity.XR.CoreUtils;
using UnityEngine;
using UnityEngine.XR;

namespace SpiderMan
{
    /// Physics-based Spider-Man swing controller.
    ///
    /// Kinematic mode  — CC active, Rigidbody kinematic. Used for ground, free-fall,
    ///                   climbing. Manual velocity integration → CC.Move().
    ///
    /// Physics mode    — CC disabled, Rigidbody non-kinematic. Used when at least one
    ///                   swing anchor (high + far) is active. SpringJoint per web; Unity
    ///                   physics drives the pendulum. No transform-position writes.
    ///
    /// Features:
    ///   Real pendulum     SpringJoint + Rigidbody. Gravity creates the arc.
    ///   Pull boost        Backward/downward hand motion while swinging adds forward speed.
    ///   Release timing    Preserves all Rigidbody momentum on detach.
    ///   Dive              Headset pitched down → extra gravity → converts to swing speed.
    ///   Web retraction    Thumbstick-up shortens the rope, pulling player toward anchor.
    ///   Dual slingshot    Both webs + pull-back gesture. Force scales with pull velocity.
    ///   Wall climbing     Handled by WallClimber. Jump-away impulse consumed here.
    [RequireComponent(typeof(XROrigin))]
    [AddComponentMenu("SpiderMan/Swing Physics")]
    public class SwingPhysics : MonoBehaviour
    {
        // ── Inspector ────────────────────────────────────────────────────────

        [Header("General Feel")]
        [Tooltip("Downward acceleration (m/s²) applied in both modes.")]
        [SerializeField] float gravity = 12f;

        [Tooltip("Maximum player speed (m/s) at any time.")]
        [SerializeField] float maxSpeed = 22f;

        [Tooltip("Fractional velocity loss per second in kinematic mode.")]
        [SerializeField, Range(0f, 0.1f)] float airDrag = 0.008f;

        [Header("Web Rope  (Physics Joint)")]
        [Tooltip("Spring stiffness of the rope joint (N/m). 150–500 typical.")]
        [SerializeField] float webRopeSpring = 250f;

        [Tooltip("Rope joint damping. Higher = less bounce at taut moment.")]
        [SerializeField] float webRopeDamper = 18f;

        [Header("Swing Conditions")]
        [Tooltip("Anchor must be at least this many metres ABOVE the player to qualify as a swing anchor.")]
        [SerializeField] float swingMinHeight = 1.5f;

        [Tooltip("Anchor must be at least this many metres away to qualify as a swing anchor.")]
        [SerializeField] float swingMinDist = 3f;

        [Header("Swing Activation  (pull-back gesture starts pendulum)")]
        [Tooltip("Minimum speed (m/s) the controller must move away from its anchor (relative to body) " +
                 "to activate the pendulum. Web attaches silently until this threshold is reached.")]
        [SerializeField] float swingActivatePullThreshold = 0.8f;

        [Tooltip("Pull speed (m/s) is multiplied by this to produce the initial tangential impulse.")]
        [SerializeField] float swingActivateImpulseScale = 1.5f;

        [Tooltip("Maximum initial swing speed (m/s) that a pull-back gesture can produce.")]
        [SerializeField] float swingMaxActivateSpeed = 9f;

        [Header("Release Boost")]
        [Tooltip("Minimum upward speed (m/s) added when a swing web is released. " +
                 "Set to 0 for pure physics momentum carry.")]
        [SerializeField] float releaseUpBoost = 2f;

        [Tooltip("Fraction of horizontal speed added as a forward launch bonus on web release. " +
                 "0 = pure momentum carry. 0.3 = 30% extra forward speed.")]
        [SerializeField, Range(0f, 1f)] float releaseForwardBoostScale = 0.3f;

        [Header("Dive")]
        [Tooltip("Camera must be pitched this many degrees below the horizon to activate the dive state.")]
        [SerializeField, Range(10f, 80f)] float diveAngleThreshold = 35f;

        [Tooltip("Gravity multiplier applied while diving (free-fall only, not while swinging).")]
        [SerializeField] float diveGravityScale = 2.0f;

        [Header("Web Retraction")]
        [Tooltip("Speed (m/s) at which the rope shortens while the thumbstick is held up.")]
        [SerializeField] float retractionSpeed = 5f;

        [Tooltip("Shortest the rope can become via retraction.")]
        [SerializeField] float minRopeLength = 1.5f;

        [Tooltip("Thumbstick Y axis must exceed this to trigger retraction.")]
        [SerializeField, Range(0.3f, 1f)] float retractionThreshold = 0.7f;

        [Header("Long Jump  (both webs near feet)")]
        [SerializeField] float groundJumpForce = 18f;
        [SerializeField] float groundJumpMaxHeight = 1.5f;
        [SerializeField] float groundJumpMaxDist   = 6f;

        [Header("Grapple  (both webs → same IMMOVABLE object)")]
        [SerializeField] float grappleSpeed = 14f;

        [Header("Dual Pull  (both webs → same Rigidbody)")]
        [SerializeField] float dualPullSpeed    = 16f;
        [SerializeField] float dualGrabDistance = 1.2f;

        [Header("Ground Detection")]
        [SerializeField] float groundRadius = 0.15f;
        [SerializeField] LayerMask groundLayers = ~0;

        [Header("Ground Assistance  (prevents ground skimming during swing)")]
        [Tooltip("Height above ground (m) at which the upward assist starts while swinging.")]
        [SerializeField] float groundAssistHeight = 1.2f;

        [Tooltip("Max upward acceleration (m/s²) of the assist. Applied proportionally — " +
                 "full strength at ground level, zero at groundAssistHeight.")]
        [SerializeField] float groundAssistForce  = 20f;

        [Header("Dual-Web Slingshot")]
        [Tooltip("Maximum distance (m) between anchor points before slingshot mode is suppressed.")]
        [SerializeField] float slingshotAnchorSpread = 5f;

        [Header("Airborne Rotation")]
        [SerializeField, Range(0f, 60f)] float rotationDeadZone = 30f;
        [SerializeField] float rotationSpeed = 60f;

        [Header("References")]
        [SerializeField] WebShooter leftShooter;
        [SerializeField] WebShooter rightShooter;
        [SerializeField] WallClimber wallClimber;
        [SerializeField] WebTether   webTether;

        // ── Public API ───────────────────────────────────────────────────────

        public Vector3 Velocity    => _physicsMode ? _rb.linearVelocity : _kinVelocity;
        public bool    IsGrounded  { get; private set; }
        public bool    IsSwinging  => _leftSwing || _rightSwing;
        public bool    IsSlingshotArmed { get; private set; }

        bool AnyWebHeld =>
            (leftShooter  != null && leftShooter.IsWebActive) ||
            (rightShooter != null && rightShooter.IsWebActive);

        bool IsDiving
        {
            get
            {
                if (_xrCamera == null || IsGrounded) return false;
                // dot(forward, down) > sin(threshold) means camera is pointed far below horizon
                return Vector3.Dot(_xrCamera.forward, Vector3.down) >
                       Mathf.Sin(diveAngleThreshold * Mathf.Deg2Rad);
            }
        }

        // ── Private — components ─────────────────────────────────────────────
        Rigidbody         _rb;
        CapsuleCollider   _physicsCollider;
        CharacterController _cc;
        Transform         _xrCamera;
        MonoBehaviour[]   _xrGravityBehaviours;

        // Retraction input devices
        InputDevice _lDev, _rDev;

        // ── Private — joints ─────────────────────────────────────────────────
        SpringJoint _leftJoint, _rightJoint;

        // ── Private — mode ───────────────────────────────────────────────────
        bool    _physicsMode;
        Vector3 _kinVelocity;

        // Retraction flags — set in LateUpdate, consumed in FixedUpdate
        bool _retractLeft, _retractRight;

        // ── Private — web state ──────────────────────────────────────────────
        float _leftLen, _rightLen;
        bool  _leftPrev, _rightPrev;
        bool  _leftSwing, _rightSwing;

        // Pending = web hit a swing anchor but player hasn't pulled back yet
        bool _leftPendingSwing, _rightPendingSwing;

        // Controller world-space velocity (differenced each LateUpdate)
        Vector3 _prevLCtrlPos, _prevRCtrlPos;
        Vector3 _lCtrlVel,     _rCtrlVel;

        bool      _groundJumpReady = true;
        Rigidbody _dualPullTarget;

        // ── Unity lifecycle ──────────────────────────────────────────────────
        void Awake()
        {
            _rb = GetComponent<Rigidbody>();
            if (_rb == null) _rb = gameObject.AddComponent<Rigidbody>();
            _rb.freezeRotation = true;
            _rb.interpolation  = RigidbodyInterpolation.Interpolate;
            _rb.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
            _rb.isKinematic = true;
            _rb.useGravity  = false; // gravity applied manually so we can tune and dive-scale it

            _cc = GetComponent<CharacterController>();

            // Separate CapsuleCollider for physics mode — the CC's built-in capsule
            // disappears when the CC component is disabled.
            _physicsCollider = GetComponent<CapsuleCollider>();
            if (_physicsCollider == null)
            {
                _physicsCollider = gameObject.AddComponent<CapsuleCollider>();
                if (_cc != null)
                {
                    _physicsCollider.height = _cc.height;
                    _physicsCollider.radius = _cc.radius;
                    _physicsCollider.center = _cc.center;
                }
                else
                {
                    _physicsCollider.height = 1.8f;
                    _physicsCollider.radius = 0.3f;
                    _physicsCollider.center = new Vector3(0f, 0.9f, 0f);
                }
                var mat = new PhysicsMaterial("SwingBody")
                    { dynamicFriction = 0f, staticFriction = 0f, bounciness = 0f };
                _physicsCollider.sharedMaterial = mat;
            }
            _physicsCollider.enabled = false;
        }

        void Start()
        {
            _xrCamera = Camera.main != null ? Camera.main.transform : transform;

            var found = new List<MonoBehaviour>();
            foreach (MonoBehaviour mb in GetComponentsInChildren<MonoBehaviour>(true))
            {
                string t = mb.GetType().Name;
                if (t == "GravityProvider" || t == "DynamicMoveProvider")
                    found.Add(mb);
            }
            _xrGravityBehaviours = found.ToArray();

            // Seed previous controller positions so velocity is zero on the first frame
            if (leftShooter  != null) _prevLCtrlPos = leftShooter.transform.position;
            if (rightShooter != null) _prevRCtrlPos = rightShooter.transform.position;
        }

        // ── FixedUpdate — Rigidbody physics (physics mode only) ──────────────
        void FixedUpdate()
        {
            if (!_physicsMode) return;

            // Manual gravity — keeps feel consistent and allows dive scaling.
            // Applied only while not on ground.
            if (!IsGrounded)
            {
                // Extra gravity when diving but not yet swinging (free-fall dive boost)
                float gravScale = (IsDiving && !IsSwinging) ? diveGravityScale : 1f;
                _rb.AddForce(Vector3.down * (gravity * gravScale), ForceMode.Acceleration);
            }

            // Web retraction — shorten the rope toward the anchor each physics step
            if (_retractLeft  && _leftSwing  && _leftJoint  != null)
            {
                _leftLen = Mathf.Max(minRopeLength, _leftLen - retractionSpeed * Time.fixedDeltaTime);
                _leftJoint.maxDistance = _leftLen;
            }
            if (_retractRight && _rightSwing && _rightJoint != null)
            {
                _rightLen = Mathf.Max(minRopeLength, _rightLen - retractionSpeed * Time.fixedDeltaTime);
                _rightJoint.maxDistance = _rightLen;
            }

            // Speed cap
            if (_rb.linearVelocity.sqrMagnitude > maxSpeed * maxSpeed)
                _rb.linearVelocity = _rb.linearVelocity.normalized * maxSpeed;

            // Rope constraint: strip outward radial velocity when the rope is taut.
            // This converts the SpringJoint from a bouncy elastic to an inelastic rope
            // — player can swing inward freely but cannot move outward beyond ropeLen.
            StripSwingRadialVelocity();

            // Height cap — a real rope pendulum cannot carry the player above its pivot
            EnforceHeightCap();

            // Ground assistance — gentle upward force when skimming near the ground
            if (IsSwinging) ApplyGroundAssist();

            // Non-swing active webs use spring force (no joint in physics mode for these)
            ApplyNonSwingRopeForces();
        }

        void EnforceHeightCap()
        {
            float capY = float.MaxValue;
            if (_leftSwing  && leftShooter  != null && leftShooter.IsWebActive)
                capY = Mathf.Min(capY, leftShooter.AnchorPoint.y);
            if (_rightSwing && rightShooter != null && rightShooter.IsWebActive)
                capY = Mathf.Min(capY, rightShooter.AnchorPoint.y);

            if (capY == float.MaxValue) return;
            if (_rb.position.y >= capY && _rb.linearVelocity.y > 0f)
            {
                var v = _rb.linearVelocity;
                v.y = 0f;
                _rb.linearVelocity = v;
            }
        }

        void ApplyNonSwingRopeForces()
        {
            bool lActive = leftShooter  != null && leftShooter.IsWebActive && !_leftSwing;
            bool rActive = rightShooter != null && rightShooter.IsWebActive && !_rightSwing;
            if (lActive) RopeForceOnRb(leftShooter.AnchorPoint,  _leftLen);
            if (rActive) RopeForceOnRb(rightShooter.AnchorPoint, _rightLen);
        }

        void RopeForceOnRb(Vector3 anchor, float maxLen)
        {
            Vector3 arm  = _rb.position - anchor;
            float   dist = arm.magnitude;
            float excess = dist - maxLen;
            if (excess <= 0f || dist < 0.001f) return;
            Vector3 dir      = arm / dist;
            float   outSpeed = Vector3.Dot(_rb.linearVelocity, dir);
            if (outSpeed > 0f)
                _rb.AddForce(-dir * outSpeed * webRopeDamper, ForceMode.Force);
            _rb.AddForce(-dir * excess * webRopeSpring, ForceMode.Force);
        }

        // Removes the outward radial component of the Rigidbody velocity when the
        // rope is at or beyond its max length. A SpringJoint alone is elastic (bouncy);
        // zeroing the outward velocity makes it behave like a true inelastic rope.
        void StripSwingRadialVelocity()
        {
            if (_leftSwing  && leftShooter  != null && leftShooter.IsWebActive)
                StripOutwardAlongRope(leftShooter.AnchorPoint,  _leftLen);
            if (_rightSwing && rightShooter != null && rightShooter.IsWebActive)
                StripOutwardAlongRope(rightShooter.AnchorPoint, _rightLen);
        }

        void StripOutwardAlongRope(Vector3 anchor, float ropeLen)
        {
            Vector3 arm  = _rb.position - anchor;
            float   dist = arm.magnitude;
            // Only enforce when rope is taut — ignore slack (player swinging inward is fine)
            if (dist < ropeLen * 0.97f || dist < 0.001f) return;
            Vector3 dir      = arm / dist;
            float   outSpeed = Vector3.Dot(_rb.linearVelocity, dir);
            if (outSpeed > 0f)
                _rb.linearVelocity -= dir * outSpeed; // cancel all outward motion immediately
        }

        // Applies a gentle upward acceleration while swinging very close to the ground.
        // Prevents the player from skimming into geometry on low arcs.
        void ApplyGroundAssist()
        {
            // Only needed when swinging downward toward the ground
            if (_rb.linearVelocity.y >= 0f) return;
            if (!Physics.Raycast(_rb.position, Vector3.down, out RaycastHit hit,
                    groundAssistHeight, groundLayers, QueryTriggerInteraction.Ignore)) return;
            // Proximity 0 at the height threshold, 1 at ground — apply proportionally
            float proximity = 1f - hit.distance / groundAssistHeight;
            _rb.AddForce(Vector3.up * (groundAssistForce * proximity), ForceMode.Acceleration);
        }

        // ── LateUpdate — state management + kinematic physics ────────────────
        void LateUpdate()
        {
            PollRetractDevices();

            // ── Wall climbing (highest priority) ─────────────────────────────
            if (wallClimber != null && wallClimber.IsClimbing)
            {
                if (_physicsMode) ExitPhysicsMode(applyBoost: false);
                _kinVelocity     = Vector3.zero;
                IsSlingshotArmed = false;

                foreach (var mb in _xrGravityBehaviours)
                    if (mb != null) mb.enabled = false;

                IsGrounded = CheckGrounded();

                Vector3 climbDelta = wallClimber.ConsumeClimbDelta();
                if (_cc != null && _cc.enabled)
                    _cc.Move(climbDelta);
                else
                    transform.position += climbDelta;

                ApplyPhysicsModeRotation();
                return;
            }

            // ── Slingshot state ──────────────────────────────────────────────
            bool lActive = leftShooter  != null && leftShooter.IsWebActive;
            bool rActive = rightShooter != null && rightShooter.IsWebActive;

            bool anchorsClose = lActive && rActive &&
                Vector3.Distance(leftShooter.AnchorPoint, rightShooter.AnchorPoint) <= slingshotAnchorSpread;

            Rigidbody lRb = lActive ? leftShooter.AnchorRigidbody  : null;
            Rigidbody rRb = rActive ? rightShooter.AnchorRigidbody : null;

            bool sameMoveable = lRb != null && lRb == rRb && !lRb.isKinematic;
            bool lStatic  = lActive && (lRb == null || lRb.isKinematic);
            bool rStatic  = rActive && (rRb == null || rRb.isKinematic);
            bool lMovable = lActive && lRb != null && !lRb.isKinematic;
            bool rMovable = rActive && rRb != null && !rRb.isKinematic;
            bool isTetherCase = (lStatic && rMovable) || (lMovable && rStatic);

            IsSlingshotArmed = !isTetherCase && (anchorsClose || sameMoveable);

            // Update grounded state FIRST so every subsequent check in this frame is current.
            // Previously this ran after xritActive was computed, causing XR IT gravity to fire
            // for one extra frame on ground→air transitions (velocity spike / jerk).
            IsGrounded = CheckGrounded();

            // ── XR IT enable/disable ─────────────────────────────────────────
            bool xritActive = !AnyWebHeld && IsGrounded && !_physicsMode;
            foreach (var mb in _xrGravityBehaviours)
                if (mb != null) mb.enabled = xritActive;

            // ── Read retraction thumbstick input ─────────────────────────────
            _lDev.TryGetFeatureValue(CommonUsages.primary2DAxis, out Vector2 lStick);
            _rDev.TryGetFeatureValue(CommonUsages.primary2DAxis, out Vector2 rStick);
            _retractLeft  = lStick.y > retractionThreshold && _leftSwing  && leftShooter  != null && leftShooter.IsWebActive;
            _retractRight = rStick.y > retractionThreshold && _rightSwing && rightShooter != null && rightShooter.IsWebActive;

            // ── Per-frame logic ──────────────────────────────────────────────
            TrackControllerVelocities();      // must be first so ctrlVel is fresh
            CheckPendingSwingActivations();   // may promote pending → active swing
            ProcessWebAttachments();
            CheckDualWebCases();

            // Wall-jump impulse from WallClimber (fired when grip released with velocity)
            if (wallClimber != null)
            {
                Vector3 jump = wallClimber.ConsumePendingJump();
                if (jump != Vector3.zero) AddImpulse(jump);
            }

            if (_physicsMode)
            {
                // Rigidbody + joints drive the swing. No manual movement code needed.
                TickDualPull();
                ApplyPhysicsModeRotation();   // rotates in-place only — no RotateAround
            }
            else
            {
                // Kinematic mode: manual velocity integration + CC.Move
                ApplyGravityKin();
                ApplyNonSwingRopeForcesKin();
                ApplyDragAndClamp();
                MoveAndRotateKinematic();     // single CC.Move that includes rotation delta
                TickDualPull();
            }
        }

        void PollRetractDevices()
        {
            RefreshDevice(ref _lDev, XRNode.LeftHand);
            RefreshDevice(ref _rDev, XRNode.RightHand);
        }

        void RefreshDevice(ref InputDevice dev, XRNode node)
        {
            if (dev.isValid) return;
            var buf = new List<InputDevice>();
            InputDevices.GetDevicesAtXRNode(node, buf);
            if (buf.Count > 0) dev = buf[0];
        }

        bool CheckGrounded() => Physics.CheckSphere(
            transform.position + Vector3.down * 0.05f,
            groundRadius, groundLayers, QueryTriggerInteraction.Ignore);

        // ── Physics mode transitions ─────────────────────────────────────────

        void EnterPhysicsMode()
        {
            if (_physicsMode) return;
            _physicsMode = true;

            if (_cc != null) _cc.enabled = false;
            _physicsCollider.enabled = true;

            _rb.isKinematic     = false;
            // useGravity stays false — gravity applied manually in FixedUpdate
            _rb.linearVelocity  = _kinVelocity;
            _rb.angularVelocity = Vector3.zero;
        }

        void ExitPhysicsMode(bool applyBoost)
        {
            if (!_physicsMode) return;

            DestroyJoint(ref _leftJoint);
            DestroyJoint(ref _rightJoint);

            // Preserve ALL Rigidbody momentum — this is the "release timing" feature.
            // Good timing (bottom of arc) = max horizontal speed naturally carried over.
            _kinVelocity = _rb.linearVelocity;

            // Only add a gentle upward nudge if released while moving sharply downward,
            // so a badly-timed release doesn't cause an immediate uncontrolled plummet.
            if (applyBoost && _kinVelocity.y < 0f && releaseUpBoost > 0f)
                _kinVelocity.y = Mathf.Max(_kinVelocity.y, -releaseUpBoost * 0.5f);

            _rb.isKinematic = true;
            _physicsCollider.enabled = false;

            if (_cc != null) _cc.enabled = true;
            _physicsMode = false;
        }

        void CreateWebJoint(ref SpringJoint joint, Vector3 anchorWorld, float ropeLength)
        {
            DestroyJoint(ref joint);
            joint = gameObject.AddComponent<SpringJoint>();
            joint.autoConfigureConnectedAnchor = false;
            joint.connectedBody   = null;
            joint.connectedAnchor = anchorWorld;
            joint.maxDistance     = ropeLength;
            joint.minDistance     = 0f;    // rope can go slack — allows dive + re-taut conversion
            joint.spring          = webRopeSpring;
            joint.damper          = webRopeDamper;
            joint.tolerance       = 0.025f;
            joint.enableCollision = false;
            joint.breakForce      = Mathf.Infinity;
            joint.breakTorque     = Mathf.Infinity;
        }

        static void DestroyJoint(ref SpringJoint joint)
        {
            if (joint != null) { Destroy(joint); joint = null; }
        }

        // ── Web attachment processing ─────────────────────────────────────────
        // On attach: if the anchor qualifies as a swing point, mark it PENDING.
        //   Nothing else happens — no physics mode, no joint, no impulse.
        //   The player can still aim, reposition, or cancel by releasing the trigger.
        //
        // On each frame (CheckPendingSwingActivations, called before this):
        //   If pull-back speed exceeds swingActivatePullThreshold, actually enter
        //   physics mode, create the SpringJoint, and seed the initial arc impulse.
        //
        // On release: clear both swing and pending flags; exit physics mode if needed.
        void ProcessWebAttachments()
        {
            bool l = leftShooter  != null && leftShooter.IsWebActive;
            bool r = rightShooter != null && rightShooter.IsWebActive;

            // New left web — store length and classify; do NOT activate swing yet
            if (l && !_leftPrev)
            {
                _leftLen          = Vector3.Distance(transform.position, leftShooter.AnchorPoint);
                _leftSwing        = false;
                _leftPendingSwing = IsSwingAnchor(leftShooter.AnchorPoint);
            }

            // New right web
            if (r && !_rightPrev)
            {
                _rightLen          = Vector3.Distance(transform.position, rightShooter.AnchorPoint);
                _rightSwing        = false;
                _rightPendingSwing = IsSwingAnchor(rightShooter.AnchorPoint);
            }

            // Left release
            if (_leftPrev && !l)
            {
                bool wasSwing     = _leftSwing;
                _leftPendingSwing = false;
                DestroyJoint(ref _leftJoint);
                _leftSwing = false;
                if (!_rightSwing && _physicsMode)
                    ExitPhysicsMode(applyBoost: wasSwing);
            }

            // Right release
            if (_rightPrev && !r)
            {
                bool wasSwing      = _rightSwing;
                _rightPendingSwing = false;
                DestroyJoint(ref _rightJoint);
                _rightSwing = false;
                if (!_leftSwing && _physicsMode)
                    ExitPhysicsMode(applyBoost: wasSwing);
            }

            if (!l || !r)
            {
                _groundJumpReady = true;
                if (!l && !r) _dualPullTarget = null;
            }

            _leftPrev  = l;
            _rightPrev = r;
        }

        void TrackControllerVelocities()
        {
            float dt = Mathf.Max(Time.deltaTime, 0.0001f);
            if (leftShooter != null)
            {
                Vector3 p   = leftShooter.transform.position;
                _lCtrlVel   = (p - _prevLCtrlPos) / dt;
                _prevLCtrlPos = p;
            }
            if (rightShooter != null)
            {
                Vector3 p   = rightShooter.transform.position;
                _rCtrlVel   = (p - _prevRCtrlPos) / dt;
                _prevRCtrlPos = p;
            }
        }

        // Each frame: for any pending swing web, measure how fast the controller is
        // moving away from its anchor (relative to the player's body). When the pull
        // speed exceeds the threshold the pendulum activates and gets a seeded impulse.
        void CheckPendingSwingActivations()
        {
            if (_leftPendingSwing && leftShooter != null && leftShooter.IsWebActive)
            {
                float pull = ControllerPullBackSpeed(leftShooter, _lCtrlVel);
                if (pull >= swingActivatePullThreshold)
                {
                    _leftPendingSwing = false;
                    _leftSwing        = true;
                    EnterPhysicsMode();
                    CreateWebJoint(ref _leftJoint, leftShooter.AnchorPoint, _leftLen);
                    ActivateSwingWithImpulse(leftShooter, pull);
                }
            }

            if (_rightPendingSwing && rightShooter != null && rightShooter.IsWebActive)
            {
                float pull = ControllerPullBackSpeed(rightShooter, _rCtrlVel);
                if (pull >= swingActivatePullThreshold)
                {
                    _rightPendingSwing = false;
                    _rightSwing        = true;
                    EnterPhysicsMode();
                    CreateWebJoint(ref _rightJoint, rightShooter.AnchorPoint, _rightLen);
                    ActivateSwingWithImpulse(rightShooter, pull);
                }
            }
        }

        // Speed at which the controller is moving AWAY from its anchor, minus body velocity.
        // Positive = player is physically pulling the controller back (tension gesture).
        float ControllerPullBackSpeed(WebShooter shooter, Vector3 ctrlVel)
        {
            Vector3 awayDir = shooter.transform.position - shooter.AnchorPoint;
            if (awayDir.sqrMagnitude < 0.001f) return 0f;
            return Vector3.Dot(ctrlVel - Velocity, awayDir.normalized);
        }

        bool IsSwingAnchor(Vector3 anchor)
        {
            float dY   = anchor.y - transform.position.y;
            float dist = Vector3.Distance(transform.position, anchor);
            return dY >= swingMinHeight && dist >= swingMinDist;
        }

        // Called when a pending swing is activated by a pull-back gesture.
        // Converts the measured pull speed into an initial tangential impulse so the
        // arc begins in the camera-forward direction. No speed is added if the player
        // already has enough tangential velocity from a dive or previous swing.
        void ActivateSwingWithImpulse(WebShooter shooter, float pullSpeed)
        {
            if (!_physicsMode || swingActivateImpulseScale <= 0f) return;

            Vector3 arm = (_rb.position - shooter.AnchorPoint).normalized;
            Vector3 vel = _rb.linearVelocity;

            // Only add up to swingMaxActivateSpeed minus any tangential speed already present
            Vector3 tangVel = vel - Vector3.Dot(vel, arm) * arm;
            float   tangSpd = tangVel.magnitude;

            float wantSpeed = Mathf.Clamp(pullSpeed * swingActivateImpulseScale, 0f, swingMaxActivateSpeed);
            float addSpeed  = wantSpeed - tangSpd;
            if (addSpeed <= 0f) return;

            Vector3 camFwd = new Vector3(_xrCamera.forward.x, 0f, _xrCamera.forward.z);
            if (camFwd.sqrMagnitude < 0.01f)
                camFwd = new Vector3(transform.forward.x, 0f, transform.forward.z);
            camFwd.Normalize();

            Vector3 kickDir = camFwd - Vector3.Dot(camFwd, arm) * arm;
            if (kickDir.sqrMagnitude < 0.01f) return;
            kickDir.Normalize();

            _rb.AddForce(kickDir * addSpeed, ForceMode.VelocityChange);
        }

        // ── Dual-web special cases ────────────────────────────────────────────
        void CheckDualWebCases()
        {
            bool l = leftShooter  != null && leftShooter.IsWebActive;
            bool r = rightShooter != null && rightShooter.IsWebActive;
            if (!l || !r) return;
            if (IsSlingshotArmed) return;
            // Either web is still in the pull-back pending state — don't trigger any
            // dual-web mechanic until the gesture actually activates the swing.
            if (_leftPendingSwing || _rightPendingSwing) return;

            // Long jump: both anchors near player's feet
            if (_groundJumpReady
                && IsGroundAnchor(leftShooter.AnchorPoint)
                && IsGroundAnchor(rightShooter.AnchorPoint))
            {
                _groundJumpReady = false;
                if (_physicsMode) _rb.AddForce(Vector3.up * groundJumpForce, ForceMode.VelocityChange);
                else              _kinVelocity = Vector3.up * groundJumpForce;
                leftShooter.ForceDetach();
                rightShooter.ForceDetach();
                return;
            }

            if (leftShooter.AnchorObject == null
                || leftShooter.AnchorObject != rightShooter.AnchorObject) return;

            var rb = leftShooter.AnchorRigidbody;
            if (rb != null && !rb.isKinematic)
            {
                if (_dualPullTarget == null) _dualPullTarget = rb;
            }
            else
            {
                Vector3 mid = (leftShooter.AnchorPoint + rightShooter.AnchorPoint) * 0.5f;
                Vector3 vel = (mid - transform.position).normalized * grappleSpeed;
                _leftSwing  = false;
                _rightSwing = false;
                if (_physicsMode) ExitPhysicsMode(applyBoost: false);
                _kinVelocity = vel;
            }
        }

        bool IsGroundAnchor(Vector3 anchor)
        {
            float dY     = anchor.y - transform.position.y;
            float horizD = Vector3.Distance(
                new Vector3(anchor.x, transform.position.y, anchor.z), transform.position);
            return dY < groundJumpMaxHeight && horizD < groundJumpMaxDist;
        }

        // ── Dual-pull object toward player ───────────────────────────────────
        void TickDualPull()
        {
            if (_dualPullTarget == null) return;

            bool l = leftShooter  != null && leftShooter.IsWebActive;
            bool r = rightShooter != null && rightShooter.IsWebActive;

            if (!l || !r || leftShooter.AnchorObject != rightShooter.AnchorObject)
            { _dualPullTarget = null; return; }

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

        // ── Kinematic mode physics ────────────────────────────────────────────

        void ApplyGravityKin()
        {
            if (IsGrounded) return;
            // Both webs attached but neither swing activated yet — hold the player completely
            // still so they don't drift from gravity before performing the pull-back gesture.
            if (_leftPendingSwing && _rightPendingSwing)
            {
                _kinVelocity = Vector3.zero;
                return;
            }
            // Dive: extra gravity during free-fall (converting dive speed → swing arc on next attach)
            float scale = IsDiving ? diveGravityScale : 1f;
            _kinVelocity += Vector3.down * (gravity * scale * Time.deltaTime);
        }

        void ApplyNonSwingRopeForcesKin()
        {
            // When both webs are pending we zero velocity above, so rope spring forces
            // would fight that and cause oscillation — skip them in that case.
            if (_leftPendingSwing && _rightPendingSwing) return;
            bool lActive = leftShooter  != null && leftShooter.IsWebActive && !_leftSwing;
            bool rActive = rightShooter != null && rightShooter.IsWebActive && !_rightSwing;
            if (lActive) RopeForceOnVelocity(leftShooter.AnchorPoint,  _leftLen);
            if (rActive) RopeForceOnVelocity(rightShooter.AnchorPoint, _rightLen);
        }

        void RopeForceOnVelocity(Vector3 anchor, float maxLen)
        {
            Vector3 arm  = transform.position - anchor;
            float   dist = arm.magnitude;
            float excess = dist - maxLen;
            if (excess <= 0f || dist < 0.001f) return;
            Vector3 dir      = arm / dist;
            float   outSpeed = Vector3.Dot(_kinVelocity, dir);
            if (outSpeed > 0f) _kinVelocity -= dir * outSpeed * webRopeDamper  * Time.deltaTime;
            _kinVelocity -= dir * excess * webRopeSpring * Time.deltaTime;
        }

        void ApplyDragAndClamp()
        {
            _kinVelocity *= 1f - airDrag * Time.deltaTime * 60f;
            _kinVelocity  = Vector3.ClampMagnitude(_kinVelocity, maxSpeed);
        }

        // ── Kinematic movement + rotation (merged into one CC.Move) ─────────
        // Root cause of "jerky air movement":
        //   Old code called CC.Move(vel*dt) then RotateAround(cam, up, step).
        //   RotateAround also TRANSLATES the origin (camera is offset from root).
        //   CC handled collision for the velocity delta but NOT the rotation delta,
        //   so the player could be pushed into geometry; the CC corrected it next
        //   frame → visible oscillation every frame the player was looking sideways.
        //
        // Fix: compute the rotation-induced translation and feed it INTO the single
        //   CC.Move call so collision is resolved for both movements together.
        void MoveAndRotateKinematic()
        {
            if (!AnyWebHeld && IsGrounded)
            { _kinVelocity = Vector3.zero; return; }

            float rotAngle = ComputeBodyRotationAngle();

            // Rotation-induced translation: what RotateAround(cam, up, step) would move origin by.
            Vector3 rotDelta = Vector3.zero;
            if (rotAngle != 0f && _xrCamera != null)
            {
                Vector3 toOrigin = transform.position - _xrCamera.position;
                rotDelta = Quaternion.AngleAxis(rotAngle, Vector3.up) * toOrigin - toOrigin;
            }

            // Single collision-aware move for velocity + rotation translation
            Vector3 totalDelta = _kinVelocity * Time.deltaTime + rotDelta;
            if (_cc != null && _cc.enabled)
                _cc.Move(totalDelta);
            else
                transform.position += totalDelta;

            // Apply only the angular part (position already handled above)
            if (rotAngle != 0f)
                transform.Rotate(0f, rotAngle, 0f, Space.World);
        }

        // ── Physics mode rotation (swing / grapple) ──────────────────────────
        // Root cause of swing jitter:
        //   RotateAround translates the Rigidbody's transform. The Rigidbody's
        //   interpolation then overwrites that position next frame → oscillation.
        //
        // Fix: rotate the XR Origin purely around its own Y axis (no position change).
        //   The camera shift is (offset * (1-cos(step))) ≈ sub-millimetre and invisible.
        void ApplyPhysicsModeRotation()
        {
            float rotAngle = ComputeBodyRotationAngle();
            if (rotAngle != 0f)
                transform.Rotate(0f, rotAngle, 0f, Space.World);
        }

        // Shared: how many degrees should the body rotate this frame to track the head.
        float ComputeBodyRotationAngle()
        {
            if (IsGrounded || _xrCamera == null) return 0f;
            Vector3 camFlat = new Vector3(_xrCamera.forward.x, 0f, _xrCamera.forward.z);
            Vector3 fwdFlat = new Vector3(transform.forward.x,  0f, transform.forward.z);
            if (camFlat.sqrMagnitude < 0.01f || fwdFlat.sqrMagnitude < 0.01f) return 0f;

            float angle = Vector3.SignedAngle(fwdFlat.normalized, camFlat.normalized, Vector3.up);
            if (Mathf.Abs(angle) <= rotationDeadZone) return 0f;

            float maxStep = Mathf.Abs(angle) - rotationDeadZone;
            return Mathf.Min(rotationSpeed * Time.deltaTime, maxStep) * Mathf.Sign(angle);
        }

        // ── Public API ───────────────────────────────────────────────────────

        /// Instantaneous velocity impulse (ForceMode.VelocityChange in physics mode,
        /// direct velocity addition in kinematic mode).
        public void AddImpulse(Vector3 impulse)
        {
            if (_physicsMode) _rb.AddForce(impulse, ForceMode.VelocityChange);
            else              _kinVelocity += impulse;
        }

        /// Continuous per-frame boost (call with force * Time.deltaTime from Update/LateUpdate).
        /// Uses ForceMode.Force in physics mode so it accumulates naturally across FixedUpdates.
        public void AddContinuousForce(Vector3 forcePerSecond)
        {
            if (_physicsMode) _rb.AddForce(forcePerSecond, ForceMode.Force);
            else              _kinVelocity += forcePerSecond * Time.deltaTime;
        }

        /// Start pulling a Rigidbody toward the player (slingshot pull-object gesture).
        public void BeginDualPull(Rigidbody target) => _dualPullTarget = target;
    }
}
