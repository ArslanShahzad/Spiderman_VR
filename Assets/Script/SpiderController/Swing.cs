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
            // Implement swinging mechanics here, such as applying forces or moving the player towards the swingPoint.
        }
        else if(SwingAction.action.WasReleasedThisFrame())
        {
            Debug.Log("Stopped swinging.");
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
            if (_targetRigidbody != null)
            {
                _isPullingObject = true;
                return;
            }

            Debug.Log("Swinging to: " + swingPoint);
            springJoint = playerRigidbody.gameObject.AddComponent<SpringJoint>();
            // Implement swinging mechanics here, such as applying forces or moving the player towards the swingPoint.
            springJoint.autoConfigureConnectedAnchor = false;
            springJoint.connectedAnchor = swingPoint;

            float distanceFromPoint = Vector3.Distance(playerRigidbody.position, swingPoint);
            // The distance grapple will try to keep from grapple point.
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
            lineRenderer.positionCount = 2;
            lineRenderer.SetPosition(0, WebOrigin.position);
            lineRenderer.SetPosition(1, swingPoint);
            lineRenderer.enabled = true;
            PullUp();
        }
        else if (_isPullingObject && _targetRigidbody != null)
        {
            lineRenderer.positionCount = 2;
            lineRenderer.SetPosition(0, WebOrigin.position);
            lineRenderer.SetPosition(1, _targetRigidbody.position);
            lineRenderer.enabled = true;
            PullObject();
        }
        else
        {
            lineRenderer.enabled = false;
        }
    }

    void PullObject()
    {
        if (_targetRigidbody == null) return;
        Vector3 toPlayer = (playerRigidbody.position - _targetRigidbody.position).normalized;
        _targetRigidbody.AddForce(toPlayer * objectPullForce, ForceMode.Acceleration);
    }
}
