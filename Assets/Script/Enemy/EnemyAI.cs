using System.Collections;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.Animations.Rigging;

#if UNITY_EDITOR
using UnityEditor;
#endif

[RequireComponent(typeof(NavMeshAgent))]
public class EnemyAI : MonoBehaviour
{
    // ─── Enums ────────────────────────────────────────────────────────────────
    public enum EnemyState  { Patrol, Alert, Chase, Combat, Search, Dead }
    public enum PatrolMode  { Waypoints, Random }
    public enum CombatType  { Shooting, Melee }
    public enum WeaponType  { SingleShot, Automatic, Burst }

    // ─── Nested Config Classes ────────────────────────────────────────────────

    /// <summary>
    /// Gun transform configuration. positionOffset and rotationOffset are
    /// additive on top of the gun's authored local transform, so the gun
    /// can be fine-tuned in the Inspector without moving the actual prefab pivot.
    /// </summary>
    [System.Serializable]
    public class GunConfig
    {
        [Tooltip("Root GameObject of the gun (typically parented to the hand_r bone)")]
        public GameObject gunRoot;

        [Tooltip("Local position added on top of the gun's authored position")]
        public Vector3 positionOffset = Vector3.zero;

        [Tooltip("Local rotation (Euler) added on top of the gun's authored rotation")]
        public Vector3 rotationOffset = Vector3.zero;

        // Runtime-only — stores the authored values so offsets are always relative
        [System.NonSerialized] public Vector3    baseLocalPos;
        [System.NonSerialized] public Quaternion baseLocalRot;
    }

    /// <summary>
    /// All melee-specific parameters. attackRange is the inner trigger distance —
    /// keep combatRange slightly larger so the enemy first closes in, then
    /// attacks when truly within reach.
    /// </summary>
    [System.Serializable]
    public class MeleeConfig
    {
        [Tooltip("Distance at which the melee swing triggers")]
        public float attackRange    = 1.8f;

        [Tooltip("Damage applied per hit")]
        public float attackDamage   = 35f;

        [Tooltip("Minimum seconds between attacks")]
        public float attackCooldown = 1.5f;

        [Tooltip("Number of distinct combo variants (cycles MeleeCombo 0 → comboCount-1)")]
        public int   comboCount     = 2;

        [Tooltip("Seconds after the trigger fires before damage is applied (wind-up)")]
        public float damageDelay    = 0.4f;

        [System.NonSerialized] public float nextAttackTime;
        [System.NonSerialized] public int   currentCombo;
    }

    /// <summary>
    /// Maps every enemy action to a direct Animator state name.
    /// The AI calls animator.Play(stateName) or CrossFade(stateName) — no
    /// parameters, blend trees, or triggers are used.
    /// Change any name here to match the state names in your Animator Controller.
    /// </summary>
    [System.Serializable]
    public class EnemyAnimationProfile
    {
        [Header("── Looping Movement States ──")]
        public string idle             = "Idle";
        public string walk             = "Walk";
        public string run              = "Run";

        [Header("── Shooting States ──")]
        [Tooltip("Looping: standing still in combat range with weapon raised")]
        public string aimIdle          = "AimIdle";
        [Tooltip("One-shot: plays once per single/burst round")]
        public string shoot            = "Shoot";
        [Tooltip("Looping: used while Automatic weapon is firing")]
        public string fireContinuous   = "FireContinuous";
        [Tooltip("One-shot: reload clip (call PlayReload() from code if needed)")]
        public string reload           = "Reload";

        [Header("── Melee States ──")]
        [Tooltip("Looping: standing still at melee range")]
        public string meleeIdle        = "MeleeIdle";
        [Tooltip("One-shot states per combo step — index matches combo counter.\n" +
                 "Add more entries to increase combo length.\n" +
                 "Falls back to index 0 if currentCombo is out of range.")]
        public string[] meleeAttacks   = { "MeleeAttack_0", "MeleeAttack_1" };

        [Header("── Alert / Death ──")]
        [Tooltip("One-shot: plays once when the enemy first spots the player")]
        public string alert            = "Alert";
        [Tooltip("One-shot: death animation — disables further state changes")]
        public string die              = "Die";

