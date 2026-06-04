using System.Collections.Generic;
using UnityEngine;
using UnityEngine.XR;

namespace SpiderMan
{
    /// Fires a web from the controller tip on Trigger press.
    /// Draws a three-point LineRenderer (hand → sag midpoint → anchor).
    /// Exposes AnchorPoint, AnchorObject, and AnchorRigidbody so SwingPhysics
    /// can determine swing mode, ground jump, and dual-web grab logic.
    [AddComponentMenu("SpiderMan/Web Shooter")]
    public class WebShooter : MonoBehaviour
    {
        // ── Inspector ────────────────────────────────────────────────────────

        [Header("Hand")]
        [Tooltip("Enable for the Left Controller. Disable for the Right Controller.")]
        [SerializeField] bool isLeftHand = true;

        [Tooltip("Trigger axis value above which the web fires (0 = never fires, 1 = fully pressed required).")]
        [SerializeField, Range(0.1f, 1f)] float triggerThreshold = 0.7f;

        [Header("Web")]
        [Tooltip("Maximum world-space distance (m) the web can reach and attach.")]
        [SerializeField] float maxWebDistance = 40f;

        [Tooltip("Physics layers the web can stick to. Set to Everything to allow any surface.")]
        [SerializeField] LayerMask webAttachLayers = ~0;

        [Header("Visual")]
        [Tooltip("Empty child transform at the controller tip. The web line starts here. Leave empty to use this transform's position.")]
        [SerializeField] Transform webOriginPoint;

        [Tooltip("Colour of the web strand rendered by the LineRenderer.")]
        [SerializeField] Color webColor = new Color(0.88f, 0.95f, 1f);

        [Tooltip("How much the web sags per metre of length (0 = perfectly taut wire look, 0.1 = loose rope look).")]
        [SerializeField, Range(0f, 0.3f)] float webSag = 0.04f;

        // ── Public API ───────────────────────────────────────────────────────
        /// True while the trigger is held and the web is attached to a surface.
        public bool IsWebActive { get; private set; }

        /// World-space position where the web is anchored.
        public Vector3 AnchorPoint { get; private set; }

        /// The root Rigidbody's GameObject hit by the web raycast.
        /// Null if the hit surface belongs to a static object with no Rigidbody.
        public GameObject AnchorObject { get; private set; }

        /// The Rigidbody of the anchored object. Null when the surface is static/immovable.
        public Rigidbody AnchorRigidbody { get; private set; }

        /// World-space position where the web strand visually originates.
        public Vector3 ShootOrigin =>
            webOriginPoint != null ? webOriginPoint.position : transform.position;

        // ── Private ──────────────────────────────────────────────────────────
        LineRenderer _line;
        InputDevice  _device;
        bool         _triggerDown;

        // ── Unity lifecycle ──────────────────────────────────────────────────
        void Awake() => BuildLineRenderer();

        void Update()
        {
            PollDevice();
            ProcessTrigger();
            if (IsWebActive) UpdateLine();
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

        void ProcessTrigger()
        {
            _device.TryGetFeatureValue(CommonUsages.trigger, out float val);
            bool pressed = val >= triggerThreshold;
            if (pressed == _triggerDown) return;
            _triggerDown = pressed;

            if (pressed) TryAttach();
            else         Detach();
        }

        // ── Web logic ────────────────────────────────────────────────────────
        void TryAttach()
        {
            if (Physics.Raycast(ShootOrigin, transform.forward,
                    out RaycastHit hit, maxWebDistance, webAttachLayers))
            {
                AnchorPoint     = hit.point;
                AnchorRigidbody = hit.collider.GetComponentInParent<Rigidbody>();
                AnchorObject    = AnchorRigidbody != null
                    ? AnchorRigidbody.gameObject
                    : hit.collider.gameObject;

                IsWebActive   = true;
                _line.enabled = true;
            }
        }

        void Detach()
        {
            IsWebActive     = false;
            AnchorObject    = null;
            AnchorRigidbody = null;
            _line.enabled   = false;
        }

        /// Called by SwingPhysics to forcefully release the web (e.g. after a ground jump).
        public void ForceDetach() => Detach();

        // ── Visual ───────────────────────────────────────────────────────────
        void UpdateLine()
        {
            Vector3 start = ShootOrigin;
            Vector3 end   = AnchorPoint;
            float   dist  = (end - start).magnitude;
            float   sag   = dist * webSag;

            float pulse = 0.016f + Mathf.Sin(Time.time * 9f) * 0.003f;
            _line.startWidth = pulse;
            _line.endWidth   = 0.005f;

            _line.SetPosition(0, start);
            _line.SetPosition(1, (start + end) * 0.5f + Vector3.down * sag);
            _line.SetPosition(2, end);
        }

        void BuildLineRenderer()
        {
            var go = new GameObject("WebLine");
            go.transform.SetParent(transform, false);
            _line = go.AddComponent<LineRenderer>();

            _line.positionCount     = 3;
            _line.useWorldSpace     = true;
            _line.textureMode       = LineTextureMode.Tile;
            _line.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            _line.receiveShadows    = false;
            _line.numCapVertices    = 4;

            var shader = Shader.Find("Universal Render Pipeline/Unlit")
                      ?? Shader.Find("Unlit/Color");
            if (shader != null)
                _line.sharedMaterial = new Material(shader) { color = webColor };

            _line.enabled = false;
        }
    }
}
