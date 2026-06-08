 using UnityEngine;
using UnityEngine.InputSystem;

public class Swing : MonoBehaviour
{
    public Transform WebOrigin;
    public LayerMask physicsLayer;
    public float maxDistance = 35f;
    public bool HasHit = false;
    public bool isSwingPointOnRight = false;

    public InputActionProperty SwingAction;
    public Transform PredictionPoint;
    public Vector3 swingPoint;

    public Rigidbody playerRigidbody;
    private SpringJoint springJoint;
    private Vector3 _prevWebOriginPos;

    public LineRenderer lineRenderer; 
    // Start is called once before the first execution of Update after the MonoBehaviour is created
    void Start()
    {
        _prevWebOriginPos = WebOrigin.position;
    }

    // Update is called once per frame
    void Update()
    {
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

        _prevWebOriginPos = WebOrigin.position;
    }

    void CheckPullback()
    {
        if (springJoint == null) return;

        Vector3 handVelocity = (WebOrigin.position - _prevWebOriginPos) / Time.deltaTime;
        Vector3 toSwingPoint = (swingPoint - playerRigidbody.position).normalized;

        // Trigger only when hand moves opposite to the swing point direction
        if (Vector3.Dot(toSwingPoint, handVelocity.normalized) < -0.5f)
        {
            playerRigidbody.AddForce(toSwingPoint * handVelocity.magnitude * 400f, ForceMode.Acceleration);
        }
    }

    void PullUp()
    {
        if(ContinuousMovementPhysics.Instance._isGrounded)
            return;

        if(Vector3.Distance(playerRigidbody.position, swingPoint) < 15f)
            return;
        Vector3 directionToSwingPoint = (swingPoint - playerRigidbody.position).normalized;
        float pullStrength = 12f; // Adjust this value to control how quickly the player is pulled towards the swing point.
        playerRigidbody.AddForce(directionToSwingPoint * pullStrength, ForceMode.Acceleration);

        Vector3 avoidanceDirection = isSwingPointOnRight ? -playerRigidbody.transform.right : playerRigidbody.transform.right;
        playerRigidbody.AddForce(avoidanceDirection * 6f, ForceMode.Acceleration);
    }

    void StartSwing()
    {
        if (HasHit)
        {
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
        // Implement logic to stop swinging, such as resetting forces or allowing the player to fall.
        Destroy(springJoint);
    }   
    void GetSwingPoint()
    {
        if(springJoint != null)
            return;
        RaycastHit hit;
       HasHit= Physics.Raycast(WebOrigin.position, WebOrigin.forward, out hit, maxDistance, physicsLayer);
        if (HasHit)        {
            Debug.Log("Swing Point: " + hit.point);
            swingPoint = hit.point;
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
        else
        {
            lineRenderer.enabled = false;
        }
    }
}