        [Header("── Playback Settings ──")]
        [Tooltip("Animator layer index (0 = Base Layer)")]
        public int   layer             = 0;
        [Tooltip("CrossFade blend time for looping-state transitions (seconds).\n" +
                 "Set to 0 for an instant snap (same as Play).")]
        public float crossfadeDuration = 0.1f;
    }

    // ─── Inspector ────────────────────────────────────────────────────────────

    [Header("── State ──")]
    [SerializeField] private EnemyState currentState = EnemyState.Patrol;
    public EnemyState CurrentState => currentState;

    [Header("── References ──")]
    [Tooltip("Target to fight (auto-finds 'Player' tag if empty)")]
    public Transform   target;
    [Tooltip("Eye / head transform — vision raycast origin")]
    public Transform   eyeTransform;
    [Tooltip("Muzzle point — bullet origin (Shooting only)")]
    public Transform   muzzlePoint;

    [Header("── Combat Type ──")]
    [Tooltip("Shooting: enemy uses gun. Melee: gun hidden, punches only.")]
    public CombatType  combatType  = CombatType.Shooting;
    [Tooltip("Used only when CombatType = Shooting")]
    public WeaponType  weaponType  = WeaponType.SingleShot;

    [Header("── Gun Configuration ──")]
    public GunConfig   gun         = new GunConfig();

    [Header("── Melee Configuration ──")]
    public MeleeConfig melee       = new MeleeConfig();

    [Header("── Animation Profile ──")]
    public EnemyAnimationProfile animProfile = new EnemyAnimationProfile();

    [Header("── Navigation ──")]
    public float patrolSpeed        = 1.5f;
    public float chaseSpeed         = 4f;
    public float combatSpeed        = 2f;
    [Tooltip("Range to enter Combat state.\n  Shooting: ~8   Melee: ~2.5")]
    public float combatRange        = 8f;
    [Tooltip("Range at which the enemy begins chasing after spotting the target")]
    public float chaseRange         = 20f;
    [Tooltip("Range at which the enemy gives up the chase")]
    public float loseRange          = 30f;

    [Header("── Patrol ──")]
    public PatrolMode  patrolMode         = PatrolMode.Waypoints;
    [Tooltip("Loop order: Waypoints[0] → [1] → ... → [0]")]
    public Transform[] waypoints;
    [Tooltip("Radius for NavMesh random sampling (PatrolMode = Random)")]
    public float       randomPatrolRadius = 15f;
    [Tooltip("Seconds to idle at each waypoint")]
    public float       waypointWaitTime   = 2f;

    [Header("── Vision ──")]
    public float visionRange              = 18f;
    [Range(1f, 180f)]
    public float horizontalFOV           = 70f;
    [Range(1f, 90f)]
    public float verticalFOV             = 40f;
    public LayerMask obstacleMask;
    public LayerMask targetMask;
    [Tooltip("How often the vision coroutine runs (seconds)")]
    public float visionCheckInterval      = 0.2f;

    [Header("── Shooting ──")]
    [Tooltip("Shots per second (SingleShot / Automatic)")]
    public float fireRate                 = 1.5f;
    public int   burstCount               = 3;
    [Tooltip("Gap between shots within a burst")]
    public float burstInterval            = 0.1f;
    [Tooltip("Cooldown after a full burst")]
    public float burstCooldown            = 1.2f;
    [Tooltip("Max random angular spread per shot (degrees)")]
    public float bulletSpread             = 2f;
    public float bulletRange              = 50f;
    public float bulletDamage             = 25f;
    public LayerMask bulletHitMask;

    [Header("── Bullet Trail ──")]
    public LineRenderer bulletTrailRenderer;
    public float trailDuration            = 0.06f;
    public Color trailStartColor          = new Color(1f, 0.95f, 0.6f, 1f);
    public Color trailEndColor            = new Color(1f, 0.4f,  0.1f, 0f);
    public float trailStartWidth          = 0.015f;
    public float trailEndWidth            = 0.004f;

