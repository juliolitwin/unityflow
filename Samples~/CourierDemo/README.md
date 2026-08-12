# Courier — a small demo game, and a tour of every verb

A complete little top-down game built out of Unity primitives, plus six flows that drive it. It
exists so you can watch UnityFlow work on something that behaves like a real game — a form with a
gate, a player moved by held keys, a drag-to-reorder inventory, a pause screen, a results screen —
without installing anything or importing an art pack.

Nothing here is a mock. The parcels are picked up by the physics trigger the game ships with, the
Play button is disabled by the game's own validation, and the drag is a real uGUI drag with real
`IBeginDragHandler` / `IDropHandler` callbacks. If a flow passes, the game worked.

```
Runtime/   the game — 15 MonoBehaviours, one per file
Editor/    CourierSceneBuilder — the menu item that generates Courier.unity
Flows/     six flows, numbered so the list reads as a tour
Courier.unity
```

---

## Run it

The flows all enter play mode, which reloads the domain, so they are launched with **`flow.start`**
and polled with `flow.status` — `flow.run` cannot survive a reload and refuses such a flow up front.

```sh
cd path/to/your/UnityProject

unity command flow.start  --file "Assets/Samples/UnityFlow/0.1.0/Courier Demo/Flows/01-menu-form.flow.yaml" --runId courier-01
unity command flow.status --runId courier-01
```

Every flow declares the scene as a parameter, defaulting to where Package Manager puts this sample.
Point it somewhere else without editing the file:

```sh
unity command flow.start --file <flow> --env '["scene=Assets/MyScenes/Courier.unity"]'
```

| flow | what it demonstrates | expected |
|---|---|---|
| `01-menu-form` | `inputText`, `tapOn`, `assertText`, `assertVisible` — Play is dead until the field is filled | pass |
| `02-keyboard-menu` | `press`, `navigateTo`, `submit` — the whole menu with no pointer at all | pass |
| `03-play-and-deliver` | `enterPlayMode`, a project `[FlowCommand]`, held keys, `waitUntil` on component fields | pass |
| `04-inventory-drag` | `drag` to reorder two slots, and assertions that survive the gesture | pass |
| `05-pause-resume` | `assertNotVisible` and `stableFor` — negative assertions worth something | pass |
| `06-fail-on-purpose` | what a FAILURE looks like | **fails, deliberately** |

`06` is not broken. It asserts something that is false so you can read the report UnityFlow produces
when an expectation is wrong, screenshot and UI snapshot included.

---

## The game

Top-down. WASD or the arrow keys move the courier. Walk over a parcel to pick it up — two at a
time — and walk into the green depot to hand them over. Red cubes cost a hit. `Tab` opens the cargo
panel, where chips can be dragged between slots. `Escape` pauses. The run ends on the clock, on
zero health, or when the street is empty.

Everything sits on a 20 × 20 plane. The two parcels the flows use are on the **centre lane** between
the start mark and the depot, so "hold W" is a whole route — two pickups and a delivery — with no
steering, which is what makes `03` and `04` reproducible.

```
                    z = +10
        +---------------------------+
        |   D E P O T   (z 4..10)   |
        |                           |
        |   [hazard]     [hazard]   |  z = 1
        |                           |
        |          (B)         (C)  |  z = -1 / -3
        |          (A)              |  z = -4
        |           @               |  z = -8   start
        +---------------------------+
                    z = -10
```

Movement runs in `FixedUpdate` through `Rigidbody.MovePosition`, so `press: { key: w, duration: 2.4s }`
buys a **distance** (2.4s × 6 m/s = 14.4 m) rather than a frame-rate lottery, and the courier is
clamped inside the depot's far edge so even a long hold ends somewhere a flow can assert on.

---

## What a flow can read without writing C#

Every one of these is a plain public property on a component in the scene. Confirm them live with
`unity command flow.probe --component ScoreKeeper`.

| component | field | type | means |
|---|---|---|---|
| `CourierGame` | `phase` | `CourierPhase` | `Menu`, `Playing`, `Paused`, `Results` |
| `CourierGame` | `difficulty` | `CourierDifficulty` | `Relaxed`, `Normal`, `Rush` |
| `CourierGame` | `courierName` | `string` | what the menu form collected |
| `GameClock` | `remaining` | `float` | seconds left |
| `GameClock` | `running` | `bool` | false while paused — the assertable half of "the game is paused" |
| `GameClock` | `elapsed` | `float` | seconds spent |
| `ScoreKeeper` | `delivered` | `int` | parcels handed over |
| `ScoreKeeper` | `score` | `int` | points, difficulty multiplier applied |
| `PlayerHealth` | `current` / `max` | `int` | hits left, hits at full |
| `CourierInventory` | `count` / `capacity` | `int` | parcels carried, and the ceiling |
| `CourierInventory` | `firstLabel` | `string` | label in slot 0 — this is what a drag-reorder changes |
| `CourierInventory` | `version` | `int` | bumped on every change |
| `CourierPlayer` | `inZone` | `bool` | standing in the depot |
| `ParcelField` | `available` | `int` | parcels still in the street |

```yaml
- assert:    { component: ScoreKeeper, field: delivered, gte: 1 }
- waitUntil: { component: CourierInventory, field: count, eq: 0, timeout: 10s }
- assert:    { component: GameClock, field: running, is: false, stableFor: 1s }
```

`PlayerHealth`, `CourierInventory` and `CourierPlayer` all live on the `Courier` capsule, and
`CourierGame`, `GameClock` and `ScoreKeeper` all live on `Game`. There is exactly one of each, so no
query in these flows needs a `find:` — add one only when your own scene has several.

### Selectors

Names are stable and are used for everything except three nodes:

| testId | why it is not selected by name or text |
|---|---|
| `courier.menu.play` | the label reads "PLAY", which is user-facing copy a localiser will change |
| `courier.slot.0` | two identical frames whose only distinguishing text is the cargo they happen to hold |
| `courier.slot.1` | " |

### The one `[FlowCommand]`

```csharp
[FlowCommand("setTimer", Description = "Set the round countdown to N seconds ...")]
public void SetTimer(float seconds)
```

An **instance** method on `GameClock`, which is what lets a flow write `- setTimer: 30` without
naming an object: `this` is the clock the step acts on, and the runner finds the single `GameClock`
in the scene. Two clocks and the same step would be an ambiguity you settle with `on:` — nothing
would be picked for you. It shows up in `unity command flow.commands` beside the built-in verbs.

---

## Rebuilding the scene

**Window ▸ UnityFlow ▸ Samples ▸ Rebuild Courier Demo Scene** regenerates `Courier.unity` from
`Editor/CourierSceneBuilder.cs`. The scene is code on purpose: every position, colour and wiring
decision is readable and reviewable there, which a diff in a `.unity` file is not. It builds into an
additive scene and closes it again, so it never touches whatever you had open.

Two things that cost time when this was built, both now load-bearing:

* **One MonoBehaviour per file.** Unity gives the class matching the file name the script asset's
  main id and hands every other class in the same file a derived one. Such a component serializes
  into a scene and its script does **not** resolve when the scene loads at run time — measured here
  as a `DeliveryZone` that was present in the Inspector and null to `GetComponent` in play mode.
* **A focused uGUI `InputField` is a keyboard trap.** `InputField.OnUpdateSelected` ends in an
  unconditional `eventData.Use()`, so the input module never reaches `ProcessNavigation` and no
  arrow key can ever leave the field. `MenuScreen` handles `Tab` to move down the form for exactly
  that reason, and `02` is the flow that proves it — which is what `navigateTo` is for.
