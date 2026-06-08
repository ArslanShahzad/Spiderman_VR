using System.Collections.Generic;
using Unity.XR.CoreUtils;
using UnityEngine;

namespace SpiderMan
{
    /// Simple physics-based web swing controller.
    ///
    /// Ground  — CharacterController drives locomotion normally. Web attached but
    ///           does not restrict movement. Auto-breaks if player walks too far.
    ///           When the player leaves the ground, swing activates automatically.
    ///
    /// Air     — Rigidbody non-kinematic + SpringJoint. Unity physics handles the
    ///           full pendulum. No custom constraints or forced positions.
    [RequireComponent(typeof(XROrigin))]
    [AddComponentMenu("SpiderMan/Swing Physics")]
    public class SwingPhysics : MonoBehaviour
    {
        // ── Inspector ────────────────────────────────────────────────────────

        [Header("Physics")]
        [SerializeField] float gravity    = 12f;
        [SerializeField] float maxSpeed   = 22f;

        [Header("Web Rope  (SpringJoint)")]
        [Tooltip("Spring stiffness. Higher = stiffer rope, less stretch.")]
        [SerializeField] float webSpring  = 500f;
        [Tooltip("Spring damping. Higher = less oscillation when rope goes taut.")]
        [SerializeField] float webDamper  = 15f;

        [Header("Web Limits")]
        [Tooltip("Web auto-breaks when the player is more than this many metres from the anchor.")]
        [SerializeField] float maxWebDistance = 30f;
        [Tooltip("Anchor must be at least this many metres above the player to count as a swing point.")]
        [SerializeField] float swingMinHeight = 1.5f;
        [Tooltip("Anchor must be at least this many metres away to count as a swing point.")]
        [SerializeField] float swingMinDist   = 3f;

        [Header("Release")]
        [Tooltip("Upward speed floor on web release while falling (prevents instant plummet).")]
        [SerializeField] float releaseUpBoost = 2f;

        [Header("Ground Detection")]
        [SerializeField] float     groundRadius = 0.15f;
        [SerializeField] LayerMask groundLayers = ~0;

        [Header("References")]
        [SerializeField] WebShooter  leftShooter;
        [SerializeField] WebShooter  rightShooter;
        [SerializeField] WallClimber wallClimber;

        // ── Public API (consumed by SelfPropulsion and other scripts) ────────

        public Vector3 Velocity         => _physicsMode ? _rb.linearVelocity : _kinVelocity;
        public bool    IsGrounded       { get; private set; }
        public bool    IsSwinging       => _leftSwing || _rightSwing;
        public bool    IsSlingshotArmed => false;

        // ── Components ───────────────────────────────────────────────────────

        Rigidbody           _rb;
        CharacterController _cc;
        CapsuleCollider     _physicsCollider;
        MonoBehaviour[]     _xrGravityBehaviours;

        // ── State ────────────────────────────────────────────────────────────

        bool    _physicsMode;
        Vector3 _kinVelocity;

        bool  _leftSwing,  _rightSwing;
        bool  _leftPrev,   _rightPrev;
        float _leftLen,    _rightLen;

        SpringJoint _leftJoint, _rightJoint;

        // ── Awake ────────────────────────────────────────────────────────────

        void Awake()
        {
            _rb = GetComponent<Rigidbody>();
            if (_rb == null) _rb = gameObject.AddComponent<Rigidbody>();
            // Assign, not OR — clears any position-freeze constraints set in the editor.
            _rb.constraints            = RigidbodyConstraints.FreezeRotation;
            _rb.interpolation          = RigidbodyInterpolation.Interpolate;
            _rb.collisionDetectionMode = CollisionDetectionMode.Continuous;
            _rb.isKinematic            = true;
            _rb.useGravity             = false;
            _rb.linearDamping          = 0f;
            _rb.angularDamping         = 0f;

            _cc = GetComponent<CharacterController>();

            // Separate capsule for physics mode — the CC's capsule deactivates with the component.
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
                _physicsCollider.sharedMaterial = new PhysicsMaterial("SwingBody")
                {
                    dynamicFriction = 0f,
                    staticFriction  = 0f,
                    bounciness      = 0f,
                    frictionCombine = PhysicsMaterialCombine.Minimum,
                    bounceCombine   = PhysicsMaterialCombine.Minimum
                };
            }
            _physicsCollider.enabled = false;
        }

