# Third Party Notices

UnityFlow itself is licensed under the PolyForm Perimeter License 1.0.0 (see `LICENSE.md`). The
components below are not, and travel under their own terms. Redistributing UnityFlow means
redistributing these notices with it.

---

## YamlDotNet 18.1.0

Vendored as `Editor/ThirdParty/YamlDotNet.dll` and used to parse `.flow.yaml` files.

Homepage: https://github.com/aaubry/YamlDotNet
License: MIT

```
Copyright (c) Antoine Aubry and contributors

Permission is hereby granted, free of charge, to any person obtaining a copy of
this software and associated documentation files (the "Software"), to deal in
the Software without restriction, including without limitation the rights to
use, copy, modify, merge, publish, distribute, sublicense, and/or sell copies of
the Software, and to permit persons to whom the Software is furnished to do so,
subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS
FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR
COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER
IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN
CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
```

---

## Unity uGUI (`com.unity.ugui`)

Four methods in the uGUI backend are *transcribed* from Unity's own uGUI source rather than
paraphrased. That is deliberate: each one has to agree with the engine exactly, because a backend
that disagrees with the raycaster or with `Selectable` by even one case reports a coordinate or a
blocking reason that is silently wrong. Each site also carries this notice inline.

Copyright (c) 2015-2020 Unity Technologies ApS
License: Unity Companion License — https://unity.com/legal/licenses/unity-companion-license

| Site in UnityFlow | Derived from |
| --- | --- |
| `Editor/Backends/UGui/UGuiRectMath.cs` — `ResolveEventCamera` | `GraphicRaycaster.eventCamera` |
| `Editor/Backends/UGui/UGuiSemanticDispatch.cs` — `SendEnter` | `BaseInputModule.HandlePointerExitAndEnter` |
| `Editor/Backends/UGui/UGuiSemanticDispatch.cs` — `SendUp` | `StandaloneInputModule.ReleaseMouse` |
| `Editor/Backends/UGui/UGuiVisibility.cs` — `FindNonInteractableGroup` | `Selectable.ParentGroupAllowsInteraction` |

The Unity Companion License permits use, reproduction and modification of Unity-provided source in
connection with Unity engine software, and requires that copyright and licence notices be retained.
No Unity source is redistributed here beyond these four transcribed methods.
