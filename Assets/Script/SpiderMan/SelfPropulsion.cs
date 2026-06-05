using System.Collections.Generic;
using UnityEngine;
using UnityEngine.XR;

namespace SpiderMan
{
    /// Primary button (A on right / X on left) blasts the player away from their active anchor(s).
    ///
    /// One web active  → push directly away from that anchor (button press).
    /// No web active   → push straight up (jump boost, button press).
    ///
    /// Dual-web slingshot (gesture-based):
    ///   SwingPhysics.IsSlingshotArmed must be true (both webs active, anchors within spread).
    ///   Both controllers must then be pulled back past pullBackThreshold.
    ///   Primary button is suppressed while slingshot is armed.
    [AddComponentMenu("SpiderMan/Self Propulsion")]
    public class SelfPropulsion : MonoBehaviour
    {
        // ── Inspector ────────────────────────────────────────────────────────

        [Header("Single-Web Push  (button-based)")]
        [Tooltip("Magnitude (m/s) of the velocity impulse added when the button is pressed.")]
        [SerializeField] float pushForce = 10f;

        [Tooltip("Minimum seconds between any push/slingshot. Prevents stacking impulses.")]
        [SerializeField, Min(0f)] float cooldown = 0.6f;

        [Tooltip("Extra upward component blended into the single-web push direction.")]
        [SerializeField, Range(0f, 1f)] float upwardBias = 0.25f;

        [Header("Dual-Web Slingshot  (gesture-based)")]
        [Tooltip("Minimum speed (m/s) each controller must reach while moving away from its anchor " +
                 "to trigger the slingshot launch. Think of it as how hard the player pulls back.")]
        [SerializeField] float pullBackThreshold = 1.5f;

        [Tooltip("Forward speed (m/s) added in the camera-facing direction on slingshot launch.")]
        [SerializeField] float slingshotForce = 16f;

        [Tooltip("Upward speed (m/s) added on slingshot launch.")]
        [SerializeField] float slingshotUpward = 6f;

        [Header("Swing Pull Boost  (continuous, gesture-based)")]
        [Tooltip("Minimum backward hand speed (m/s, body-relative) before the boost kicks in.")]
        [SerializeField] float swingPullThreshold = 0.5f;

        [Tooltip("How much each m/s of backward hand speed translates into forward force (N per m/s). " +
                 "Like tightening your grip mid-swing to add momentum.")]
        [SerializeField] float swingPullBoostScale = 4f;

        [Tooltip("Maximum continuous forward force (N) that the pull boost can contribute per second.")]
        [SerializeField] float maxSwingPullForce = 8f;

        [Header("References")]
        [Tooltip("WebShooter component on the Left Controller.")]
        [SerializeField] WebShooter leftShooter;

        [Tooltip("WebShooter component on the Right Controller.")]
        [SerializeField] WebShooter rightShooter;

        [Tooltip("SwingPhysics component on the XR Origin.")]
        [SerializeField] SwingPhysics swingPhysics;

        // ── Private ──────────────────────────────────────────────────────────
        float       _lastPush = -99f;
        InputDevice _lDev, _rDev;
        bool        _primWasDown;

        // Per-frame controller velocity tracked by differencing transform positions.
        Vector3 _prevLeftPos, _prevRightPos;
        Vector3 _leftVel,     _rightVel;

        // ── Unity lifecycle ──────────────────────────────────────────────────
        void Start()
        {
            if (leftShooter  != null) _prevLeftPos  = leftShooter.transform.position;
            if (rightShooter != null) _prevRightPos = rightShooter.transform.position;
        }

        void Update()
        {
            PollDevices();
            TrackControllerVelocities();

            bool slingshotArmed = swingPhysics != null && swingPhysics.IsSlingshotArmed;

            CheckSwingPullBoost();            // continuous per-frame boost while swinging

            if (slingshotArmed)
                CheckSlingshotGesture();      // both webs close — pull back both to slingshot
            else
            {
                CheckSingleWebLaunch();       // one web — pull back that controller to swing-launch
                CheckAerialGesture();         // no web + airborne — pull back both to boost forward
                ReadPrimaryButton();          // button: push away from anchor, or jump if no web
            }
        }

        // ── Device polling ───────────────────────────────────────────────────
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

        // ── Controller velocity ──────────────────────────────────────────────
        void TrackControllerVelocities()
        {
            float dt = Mathf.Max(Time.deltaTime, 0.0001f);

            if (leftShooter != null)
            {
                Vector3 pos  = leftShooter.transform.position;
                _leftVel     = (pos - _prevLeftPos) / dt;
                _prevLeftPos = pos;
            }

            if (rightShooter != null)
            {
                Vector3 pos   = rightShooter.transform.position;
                _rightVel     = (pos - _prevRightPos) / dt;
                _prevRightPos = pos;
            }
        }