        void Start()
        {
            var found = new List<MonoBehaviour>();
            foreach (var mb in GetComponentsInChildren<MonoBehaviour>(true))
            {
                string t = mb.GetType().Name;
                if (t == "GravityProvider" || t == "DynamicMoveProvider") found.Add(mb);
            }
            _xrGravityBehaviours = found.ToArray();
        }

        // ── FixedUpdate — Rigidbody physics (physics / swing mode only) ──────

        void FixedUpdate()
        {
            if (!_physicsMode) return;

            if (!IsGrounded)
                _rb.AddForce(Vector3.down * gravity, ForceMode.Acceleration);

            if (_rb.linearVelocity.sqrMagnitude > maxSpeed * maxSpeed)
                _rb.linearVelocity = _rb.linearVelocity.normalized * maxSpeed;
        }

        // ── LateUpdate — state + kinematic movement ──────────────────────────

        void LateUpdate()
        {
            // Wall climbing takes full control
            if (wallClimber != null && wallClimber.IsClimbing)
            {
                if (_physicsMode) ExitPhysicsMode(false);
                _kinVelocity = Vector3.zero;
                IsGrounded   = CheckGrounded();

                Vector3 climbDelta = wallClimber.ConsumeClimbDelta();
                if (_cc != null && _cc.enabled) _cc.Move(climbDelta);
                else                            transform.position += climbDelta;

                Vector3 wallJump = wallClimber.ConsumePendingJump();
                if (wallJump != Vector3.zero) AddImpulse(wallJump);
                return;
            }

            IsGrounded = CheckGrounded();

            // XR IT locomotion: active on ground regardless of web attachment.
            // This is what lets the player walk freely while a web is connected.
            bool xritActive = IsGrounded && !_physicsMode;
            foreach (var mb in _xrGravityBehaviours)
                if (mb != null) mb.enabled = xritActive;

            if (wallClimber != null)
            {
                Vector3 wallJump = wallClimber.ConsumePendingJump();
                if (wallJump != Vector3.zero) AddImpulse(wallJump);
            }

            ProcessWebs();

            // Kinematic path: gravity + CC movement (ground walk or free-fall without swing)
            if (!_physicsMode)
            {
                if (IsGrounded)
                    _kinVelocity = Vector3.zero;
                else
                    _kinVelocity = Vector3.ClampMagnitude(
                        _kinVelocity + Vector3.down * (gravity * Time.deltaTime), maxSpeed);

                if (_cc != null && _cc.enabled) _cc.Move(_kinVelocity * Time.deltaTime);
                else                            transform.position += _kinVelocity * Time.deltaTime;
            }
        }

        // ── Web management ───────────────────────────────────────────────────

        void ProcessWebs()
        {
            bool l = leftShooter  != null && leftShooter.IsWebActive;
            bool r = rightShooter != null && rightShooter.IsWebActive;

            // New left web
            if (l && !_leftPrev)
            {
                _leftSwing = false;
                _leftLen   = Vector3.Distance(transform.position, leftShooter.AnchorPoint);
                // Airborne + valid anchor → swing immediately
                if (!IsGrounded && IsValidSwingAnchor(leftShooter))
                    ActivateSwing(ref _leftSwing, ref _leftJoint, leftShooter.AnchorPoint, _leftLen);
                // On ground → web attaches silently; player can still walk freely
            }

            // New right web
            if (r && !_rightPrev)
            {
                _rightSwing = false;
                _rightLen   = Vector3.Distance(transform.position, rightShooter.AnchorPoint);
                if (!IsGrounded && IsValidSwingAnchor(rightShooter))
                    ActivateSwing(ref _rightSwing, ref _rightJoint, rightShooter.AnchorPoint, _rightLen);
            }

            // Ground → air transition: if a web was attached while grounded, activate
            // the swing as soon as the player leaves the ground (jump, walk off edge, etc.)
            if (!IsGrounded)
            {
                if (l && !_leftSwing && IsValidSwingAnchor(leftShooter))
                {
                    _leftLen = Vector3.Distance(transform.position, leftShooter.AnchorPoint);
                    ActivateSwing(ref _leftSwing, ref _leftJoint, leftShooter.AnchorPoint, _leftLen);
                }
                if (r && !_rightSwing && IsValidSwingAnchor(rightShooter))
                {
                    _rightLen = Vector3.Distance(transform.position, rightShooter.AnchorPoint);
                    ActivateSwing(ref _rightSwing, ref _rightJoint, rightShooter.AnchorPoint, _rightLen);
                }
            }

            // Auto-break: too far from anchor
            if (l && Vector3.Distance(transform.position, leftShooter.AnchorPoint)  > maxWebDistance)
                leftShooter.ForceDetach();
            if (r && Vector3.Distance(transform.position, rightShooter.AnchorPoint) > maxWebDistance)
                rightShooter.ForceDetach();

            // Left web released
            if (_leftPrev && !l)
            {
                bool wasSwing = _leftSwing;
                _leftSwing = false;
                if (_leftJoint != null) { Destroy(_leftJoint); _leftJoint = null; }
                if (!_rightSwing && _physicsMode) ExitPhysicsMode(wasSwing);
            }

            // Right web released
            if (_rightPrev && !r)
            {
                bool wasSwing = _rightSwing;
                _rightSwing = false;
                if (_rightJoint != null) { Destroy(_rightJoint); _rightJoint = null; }
                if (!_leftSwing && _physicsMode) ExitPhysicsMode(wasSwing);
            }

            _leftPrev  = l;
            _rightPrev = r;
        }

