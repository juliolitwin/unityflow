# Example flows

Three flows, smallest first. They assume a scene with a Canvas, a Button named `OpenButton` whose
label reads "Open", and a panel GameObject named `SettingsPanel` that the button activates. Rename
the selectors, or add `FlowTestId` components, to point them at your own UI.

Copy these into a `Flows/` folder at your project root and run them from there:

```sh
unityflow run Flows/01-tap.flow.yaml --budget 120s
unityflow run Flows/02-play-mode.flow.yaml --start --budget 180s
unityflow run Flows/03-parameterised.flow.yaml --start --env panel=SettingsPanel
```

`02` and `03` enter play mode themselves, which reloads the domain — hence `--start`. `01` assumes
the editor is already in play mode.