    [Header("── Animation Rigging ──")]
    [Tooltip("Rig component for the upper-body aim layer (Shooting only)")]
    public Rig       aimRig;
    [Tooltip("World-space transform that the MultiAimConstraint tracks")]
    public Transform aimTarget;
    public float     aimBlendSpeed   = 5f;
    public float     aimActiveWeight = 1f;
    public float     aimIdleWeight   = 0f;

    [Header("── Search ──")]
    public float searchWaitTime = 4f;

    [Header("── Audio (optional) ──")]
    public AudioSource audioSource;
    public AudioClip[] shootSounds;
    public AudioClip   reloadSound;
    public AudioClip   alertSound;
    public AudioClip   deathSound;
    public AudioClip[] meleeSounds;

    // ─── Private Runtime State ────────────────────────────────────────────────
    private NavMeshAgent agent;
    private Animator     animator;
    private EnemyHealth  health;

    private bool    targetVisible;
    private bool    isShooting;
    private float   nextFireTime;
    private float   nextBurstTime;
    private float   searchTimer;
    private float   waypointWaitTimer;
    private bool    isWaiting;
    private int     currentWaypoint;
    private Vector3 lastKnownPos;
    private bool    hasLastKnownPos;
    private float   targetAimWeight;

    // Animation state tracking
    private string currentAnimState = "";  // last state passed to PlayAnim/PlayAnimOneShot
    private bool   isOneShotPlaying  = false; // true while a one-shot is still running
    private bool   isAutoFiring      = false; // true while Automatic weapon is actively firing

    // ─── Init ─────────────────────────────────────────────────────────────────

    private void Awake()
    {
        agent    = GetComponent<NavMeshAgent>();
        animator = GetComponent<Animator>();
        health   = GetComponent<EnemyHealth>();

        ApplyGunOffset();
        SetGunVisible(combatType == CombatType.Shooting);
        SetupBulletTrail();

        if (target == null)
        {
            GameObject p = GameObject.FindWithTag("Player");
            if (p != null) target = p.transform;
        }

        StartCoroutine(VisionRoutine());
    }

    // Store the authored local transform, then layer the inspector offset on top.
    private void ApplyGunOffset()
    {
        if (gun.gunRoot == null) return;
        gun.baseLocalPos = gun.gunRoot.transform.localPosition;
        gun.baseLocalRot = gun.gunRoot.transform.localRotation;
        gun.gunRoot.transform.localPosition = gun.baseLocalPos + gun.positionOffset;
        gun.gunRoot.transform.localRotation = gun.baseLocalRot * Quaternion.Euler(gun.rotationOffset);
    }

    private void SetGunVisible(bool visible)
    {
        if (gun.gunRoot != null)
            gun.gunRoot.SetActive(visible);
    }

    private void SetupBulletTrail()
    {
        if (combatType != CombatType.Shooting) return;
        if (bulletTrailRenderer != null) { bulletTrailRenderer.enabled = false; return; }

        // Auto-create a minimal LineRenderer if none was assigned in Inspector
        GameObject go = new GameObject("BulletTrail");
        go.transform.SetParent(muzzlePoint != null ? muzzlePoint : transform);
        go.transform.localPosition = Vector3.zero;
        bulletTrailRenderer = go.AddComponent<LineRenderer>();
        bulletTrailRenderer.positionCount = 2;
        bulletTrailRenderer.useWorldSpace = true;
        bulletTrailRenderer.material      = new Material(Shader.Find("Sprites/Default"));
        bulletTrailRenderer.enabled       = false;
    }

    // ─── Update ───────────────────────────────────────────────────────────────

    private void Update()
    {
        if (currentState == EnemyState.Dead) return;

        UpdateAimRig();
        UpdateAnimator();

        switch (currentState)
        {
            case EnemyState.Patrol:  UpdatePatrol();  break;
            case EnemyState.Alert:   UpdateAlert();   break;
            case EnemyState.Chase:   UpdateChase();   break;
            case EnemyState.Combat:  UpdateCombat();  break;
            case EnemyState.Search:  UpdateSearch();  break;
        }
    }