        // ── Swing pull boost (continuous gesture) ────────────────────────────
        // While at least one hand has an active swing web, detect the backward-hand
        // pulling gesture (controller moving opposite to camera-forward, after removing
        // the body's current velocity). Add a proportional continuous forward force.
        // This simulates pulling on the web rope mid-arc to pump the swing.
        void CheckSwingPullBoost()
        {
            if (swingPhysics == null || !swingPhysics.IsSwinging) return;

            Camera cam = Camera.main;
            if (cam == null) return;

            Vector3 bodyVel = swingPhysics.Velocity;
            Vector3 camBack = -cam.transform.forward; // "backward" in world space

            bool lSwing = leftShooter  != null && leftShooter.IsWebActive;
            bool rSwing = rightShooter != null && rightShooter.IsWebActive;

            float leftBack  = lSwing ? Vector3.Dot(_leftVel  - bodyVel, camBack) : 0f;
            float rightBack = rSwing ? Vector3.Dot(_rightVel - bodyVel, camBack) : 0f;

            float maxPull = Mathf.Max(leftBack, rightBack);
            if (maxPull < swingPullThreshold) return;

            Vector3 fwd = new Vector3(cam.transform.forward.x, 0f, cam.transform.forward.z);
            if (fwd.sqrMagnitude < 0.01f) return;
            fwd.Normalize();

            float force = Mathf.Clamp(maxPull * swingPullBoostScale, 0f, maxSwingPullForce);
            swingPhysics.AddContinuousForce(fwd * force);
        }

        // ── Single-web push (button) ─────────────────────────────────────────
        void ReadPrimaryButton()
        {
            _lDev.TryGetFeatureValue(CommonUsages.primaryButton, out bool lb);
            _rDev.TryGetFeatureValue(CommonUsages.primaryButton, out bool rb);
            bool pressed = lb || rb;

            if (pressed && !_primWasDown && Time.time - _lastPush >= cooldown)
                ExecuteSinglePush();

            _primWasDown = pressed;
        }

        void ExecuteSinglePush()
        {
            Vector3 dir   = Vector3.zero;
            int     count = 0;

            if (leftShooter  != null && leftShooter.IsWebActive)
            {
                dir += (transform.position - leftShooter.AnchorPoint).normalized;
                count++;
            }

            if (rightShooter != null && rightShooter.IsWebActive)
            {
                dir += (transform.position - rightShooter.AnchorPoint).normalized;
                count++;
            }

            dir = count > 0 ? dir / count : Vector3.up;
            dir = (dir + Vector3.up * upwardBias).normalized;

            if (swingPhysics != null) swingPhysics.AddImpulse(dir * pushForce);
            _lastPush = Time.time;
        }

        // ── Single-web swing launch (gesture) ────────────────────────────────
        // Exactly one web must be active. When that controller is pulled back
        // (moving away from its anchor) past pullBackThreshold, launch forward+up.
        void CheckSingleWebLaunch()
        {
            bool l = leftShooter  != null && leftShooter.IsWebActive;
            bool r = rightShooter != null && rightShooter.IsWebActive;
            if (l == r) return; // need exactly one web (both or neither → not this path)

            if (Time.time - _lastPush < cooldown) return;

            // Subtract the body's current velocity so that only the player's PHYSICAL
            // hand movement counts. Without this, falling makes the controllers appear
            // to move "away from anchor" even when the hands are perfectly still.
            Vector3 bodyVel = swingPhysics != null ? swingPhysics.Velocity : Vector3.zero;

            float pull = l
                ? PullBackSpeed(leftShooter.transform,  leftShooter.AnchorPoint,  _leftVel  - bodyVel)
                : PullBackSpeed(rightShooter.transform, rightShooter.AnchorPoint, _rightVel - bodyVel);

            if (pull >= pullBackThreshold)
                ExecuteSlingshot(pull);
        }

