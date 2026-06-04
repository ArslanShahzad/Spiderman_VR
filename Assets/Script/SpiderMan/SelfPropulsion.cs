using System.Collections.Generic;
using UnityEngine;
using UnityEngine.XR;

namespace SpiderMan
{
    /// Primary button (A on right / X on left) blasts the player away from their active anchor(s).
    ///
    /// One web active  → push directly away from that anchor.
    /// Two webs active → average the two away-directions, then push.
    /// No web active   → push straight up (jump boost).
    /// An upward bias ensures the push always carries the player slightly upward.
    [AddComponentMenu("SpiderMan/Self Propulsion")]
    public class SelfPropulsion : MonoBehaviour
    {
        // ── Inspector ────────────────────────────────────────────────────────

        [Header("Push")]
        [Tooltip("Magnitude (m/s) of the velocity impulse added when the button is pressed.")]
        [SerializeField] float pushForce = 10f;

        [Tooltip("Minimum seconds between pushes. Prevents rapid button mashing from stacking impulses.")]
        [SerializeField, Min(0f)] float cooldown = 0.6f;

        [Tooltip("Extra upward component blended into the push direction (0 = pure radial push, 1 = pure upward jump).")]
        [SerializeField, Range(0f, 1f)] float upwardBias = 0.25f;

        [Header("References")]
        [Tooltip("WebShooter component on the Left Controller. Used to read the left anchor point.")]
        [SerializeField] WebShooter leftShooter;

        [Tooltip("WebShooter component on the Right Controller. Used to read the right anchor point.")]
        [SerializeField] WebShooter rightShooter;

        [Tooltip("SwingPhysics component on the XR Origin. Receives the velocity impulse via AddImpulse().")]
        [SerializeField] SwingPhysics swingPhysics;

        // ── Private ──────────────────────────────────────────────────────────
        float       _lastPush = -99f;
        InputDevice _lDev, _rDev;
        bool        _primWasDown;

        // ── Unity lifecycle ──────────────────────────────────────────────────
        void Update()
        {
            PollDevices();
            ReadPrimaryButton();
        }

        // ── Input ────────────────────────────────────────────────────────────
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

        void ReadPrimaryButton()
        {
            _lDev.TryGetFeatureValue(CommonUsages.primaryButton, out bool lb);
            _rDev.TryGetFeatureValue(CommonUsages.primaryButton, out bool rb);
            bool pressed = lb || rb;

            if (pressed && !_primWasDown && Time.time - _lastPush >= cooldown)
                ExecutePush();

            _primWasDown = pressed;
        }

        // ── Push logic ───────────────────────────────────────────────────────
        void ExecutePush()
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

            swingPhysics?.AddImpulse(dir * pushForce);
            _lastPush = Time.time;
        }
    }
}