        void ActivateSwing(ref bool swingFlag, ref SpringJoint joint, Vector3 anchor, float ropeLen)
        {
            swingFlag = true;
            EnterPhysicsMode();

            if (joint != null) Destroy(joint);
            joint = gameObject.AddComponent<SpringJoint>();
            joint.autoConfigureConnectedAnchor = false;
            joint.connectedBody   = null;
            joint.connectedAnchor = anchor;
            joint.maxDistance     = ropeLen;
            joint.minDistance     = 0f;       // rope can go slack — natural pendulum
            joint.spring          = webSpring;
            joint.damper          = webDamper;
            joint.enableCollision = true;
            joint.breakForce      = Mathf.Infinity;
            joint.breakTorque     = Mathf.Infinity;
        }

        bool IsValidSwingAnchor(WebShooter shooter)
        {
            Vector3   anchor = shooter.AnchorPoint;
            Rigidbody rb     = shooter.AnchorRigidbody;
            return (rb == null || rb.isKinematic)
                && anchor.y - transform.position.y >= swingMinHeight
                && Vector3.Distance(transform.position, anchor) >= swingMinDist;
        }

        // ── Mode transitions ─────────────────────────────────────────────────

        void EnterPhysicsMode()
        {
            if (_physicsMode) return;
            _physicsMode = true;
            if (_cc != null) _cc.enabled = false;
            _physicsCollider.enabled = true;
            _rb.isKinematic    = false;
            _rb.linearDamping  = 0f;
            _rb.angularDamping = 0f;
            _rb.constraints    = RigidbodyConstraints.FreezeRotation;
            _rb.linearVelocity  = _kinVelocity;
            _rb.angularVelocity = Vector3.zero;
        }

        void ExitPhysicsMode(bool applyBoost)
        {
            if (!_physicsMode) return;
            _kinVelocity = _rb.linearVelocity;
            // Prevent instant plummet on a badly-timed release
            if (applyBoost && _kinVelocity.y < 0f && releaseUpBoost > 0f)
                _kinVelocity.y = Mathf.Max(_kinVelocity.y, -releaseUpBoost * 0.5f);

            if (_leftJoint  != null) { Destroy(_leftJoint);  _leftJoint  = null; }
            if (_rightJoint != null) { Destroy(_rightJoint); _rightJoint = null; }
            _rb.isKinematic = true;
            _physicsCollider.enabled = false;
            if (_cc != null) _cc.enabled = true;
            _physicsMode = false;
        }

        bool CheckGrounded() => Physics.CheckSphere(
            transform.position + Vector3.down * 0.05f,
            groundRadius, groundLayers, QueryTriggerInteraction.Ignore);

        // ── Public API ───────────────────────────────────────────────────────

        public void AddImpulse(Vector3 impulse)
        {
            if (_physicsMode) _rb.AddForce(impulse, ForceMode.VelocityChange);
            else              _kinVelocity += impulse;
        }

        public void AddContinuousForce(Vector3 forcePerSecond)
        {
            if (_physicsMode) _rb.AddForce(forcePerSecond, ForceMode.Force);
            else              _kinVelocity += forcePerSecond * Time.deltaTime;
        }

        public void BeginDualPull(Rigidbody _) { }
    }
}
