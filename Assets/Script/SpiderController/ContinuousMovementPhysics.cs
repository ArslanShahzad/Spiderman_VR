using UnityEngine;
using UnityEngine.InputSystem;

[RequireComponent(typeof(Rigidbody))]
public class ContinuousMovementPhysics : MonoBehaviour
{

    public static ContinuousMovementPhysics Instance { get; private set; }
    [Header("Movement")]
    public float speed      = 5f;
    public float turnSpeed  = 60f;
    public float jumpHeight = 3f;
    public bool  onlyMoveIfGrounded = true;

    [Header("Input Sources")]
    public InputActionProperty moveInputSource;
    public InputActionProperty turnInputSource;
    public InputActionProperty jumpInputSource;

    [Header("References")]
    public Rigidbody      rb;
    public LayerMask      groundLayer;
    public Transform      directionSource;
    public Transform      turnSource;
    public CapsuleCollider bodyCollider;

    // ── private state ────────────────────────────────────────────────────────

    public bool    _isGrounded;
    bool    _jumpPending;     // flagged in Update, consumed in FixedUpdate
    Vector2 _moveInput;
    Vector2 _turnInput;

    // ────────────────────────────────────────────────────────────────────────

    void Update()
    {
        _moveInput = moveInputSource.action.ReadValue<Vector2>();
        _turnInput = turnInputSource.action.ReadValue<Vector2>();
  
        if (jumpInputSource.action.WasPressedThisFrame())
            _jumpPending = true;


    }

    void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(gameObject);
            return;
        }
        Instance = this;
    }

    void FixedUpdate()
    {
        _isGrounded = CheckifGrounded();

        // Rotation always applies regardless of grounded state
        float turnAmount = _turnInput.x * turnSpeed * Time.fixedDeltaTime;
        Quaternion turnRotation = Quaternion.AngleAxis(turnAmount, Vector3.up);
        rb.MoveRotation(rb.rotation * turnRotation);

        if (onlyMoveIfGrounded && !_isGrounded)
            return;

        Quaternion rotation = Quaternion.Euler(0, directionSource.rotation.eulerAngles.y, 0);
        Vector3 Direction = rotation * new Vector3(_moveInput.x, 0, _moveInput.y);
        Vector3 TargetPosition = speed * Time.fixedDeltaTime * Direction + rb.position;
        Vector3 newPosition = turnRotation * (TargetPosition - turnSource.position) + turnSource.position;
        rb.MovePosition(newPosition);
    }
    bool CheckifGrounded()
    {

        Vector3 StartPoint = bodyCollider.transform.TransformPoint(bodyCollider.center);
        float rayLength = bodyCollider.height / 2 - bodyCollider.radius + 0.05f;

        bool HasHit = Physics.SphereCast(StartPoint, bodyCollider.radius, Vector3.down, out RaycastHit hitInfo, rayLength, groundLayer);
        return HasHit;
    
    }
}