    // ─── Vision ───────────────────────────────────────────────────────────────

    private IEnumerator VisionRoutine()
    {
        WaitForSeconds wait = new WaitForSeconds(visionCheckInterval);
        while (true)
        {
            if (currentState != EnemyState.Dead)
                targetVisible = CheckVision();
            yield return wait;
        }
    }

    private bool CheckVision()
    {
        if (target == null) return false;
        Transform eye    = eyeTransform != null ? eyeTransform : transform;
        Vector3 toTarget = target.position - eye.position;
        float dist       = toTarget.magnitude;

        if (dist > visionRange) return false;

        // Horizontal (yaw) angle
        Vector3 flatDir     = new Vector3(toTarget.x, 0f, toTarget.z).normalized;
        Vector3 flatForward = new Vector3(eye.forward.x, 0f, eye.forward.z).normalized;
        if (Vector3.Angle(flatForward, flatDir) > horizontalFOV) return false;

        // Vertical (pitch) angle
        float vAngle = Mathf.Abs(Vector3.Angle(
            toTarget.normalized,
            new Vector3(toTarget.x, 0f, toTarget.z).normalized));
        if (vAngle > verticalFOV) return false;

        // Line-of-sight raycast
        if (Physics.Raycast(eye.position, toTarget.normalized, out RaycastHit hit, dist, obstacleMask | targetMask))
            return ((1 << hit.collider.gameObject.layer) & targetMask) != 0;

        return false;
    }

    // ─── State Updates ────────────────────────────────────────────────────────

    private void UpdatePatrol()
    {
        targetAimWeight = aimIdleWeight;
        if (targetVisible) { ChangeState(EnemyState.Alert); return; }
        if (patrolMode == PatrolMode.Waypoints) WaypointPatrol();
        else                                    RandomPatrol();
    }

    private void WaypointPatrol()
    {
        if (waypoints == null || waypoints.Length == 0) return;

        if (isWaiting)
        {
            waypointWaitTimer -= Time.deltaTime;
            if (waypointWaitTimer <= 0f)
            {
                isWaiting = false;
                currentWaypoint = (currentWaypoint + 1) % waypoints.Length;
                agent.SetDestination(waypoints[currentWaypoint].position);
            }
            return;
        }

        agent.speed = patrolSpeed;
        if (!agent.pathPending && agent.remainingDistance < 0.5f)
        {
            isWaiting = true;
            waypointWaitTimer = waypointWaitTime;
        }
    }

    private void RandomPatrol()
    {
        if (!agent.pathPending && agent.remainingDistance < 0.5f)
        {
            if (isWaiting)
            {
                waypointWaitTimer -= Time.deltaTime;
                if (waypointWaitTimer > 0f) return;
                isWaiting = false;
            }

            if (RandomNavMeshPoint(transform.position, randomPatrolRadius, out Vector3 point))
            {
                agent.speed = patrolSpeed;
                agent.SetDestination(point);
                isWaiting       = true;
                waypointWaitTimer = waypointWaitTime;
            }
        }
    }

    private bool RandomNavMeshPoint(Vector3 origin, float radius, out Vector3 result)
    {
        for (int i = 0; i < 5; i++)
        {
            Vector3 rand = origin + Random.insideUnitSphere * radius;
            if (NavMesh.SamplePosition(rand, out NavMeshHit navHit, radius, NavMesh.AllAreas))
            { result = navHit.position; return true; }
        }
        result = origin;
        return false;
    }

    private void UpdateAlert()
    {
        targetAimWeight = aimActiveWeight;
        if (targetVisible)
        {
            lastKnownPos    = target.position;
            hasLastKnownPos = true;
            float dist = Vector3.Distance(transform.position, target.position);
            ChangeState(dist <= combatRange ? EnemyState.Combat : EnemyState.Chase);
        }
        else ChangeState(EnemyState.Search);
    }

