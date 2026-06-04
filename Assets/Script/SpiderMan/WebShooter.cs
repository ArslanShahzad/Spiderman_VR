using System.Collections.Generic;
using UnityEngine;
using UnityEngine.XR;

namespace SpiderMan
{
    /// Fires a web from the controller tip on Trigger press.
    /// Draws a 5-point catenary LineRenderer with spring-simulated elasticity:
    ///   • The visual tip springs from the hand to the anchor on attach (shoot-out feel).
    ///   • The sag springs from 0 to its resting value and oscillates (elastic bounce).
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

        [Tooltip("Resting sag per metre of web length (0 = taut wire, 0.1 = loose rope).")]
        [SerializeField, Range(0f, 0.3f)] float webSag = 0.04f;

        [Header("Web Spring  (visual only)")]
        [Tooltip("Stiffness of the spring pulling the visual tip to the real anchor. " +
                 "Higher = tip snaps faster; lower = slower shoot-out animation.")]
        [SerializeField, Range(50f, 600f)] float anchorSpringStiffness = 200f;

        [Tooltip("Damping on the anchor spring. Lower values give more bounce after snap.")]
        [SerializeField, Range(1f, 40f)] float anchorSpringDamping = 14f;

        [Tooltip("Stiffness of the spring driving the sag. Lower = slower, bouncier sag settle.")]
        [SerializeField, Range(1f, 20f)] float sagSpringStiffness = 8f;

        [Tooltip("Damping on the sag spring. Lower values let the sag bounce longer.")]
        [SerializeField, Range(0.5f, 15f)] float sagSpringDamping = 3f;

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

        // Spring state for the visual anchor tip
        Vector3 _visualAnchor;
        Vector3 _visualAnchorVel;

        // Spring state for the sag amount
        float _currentSag;
        float _sagVel;

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

                // Visual spring starts at the hand so the tip travels to the anchor
                _visualAnchor    = ShootOrigin;
                _visualAnchorVel = Vector3.zero;
                _currentSag      = 0f;
                _sagVel          = 0f;
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

            // ── Anchor spring ─────────────────────────────────────────────────
            // Visual tip chases the real anchor with spring physics.
            // On first frames it travels from the hand, creating the shoot-out animation.
            // While swinging, inertia makes it lag and snap back, giving an elastic stretch.
            _visualAnchorVel += (AnchorPoint - _visualAnchor) * (anchorSpringStiffness * Time.deltaTime);
            _visualAnchorVel *= Mathf.Clamp01(1f - anchorSpringDamping * Time.deltaTime);
            _visualAnchor    += _visualAnchorVel * Time.deltaTime;

            Vector3 end  = _visualAnchor;
            float   dist = (end - start).magnitude;

            // ── Sag spring ────────────────────────────────────────────────────
            // Sag starts at 0 on attach and bounces to its resting value.
            float targetSag = dist * webSag;
            _sagVel     += (targetSag - _currentSag) * (sagSpringStiffness * Time.deltaTime);
            _sagVel     *= Mathf.Clamp01(1f - sagSpringDamping * Time.deltaTime);
            _currentSag += _sagVel * Time.deltaTime;
            _currentSag  = Mathf.Max(_currentSag, 0f);

            // ── Width pulse ───────────────────────────────────────────────────
            float pulse = 0.016f + Mathf.Sin(Time.time * 9f) * 0.003f;
            _line.startWidth = pulse;
            _line.endWidth   = 0.005f;

            // ── 5-point smooth catenary ───────────────────────────────────────
            // Sin curve peaks at the midpoint (t=0.5), giving a natural droop shape.
            for (int i = 0; i < 5; i++)
            {
                float   t    = i / 4f;
                Vector3 pt   = Vector3.Lerp(start, end, t);
                float   droop = _currentSag * Mathf.Sin(t * Mathf.PI);
                _line.SetPosition(i, pt + Vector3.down * droop);
            }
        }

        void BuildLineRenderer()
        {
            var go = new GameObject("WebLine");
            go.transform.SetParent(transform, false);
            _line = go.AddComponent<LineRenderer>();

            _line.positionCount     = 5;
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
