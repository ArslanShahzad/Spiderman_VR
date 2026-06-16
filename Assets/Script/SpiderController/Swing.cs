 using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.XR;

public class Swing : MonoBehaviour
{
    public Transform TargetObject;
    public Transform WebOrigin;
    public LayerMask physicsLayer;
    public float maxDistance = 35f;
    public bool HasHit = false;
    public bool isSwingPointOnRight = false;

    public InputActionProperty SwingAction;
    public Transform PredictionPoint;
    public Vector3 swingPoint;

    public Rigidbody playerRigidbody;
    public float maxSpeed = 20f;
    private SpringJoint springJoint;
    private Vector3 _prevWebOriginPos;

    public LineRenderer lineRenderer;
    public float objectPullForce = 30f;

    [Header("Web Visual Settings")]
    [SerializeField] private int ropeSegments = 20;
    [SerializeField] private float sagFactor = 0.04f;
    [SerializeField] private float shootDuration = 0.12f;
    [SerializeField] private float swayFrequency = 8f;
    [Tooltip("Side-to-side sway amplitude. Set to 0 to disable completely.")]
    [SerializeField] private float swayAmplitude = 0f;
    [Tooltip("How fast the sway fades after the web attaches. Higher = fades faster.")]
    [SerializeField] private float swayDamping = 2.5f;

    private bool _isShootingWeb = false;
    private float _shootProgress = 0f;
    private float _swingAge = 0f;

    private Rigidbody _targetRigidbody;
    private bool _isPullingObject;

    [Header("Wall Climb")]
    public InputActionProperty gripAction;
    public InputActionProperty leftControllerPositionAction;
    public TrackedPoseDriver leftControllerPoseDriver;
    private bool _isTouchingWall = false;
    public bool _isStuckToWall = false;
    private Vector3 _stuckPosition;
    private Vector3 _wallNormal;
    private Vector3 _wallNormalLocal;
    private Vector3 _prevRawHandPos;

    public GameObject WebParticle;
    public bool isWebParticleActive = false;


    // Start is called once before the first execution of Update after the MonoBehaviour is created
    void Start()
    {
        _prevWebOriginPos = WebOrigin.position;
    }

    void FixedUpdate()
    {
        if (playerRigidbody == null) return;
        if (playerRigidbody.linearVelocity.sqrMagnitude > maxSpeed * maxSpeed)
            playerRigidbody.linearVelocity = playerRigidbody.linearVelocity.normalized * maxSpeed;
    }

    // Update is called once per frame
    void Update()
    {
   
        CheckWallStick();
        GetSwingPoint();
        CheckPullback();
        DrawRope();
        if(SwingAction.action.WasPressedThisFrame())
        {
            Debug.Log("Swinging to: " + swingPoint);
            StartSwing();
            if(WebParticle != null && !isWebParticleActive)
            {
                isWebParticleActive = true;
                GameObject particleInstance = Instantiate(WebParticle, WebOrigin);
                particleInstance.transform.position = WebOrigin.position;
                particleInstance.transform.rotation = WebOrigin.rotation;
                Destroy(particleInstance, 2f);
            }
            // Implement swinging mechanics here, such as applying forces or moving the player towards the swingPoint.
        }
        else if(SwingAction.action.WasReleasedThisFrame())
        {
            Debug.Log("Stopped swinging.");
            isWebParticleActive = false;
            StopSwing();
            // Implement logic to stop swinging, such as resetting forces or allowing the player to fall.
        }

        if (gripAction.action.WasPressedThisFrame() && _isTouchingWall && !_isStuckToWall)
        {
            if (playerRigidbody == null || leftControllerPositionAction.action == null)
            {
                Debug.LogError($"[Swing] Missing assignment on {gameObject.name}: " +
                    $"playerRigidbody={(playerRigidbody == null ? "NULL" : "ok")}, " +
                    $"leftControllerPositionAction={(leftControllerPositionAction.action == null ? "NULL" : "ok")}");
                return;
            }

            _wallNormalLocal = playerRigidbody.transform.InverseTransformDirection(_wallNormal);
            _prevRawHandPos = leftControllerPositionAction.action.ReadValue<Vector3>();
            playerRigidbody.linearVelocity = Vector3.zero;
            playerRigidbody.useGravity = false;
            _isStuckToWall = true;
            if (leftControllerPoseDriver != null)
                leftControllerPoseDriver.enabled = false;
        }
        else if (gripAction.action.WasReleasedThisFrame() && _isStuckToWall)
        {
            _isStuckToWall = false;
            _isTouchingWall = false;
            if (leftControllerPoseDriver != null)
                leftControllerPoseDriver.enabled = true;
            playerRigidbody.useGravity = true;
        }

        _prevWebOriginPos = WebOrigin.position;

        if (_isStuckToWall) return;
        transform.localPosition = TargetObject.localPosition;
        transform.localRotation = TargetObject.localRotation;
    }

