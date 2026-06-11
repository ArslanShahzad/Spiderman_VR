# Enemy AI System

Complete enemy AI for the Spiderman VR project.  
Covers state machine, vision, shooting, melee, health, ragdoll, and animation rigging.

---

## File Overview

| File | Purpose |
|------|---------|
| `EnemyAI.cs` | Main AI — state machine, vision, combat, patrol |
| `EnemyHealth.cs` | Health, hit flash, ragdoll on death |
| `Editor/EnemyAISetup.cs` | One-click setup tool (Editor-only) |

---

## Quick Start

1. Drag a **Survivalist prefab** into your scene.
2. Select its root GameObject.
3. Run **Tools → Enemy AI → Setup as Shooting Enemy** or **Setup as Melee Enemy**.
4. Check the **Console** — a full checklist is printed.
5. Follow the manual steps in the checklist (Animator Controller, NavMesh bake, Player tag).
6. Press **Play**.

---

## State Machine

```
         ┌──────────────────────────────────┐
         │              PATROL              │◄─────────────────────────┐
         │  Walks waypoints / random nav    │                          │
         └─────────────┬────────────────────┘                          │
                       │ spots target                                   │
                       ▼                                                │ search timer expires
         ┌──────────────────────────────────┐                          │
         │              ALERT               │                          │
         │  Pauses, plays alert sound       │                          │
         └────┬─────────────────────────────┘                          │
              │ in range?        │ out of range?                        │
              ▼                  ▼                                      │
  ┌─────────────────┐   ┌──────────────────┐    loses sight   ┌────────┴──────────┐
  │     COMBAT      │◄──│      CHASE       │─────────────────►│      SEARCH       │
  │ Shoot or punch  │   │ Follow to last   │                   │ Move to last pos  │
  │                 │───► known position  │◄──── spots again ─┤ Wait, then return │
  └─────────────────┘   └──────────────────┘                   └───────────────────┘
         │
         │ health = 0
         ▼
  ┌─────────────────┐
  │      DEAD       │
  │ Ragdoll + sound │
  └─────────────────┘
```

---

## Combat Types

### Shooting (`CombatType.Shooting`)

- Enemy uses a **gun** that must be a child of the skeleton (typically parented to `hand_r`).
- Assign the gun root to **EnemyAI → Gun → Gun Root**.
- Fine-tune placement with **Position Offset** and **Rotation Offset** — these are additive on
  top of the authored local transform, so the authored position acts as a baseline.
- Three firing modes driven by **Weapon Type**:

| WeaponType | Behaviour | Animation state used |
|------------|-----------|----------------------|
| SingleShot | One shot per `fireRate` interval | `animProfile.shoot` (one-shot) |
| Automatic  | Continuous fire at `fireRate` while in range | `animProfile.fireContinuous` (loop) |
| Burst      | 3 shots (`burstInterval` apart), then `burstCooldown` | `animProfile.shoot` per shot (one-shot) |

- A **Line Renderer** shows the bullet trace for `trailDuration` seconds.
- **Animation Rigging** (`MultiAimConstraint` on `spine_02`) tilts the upper body up/down toward the
  target chest, controlled by the **Aim Rig** weight which blends in/out per state.

### Melee (`CombatType.Melee`)

- **Gun root is automatically hidden** (`SetActive(false)`) on Awake.
- The enemy closes to within `melee.attackRange` (default 1.8 m) before striking.
- Set **Combat Range** to match approximately how close you want the enemy before it enters
  the Combat state (recommended: 2.5 m for melee, 8 m for shooting).
- **Combo system**: `MeleeCombo` int cycles 0 → `comboCount-1` on each attack.
  Wire different animation states in the Animator to respond to each value.
- **Damage delay**: `melee.damageDelay` seconds after the trigger fires before damage is applied,
  matching the animation wind-up.  Validates distance on impact to avoid phantom hits.
- Animation Rigging is disabled for melee enemies (weight stays at 0).

---

## Animation State System

The enemy uses **direct state-name calls** — no Animator parameters, blend trees, or
triggers. `EnemyAI` calls `animator.Play(stateName)` / `animator.CrossFade(stateName, …)`
to switch states at exactly the right moment.

