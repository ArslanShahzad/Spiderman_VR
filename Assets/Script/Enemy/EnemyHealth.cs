using System.Collections;
using UnityEngine;

[RequireComponent(typeof(EnemyAI))]
public class EnemyHealth : MonoBehaviour, IDamageable
{
    // ─── Settings ─────────────────────────────────────────────────────────────
    [Header("── Health ──")]
    public float maxHealth    = 100f;
    public float currentHealth { get; private set; }

    [Header("── Ragdoll ──")]
    [Tooltip("Root bone of the ragdoll hierarchy (usually 'pelvis' or 'root')")]
    public Transform ragdollRoot;
    [Tooltip("Force applied to the ragdoll on death (from the direction of the last hit)")]
    public float deathImpulse  = 150f;
    [Tooltip("Force applied upward on ragdoll death")]
    public float deathUpForce  = 50f;

    [Header("── Hit Flash ──")]
    [Tooltip("Renderers to flash when hit")]
    public Renderer[] hitRenderers;
    public Color hitFlashColor = new Color(1f, 0.2f, 0.2f, 1f);
    public float hitFlashDuration = 0.12f;

    [Header("── Events (optional) ──")]
    [Tooltip("Called the moment the enemy dies")]
    public UnityEngine.Events.UnityEvent OnDeath;
    [Tooltip("Called each time damage is taken (while alive)")]
    public UnityEngine.Events.UnityEvent OnHit;

    // ─── Internal ─────────────────────────────────────────────────────────────
    private EnemyAI  ai;
    private Animator animator;

    private Rigidbody[]  ragdollBodies;
    private Collider[]   ragdollColliders;
    private bool         isDead = false;

    // last-hit data used for ragdoll impulse
    private Vector3 lastHitDir     = Vector3.back;
    private Vector3 lastHitPoint   = Vector3.zero;

    // material property block for flash
    private MaterialPropertyBlock mpb;
    private static readonly int ColorPropID = Shader.PropertyToID("_BaseColor");

    // ─── Init ─────────────────────────────────────────────────────────────────
    private void Awake()
    {
        currentHealth = maxHealth;
        ai            = GetComponent<EnemyAI>();
        animator      = GetComponent<Animator>();
        mpb           = new MaterialPropertyBlock();

        GatherRagdollComponents();
        SetRagdollEnabled(false);   // physics-driven ragdoll off at start
    }

    private void GatherRagdollComponents()
    {
        Transform root = ragdollRoot != null ? ragdollRoot : transform;
        ragdollBodies    = root.GetComponentsInChildren<Rigidbody>();
        ragdollColliders = root.GetComponentsInChildren<Collider>();

        // Disable kinematic bodies that belong to the ragdoll skeleton
        // (keep the main capsule / agent collider — it should be on the root object itself)
        foreach (Rigidbody rb in ragdollBodies)
        {
            if (rb.transform == transform) continue; // skip agent root rb if any
            rb.isKinematic = true;
            rb.detectCollisions = false;
        }
    }

    // ─── IDamageable ──────────────────────────────────────────────────────────

    public void TakeDamage(float amount)
    {
        if (isDead) return;
        currentHealth -= amount;
        OnHit?.Invoke();
        StartCoroutine(HitFlash());

        if (currentHealth <= 0f)
            Die(lastHitDir, lastHitPoint);
    }

    // Overload that stores hit direction for directional ragdoll force
    public void TakeDamage(float amount, Vector3 hitDir, Vector3 hitPoint)
    {
        lastHitDir   = hitDir;
        lastHitPoint = hitPoint;
        TakeDamage(amount);
    }

    // ─── Death ────────────────────────────────────────────────────────────────

    private void Die(Vector3 hitDir, Vector3 hitPoint)
    {
        if (isDead) return;
        isDead = true;

        ai?.Die();
        OnDeath?.Invoke();

        // Disable main collider / capsule
        Collider mainCol = GetComponent<Collider>();
        if (mainCol != null) mainCol.enabled = false;

        // Disable animator so ragdoll takes over
        if (animator != null) animator.enabled = false;

        ActivateRagdoll(hitDir, hitPoint);
    }

    // ─── Ragdoll ──────────────────────────────────────────────────────────────

    private void ActivateRagdoll(Vector3 hitDir, Vector3 hitPoint)
    {
        SetRagdollEnabled(true);
        ApplyDeathForce(hitDir, hitPoint);
    }

    private void SetRagdollEnabled(bool enabled)
    {
        foreach (Rigidbody rb in ragdollBodies)
        {
            if (rb.transform == transform) continue;
            rb.isKinematic     = !enabled;
            rb.detectCollisions = enabled;
        }

        foreach (Collider col in ragdollColliders)
        {
            if (col.transform == transform) continue;
            col.enabled = enabled;
        }
    }

    // Call this from outside to force-enable ragdoll (e.g. explosion)
    public void ActivateRagdoll()
    {
        if (isDead) { SetRagdollEnabled(true); return; }
        Die(Vector3.back, transform.position);
    }

    private void ApplyDeathForce(Vector3 hitDir, Vector3 hitPoint)
    {
        if (ragdollBodies == null || ragdollBodies.Length == 0) return;

        // Find closest bone to hit point
        Rigidbody closest = ragdollBodies[0];
        float minDist = float.MaxValue;
        foreach (Rigidbody rb in ragdollBodies)
        {
            if (rb.transform == transform) continue;
            float d = Vector3.Distance(rb.position, hitPoint);
            if (d < minDist) { minDist = d; closest = rb; }
        }

        Vector3 force = hitDir.normalized * deathImpulse + Vector3.up * deathUpForce;
        closest.AddForce(force, ForceMode.Impulse);
    }

    // ─── Hit Flash ────────────────────────────────────────────────────────────

    private IEnumerator HitFlash()
    {
        if (hitRenderers == null || hitRenderers.Length == 0) yield break;

        foreach (Renderer r in hitRenderers)
        {
            if (r == null) continue;
            r.GetPropertyBlock(mpb);
            mpb.SetColor(ColorPropID, hitFlashColor);
            r.SetPropertyBlock(mpb);
        }

        yield return new WaitForSeconds(hitFlashDuration);

        foreach (Renderer r in hitRenderers)
        {
            if (r == null) continue;
            r.GetPropertyBlock(mpb);
            mpb.SetColor(ColorPropID, Color.white);
            r.SetPropertyBlock(mpb);
        }
    }

    // ─── Public Helpers ───────────────────────────────────────────────────────

    public bool  IsDead      => isDead;
    public float HealthRatio => currentHealth / maxHealth;

    public void Heal(float amount)
    {
        if (isDead) return;
        currentHealth = Mathf.Min(currentHealth + amount, maxHealth);
    }

#if UNITY_EDITOR
    private void OnDrawGizmosSelected()
    {
        // Health bar above head
        Vector3 top = transform.position + Vector3.up * 2.2f;
        float barWidth = 1f;
        float ratio = Application.isPlaying ? HealthRatio : 1f;

        UnityEditor.Handles.color = Color.red;
        UnityEditor.Handles.DrawLine(top + Vector3.left * barWidth * 0.5f,
                                     top + Vector3.right * barWidth * 0.5f);
        UnityEditor.Handles.color = Color.green;
        Vector3 from = top + Vector3.left * barWidth * 0.5f;
        Vector3 to   = from + Vector3.right * barWidth * ratio;
        UnityEditor.Handles.DrawLine(from, to);
    }
#endif
}
