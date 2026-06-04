using System.Collections.Generic;
using UnityEngine;
using UnityEngine.XR;

namespace SpiderMan
{
    /// Grip press → raycasts for a Rigidbody and magnetically pulls it toward the hand.
    /// When the object is within snapDistance it becomes kinematic and parents to the hand.
    /// Grip release → unparents, restores physics, and throws with tracked hand velocity.
    [AddComponentMenu("SpiderMan/Object Pull Grab")]
    public class ObjectPullGrab : MonoBehaviour
    {
        // ── Inspector ────────────────────────────────────────────────────────

        [Header("Hand")]
        [Tooltip("Enable for the Left Controller. Disable for the Right Controller.")]
        [SerializeField] bool isLeftHand = true;

        [Tooltip("Grip axis value above which pulling begins (0.1 = very sensitive, 1.0 = fully squeezed).")]
        [SerializeField, Range(0.1f, 1f)] float gripThreshold = 0.7f;

        [Header("Pull")]
        [Tooltip("Maximum raycast distance (m) used to find a pullable object when grip is pressed.")]
        [SerializeField] float pullRange = 20f;

        [Tooltip("Speed (m/s) at which a pulled object flies toward the hand anchor.")]
        [SerializeField] float pullSpeed = 14f;

        [Tooltip("Physics layers that can be pulled. Default = Everything.")]
        [SerializeField] LayerMask pullLayers = ~0;

        [Header("Hold")]
        [Tooltip("Distance (m) at which a pulled object snaps into the hand and becomes held.")]
        [SerializeField] float snapDistance = 0.4f;

        [Tooltip("Empty child transform at the palm or fingertip used as the hold position. Leave empty to use this transform.")]
        [SerializeField] Transform handAnchor;

        // ── Private ──────────────────────────────────────────────────────────
        InputDevice _device;
        bool        _gripDown;

        Rigidbody   _pulling;
        GameObject  _held;

        Vector3 _prevHandPos;
        Vector3 _handVelocity;

        // ── Unity lifecycle ──────────────────────────────────────────────────
        void Update()
        {
            PollDevice();
            TrackHandVelocity();
            ReadGrip();
            TickPull();
        }

        // ── Input ────────────────────────────────────────────────────────────
        void PollDevice()
        {
            if (_device.isValid) return;
            var buf = new List<InputDevice>();
            InputDevices.GetDevicesAtXRNode(
                isLeftHand ? XRNode.LeftHand : XRNode.RightHand, buf);
            if (buf.Count > 0) _device = buf[0];
        }

        void TrackHandVelocity()
        {
            _handVelocity = (transform.position - _prevHandPos)
                            / Mathf.Max(Time.deltaTime, 0.001f);
            _prevHandPos = transform.position;
        }

        void ReadGrip()
        {
            _device.TryGetFeatureValue(CommonUsages.grip, out float g);
            bool pressed = g >= gripThreshold;
            if (pressed == _gripDown) return;
            _gripDown = pressed;

            if (pressed) BeginPull();
            else         Release();
        }

        // ── Pull / grab logic ────────────────────────────────────────────────
        void BeginPull()
        {
            if (_held != null) return;

            if (Physics.Raycast(transform.position, transform.forward,
                    out RaycastHit hit, pullRange, pullLayers))
            {
                var rb = hit.collider.GetComponentInParent<Rigidbody>();
                if (rb != null) _pulling = rb;
            }
        }

        void Release()
        {
            if (_held != null) Drop();
            _pulling = null;
        }

        void TickPull()
        {
            if (_pulling == null || _held != null) return;

            Transform anchor = handAnchor != null ? handAnchor : transform;
            Vector3   delta  = anchor.position - _pulling.position;

            if (delta.magnitude <= snapDistance)
            {
                Grab(_pulling.gameObject);
                return;
            }

            _pulling.linearVelocity = delta.normalized * pullSpeed;
        }

        void Grab(GameObject obj)
        {
            _held    = obj;
            _pulling = null;

            var rb = obj.GetComponent<Rigidbody>();
            if (rb != null) rb.isKinematic = true;

            Transform anchor = handAnchor != null ? handAnchor : transform;
            obj.transform.SetParent(anchor, true);
            obj.transform.localPosition = Vector3.zero;
            obj.transform.localRotation = Quaternion.identity;
        }

        void Drop()
        {
            if (_held == null) return;

            _held.transform.SetParent(null, true);

            var rb = _held.GetComponent<Rigidbody>();
            if (rb != null)
            {
                rb.isKinematic    = false;
                rb.linearVelocity = _handVelocity;
            }

            _held = null;
        }
    }
}
