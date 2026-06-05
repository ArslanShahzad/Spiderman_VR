using System.Collections.Generic;
using UnityEngine;
using UnityEngine.XR;

namespace SpiderMan
{
    /// Grip-based wall climbing.
    /// When the grip button is pressed and a static surface is within grabReach,
    /// the hand sticks to that point. Moving the controller then moves the player
    /// in the opposite direction — controller up = player descends, controller down = player climbs up.
    /// SwingPhysics reads IsClimbing and ConsumeClimbDelta() each LateUpdate to apply movement.
    [AddComponentMenu("SpiderMan/Wall Climber")]
    public class WallClimber : MonoBehaviour
    {
        // ── Inspector ────────────────────────────────────────────────────────

        [Header("Grip")]
        [Tooltip("Grip axis value above which a climb grab is attempted.")]
        [SerializeField, Range(0.1f, 1f)] float gripThreshold = 0.6f;

        [Header("Climb")]
        [Tooltip("Maximum distance (m) from the controller to a surface for the grab to succeed. " +
                 "Keep small so the player must actually reach the wall.")]
        [SerializeField] float grabReach = 0.35f;

        [Tooltip("Layers that count as climbable. Exclude layers used by moveable props.")]
        [SerializeField] LayerMask climbLayers = ~0;

        [Header("Wall Jump  (fast outward release while climbing)")]
        [Tooltip("Minimum outward hand speed (m/s) on grip release to trigger a wall jump.")]
        [SerializeField] float wallJumpMinSpeed = 1.2f;

        [Tooltip("Velocity multiplier applied to the release hand velocity to produce the jump impulse.")]
        [SerializeField] float wallJumpScale = 1.2f;

        [Header("References")]
        [Tooltip("Transform of the Left Controller (WebShooter parent).")]
        [SerializeField] Transform leftController;

        [Tooltip("Transform of the Right Controller (WebShooter parent).")]
        [SerializeField] Transform rightController;

        // ── Public API ───────────────────────────────────────────────────────

        /// True while at least one hand has an active wall grab.
        public bool IsClimbing => _lGrab || _rGrab;

        /// Returns the XR Origin displacement that keeps all grabbed hands on their wall points.
        /// Resets to zero after being read — call exactly once per frame from SwingPhysics.
        public Vector3 ConsumeClimbDelta()
        {
            Vector3 d = _delta;
            _delta    = Vector3.zero;
            return d;
        }

        /// Returns and clears any pending wall-jump impulse produced by releasing the grip fast.
        /// SwingPhysics calls this each LateUpdate and feeds the result to AddImpulse().
        public Vector3 ConsumePendingJump()
        {
            Vector3 j    = _pendingJump;
            _pendingJump = Vector3.zero;
            return j;
        }

        // ── Private ──────────────────────────────────────────────────────────
        InputDevice _lDev, _rDev;
        bool        _lPrev, _rPrev;

        bool    _lGrab, _rGrab;
        Vector3 _lGrabPt, _rGrabPt;

        Vector3 _delta;
        Vector3 _pendingJump;

        // Hand velocities (world-space, measured by differencing controller positions each frame)
        Vector3 _prevLPos, _prevRPos;
        Vector3 _lHandVel, _rHandVel;

        // ── Unity lifecycle ──────────────────────────────────────────────────
        void Awake()
        {
            if (leftController  != null) _prevLPos = leftController.position;
            if (rightController != null) _prevRPos = rightController.position;
        }

        void Update()
        {
            PollDevices();
            TrackHandVelocities();
            ReadGrips();
            ComputeDelta();
        }

        void TrackHandVelocities()
        {
            float dt = Mathf.Max(Time.deltaTime, 0.0001f);
            if (leftController  != null)
            {
                _lHandVel = (leftController.position  - _prevLPos) / dt;
                _prevLPos = leftController.position;
            }
            if (rightController != null)
            {
                _rHandVel = (rightController.position - _prevRPos) / dt;
                _prevRPos = rightController.position;
            }
        }

        // ── Devices ──────────────────────────────────────────────────────────
        void PollDevices()
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

        // ── Grip input ───────────────────────────────────────────────────────
        void ReadGrips()
        {
            _lDev.TryGetFeatureValue(CommonUsages.grip, out float lg);
            _rDev.TryGetFeatureValue(CommonUsages.grip, out float rg);

            bool lHeld = lg >= gripThreshold;
            bool rHeld = rg >= gripThreshold;

            // Leading edge: attempt grab
            if ( lHeld && !_lPrev) TryGrab(leftController,  ref _lGrabPt, ref _lGrab);
            if ( rHeld && !_rPrev) TryGrab(rightController, ref _rGrabPt, ref _rGrab);

            // Release on grip release — check for wall-jump gesture BEFORE clearing grab
            if (!lHeld && _lPrev && _lGrab) OnGripRelease(_lHandVel, _lGrabPt);
            if (!rHeld && _rPrev && _rGrab) OnGripRelease(_rHandVel, _rGrabPt);
            if (!lHeld) _lGrab = false;
            if (!rHeld) _rGrab = false;

            _lPrev = lHeld;
            _rPrev = rHeld;
        }

        // When grip is released while climbing, check if the hand was moving quickly
        // away from the grab point (pushing off the wall). If so, accumulate a jump impulse.
        void OnGripRelease(Vector3 handVel, Vector3 grabPt)
        {
            Vector3 outDir = transform.position - grabPt;
            if (outDir.sqrMagnitude < 0.001f) return;
            outDir.Normalize();

            // Only the outward component of hand velocity counts
            float outSpeed = Vector3.Dot(handVel, outDir);
            if (outSpeed < wallJumpMinSpeed) return;

            // Accumulate across both hands so a two-hand push-off combines
            _pendingJump += outDir * (outSpeed * wallJumpScale);
        }

        void TryGrab(Transform ctrl, ref Vector3 grabPt, ref bool success)
        {
            success = false;
            if (ctrl == null) return;

            Collider[] hits = Physics.OverlapSphere(ctrl.position, grabReach,
                                  climbLayers, QueryTriggerInteraction.Ignore);

            float    bestDist = float.MaxValue;
            Collider bestCol  = null;

            foreach (var col in hits)
            {
                // Moveable objects belong to ObjectPullGrab — skip them here
                if (col.GetComponentInParent<Rigidbody>() != null) continue;

                Vector3 closest = col.ClosestPoint(ctrl.position);
                float   d       = Vector3.Distance(ctrl.position, closest);
                if (d < bestDist) { bestDist = d; bestCol = col; }
            }

            if (bestCol == null) return;

            grabPt  = bestCol.ClosestPoint(ctrl.position);
            success = true;
        }

        // ── Delta computation ────────────────────────────────────────────────
        // For each active grab, compute how far the origin must move so the
        // controller returns to its grab point. Average across both hands.
        void ComputeDelta()
        {
            _delta = Vector3.zero;
            if (!IsClimbing) return;

            Vector3 sum   = Vector3.zero;
            int     count = 0;

            if (_lGrab && leftController  != null) { sum += _lGrabPt - leftController.position;  count++; }
            if (_rGrab && rightController != null) { sum += _rGrabPt - rightController.position; count++; }

            if (count > 0) _delta = sum / count;
        }
    }
}