        // ── Aerial gesture (no web, airborne) ────────────────────────────────
        // When falling freely with no web, pulling both controllers backward (in camera
        // space) applies a forward boost so the player can redirect their fall.
        void CheckAerialGesture()
        {
            bool l = leftShooter  != null && leftShooter.IsWebActive;
            bool r = rightShooter != null && rightShooter.IsWebActive;
            if (l || r) return;                                         // web active → other paths handle it
            if (swingPhysics == null || swingPhysics.IsGrounded) return;
            if (Time.time - _lastPush < cooldown) return;

            Camera cam = Camera.main;
            if (cam == null) return;

            // "Pulling back" = controllers moving opposite to camera forward
            Vector3 bodyVel = swingPhysics.Velocity;
            Vector3 camBack = -cam.transform.forward;

            float leftBack  = Vector3.Dot(_leftVel  - bodyVel, camBack);
            float rightBack = Vector3.Dot(_rightVel - bodyVel, camBack);

            if (leftBack < pullBackThreshold || rightBack < pullBackThreshold) return;

            Vector3 fwd = new(cam.transform.forward.x, 0f, cam.transform.forward.z);
            if (fwd.sqrMagnitude < 0.01f) return;

            // Slightly weaker than a full slingshot — gives directional control, not a massive launch
            swingPhysics.AddImpulse(fwd.normalized * (slingshotForce * 0.55f));
            _lastPush = Time.time;
        }

        // ── Dual-web slingshot (gesture) ─────────────────────────────────────
        void CheckSlingshotGesture()
        {
            if (Time.time - _lastPush < cooldown) return;

            // Subtract body velocity so falling with webs active doesn't falsely trigger.
            Vector3 bodyVel = swingPhysics != null ? swingPhysics.Velocity : Vector3.zero;

            float leftPull  = PullBackSpeed(leftShooter.transform,  leftShooter.AnchorPoint,  _leftVel  - bodyVel);
            float rightPull = PullBackSpeed(rightShooter.transform, rightShooter.AnchorPoint, _rightVel - bodyVel);

            if (leftPull >= pullBackThreshold && rightPull >= pullBackThreshold)
                ExecuteSlingshot((leftPull + rightPull) * 0.5f);
        }

        // Returns the speed at which the controller is moving AWAY from its anchor point.
        // Positive = controller moving away from anchor (i.e., player is pulling back).
        float PullBackSpeed(Transform ctrl, Vector3 anchor, Vector3 vel)
        {
            Vector3 awayDir = ctrl.position - anchor;
            if (awayDir.sqrMagnitude < 0.0001f) return 0f;
            return Vector3.Dot(vel, awayDir.normalized);
        }

        // avgPull: average backward hand speed that triggered this launch (m/s).
        // Force scales up from 60% at threshold to 100% at 3× threshold, so harder
        // pulls reward the player with more launch speed.
        void ExecuteSlingshot(float avgPull = 0f)
        {
            // Determine if both webs are on the same moveable Rigidbody.
            // Primary check: same Rigidbody component reference.
            // Fallback: same AnchorObject (covers compound-collider setups where
            //   GetComponentInParent may return different Rigidbody instances on sub-hierarchies).
            Rigidbody sharedRb = leftShooter  != null ? leftShooter.AnchorRigidbody  : null;
            Rigidbody rightRb  = rightShooter != null ? rightShooter.AnchorRigidbody : null;

            bool sameRb  = sharedRb != null && sharedRb == rightRb && !sharedRb.isKinematic;
            bool sameObj = !sameRb
                        && leftShooter  != null && leftShooter.AnchorObject  != null
                        && rightShooter != null
                        && leftShooter.AnchorObject == rightShooter.AnchorObject
                        && rightRb != null && !rightRb.isKinematic;

            bool pullObject = sameRb || sameObj;
            if (sameObj) sharedRb = rightRb; // use the rb we found via AnchorObject path

            if (pullObject)
            {
                if (swingPhysics != null) swingPhysics.BeginDualPull(sharedRb);
            }
            else
            {
                // Scale launch by how hard the player pulled — 60% at minimum threshold,
                // ramping to 100% at 3× the threshold.
                float forceScale = avgPull > 0f
                    ? Mathf.Clamp(0.6f + 0.4f * (avgPull - pullBackThreshold) / (pullBackThreshold * 2f), 0.6f, 1f)
                    : 1f;

                Camera cam = Camera.main;
                Vector3 fwd = cam != null ? cam.transform.forward : transform.forward;
                fwd = new Vector3(fwd.x, 0f, fwd.z);
                if (fwd.sqrMagnitude < 0.01f)
                    fwd = new Vector3(transform.forward.x, 0f, transform.forward.z);
                fwd.Normalize();

                Vector3 impulse = fwd * (slingshotForce * forceScale)
                                + Vector3.up * (slingshotUpward * forceScale);
                if (swingPhysics != null) swingPhysics.AddImpulse(impulse);
            }

            _lastPush = Time.time;
        }

    }
}
