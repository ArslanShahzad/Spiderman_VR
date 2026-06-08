using UnityEngine;
using UnityEngine.InputSystem;

public class Swing : MonoBehaviour
{
    public Transform WebOrigin;
    public LayerMask physicsLayer;
    public float maxDistance = 35f;
    public bool HasHit = false;

    public InputActionProperty SwingAction;
    public Transform PredictionPoint;
    public Vector3 swingPoint;

    public Rigidbody playerRigidbody;
    private SpringJoint springJoint;    
    // Start is called once before the first execution of Update after the MonoBehaviour is created
    void Start()
    {
        
    }

    // Update is called once per frame
    void Update()
    {
        GetSwingPoint();

        if(SwingAction.action.WasPressedThisFrame() && HasHit)
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
        RaycastHit hit;
       HasHit= Physics.Raycast(WebOrigin.position, WebOrigin.forward, out hit, maxDistance, physicsLayer);
        if (HasHit)        {
            Debug.Log("Swing Point: " + hit.point);
            swingPoint = hit.point;
            PredictionPoint.gameObject.SetActive(true);
            PredictionPoint.position = swingPoint;
        }
        else    {
            PredictionPoint.gameObject.SetActive(false);
        }
    }
}