### How the two playback methods work

| Method | Behaviour |
|--------|-----------|
| `PlayAnim(name)` | Smooth CrossFade into a **looping** state. Skipped if already playing. Blocked while a one-shot is still running. |
| `PlayAnimOneShot(name)` | Always restarts from frame 0, locks `PlayAnim` until the clip's `normalizedTime` reaches 1.0. |

### EnemyAnimationProfile — state name fields

Configure the **Animation Profile** section in the EnemyAI Inspector.
Each field holds the **exact state name** as it appears in your Animator Controller.

| Field | Kind | Default | When it plays |
|-------|------|---------|---------------|
| `idle` | Looping | `Idle` | Patrol / Search, standing still |
| `walk` | Looping | `Walk` | Patrol / Search / Combat (Shooting, moving) |
| `run` | Looping | `Run` | Chase / Combat (Melee, closing distance) |
| `aimIdle` | Looping | `AimIdle` | Combat (Shooting), in range, not firing |
| `shoot` | One-shot | `Shoot` | Each SingleShot or Burst round |
| `fireContinuous` | Looping | `FireContinuous` | Automatic weapon while trigger held |
| `reload` | One-shot | `Reload` | Call `PlayAnimOneShot(animProfile.reload)` manually |
| `meleeIdle` | Looping | `MeleeIdle` | Combat (Melee), at attack range, waiting |
| `meleeAttacks[]` | One-shot | `MeleeAttack_0`, `MeleeAttack_1` | Cycles per combo hit |
| `alert` | One-shot | `Alert` | Fires once when entering Alert state |
| `die` | One-shot | `Die` | On death |
| `layer` | — | `0` | Animator layer index for all state calls |
| `crossfadeDuration` | — | `0.1` | Blend time in seconds for `PlayAnim` |

---

## Animator Controller Setup

Create an Animator Controller and assign it to the Survivalist's **Animator** component.

**No parameters needed.** The code never sets floats, bools, or triggers — it drives
everything via direct state transitions.

**Required states (names must match the Animation Profile fields above):**
```
Idle            — loop
Walk            — loop
Run             — loop
AimIdle         — loop
Shoot           — no loop  (clip plays once; freeze on last frame)
FireContinuous  — loop
MeleeIdle       — loop
MeleeAttack_0   — no loop
MeleeAttack_1   — no loop
Alert           — no loop
Die             — no loop
```

- **No transitions required.** Code drives every state change directly.
- **One-shot clips must have Loop Time unchecked** so `normalizedTime` reaches 1.0 and
  the one-shot lock releases correctly.
- To add more melee combos: add states `MeleeAttack_2`, `MeleeAttack_3`, … in the
  Animator Controller and add those names to `animProfile.meleeAttacks[]` in the Inspector.
- To use different weapon animations per enemy type (pistol vs rifle): just change the
  state name in the Inspector — e.g. `shoot = "Shoot_Pistol"` on one prefab,
  `shoot = "Shoot_Rifle"` on another. The Animator Controller must have a state with
  that exact name.

---

## Patrol

| Mode | Description |
|------|-------------|
| **Waypoints** | Loops through the `waypoints` Transform array in order. Waits `waypointWaitTime` seconds at each point. |
| **Random** | Samples a random point on the NavMesh within `randomPatrolRadius`, moves there, waits, repeats. |

In the Inspector assign patrol points as empty GameObjects placed around your level.  
Waypoints are visible as cyan spheres + lines in the Scene view when the enemy is selected.

---

## Vision System

The vision cone runs in a coroutine every `visionCheckInterval` seconds (default 0.2 s) to avoid
a per-frame raycast.

**Two-stage check:**
1. **Horizontal FOV** (`horizontalFOV`) — left/right angle from the eye's forward on the XZ plane.
2. **Vertical FOV** (`verticalFOV`) — up/down pitch angle.
3. **Line-of-sight raycast** against `obstacleMask | targetMask`.

**Scene View Gizmos (when selected):**
- Yellow sphere: vision range
- Yellow arc: horizontal FOV
- Cyan arc: vertical FOV
- Red ring: combat range  |  smaller red ring: melee attack range (Melee only)
- Orange ring: chase range
- Magenta sphere + line: last known target position (Play mode only)

