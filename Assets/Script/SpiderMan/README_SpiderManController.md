# Spider-Man Web Controller — Setup Guide

Inspired by Battleglide (Meta Quest). Full pendulum-physics web-swinging for Meta Quest via OpenXR.

---

## Scripts Overview

| Script | Component Menu | Attach To |
|---|---|---|
| `WebShooter.cs` | SpiderMan / Web Shooter | Left Controller + Right Controller |
| `SwingPhysics.cs` | SpiderMan / Swing Physics | XR Origin (XR Rig) |
| `ObjectPullGrab.cs` | SpiderMan / Object Pull Grab | Left Controller + Right Controller |
| `SelfPropulsion.cs` | SpiderMan / Self Propulsion | XR Origin (XR Rig) |

---

## Controller Input Mapping

| Button | Hand | Action |
|---|---|---|
| **Trigger** (index finger) | Left | Shoot left web |
| **Trigger** (index finger) | Right | Shoot right web |
| **Grip** (middle finger) | Left | Pull / grab objects |
| **Grip** (middle finger) | Right | Pull / grab objects |
| **X button** | Left | Push self away from anchor |
| **A button** | Right | Push self away from anchor |

---

## Step-by-Step Setup

### 1 — Open BasicScene

Open `Assets/Scenes/BasicScene.unity`.

---

### 2 — Create Hand Tip Transforms

For each controller, create an empty child GameObject that marks the hand tip
(this is where the web strand originates):

1. Expand **XR Origin (XR Rig)** → **Left Controller** in the Hierarchy.
2. Right-click **Left Controller** → **Create Empty** → rename it `WebOrigin_Left`.
3. Set its **Local Position** to approximately `(0, 0, 0.05)` (5 cm forward = tip of controller).
4. Repeat for **Right Controller** → name it `WebOrigin_Right`.

---

### 3 — Add WebShooter to Each Controller

#### Left Controller

1. Select **Left Controller** in the Hierarchy.
2. In the Inspector click **Add Component** → search **Web Shooter**.
3. Set the fields:

| Field | Value |
|---|---|
| Is Left Hand | **☑ checked** |
| Trigger Threshold | 0.7 |
| Max Web Distance | 40 |
| Web Attach Layers | Everything (default) |
| Web Origin Point | Drag **WebOrigin_Left** here |
| Web Color | `(224, 242, 255)` — light blue |
| Web Sag | 0.04 |

#### Right Controller

Repeat with **Is Left Hand** = **☐ unchecked** and **Web Origin Point** = `WebOrigin_Right`.

---

### 4 — Add SwingPhysics to XR Origin

1. Select **XR Origin (XR Rig)**.
2. Add Component → **Swing Physics**.
3. Set the fields:

| Field | Value |
|---|---|
| Gravity | 12 |
| Max Speed | 18 |
| Air Drag | 0.012 |
| Ground Radius | 0.15 |
| Ground Layers | Everything (default) |
| Left Shooter | Drag **Left Controller's WebShooter** component here |
| Right Shooter | Drag **Right Controller's WebShooter** component here |

---

### 5 — Add ObjectPullGrab to Each Controller

#### Left Controller

Add Component → **Object Pull Grab**:

| Field | Value |
|---|---|
| Is Left Hand | **☑ checked** |
| Grip Threshold | 0.7 |
| Pull Range | 20 |
| Pull Speed | 14 |
| Pull Layers | Everything (default) |
| Snap Distance | 0.4 |
| Hand Anchor | Drag **WebOrigin_Left** (or a dedicated palm transform) |

#### Right Controller

Same, with **Is Left Hand** = **☐ unchecked** and **Hand Anchor** = `WebOrigin_Right`.

---

### 6 — Add SelfPropulsion to XR Origin

1. Select **XR Origin (XR Rig)**.
2. Add Component → **Self Propulsion**.
3. Set the fields:

| Field | Value |
|---|---|
| Push Force | 10 |
| Cooldown | 0.6 |
| Upward Bias | 0.25 |
| Left Shooter | Left Controller's **WebShooter** |
| Right Shooter | Right Controller's **WebShooter** |
| Swing Physics | XR Origin's **SwingPhysics** |

---

### 7 — Tag Ground / Building Objects

For objects you want the web to attach to, ensure they:
- Have a **Collider** component (MeshCollider, BoxCollider, etc.).
- Are on a layer included in the **Web Attach Layers** mask on WebShooter.

The default mask is **Everything**, so all colliders are valid targets out of the box.

For objects you want to be **pullable**:
- Add a **Rigidbody** component.
- Add a **Collider**.
- Ensure they are on a layer in **Pull Layers** on ObjectPullGrab.

---

## Final Hierarchy (after setup)

```
XR Origin (XR Rig)
  ├─ [SwingPhysics]
  ├─ [SelfPropulsion]
  ├─ Camera Offset
  │   └─ Main Camera
  ├─ Left Controller
  │   ├─ [WebShooter]  isLeftHand = true
  │   ├─ [ObjectPullGrab]  isLeftHand = true
  │   └─ WebOrigin_Left    (empty transform, hand tip)
  │       └─ WebLine        (LineRenderer — created at runtime)
  └─ Right Controller
      ├─ [WebShooter]  isLeftHand = false
      ├─ [ObjectPullGrab]  isLeftHand = false
      └─ WebOrigin_Right
          └─ WebLine        (LineRenderer — created at runtime)
```

---

## How Physics Works

### Web Swing (pendulum)

When the trigger is held:
1. A raycast fires from the controller tip along the controller's forward axis.
2. On hit, the world-space **anchor point** is stored and the **constraint length** is captured
   (distance from XR Origin to anchor at the moment of attachment).
3. Every frame:
   - Gravity accelerates a `Velocity` vector downward.
   - If the player has drifted beyond the constraint length the position is snapped
     back to the constraint sphere and the rope-extending velocity component is removed.
   - The remaining tangential velocity produces natural pendulum arcs.
4. With **both hands webbed** simultaneously the two constraints combine, letting the
   player swing around building corners or arc between structures.

### Object Pull / Grab

- Grip pressed → raycast from controller forward up to `Pull Range`.
- Found Rigidbody → its `linearVelocity` is driven toward the hand each frame at `Pull Speed`.
- Within `Snap Distance` → object becomes kinematic and is parented to the hand anchor.
- Grip released → object un-parents, Rigidbody re-enabled, velocity set to tracked hand velocity (throw).

### Push Self

- Primary button (A / X) → calculates a direction **away** from each active anchor, averages them,
  adds an upward bias, and calls `SwingPhysics.AddImpulse()`.
- With no web active the push is straight up (jump boost).
- A `cooldown` prevents spam.

---

## Tuning Reference

| Parameter | Lower value | Higher value |
|---|---|---|
| `gravity` | floaty, slow swings | snappy, fast drops |
| `maxSpeed` | gentle arc | fast superhero launch |
| `airDrag` | long momentum carry | quick deceleration |
| `pushForce` | subtle nudge | rocket launch |
| `upwardBias` | horizontal push | vertical jump feel |
| `webSag` | taut wire look | loose rope look |
| `pullSpeed` | slow magnetism | instant snap |

---

## Known Limitations & Future Enhancements

- **No mid-air coasting** — releasing all webs immediately zeros velocity.
  To add this, track velocity independently of swinging and apply gravity-only
  mode when airborne without a web.
- **No collision detection during swing** — very fast swings may clip through thin geometry.
  Add a `CharacterController` or per-frame sphere-cast to prevent tunnelling.
- **Web visual** — currently a simple 3-point LineRenderer. For a multi-strand web look,
  add 5–8 points and wave them with a sine function on `webSag`.
- **Haptic feedback** — call `UnityEngine.XR.InputDevice.SendHapticImpulse()` on web attach
  for a satisfying wrist vibration.