    private void UpdateChase()
    {
        targetAimWeight = aimActiveWeight;
        agent.speed = chaseSpeed;

        if (targetVisible)
        {
            lastKnownPos    = target.position;
            hasLastKnownPos = true;
            agent.SetDestination(lastKnownPos);

            float dist = Vector3.Distance(transform.position, target.position);
            if (dist <= combatRange) { ChangeState(EnemyState.Combat); return; }
            if (dist  > loseRange)  { ChangeState(EnemyState.Search);  return; }
        }
        else
        {
            if (hasLastKnownPos) agent.SetDestination(lastKnownPos);
            if (!agent.pathPending && agent.remainingDistance < 1f)
                ChangeState(EnemyState.Search);
        }
    }

    private void UpdateCombat()
    {
        targetAimWeight = aimActiveWeight;

        if (!targetVisible)
        {
            isAutoFiring = false;
            ChangeState(hasLastKnownPos ? EnemyState.Chase : EnemyState.Search);
            return;
        }

        lastKnownPos = target.position;

        if (combatType == CombatType.Shooting) UpdateShootingCombat();
        else                                   UpdateMeleeCombat();
    }

    private void UpdateShootingCombat()
    {
        float dist = Vector3.Distance(transform.position, target.position);

        if (dist > combatRange * 1.2f)
        {
            agent.speed = combatSpeed;
            agent.SetDestination(target.position);
        }
        else
        {
            agent.SetDestination(transform.position);
            FaceTarget();
        }

        // Track aim target toward enemy chest height
        if (aimTarget != null)
            aimTarget.position = Vector3.Lerp(
                aimTarget.position,
                target.position + Vector3.up * 1.2f,
                Time.deltaTime * 10f);

        if (!isShooting)
        {
            switch (weaponType)
            {
                case WeaponType.SingleShot: TryShootSingle();                        break;
                case WeaponType.Automatic:  TryShootAutomatic();                     break;
                case WeaponType.Burst:      StartCoroutine(TryShootBurst());         break;
            }
        }
    }

    private void UpdateMeleeCombat()
    {
        float dist = Vector3.Distance(transform.position, target.position);

        if (dist > melee.attackRange * 0.9f)
        {
            // Still closing in
            agent.speed = combatSpeed;
            agent.SetDestination(target.position);
        }
        else
        {
            // Within range — stop and strike
            agent.SetDestination(transform.position);
            FaceTarget();
            TryMeleeAttack();
        }
    }

    private void UpdateSearch()
    {
        targetAimWeight = aimActiveWeight * 0.5f;
        agent.speed = patrolSpeed;

        if (targetVisible) { ChangeState(EnemyState.Alert); return; }

        if (hasLastKnownPos && agent.remainingDistance > 1f)
        { agent.SetDestination(lastKnownPos); return; }

        searchTimer -= Time.deltaTime;
        if (searchTimer <= 0f) { hasLastKnownPos = false; ChangeState(EnemyState.Patrol); }
    }

    // ─── Shooting ─────────────────────────────────────────────────────────────

    // One shot per cooldown — plays a one-shot Shoot state per round.
    private void TryShootSingle()
    {
        if (Time.time < nextFireTime) return;
        nextFireTime = Time.time + 1f / fireRate;
        FireBullet();
    }

    // Continuous fire — sets isAutoFiring so the looping FireContinuous state plays.
    private void TryShootAutomatic()
    {
        if (Time.time < nextFireTime) return;
        nextFireTime = Time.time + 1f / fireRate;
        isAutoFiring = true;
        FireBullet();
    }

    // 3-round burst then cooldown — IsFiring false between bursts.
    private IEnumerator TryShootBurst()
    {
        if (Time.time < nextBurstTime) yield break;
        isShooting = true;

        for (int i = 0; i < burstCount; i++)
        {
            if (currentState != EnemyState.Combat) break;
            FireBullet();
            yield return new WaitForSeconds(burstInterval);
        }

        nextBurstTime = Time.time + burstCooldown;
        isShooting    = false;
    }