    void OnTriggerEnter(Collider other)
    {
        Vector3 closestPoint = other.ClosestPoint(transform.position);
        Vector3 normal = (transform.position - closestPoint).normalized;
        if (normal == Vector3.zero) normal = Vector3.forward;
        _stuckPosition = closestPoint + normal * 0.02f;
        _wallNormal = normal;
        _isTouchingWall = true;
    }

    void OnTriggerExit(Collider other)
    {
        if (!_isStuckToWall)
            _isTouchingWall = false;
    }

    void CheckWallStick()
    {
        if (!_isStuckToWall) return;

        // Keep controller frozen at the wall grip point
        if (TargetObject != null)
            TargetObject.position = _stuckPosition;

        // Read actual physical hand position in tracking space (unaffected by frozen transform)
        Vector3 rawHandPos = leftControllerPositionAction.action.ReadValue<Vector3>();
        Vector3 handDelta = rawHandPos - _prevRawHandPos;
        _prevRawHandPos = rawHandPos;

        // Only apply movement parallel to wall — perpendicular axis is ignored while gripping
        Vector3 parallelDelta = handDelta - Vector3.Project(handDelta, _wallNormalLocal);
        Vector3 worldDelta = playerRigidbody.transform.TransformDirection(parallelDelta);
        playerRigidbody.position -= worldDelta;
    }

    void CheckPullback()
    {
        if (springJoint == null) return;

             if(Vector3.Distance(playerRigidbody.position, swingPoint) < 10f)
            return;


        Vector3 handVelocity = (WebOrigin.position - _prevWebOriginPos) / Time.deltaTime;
        Vector3 toSwingPoint = (swingPoint - playerRigidbody.position).normalized;


        // Trigger only when hand moves opposite to the swing point direction
        if (Vector3.Dot(toSwingPoint, handVelocity.normalized) < -0.5f)
        {
            if (ContinuousMovementPhysics.Instance._isGrounded)
            {
                     playerRigidbody.AddForce(toSwingPoint * handVelocity.magnitude * 200f, ForceMode.Acceleration);
            }
            else
            {
                     playerRigidbody.AddForce(toSwingPoint * handVelocity.magnitude * 100f, ForceMode.Acceleration);
            }
       
        }
    }

    void PullUp()
    {
        if(ContinuousMovementPhysics.Instance._isGrounded)
            return;

        if(Vector3.Distance(playerRigidbody.position, swingPoint) < 10f)
            return;
        Vector3 directionToSwingPoint = (swingPoint - playerRigidbody.position).normalized;
        float pullStrength = 4f; // Adjust this value to control how quickly the player is pulled towards the swing point.
        playerRigidbody.AddForce(directionToSwingPoint * pullStrength, ForceMode.Acceleration);

        Vector3 avoidanceDirection = isSwingPointOnRight ? -playerRigidbody.transform.right : playerRigidbody.transform.right;
        playerRigidbody.AddForce(avoidanceDirection * 3f, ForceMode.Acceleration);
    }

    void StartSwing()
    {
        if (HasHit)
        {
            _isShootingWeb = true;
            _shootProgress = 0f;
            _swingAge = 0f;

            if (_targetRigidbody != null)
            {
                _isPullingObject = true;
                return;
            }

            Debug.Log("Swinging to: " + swingPoint);
            springJoint = playerRigidbody.gameObject.AddComponent<SpringJoint>();
            springJoint.autoConfigureConnectedAnchor = false;
            springJoint.connectedAnchor = swingPoint;

            float distanceFromPoint = Vector3.Distance(playerRigidbody.position, swingPoint);
            springJoint.maxDistance = distanceFromPoint;
            springJoint.spring = 4.5f;
            springJoint.damper = 7f;
            springJoint.massScale = 4.5f;
        }
    }

