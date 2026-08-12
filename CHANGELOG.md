# Changelog

All notable changes to UnityFlow are recorded here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and this package follows
[Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Added

* **`LICENSE.md`** — the package is licensed under the **PolyForm Perimeter License 1.0.0**
  (`LicenseRef-PolyForm-Perimeter-1.0.0`), carrying the notice
  `Required Notice: Copyright (c) 2026 Julio Litwin`. Use it anywhere, including in a commercial
  game; do not build a competing testing product out of it. The host CLI's `package.json` said
  `MIT`, which was simply wrong, and now says the same thing.
* **Unity uGUI entry in `Third Party Notices.md`.** Four sites in the uGUI backend are transcribed
  from `com.unity.ugui` rather than paraphrased — `SendUp`, `SendEnter`, `FindNonInteractableGroup`
  and `ResolveEventCamera` — and the Unity Companion License requires the notice to travel with
  them. Each of the four now carries a three-line notice inline as well.
* **`Samples~/ExampleFlows`** — three runnable example flows, smallest first, which the package
  manifest has advertised for a while without shipping.

* **A drag endpoint can be a COMPUTED coordinate.** `fromPoint` / `toPoint` accept `"@name"`
  alongside the literal `[x, y]`, resolving a value an earlier step bound with `as:`. That is what
  lets a flow aim at geometry it cannot know when it is written: a literal is a measurement of one
  Game View size baked into a file, and nudging the view two pixels makes every literal in the flow
  aim at a different element and fail for a reason that is not the game's. A `runScript` reads the
  live rect and returns the point instead.
  The wire format is `"x,y"` in INVARIANT culture, written out by the script, because the pipeline's
  evaluator JSON round-trips every value a script returns and a `Vector2` does not survive that — it
  comes back as its own `ToString`, rounded to two decimals and punctuated by the editor's culture.
  Nothing else is coerced: a `Vector3`, a stringified `(875.00, 212.00)` or three numbers are each
  refused with the fix named.

* **Flow parameters.** A top-level `env:` mapping declares variable DEFAULTS, and `${name}` is
  substituted into every string scalar of the file — selector fields, verb arguments, step
  modifiers, screenshot names and `runScript` bodies alike. Substitution happens at PARSE time,
  after the defaults are merged with the caller's overrides, so a misspelled `${charater}` fails in
  milliseconds with a `file:line:column`, the closest defined name and the full list of what is
  defined. An undefined variable is never an empty string, and a `--env` naming something the flow
  does not declare is refused rather than ignored.
* **`$${` escapes a literal `${`**, the same doubling that already makes `@@` a literal `@`. A lone
  `$` is never special, so shell-looking text and C# inside a `runScript` body pass through
  untouched.
* **`env` argument on `flow.run` and `flow.start`**, taking repeated `name=value` pairs, mirrored in
  the host CLI as a repeatable `--env name=value`. The EFFECTIVE env — defaults plus overrides,
  sorted — is written into the run's `run.start` header, so a report always shows what the run
  actually used.
* **`FlowResumeState.Env`** carries that effective env across the domain reload. Without it a
  resumed segment would re-parse the flow with its bare defaults and silently run the second half
  of a flow with different parameters than the first.
* **`FlowValue.IsBlock`**, recording that a scalar was written as `|` or `>`. Such a node's YAML
  mark is its indicator and its text starts on the next line, so without it a bad `${variable}`
  inside a thirty-line `runScript` body was reported one line short.

### Changed

* **The package no longer refers to the game it was developed against.** Example account names,
  selector names, class names and flow paths are generic; the measurements that justify a constant
  (the 600ms drag hold, the 50px grid threshold, the 355 assemblies behind the compiler cache) are
  kept exactly as measured, with only the project identity dropped. `flow.probe`'s always-present
  note no longer claims the host project runs a particular ECS — it now says what a component probe
  structurally cannot see, which is true everywhere.
* The into-world example flow no longer hardcodes a character name. `character` is empty by
  default, which means "no preference" and takes the first entry of whatever the account has — so a
  fresh account with one unknown character works with zero configuration. Naming one is a demand:
  the flow fails and lists the names that ARE present rather than falling back to the first entry,
  and an empty character list fails with its own message instead of an index error.
* The login example flow takes `account` and `password` from `env:`, defaulted to a shared test
  account so the file still runs unchanged.

* **Keyboard and controller verbs.** `press`, `navigateTo`, `submit` and `cancel`. All four drive
  the injected keyboard DEVICE, so the key travels through the project's own action bindings
  (`DefaultInputActions` `UI/Navigate`, `UI/Submit`, `UI/Cancel`) exactly as a player's would.
* **`navigateTo` walks uGUI's navigation graph with real arrow keys**, one press per frame,
  re-reading `EventSystem.current.currentSelectedGameObject` after each. It never calls
  `SetSelectedGameObject` to reach the target; seeding the very first selection is the single
  exception and is reported on the progress stream as a `step.assist` record. Failures name the path
  the selection took, detect cycles, and print the `Selectable.navigation` wiring that stopped it —
  which makes the verb an accessibility check as much as a navigation step.
* **`IUiBackend.FocusRing`** — one new member, returning the optional `IFocusRing` facet (read the
  focused element, seed the first focus, describe a navigation link). Nullable, because a UI system
  need not have a focus ring at all.
* **Key-name aliases in the Input System driver.** `up`/`down`/`left`/`right` for the arrow keys,
  `esc`, `return`, `del`, `ins`, `pgup`, `pgdn`, and `digit0..digit9` for the `0..9` controls. An
  unknown name is refused with the closest real control names listed, never silently ignored.
* **`StepContext.Note`** so a step can put a record on the run's progress stream.

## [0.1.0] — 2026-08-10

First working release. A flow drove a real button in a running game client and the click changed
game state.

### Added

* **Flow language.** `name`, `requires`, `timeScale`, `seed`, `defs`, `before` / `steps` / `after`;
  the step modifiers `timeout`, `on` and `as`; selectors by `testId`, `text` / `contains` /
  `matches`, `name`, `path` and `type`, narrowed by `within`, `index` and `backend`.
* **Built-in verbs.** `tapOn`, `inputText`, `waitFor`, `waitUntilNotVisible`, `assertVisible`,
  `assertNotVisible`, `assertText`, `screenshot`, `wait`, `enterPlayMode`, `exitPlayMode`.
* **Declarative game-state queries.** `assert` and `waitUntil` read any field or property by name —
  public or not, instance or static — with `find` / `component` / `field` / `count`, the comparisons
  `is` `eq` `ne` `gt` `gte` `lt` `lte` `contains` `exists`, and `stableFor` for a value a client
  predicts and a server reconciles. `expr:` compiles a C# expression for statics and for state that
  lives off components entirely. Comparison values are coerced against
  the member they are compared with, not at parse time.
* **`[FlowCommand]`** — project methods callable from a flow. Instance and static, returning `void`,
  `IEnumerator` or `Task`. Component and GameObject parameters are resolved from the live scene, so
  an instance method needs no reference from the YAML.
* **`FlowTestId`** — an optional, Inspector-assigned stable id. Pure data: no `Awake`, no `Update`,
  no registry.
* **uGUI backend** — enumeration, visibility with reasons, actionability, text adapters, screen-rect
  projection and hit testing.
* **Input System driver** — real virtual pointer and keyboard injection, with a session that sets
  `AllDeviceInputAlwaysGoesToGameView`, `IgnoreFocus` and `runInBackground` so an unfocused editor
  still receives injected input.
* **Frame driver** — `EditorApplication.update` in edit mode, a `PostLateUpdate` PlayerLoop hook in
  play mode. Not a `MonoBehaviour`: nothing is added to the scene and nothing survives into a build.
* **Run artifacts** — `progress.ndjson` (append-only, flushed per line), `status.json` (written
  atomically at every step boundary), a `cancel` sentinel and an `artifacts/` folder, all under
  `<project>/.unityflow/runs/<runId>/`.
* **Resumable runs** — `flow.start` writes a resume ledger and returns immediately, and the run is
  rebuilt on the far side of a domain reload. This is what makes a flow that enters play mode
  possible at all.
* **CLI surface** — `flow.run`, `flow.start`, `flow.status`, `flow.cancel`, `flow.commands`,
  `flow.snapshot`, `flow.probe`, `flow.doctor`.
* **Host CLI** — `tools/unityflow-cli`, published as `unityflow`. Zero npm dependencies. Tails the
  progress file instead of polling, always passes an explicit `--timeout` of budget + 30s, writes
  the cancel sentinel on Ctrl-C, re-runs on save with `--watch`, drives resumable runs with
  `--start`, and exits 0 / 1 / 2 / 130 for pass / fail / could-not-run / cancelled.
* **Documentation** — `README.md`, `Documentation~/index.md`, `Third Party Notices.md`.

### Known limits

* **The editor is unreachable over HTTP while a run is in flight.** The pipeline server is strictly
  serial. This is why progress is a file and cancellation is a sentinel, and it is not going to
  change from this side.
* **A flow that enters play mode needs `flow.start`, not `flow.run`** (`unityflow run --start`).
  This project keeps `EditorSettings.enterPlayModeOptionsEnabled` **off** on purpose (its statics
  are not reload-safe), so entering play mode is a full domain reload that destroys the HTTP server
  and the in-flight request. `flow.run` refuses such a flow up front.
* **Occlusion is only fully verified in play mode.** Edit mode reports `PerElement` and `tapOn`
  refuses to tap unless `allowUnverifiedOcclusion` is set.
* **uGUI only.** There is no UI Toolkit backend yet; the backend interface exists for one.
* **No pixel diffing and no device farm.** Screenshots are evidence, not assertions.