    private void FireBullet()
    {
        if (muzzlePoint == null) return;

        // Automatic sustains its looping fire state; single/burst play a one-shot per round
        if (weaponType == WeaponType.Automatic)
            PlayAnim(animProfile.fireContinuous);
        else
            PlayAnimOneShot(animProfile.shoot);

        PlayRandomSound(shootSounds);

        Vector3 dir      = CalculateShootDirection();
        Vector3 hitPoint = muzzlePoint.position + dir * bulletRange;

        if (Physics.Raycast(muzzlePoint.position, dir, out RaycastHit hit, bulletRange, bulletHitMask))
        {
            hitPoint = hit.point;
            hit.collider.GetComponentInParent<IDamageable>()?.TakeDamage(bulletDamage);
        }

        StartCoroutine(ShowBulletTrail(muzzlePoint.position, hitPoint));
    }

    private Vector3 CalculateShootDirection()
    {
        Vector3 baseDir = target != null
            ? (target.position + Vector3.up * 1.2f - muzzlePoint.position).normalized
            : muzzlePoint.forward;

        return (Quaternion.Euler(
            Random.Range(-bulletSpread, bulletSpread),
            Random.Range(-bulletSpread, bulletSpread),
            0f) * baseDir).normalized;
    }

    private IEnumerator ShowBulletTrail(Vector3 from, Vector3 to)
    {
        if (bulletTrailRenderer == null) yield break;

        bulletTrailRenderer.enabled = true;
        bulletTrailRenderer.SetPosition(0, from);
        bulletTrailRenderer.SetPosition(1, to);
        bulletTrailRenderer.startColor = trailStartColor;
        bulletTrailRenderer.endColor   = trailEndColor;
        bulletTrailRenderer.startWidth = trailStartWidth;
        bulletTrailRenderer.endWidth   = trailEndWidth;

        Color sc = trailStartColor, ec = trailEndColor;
        float t = 0f;
        while (t < trailDuration)
        {
            float f = t / trailDuration;
            bulletTrailRenderer.startColor = Color.Lerp(sc, Color.clear, f);
            bulletTrailRenderer.endColor   = Color.Lerp(ec, Color.clear, f);
            t += Time.deltaTime;
            yield return null;
        }
        bulletTrailRenderer.enabled = false;
    }

    // ─── Melee ────────────────────────────────────────────────────────────────

    private void TryMeleeAttack()
    {
        if (Time.time < melee.nextAttackTime) return;
        melee.nextAttackTime = Time.time + melee.attackCooldown;

        // Pick the correct combo state name — fall back to index 0 if array is shorter
        string[] attacks = animProfile.meleeAttacks;
        string attackState = (attacks != null && attacks.Length > 0)
            ? attacks[melee.currentCombo % attacks.Length]
            : "MeleeAttack_0";

        PlayAnimOneShot(attackState);
        PlayRandomSound(meleeSounds);
        StartCoroutine(ApplyMeleeDamage());

        melee.currentCombo = (melee.currentCombo + 1) % Mathf.Max(1, melee.comboCount);
    }

    // Damage is deferred so it lines up with the animation wind-up.
    // Validates that the target is still in range on impact (avoids phantom hits).
    private IEnumerator ApplyMeleeDamage()
    {
        yield return new WaitForSeconds(melee.damageDelay);
        if (currentState == EnemyState.Dead || target == null) yield break;

        float dist = Vector3.Distance(transform.position, target.position);
        if (dist <= melee.attackRange * 1.25f)
            target.GetComponentInParent<IDamageable>()?.TakeDamage(melee.attackDamage);
    }

    // ─── Animation Rigging ────────────────────────────────────────────────────

    private void UpdateAimRig()
    {
        // Aim rig only active for shooting enemies
        if (aimRig == null || combatType == CombatType.Melee) return;
        aimRig.weight = Mathf.Lerp(aimRig.weight, targetAimWeight, Time.deltaTime * aimBlendSpeed);
    }

    // ─── Animation Playback ───────────────────────────────────────────────────