    void StopSwing()
    {
        Debug.Log("Stopped swinging.");
        _isPullingObject = false;
        _targetRigidbody = null;
        _isShootingWeb = false;
        _shootProgress = 0f;
        _swingAge = 0f;
        Destroy(springJoint);
    }
    void GetSwingPoint()
    {
        if(springJoint != null || _isPullingObject)
            return;
        RaycastHit hit;
       HasHit= Physics.Raycast(WebOrigin.position, WebOrigin.forward, out hit, maxDistance, physicsLayer);
        if (HasHit)        {
            Debug.Log("Swing Point: " + hit.point);
            swingPoint = hit.point;
            _targetRigidbody = hit.rigidbody;
            Vector3 toSwingPoint = swingPoint - playerRigidbody.position;
            Vector3 toSwingPointFlat = new(toSwingPoint.x, 0f, toSwingPoint.z);
            float swingAngle = Vector3.SignedAngle(playerRigidbody.transform.forward, toSwingPointFlat, Vector3.up);
            if (swingAngle > 20f)
                isSwingPointOnRight = true;
            else if (swingAngle < -20f)
                isSwingPointOnRight = false;
            PredictionPoint.gameObject.SetActive(true);
            PredictionPoint.position = swingPoint;
        }
        else    {
            _targetRigidbody = null;
            PredictionPoint.gameObject.SetActive(false);
        }
    }

    void DrawRope()
    {
        if (springJoint != null)
        {
            _swingAge += Time.deltaTime;

            Vector3 start = WebOrigin.position;
            Vector3 end = AdvanceShootAnimation(swingPoint);

            DrawCurvedRope(start, end);
            lineRenderer.enabled = true;
            PullUp();
        }
        else if (_isPullingObject && _targetRigidbody != null)
        {
            _swingAge += Time.deltaTime;

            Vector3 start = WebOrigin.position;
            Vector3 end = AdvanceShootAnimation(_targetRigidbody.position);

            DrawCurvedRope(start, end);
            lineRenderer.enabled = true;
            PullObject();
        }
        else
        {
            lineRenderer.enabled = false;
        }
    }

    Vector3 AdvanceShootAnimation(Vector3 target)
    {
        if (!_isShootingWeb) return target;

        _shootProgress += Time.deltaTime / Mathf.Max(shootDuration, 0.001f);
        if (_shootProgress >= 1f)
        {
            _shootProgress = 1f;
            _isShootingWeb = false;
        }
        return Vector3.Lerp(WebOrigin.position, target, _shootProgress);
    }

    void DrawCurvedRope(Vector3 start, Vector3 end)
    {
        int segments = Mathf.Max(ropeSegments, 2);
        lineRenderer.positionCount = segments;

        Vector3 ropeDir = end - start;
        float ropeLength = ropeDir.magnitude;

        // Reduce sag when rope points nearly vertical so it doesn't look wrong
        float verticalFactor = Mathf.Abs(Vector3.Dot(ropeDir.normalized, Vector3.up));
        float baseSag = ropeLength * sagFactor * (1f - verticalFactor * 0.8f);

        // Tension: shrink sag as SpringJoint pulls rope taut
        if (springJoint != null && springJoint.maxDistance > 0f)
        {
            float currentDist = Vector3.Distance(playerRigidbody.position, swingPoint);
            float tensionRatio = Mathf.Clamp01(1f - (currentDist / springJoint.maxDistance));
            baseSag *= 1f - tensionRatio;
        }

        // Sway axis perpendicular to rope; fall back to right if rope is vertical
        Vector3 swayAxis = Vector3.Cross(ropeDir.normalized, Vector3.up);
        if (swayAxis.sqrMagnitude < 0.001f) swayAxis = Vector3.right;
        else swayAxis.Normalize();

        // Damped oscillation controlled entirely by swayAmplitude (0 = off)
        float damping = Mathf.Exp(-_swingAge * swayDamping);
        float swayAmount = Mathf.Sin(Time.time * swayFrequency) * swayAmplitude * damping * ropeLength;

        for (int i = 0; i < segments; i++)
        {
            float t = (float)i / (segments - 1);
            Vector3 pos = Vector3.Lerp(start, end, t);

            // Parabolic sag peaks at midpoint, zero at both ends
            float envelope = 4f * t * (1f - t);
            pos -= Vector3.up * (baseSag * envelope);
            pos += swayAxis * (swayAmount * envelope);

            lineRenderer.SetPosition(i, pos);
        }
    }

    void PullObject()
    {
        if (_targetRigidbody == null) return;
        Vector3 toPlayer = (playerRigidbody.position - _targetRigidbody.position).normalized;
        _targetRigidbody.AddForce(toPlayer * objectPullForce, ForceMode.Acceleration);
    }
}
