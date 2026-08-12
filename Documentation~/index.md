# UnityFlow — reference

The [README](../README.md) is the introduction and the honest capability table. This is the
reference: the complete flow language, the verb and selector surface, the run artifacts, the CLI,
and the architecture notes that explain why the pieces are shaped the way they are.

- [Flow file reference](#flow-file-reference)
- [Selectors](#selectors)
- [Built-in verbs](#built-in-verbs)
- [Project commands](#project-commands)
- [Run artifacts](#run-artifacts)
- [Editor commands](#editor-commands)
- [Host CLI](#host-cli)
- [Architecture](#architecture)
- [Troubleshooting](#troubleshooting)

---

## Flow file reference

A flow file is exactly **one** YAML document.

| key | type | meaning |
|---|---|---|
| `name` | string, required | Flow name. Appears in the run header, `status.json` and every report. |
| `requires` | mapping | Preconditions. Only `input:` is defined: `system` (real device injection, mandatory) or `semantic` (synthesized UI events are acceptable). |
| `timeScale` | float | `Time.timeScale` for the duration of the run. |
| `seed` | int | Deterministic seed. |
| `env` | mapping | Variable **defaults**, substituted as `${name}` and overridable per run with `--env '["name=value`."]' See [Variables](#variables). |
| `defs` | anything | Never executed and never validated. It exists so a flow can host YAML anchors it reuses further down. |
| `before` | list of steps | Setup. |
| `steps` | list of steps, required | The flow proper. At least one step. |
| `after` | list of steps | Teardown. **Runs even when the body failed**, so a failed run does not leave persisted state behind for the next one. A failure inside `after` never overwrites the real diagnosis. |

Unknown top-level keys, unknown verbs and unknown arguments are all **parse errors** with a
`file:line:column` gutter and a "did you mean" suggestion. Validation is exhaustive and eager: a
typo fails in milliseconds instead of halfway through a run.

### Step shape

A step is a verb on its own, or exactly one `verb: arguments` pair:

```yaml
steps:
  - screenshot: 01-before          # bare scalar, bound to the verb's single required argument
  - waitFor: { name: SubmitButton }      # mapping
  - tapOn:
      name: SubmitButton
      timeout: 20s                 # modifiers live inside the same mapping
      as: okButton
```

Every verb accepts three **modifiers** alongside its own arguments:

| modifier | meaning |
|---|---|
| `timeout` | Per-step ceiling. Default **7s**. |
| `on` | Selector that says which object a `[FlowCommand]` acts on, when several carry the component. Also the way to select by visible text when a verb's own argument shadows a selector key. |
| `as` | Bind the resolved node to a name. A later step refers to it as `"@name"`. |

A two-key step mapping is reported rather than accepted — modifiers go *inside* the verb's argument
mapping, not beside the verb.

### Argument precedence

When a verb takes an inline selector, its argument mapping holds both the verb's arguments and the
selector's keys. **A key the verb declares always binds to the verb.** That is what makes

```yaml
- inputText: { testId: login.account, text: "demo" }
```

mean "type *demo* into `login.account`" rather than "find the element whose visible text is
*demo*". When a verb shadows a selector key that way, select with `on:`.

### Durations

`500ms`, `5s`, `1.5s`, or a bare number meaning **seconds**. Only lowercase `s` and `ms` are units.
`5m`, `5S`, `5MS` and `5 s` are all rejected rather than guessed at — reading `5M` as milliseconds
would be a silent thousand-fold misreading of the author's intent.

> The host CLI's own `--budget` option is a separate surface and additionally accepts `m` and `h`.
> Inside a flow file, only `s`, `ms` and a bare number are legal.

### Strings and references

A leading `@` in a string means "what an earlier `as:` bound under that name". To write a literal
leading `@`, escape it as `@@`, which is what a flow does for a password like `"@@#secret"`.

`as:` binds two different things and they are not interchangeable. A verb that resolved a UI node
binds the **node**, which only a selector can consume. `runScript` binds its **return value**, which
only an argument can consume — today that is `drag`'s `fromPoint` / `toPoint`:

```yaml
  - runScript:
      as: dropCorner
      code: |
        var grid = /* the live RectTransform the game itself measures against */;
        var point = UnityEngine.RectTransformUtility.WorldToScreenPoint(
            uiCamera, grid.TransformPoint(224f, -392f, 0f));
        var invariant = System.Globalization.CultureInfo.InvariantCulture;
        return point.x.ToString(invariant) + "," + point.y.ToString(invariant);
  - drag: { fromPoint: [965, 391], toPoint: "@dropCorner", duration: 6s, steps: 60 }
```

A literal `[x, y]` is only correct at the Game View size it was measured at; a computed one is
correct at any size, because the rect it came from moved with the view.

The `"x,y"` spelling is not a style choice. The pipeline's evaluator JSON round-trips whatever a
`runScript` returns, and a `Vector2` does not survive it — it comes back as Unity's own `ToString`,
rounded to two decimals and punctuated by the **editor's** culture, which in a pt-BR editor writes
`(875,00, 212,00)`. So a script writes the two numbers itself, in invariant culture. Nothing is
coerced beyond that: any other string, and any other type, is reported with the fix rather than
guessed at.

### Variables

`env:` declares the flow's parameters and their defaults; `${name}` substitutes one anywhere a
string is written.

```yaml
name: into-world
env:
  character: ""            # empty is a real value: "no preference"
  account: demo
steps:
  - inputText: { name: Input, within: { name: AccountField }, text: "${account}" }
```

```sh
unity command flow.start --file Flows/into-world.flow.yaml --env '["character=demo-user"]'
```

| rule | |
|---|---|
| **Where** | Every string scalar in the file except the `env:` block itself: selector fields, verb arguments, step modifiers, screenshot names, and `runScript` bodies. Mapping KEYS are structural and are never substituted. |
| **When** | At **parse time**, after the defaults are merged with the overrides — so a typo fails in milliseconds instead of mid-run. |
| **Undefined** | A **parse error** with `file:line:column`, the closest defined name, and the full list of what IS defined. Never an empty string: a variable that silently resolved to nothing is how a flow types `""` into a login field and reports a pass for a session it never had. |
| **Unknown override** | `--env` naming something the flow does not declare is refused at the `env:` block, with a "did you mean". An override nothing reads is a typo every time. |
| **Escape** | `$${` is a literal `${`, the same doubling that makes `@@` a literal `@`. A lone `$` is never special, so shell-looking text and C# in a `runScript` body pass through untouched. |
| **Types** | Values are always strings here; the substituted text is then converted against the argument's declared type exactly as if it had been typed in place. `timeout: "${wait}"` with `wait: 5s` is a duration, and `wait: soon` fails with `expects a duration ..., got 'soon' (substituted from "${wait}")`. |
| **Quoting** | A substituted scalar is read as UNQUOTED. YAML forces the quotes — `{ timeout: ${wait} }` is unwritable in flow style — and keeping them would make every numeric argument reject its own variable with "remove the quotes". |
| **Defaults** | Literals. A default may not be written in terms of another variable; there is no defined order between entries of a mapping, so it is refused rather than resolved. `$${` still escapes. |
| **Reproducibility** | The EFFECTIVE env (defaults plus overrides, sorted) is written into the `run.start` record and carried in `resume.json`, so a run that crosses a domain reload resumes with the parameters it started with rather than the file's defaults. |

---

## Selectors

| key | matches |
|---|---|
| `testId` | The value of a `FlowTestId` component (or the UI Toolkit equivalent). |
| `text` | Visible text, **exact**. |
| `contains` | Visible text, substring, case-insensitive. |
| `matches` | Visible text, regular expression. |
| `name` | GameObject name (uGUI) or VisualElement name. Exact. |
| `path` | Full hierarchy path, e.g. `/Canvas/Shop/Buy`. |
| `type` | Control type, matched against the node's reported type. An open string, not an enum; subclasses match. |
| `within` | A nested selector. Restricts the search to that node's descendants. |
| `index` | 0-based index among the candidates that survive every other criterion. |
| `backend` | Restrict to one backend id, e.g. `ugui`. |

`index`, `within` and `backend` only narrow an existing candidate set, so a selector made only of
those cannot identify anything and is rejected at parse time.

Resolution priority is **testId → visible text → name → hierarchy path**.

### The 0 / 1 / N rule

| matches | outcome |
|---|---|
| 0 | `NotFound` — **retryable**. The step keeps re-checking every frame until its timeout, then fails with near-miss suggestions. |
| 1 | `Resolved`. |
| N | `Ambiguous` — **fails immediately**, never "take the first", and lists the matches. Retrying cannot help. |

Silently taking the first match is the single largest source of tests that pass by accident: the
flow keeps working while pointing at the wrong node.

### Regular expressions

A `matches:` pattern is compiled **once at parse time**, culture-invariant, with a **250ms match
timeout**. Both details are load-bearing. The retry loop re-evaluates every selector against every
candidate node every frame, so compiling in the loop would allocate on the hot path, and an
unbounded pattern would not merely be slow — `^(a+)+$` against a 29-character string backtracks for
about eleven seconds, and with a serial HTTP server that turns one typo into an editor that never
answers again and cannot be told to stop. Exceeding the ceiling fails the step and names the
pattern.

---

## Built-in verbs

`unity command flow.commands` prints this list for your project, including every `[FlowCommand]`, with
argument types. It is generated from the same vocabulary the parser validates against.

| verb | selector | arguments | notes |
|---|---|---|---|
| `tapOn` | required | `allowUnverifiedOcclusion: bool` | Places a real pointer at the node's projected centre and clicks. Waits for the node to be actionable for **2 consecutive frames** before acting. |
| `inputText` | required | `text: string` (required) | Sets the text of the resolved input control. |
| `press` | none | `key: string` (required, bare scalar), `count: int` (default 1), `duration` | Sends a real key through the injected keyboard. Key names are Input System control names plus the aliases `up`/`down`/`left`/`right`; an unknown name fails the step and lists the closest real names. |
| `navigateTo` | required | `maxSteps: int` (default 40), `from: selector` | Moves the selection to the node with **real arrow keys** through uGUI's navigation graph. Never jumps with `SetSelectedGameObject`. |
| `submit` | optional | — | Sends the UI Submit action (Enter) to the current selection. With a selector it first **asserts** that the selector is what is selected. |
| `cancel` | optional | — | Sends the UI Cancel action (Escape), with the same assertion. |
| `waitFor` | required | — | Waits until the node exists **and is visible**. |
| `waitUntilNotVisible` | required | — | Waits until the node is gone or hidden. |
| `assertVisible` | required | — | Same as `waitFor`, but reads as an assertion in the report. |
| `assertNotVisible` | required | `stableFor: duration` (default 500ms) | Requires the node to **stay** absent for a window. |
| `assertText` | required | `equals` / `contains` / `matches` | At least one is required; several are combined with AND. |
| `assert` | none | a game-state query (below) | Asserts something about the **game**, not the UI. Positive, so it retries until true or the step times out. |
| `waitUntil` | none | the same query, without `stableFor` | Wait for a load or a spawn instead of guessing `wait: 2s`. |
| `screenshot` | none | `name: string` (required, bare scalar) | PNG into the run's `artifacts/`. |
| `wait` | none | `duration` (required, bare scalar) | An unconditional wait. Prefer `waitFor`: a fixed wait is either too short or too slow. |
| `enterPlayMode` | none | `scene: string` (bare scalar) | Enters play mode and waits until it is actually active. **Requires `flow.start`** — this reloads the domain and `flow.run` cannot survive it. |
| `exitPlayMode` | none | — | Leaves play mode, waits for edit mode, and puts back the scene the run replaced. |

### Game-state queries (`assert`, `waitUntil`)

```yaml
- waitUntil: { find: Player, component: PlayerController, field: isGrounded, is: true }
- assert:    { find: Player, component: Health, field: current, gt: 0, stableFor: 500ms }
- assert:    { count: Monster, gte: 3 }
- assert:    { find: /UIRoot/HUD, exists: true }
```

| argument | meaning |
|---|---|
| `find` | GameObject name, or a full hierarchy path when it contains `/`. Matched exactly across every loaded scene, inactive objects and `DontDestroyOnLoad` included. |
| `component` | Component type, simple or full name. An ambiguous simple name **fails with the candidates listed**; nothing is picked for you. |
| `field` | Field or property to read — public or not, instance or static. Exact name. |
| `count` | Count live objects instead of reading one. |
| `expr` | Escape hatch: a C# **expression**, compiled once by Roslyn and cached. This is how you reach statics and any state that does not live on a component (an ECS world, a plain C# service). |
| `is` `eq` `ne` `gt` `gte` `lt` `lte` `contains` `exists` | The comparison. `gt`/`gte`/`lt`/`lte` are numbers only; `contains` is text only. |
| `stableFor` | (`assert` only) how long the comparison must **stay** true after it first holds. Off by default. It matters in a networked client where movement is predicted locally and reconciled by the server, so a value can be right for one frame and reverted on the next. |

Comparison values are deliberately **not** converted at parse time: the type a value must be is the
type of the field, which is unknown until the scene is looked at. `eq: 5` coerced to an int at parse
time would be wrong for an enum, a float or a string field, so the raw value travels to the state
resolver and is coerced against the member it is actually compared with.

`unity command flow.probe --component Health --find Player` lists the members with their current values.
Nobody guesses that health is `m_Current` and not `current`, and a query naming the wrong member
fails in a way that looks exactly like the game being wrong.

### Why `navigateTo` refuses to jump

`EventSystem.SetSelectedGameObject` can put the selection on any element instantly. Using it to
reach the target would make the verb pass on UI that no keyboard and no controller can operate,
which is the exact defect the verb exists to find. So the selection is only ever moved by injecting
an arrow key and re-reading `EventSystem.current.currentSelectedGameObject`; the direction of each
press is chosen by comparing the target's screen rect with the current selection's, and the other
axis is tried when a press moves nothing (an element wired `Navigation.Mode.Horizontal` simply does
not travel vertically).

The one exception is **seeding** the first selection when nothing is selected at all — a navigation
has to start somewhere. That single call is allowed, is refused the moment anything is already
selected, is refused outright when it would land on the target itself, and is written to the
progress stream as a `step.assist` record so no reader can mistake the result for a fully navigated
path. Use `from:` to say where it should start; otherwise the first navigable `Selectable` of the
target's own Canvas is used.

A failure names the trail and the wiring:

```
navigateTo name=char_2 could not reach it from /Canvas/List/char_0 in 40 steps; the navigation path
visited char_0 -> char_1 -> char_0 (a cycle). Selectable.navigation on 'char_1' is:
    up: Automatic -> /Canvas/List/char_0
    down: selectOnDown = None
    left: navigation.mode = Horizontal, which never travels vertically
    right: Automatic -> /Canvas/List/char_2
```

### Why `submit` takes a selector it does not use

`submit: { name: SubmitButton }` does not submit *to* `SubmitButton`. It asserts that `SubmitButton`
is what the EventSystem currently has selected and fails if it is not, then sends Enter to whatever
is selected
— which is now provably the same thing. Submitting to a named element directly would be a second
activation path that bypasses the selection, and it would pass on a screen where the keyboard focus
had silently landed somewhere else.

### Why `tapOn` waits two frames

Almost every popup fades or scales in. Acting on the first frame a node reports actionable can land
a click mid-tween, on a rect that moves out from under the pointer before the release. Two
consecutive agreeing frames cost about 16ms and remove that class of flake entirely.

### Why `assertNotVisible` is different

A positive assertion retries until it becomes true. A negative one that returned as soon as it
looked true would be **vacuous**: `assertNotVisible` immediately after the action that might trigger
a popup passes before the system has even evaluated, and a bug that makes the popup appear 300ms
later still shows green. So it requires the condition to hold for a window, and fails the instant it
is violated.

---

## Project commands

```csharp
[FlowCommand("giveCoins", Description = "Add coins to the player's wallet.")]
public void GiveCoins(int amount) => money += amount;
```

* **Instance or static.** An instance method on a `MonoBehaviour` solves the reference problem by
  construction — `this` is the object the step acts on. The runner finds the component in the loaded
  scenes; `on:` disambiguates when several exist, following the same 0 / 1 / N rule as selectors.
* **Component and GameObject parameters are resolved from the scene**, not declared in YAML. A
  static method can therefore take the objects it needs to operate on.
* **Return `void`, `IEnumerator` or `Task`.** The runner yields on `IEnumerator` and awaits `Task`,
  so a scene load or an animation is awaited properly instead of degrading into a guessed
  `wait: 2s`. A `Task` that outlives the step's timeout fails the step.
* **Discovery is by `TypeCache`**, which Unity maintains as part of its own indexing, so it costs
  nothing and survives a domain reload with no registration handshake.
* **A name collision disables both.** A `[FlowCommand]` that shadows a built-in verb (or another
  command) makes neither available and is reported by `flow.doctor` and `flow.commands`. Shadowing
  would silently change what an existing flow does.

`FlowCommandAttribute` and `FlowTestId` live in the `Runtime` assembly and are the only parts of
UnityFlow that a player build ever sees. Both are dependency-free markers.

---

## Run artifacts

```
<project>/.unityflow/runs/<runId>/
    progress.ndjson    append-only event stream, flushed after every line
    status.json        atomic snapshot, rewritten at every step boundary
    cancel             sentinel file written by the host to request cancellation
    artifacts/         screenshots and failure captures
```

This lives outside `Assets/` on purpose: a `.png` under `Assets/` would trigger a texture import on
every screenshot. It is not under `Temp/` either — artifacts must survive the editor closing, and
the cancel sentinel must be writable by a process that is not Unity. Add `.unityflow/` to
`.gitignore`.

### `progress.ndjson`

One JSON object per line. Every line carries `seq` (monotonic) and `type`.

| type | fields |
|---|---|
| `run.start` | `runId`, `flow`, `path`, `steps`, `nextStep`, `section`, `backends[]`, `env[]`, `playMode` |
| `run.resume` | same fields, written instead of `run.start` by a segment picked up after a domain reload |
| `run.writeMode` | `writeMode`, `occlusion`, `inputDriver` — emitted when input is first needed, not in the header |
| `run.warning` | `message` |
| `step.start` | `section` (`before` / `steps` / `after`), `index`, `verb`, `step`, `line` |
| `step.pass` | `index`, `verb`, `ms` |
| `step.fail` | `index`, `verb`, `ms`, `line`, `summary`, `detail`, `nearMisses[]`, `screenshot` |
| `run.end` | `state`, `seconds`, `failure` |

`detail` is the diagnostic block: the UI tree as it looked at the moment of failure (hidden nodes
included, **with the reason they are hidden**) followed by every warning and error logged since the
step began, with the first useful stack frame. That is what turns a timeout into a diagnosis:
"waitFor timed out" says nothing, while "the object exists at alpha 0.00 **and** a
NullReferenceException was thrown in `AchievementSystem.Evaluate:42`" says exactly where to go.

The per-line flush is what makes tailing work, and it also means a run killed by a domain reload or
an editor crash leaves a complete, readable record up to the moment it died.

### `status.json`

```json
{ "runId": "...", "state": "Failed", "flowName": "...", "flowPath": "...",
  "stepIndex": 0, "stepCount": 7, "step": "waitFor name=LoginPanel",
  "failure": "...", "progressSeq": 3,
  "startedAtUtc": 1786405564.05, "updatedAtUtc": 1786405624.17,
  "occlusion": "CrossSurface", "inputDriver": "inputsystem" }
```

Written to a temp file and moved over the target, so a reader never sees truncated JSON.

States: `Pending`, `Running`, `AwaitingReload`, `Passed`, `Failed`, `Cancelled`, `Errored`.
`Errored` means the run could not happen at all — a parse error, a missing backend, a wedged
environment — as opposed to `Failed`, which means the game did the wrong thing.

`occlusion` and `inputDriver` are recorded on every run so a reader always knows what "passed" was
actually worth.

---

## Editor commands

Invoked through the `unity` CLI, e.g.
`unity command flow.doctor --format json --timeout 60`. Prefer the host CLI, which wraps these.

| command | main thread | purpose |
|---|---|---|
| `flow.run` | yes | Start a run: `file`, `runId`, `budgetMs`, `allowUnverifiedOcclusion`, `env[]`. Returns when the run ends. Refuses a flow that would cross a domain reload. |
| `flow.start` | yes | Same arguments, but returns the run id **immediately** after writing a resume ledger. The only way to run a flow that enters play mode. Poll `flow.status`. |
| `flow.status` | **no** | Read a run's `status.json` by id. Answers while the main thread is busy compiling. |
| `flow.cancel` | **no** | Write the cancel sentinel. |
| `flow.commands` | yes | Every verb, with argument types and source. |
| `flow.snapshot` | yes | The live UI tree: `visibleOnly`, `text`, `max`. |
| `flow.probe` | yes | Live instances of a component with every readable member and its current value: `component`, `find`, `max`, `maxMembers`, `includeUnityBase`. |
| `flow.doctor` | yes | Whether the environment can run flows, and precisely what is missing. |

`flow.run` returns a `Task` from a **non-async** method, and that detail is what makes the design
work — see *Architecture*.

---

## Host CLI

See [`tools/unityflow-cli/README.md`](../../../../../tools/unityflow-cli/README.md) for the full
surface. In short:

```sh
unity command flow.run --file <flow.yaml> [--watch] [--budgetMs 120000 [--run-id X] [--start] [--json]
                          [--env '["name=value"]' ...]
unity command flow.doctor
unity command flow.snapshot [--text X] [--visibleOnly true]
unity command flow.commands
unity command flow.probe [--component X] [--find Y]
unity command flow.status <runId>
unityflow cancel <runId>
```

Exit codes: **0** pass, **1** fail, **2** could-not-run, **130** cancelled.

`--env` is repeatable and sets one variable declared by the flow's `env:` block. It is forwarded to
the editor as a JSON array and validated there, against the flow file, so a bad name is reported with
the flow's own line and column rather than by a second set of rules in the host.

`--start` uses `flow.start` instead of `flow.run` and is required for a flow that enters play mode.
The CLI keeps tailing `progress.ndjson` and reads `status.json` from disk until the run reaches a
terminal state, so it tolerates the editor being absent entirely for the seconds a domain reload
takes.

---

## Architecture

### A flow cannot run inside a `[CliCommand]` handler

Measured, not assumed: a synchronous `MainThreadRequired` handler occupies the main thread for its
whole duration. A probe that busy-waited 1.5 real seconds inside one observed `Time.frameCount` go
from 12 to **12** — zero frames elapsed. Nothing that needs a frame to happen (a UI fading in, a
click being processed, a scene loading) can ever complete there.

So `flow.run` only **registers a frame driver** and returns a pending `Task` in the same tick. The
server awaits that Task on a background thread, leaving the main thread free to keep pumping, and
the flow advances frame by frame while the request is still open.

### Two pump sources

| mode | pump | why |
|---|---|---|
| edit mode | `EditorApplication.update` | There is no player loop. |
| play mode | a `PlayerLoop` hook in `PostLateUpdate` | Correctness, not tidiness. uGUI dispatches pointer events from `InputSystemUIInputModule.Process()`, which runs from `EventSystem.Update()` inside the player loop. Ticking from `EditorApplication.update` in play mode would advance the flow at moments unrelated to input processing, so a press and its release could land in the same player frame and never produce a click. |

The PlayerLoop hook is also why the driver is **not** a `MonoBehaviour`: nothing is added to the
scene, nothing survives into a build, and there is no object for the game to accidentally find.

For the same reason the tap sequence yields between move, press and release. Those yields are
load-bearing, not padding: queueing a press and a release without a frame between them collapses
both into one poll and produces no click at all.

### The retry loop is the product

Every verb is a frame-aligned retry loop. Resolving, checking actionability and retrying next frame
costs nothing — no round trip, no polling interval — so network latency, async loads and animation
timing are absorbed for free. It is the reason a well-written flow never contains `wait: 2s`.

This is also why the interpreter runs **in the editor** rather than being driven step-by-step from
the host: a per-step round trip would make the retry model unaffordable. A `waitFor` polling at
100ms for 5s becomes 50 process spawns and 50 HTTP requests; in here the same wait is 300 frames
that cost nothing.

### The serial server

The pipeline HTTP server answers one request at a time. While `flow.run` is in flight, **no other
command is served at all** — `unity status` reports *unreachable*. Everything downstream follows
from that:

* progress is a file the host tails, not a response it polls;
* cancellation is a sentinel file, not a command;
* `flow.status` and `flow.cancel` are declared `MainThreadRequired = false` and touch only files, so
  they answer during a compile — though not during a run, because the server itself is busy.

### Domain reloads

`EditorSettings.enterPlayModeOptionsEnabled` is **false** in this project and stays false: the
project's statics are not reload-safe. Entering play mode is therefore a full domain reload, which
destroys statics, the HTTP server, the in-flight request and the bearer token.

The frame driver hooks `AssemblyReloadEvents.beforeAssemblyReload` and completes with an explicit
`FlowInterruptedException`, so the caller reports *interrupted by a domain reload* instead of
hanging until its deadline with no explanation. A flow that crosses a reload must be started with
**`flow.start`**, which keeps its state in `UnityEditor.SessionState` — verified to survive a reload
in this editor — and resumes on the other side.

### Capability negotiation, not fallback

Write mode and occlusion fidelity are two independent axes, chosen **once** from what the
environment actually supports, written into the run header and the report, and never varied
mid-run:

| write mode | meaning |
|---|---|
| `DeviceInjection` | A real virtual device is driven. Occlusion is genuine and gameplay input works. |
| `SemanticDispatch` | The UI system's own event sequence is synthesized. Works with no Input System and in edit mode, but proves less: nothing outside the UI system observes the input. |

A flow declaring `requires: { input: system }` fails immediately when device injection is
unavailable, rather than quietly running with the weaker mechanism and reporting a pass that means
less than the reader thinks.

Backends and input drivers are discovered per run via `TypeCache` and constructed fresh, so a run
can never inherit stale state, registration order cannot matter, and a domain reload needs no
re-registration handshake.

---

## Troubleshooting

| symptom | cause | fix |
|---|---|---|
| `unity status` says *unreachable* | A run is in flight and the server is serial. | Expected. Use `unity command flow.status <runId>`, which reads files. |
| `refusing to tap …: occlusion could not be verified` | Edit mode: `EventSystem` is not `[ExecuteAlways]`, so `EventSystem.current` is null and `GraphicRaycaster` returns zero hits. | Enter play mode, or set `allowUnverifiedOcclusion: true` and accept that a tap may pass through a modal. |
| `requires: { input: system }` refused | No usable input driver: not in play mode, or no enabled `EventSystem`. | `unity command flow.doctor` prints the exact chain. |
| `interrupted by a domain reload` | Scripts recompiled, or play mode was entered mid-run. | Use `flow.start` for flows that cross a reload; do not edit C# while a flow runs. |
| Injected input goes nowhere | The Game View is unfocused and the default input behaviour discards events. | The runner's input session already handles this for the run's duration; if it still fails, `doctor` reports which of the three settings did not take. |
| `matched no VISIBLE node, but N hidden node(s) match` | The object exists but is inactive, transparent, masked or off-screen. | The reason is printed per node in the failure's UI snapshot. |
| `N objects have a <Component>; add 'on:' to say which` | An instance `[FlowCommand]` with several candidates. | Add `on: { name: ... }` or `on: { path: ... }`. |
| Step fails with the regex timeout message | A `matches:` pattern backtracks catastrophically and is re-evaluated every frame. | Rewrite the pattern; nested quantifiers such as `(a+)+` are the usual cause. |