    /// <summary>
    /// Play a looping state. Uses CrossFade so movement transitions are smooth.
    /// Skips the call if the state is already playing, and waits for any
    /// in-progress one-shot to finish before blending away.
    /// </summary>
    private void PlayAnim(string stateName)
    {
        if (animator == null || string.IsNullOrEmpty(stateName)) return;

        // Let a one-shot finish naturally before returning to a looping state
        if (isOneShotPlaying)
        {
            AnimatorStateInfo si = animator.GetCurrentAnimatorStateInfo(animProfile.layer);
            if (!si.loop && si.normalizedTime < 1f) return;
            isOneShotPlaying = false;
        }

        if (currentAnimState == stateName) return;
        currentAnimState = stateName;

        if (animProfile.crossfadeDuration > 0f)
            animator.CrossFade(stateName, animProfile.crossfadeDuration, animProfile.layer);
        else
            animator.Play(stateName, animProfile.layer);
    }

    /// <summary>
    /// Play a one-shot state (shoot, melee, alert, die). Always restarts from
    /// frame 0 and sets a lock so PlayAnim won't interrupt it mid-clip.
    /// </summary>
    private void PlayAnimOneShot(string stateName)
    {
        if (animator == null || string.IsNullOrEmpty(stateName)) return;
        isOneShotPlaying = true;
        currentAnimState = stateName;
        animator.Play(stateName, animProfile.layer, 0f);
    }

    // ─── Animator State Driver ────────────────────────────────────────────────

    private void UpdateAnimator()
    {
        if (animator == null) return;
        bool moving = agent.velocity.sqrMagnitude > 0.04f;

        switch (currentState)
        {
            case EnemyState.Patrol:
                PlayAnim(moving ? animProfile.walk : animProfile.idle);
                break;

            case EnemyState.Alert:
                // One-shot fired in OnEnterState — nothing to loop here
                break;

            case EnemyState.Chase:
                PlayAnim(animProfile.run);
                break;

            case EnemyState.Combat:
                if (combatType == CombatType.Shooting)
                {
                    // Automatic weapon sustains a looping fire animation while firing
                    if (weaponType == WeaponType.Automatic && isAutoFiring)
                        PlayAnim(animProfile.fireContinuous);
                    else
                        PlayAnim(moving ? animProfile.walk : animProfile.aimIdle);
                }
                else
                {
                    PlayAnim(moving ? animProfile.run : animProfile.meleeIdle);
                }
                break;

            case EnemyState.Search:
                PlayAnim(moving ? animProfile.walk : animProfile.idle);
                break;

            // Dead: handled by Die()
        }
    }

    // ─── State Management ─────────────────────────────────────────────────────

    public void ChangeState(EnemyState next)
    {
        if (currentState == EnemyState.Dead || currentState == next) return;
        OnExitState(currentState);
        currentState = next;
        OnEnterState(currentState);
    }

    private void OnEnterState(EnemyState state)
    {
        switch (state)
        {
            case EnemyState.Patrol:
                agent.speed = patrolSpeed;
                isWaiting   = false;
                if (waypoints != null && waypoints.Length > 0)
                    agent.SetDestination(waypoints[currentWaypoint].position);
                break;

            case EnemyState.Alert:
                agent.speed = 0f;
                PlaySound(alertSound);
                PlayAnimOneShot(animProfile.alert);
                break;

            case EnemyState.Chase:
                agent.speed = chaseSpeed;
                if (hasLastKnownPos) agent.SetDestination(lastKnownPos);
                break;

            case EnemyState.Combat:
                agent.speed = combatSpeed;
                isShooting  = false;
                break;

            case EnemyState.Search:
                searchTimer = searchWaitTime;
                if (hasLastKnownPos) agent.SetDestination(lastKnownPos);
                break;
        }
    }

    private void OnExitState(EnemyState state)
    {
        if (state == EnemyState.Combat)
        {
            isShooting   = false;
            isAutoFiring = false;
        }
    }

    // ─── Death ────────────────────────────────────────────────────────────────

    public void Die()
    {
        if (currentState == EnemyState.Dead) return;
        currentState = EnemyState.Dead;
        agent.enabled   = false;
        targetAimWeight = 0f;
        isAutoFiring    = false;
        if (aimRig != null) aimRig.weight = 0f;
        StopAllCoroutines();
        PlayAnimOneShot(animProfile.die);
        PlaySound(deathSound);
    }

