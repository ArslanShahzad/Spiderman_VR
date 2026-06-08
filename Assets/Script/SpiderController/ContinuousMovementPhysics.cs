using UnityEngine;
using UnityEngine.InputSystem;

[RequireComponent(typeof(Rigidbody))]
public class ContinuousMovementPhysics : MonoBehaviour
{
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

    bool    _isGrounded;
    bool    _jumpPending;     // flagged in Update, consumed in FixedUpdate
    Vector2 _moveInput;
    Vector2 _turnInput;

    // ────────────────────────────────────────────────────────────────────────

    void Awake()
    {
        if (rb == null) rb = GetComponent<Rigidbody>();

        rb.freezeRotation          = true;
        rb.isKinematic             = false;
        rb.useGravity              = true;
        rb.interpolation           = RigidbodyInterpolation.Interpolate;
        rb.collisionDetectionMode  = CollisionDetectionMode.Continuous;
    }

    void OnEnable()
    {
        moveInputSource.action?.Enable();
        turnInputSource.action?.Enable();
        jumpInputSource.action?.Enable();
    }

    void OnDisable()
    {
        moveInputSource.action?.Disable();
        turnInputSource.action?.Disable();
        jumpInputSource.action?.Disable();
    }

    // Read inputs and flag jump in Update so WasPressedThisFrame is never missed.
    void Update()
    {
        _moveInput = moveInputSource.action?.ReadValue<Vector2>() ?? Vector2.zero;
        _turnInput = turnInputSource.action?.ReadValue<Vector2>() ?? Vector2.zero;

        if (jumpInputSource.action != null && jumpInputSource.action.WasPressedThisFrame())
            if (_isGrounded) _jumpPending = true;

        _isGrounded = CheckGrounded();
    }

    void FixedUpdate()
    {
        ApplyMovement();
        ApplyTurn();
        ApplyJump();
    }

    // ── locomotion ───────────────────────────────────────────────────────────

    void ApplyMovement()
    {
        bool canMove = !onlyMoveIfGrounded || _isGrounded;

        if (!canMove || _moveInput.sqrMagnitude < 0.01f)
        {
            // Kill horizontal velocity; preserve vertical (gravity / swing momentum).
            rb.linearVelocity = new Vector3(0f, rb.linearVelocity.y, 0f);
            return;
        }

        Camera mainCam = Camera.main;
        Transform cam = directionSource != null ? directionSource : (mainCam != null ? mainCam.transform : null);
        if (cam == null) return;

        Vector3 fwd   = cam.forward; fwd.y = 0f; fwd.Normalize();
        Vector3 right = cam.right;   right.y = 0f; right.Normalize();

        Vector3 horizontal = (fwd * _moveInput.y + right * _moveInput.x).normalized * speed;
        rb.linearVelocity  = new Vector3(horizontal.x, rb.linearVelocity.y, horizontal.z);
    }

    void ApplyTurn()
    {
        if (Mathf.Abs(_turnInput.x) < 0.1f) return;

        float   angle = _turnInput.x * turnSpeed * Time.fixedDeltaTime;
        rb.MoveRotation(rb.rotation * Quaternion.Euler(0f, angle, 0f));
    }

    void ApplyJump()
    {
        if (!_jumpPending) return;
        _jumpPending = false;

        float jumpVel = Mathf.Sqrt(2f * Mathf.Abs(Physics.gravity.y) * jumpHeight);
        rb.linearVelocity = new Vector3(rb.linearVelocity.x, jumpVel, rb.linearVelocity.z);
    }

    // ── ground check ─────────────────────────────────────────────────────────

    bool CheckGrounded()
    {
        if (bodyCollider == null)
            return Physics.CheckSphere(transform.position, 0.2f, groundLayer,
                                       QueryTriggerInteraction.Ignore);

        // Cast a sphere from the bottom hemisphere of the capsule, slightly below it.
        Vector3 worldCenter = bodyCollider.transform.TransformPoint(bodyCollider.center);
        float   halfHeight  = bodyCollider.height * 0.5f;
        float   radius      = bodyCollider.radius;
        Vector3 bottomPoint = worldCenter + Vector3.down * (halfHeight - radius);

        return Physics.CheckSphere(bottomPoint, radius + 0.05f, groundLayer,
                                   QueryTriggerInteraction.Ignore);
    }
}