---

## Health & Ragdoll (`EnemyHealth.cs`)

| Feature | Detail |
|---------|--------|
| `TakeDamage(float amount)` | Subtract HP, flash renderers, die if ≤ 0 |
| `TakeDamage(float amount, Vector3 dir, Vector3 point)` | Same but stores hit direction for directional ragdoll force |
| `Heal(float amount)` | Restore HP up to `maxHealth` |
| `ActivateRagdoll()` | Force ragdoll without dealing damage (e.g. explosion knockback) |
| **Hit Flash** | Briefly tints all `hitRenderers` to `hitFlashColor` using `MaterialPropertyBlock` |
| **Ragdoll** | On death: disables Animator + main collider, enables all bone Rigidbodies/Colliders, applies impulse to the closest bone to the hit point |
| **Events** | `OnDeath` and `OnHit` UnityEvents — wire in Inspector (e.g. spawn particles, play audio) |

**Ragdoll Requirements:**  
Add a `Rigidbody` + `Collider` to each significant bone (pelvis, spine, arms, legs, head)
via Unity's built-in **Ragdoll Wizard** (GameObject → 3D Object → Ragdoll), then assign
`ragdollRoot` to the `pelvis` bone. The setup tool does this automatically if the bone is found.

---

## IDamageable Interface

Defined at the bottom of `EnemyAI.cs`:

```csharp
public interface IDamageable
{
    void TakeDamage(float amount);
}
```

Add this interface to any GameObject that should receive damage from enemies:

```csharp
// Example: Player health
public class PlayerHealth : MonoBehaviour, IDamageable
{
    public float hp = 100f;
    public void TakeDamage(float amount) { hp -= amount; }
}
```

Enemy bullets and melee hits both use `GetComponentInParent<IDamageable>()`, so the component
can sit anywhere in the target's hierarchy.

---

## Layer Masks

Two masks must be set in the **EnemyAI Inspector** for the enemy to work correctly in your scene:

| Field | What to include |
|-------|----------------|
| `Obstacle Mask` | Everything that blocks vision (walls, props, Default layer) |
| `Target Mask` | The Player's layer (e.g. "Player" layer) |
| `Bullet Hit Mask` | Everything bullets should register hits on (Default, Player, etc.) |

---

## Gun Configuration Reference

```
EnemyAI
└── Gun
    ├── Gun Root          ← assign the gun GameObject here
    ├── Position Offset   ← Vector3 added to local pos on top of authored pos
    └── Rotation Offset   ← Euler angles added to local rot on top of authored rot
```

The offsets let you adjust gun alignment in the Inspector at runtime without
permanently changing the gun prefab's transform.  Setting offsets back to `(0,0,0)`
restores the authored position.

---

## Changing Combat Type at Design Time

Set **Combat Type** in the EnemyAI Inspector:
- `Shooting` → gun shown, aim rig active, bullet logic runs
- `Melee` → gun hidden, aim rig inactive, melee logic runs

If you change the type after running the setup tool, call **Tools → Enemy AI → Setup as [type] Enemy**
again on the same object to re-wire the components.

---

## Tips & Gotchas

- **NavMesh must be baked** before entering Play mode. Window → AI → Navigation → Bake.
- **Animation Rigging**: After setup, click **Update Animation Rigging** in the RigBuilder
  component to rebuild the rig job chain before pressing Play.
- **Ragdoll and Animator conflict**: The enemy disables its Animator on death so the physics
  ragdoll takes over. If you want a blend-out instead, replace `animator.enabled = false` in
  `EnemyHealth.Die()` with an Animator layer weight fade.
- **Melee hit range**: `melee.attackRange` is the inner strike radius. Set `combatRange`
  slightly larger (e.g. 2.5 vs 1.8) so the enemy has a small approach window before attacking.
- **Multiple enemies**: Each enemy is fully self-contained. Spawn as many instances as needed;
  they share no static state.
- **Friendly fire**: The `IDamageable` system is generic. If enemies should not damage each other,
  keep them on a different layer and exclude that layer from `bulletHitMask`.