    // ─── Helpers ──────────────────────────────────────────────────────────────

    private void FaceTarget()
    {
        if (target == null) return;
        Vector3 dir = target.position - transform.position;
        dir.y = 0f;
        if (dir.sqrMagnitude > 0.001f)
            transform.rotation = Quaternion.Slerp(
                transform.rotation, Quaternion.LookRotation(dir), Time.deltaTime * 8f);
    }

    private void PlayRandomSound(AudioClip[] clips)
    {
        if (audioSource == null || clips == null || clips.Length == 0) return;
        audioSource.PlayOneShot(clips[Random.Range(0, clips.Length)]);
    }

    private void PlaySound(AudioClip clip)
    {
        if (audioSource == null || clip == null) return;
        audioSource.PlayOneShot(clip);
    }

    // ─── Gizmos ───────────────────────────────────────────────────────────────

#if UNITY_EDITOR
    private void OnDrawGizmosSelected()
    {
        Transform eye = eyeTransform != null ? eyeTransform : transform;

        // Vision sphere + cone arcs
        Gizmos.color = Color.yellow;
        Gizmos.DrawWireSphere(eye.position, visionRange);
        DrawVisionArc(eye, eye.forward, horizontalFOV, Vector3.up,  new Color(1f, 1f, 0f, 0.4f));
        DrawVisionArc(eye, eye.forward, verticalFOV,   eye.right,   new Color(0f, 1f, 1f, 0.4f));

        // Patrol waypoints
        if (waypoints != null && waypoints.Length > 1)
        {
            Gizmos.color = Color.cyan;
            for (int i = 0; i < waypoints.Length; i++)
            {
                if (waypoints[i] == null) continue;
                Gizmos.DrawSphere(waypoints[i].position, 0.25f);
                Gizmos.DrawLine(waypoints[i].position,
                                waypoints[(i + 1) % waypoints.Length].position);
            }
        }

        // Combat range (outer)
        Gizmos.color = new Color(1f, 0.3f, 0.3f, 0.25f);
        Gizmos.DrawWireSphere(transform.position, combatRange);

        // Melee attack range (inner red ring)
        if (combatType == CombatType.Melee)
        {
            Gizmos.color = new Color(1f, 0f, 0f, 0.5f);
            Gizmos.DrawWireSphere(transform.position, melee.attackRange);
        }

        // Chase / lose range
        Gizmos.color = new Color(1f, 0.6f, 0f, 0.15f);
        Gizmos.DrawWireSphere(transform.position, chaseRange);

        // Last known position
        if (Application.isPlaying && hasLastKnownPos)
        {
            Gizmos.color = Color.magenta;
            Gizmos.DrawSphere(lastKnownPos, 0.3f);
            Gizmos.DrawLine(transform.position, lastKnownPos);
        }
    }

    private void DrawVisionArc(Transform origin, Vector3 fwd, float half, Vector3 axis, Color col)
    {
        Gizmos.color = col;
        const int steps = 20;
        Vector3 prev = origin.position + Quaternion.AngleAxis(-half, axis) * fwd * visionRange;
        for (int i = 1; i <= steps; i++)
        {
            float   a    = Mathf.Lerp(-half, half, i / (float)steps);
            Vector3 next = origin.position + Quaternion.AngleAxis(a, axis) * fwd * visionRange;
            Gizmos.DrawLine(prev, next);
            prev = next;
        }
        Gizmos.DrawLine(origin.position,
                        origin.position + Quaternion.AngleAxis(-half, axis) * fwd * visionRange);
        Gizmos.DrawLine(origin.position,
                        origin.position + Quaternion.AngleAxis( half, axis) * fwd * visionRange);
    }
#endif
}

// Any GameObject that can receive damage should implement this interface.
// Add it to the Player, destructible props, other enemies, etc.
public interface IDamageable
{
    void TakeDamage(float amount);
}
