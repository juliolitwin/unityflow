using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem.UI;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace UnityFlow.Samples.Courier.Editor
{
    /// <summary>
    /// Builds <c>Courier.unity</c> from scratch.
    ///
    /// The scene is CODE, not hand-written YAML: every position, colour and wiring decision is
    /// readable here and reproducible by anyone who imported the sample. It is also the only
    /// reviewable way to change a scene — a diff in a .unity file is not something a reader can
    /// check.
    ///
    /// It builds into an ADDITIVE scene and closes it again, so running the menu item never touches
    /// whatever the editor already had open.
    /// </summary>
    public static class CourierSceneBuilder
    {
        private const string SceneFileName = "Courier.unity";

        // Arena palette: one family of desaturated slates for the world, three saturated accents that
        // each mean exactly one thing - cargo, danger, courier.
        private static readonly Color GroundSlate = Rgb(0x262E3B);
        private static readonly Color DepotGreen = Rgb(0x2FA477);
        private static readonly Color CargoCyan = Rgb(0x4FC3D9);
        private static readonly Color DangerRed = Rgb(0xE05263);
        private static readonly Color CourierAmber = Rgb(0xF2B441);

        // UI palette. The accent is the SAME amber as the courier capsule, so the button you press
        // and the thing you drive read as one game.
        private static readonly Color Ink = Rgb(0xE7ECF3);
        private static readonly Color MutedInk = Rgb(0x8C97A8);
        private static readonly Color Backdrop = Rgba(0x12161E, 0.94f);
        private static readonly Color CardFace = Rgb(0x19202B);
        private static readonly Color SlotFace = Rgb(0x222A38);
        private static readonly Color Accent = Rgb(0xF2B441);
        private static readonly Color AccentInk = Rgb(0x191104);
        private static readonly Color Secondary = Rgb(0x2B3442);

        private static Font s_Font;

        [MenuItem("Window/UnityFlow/Samples/Rebuild Courier Demo Scene")]
        public static void Rebuild()
        {
            var scenePath = Path.Combine(SampleRoot(), SceneFileName).Replace('\\', '/');

            var previous = SceneManager.GetActiveScene();
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Additive);
            SceneManager.SetActiveScene(scene);

            try
            {
                s_Font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
                Build();

                if (!EditorSceneManager.SaveScene(scene, scenePath))
                    throw new InvalidOperationException($"Unity refused to save the scene to '{scenePath}'.");
            }
            finally
            {
                s_Font = null;

                if (previous.IsValid())
                    SceneManager.SetActiveScene(previous);

                EditorSceneManager.CloseScene(scene, true);
            }

            AssetDatabase.ImportAsset(scenePath, ImportAssetOptions.ForceUpdate);
            Debug.Log($"Courier demo scene rebuilt at {scenePath}");
        }

        private static void Build()
        {
            // Flat ambient and no skybox: the camera's solid colour IS the background, which is what
            // keeps the palette identical wherever the sample is imported.
            RenderSettings.skybox = null;
            RenderSettings.ambientMode = AmbientMode.Flat;
            RenderSettings.ambientLight = Rgb(0x2B3242);
            RenderSettings.fog = false;

            BuildCamera();
            BuildSun();

            var game = new GameObject("Game").AddComponent<CourierGame>();
            var clock = game.gameObject.AddComponent<GameClock>();
            var score = game.gameObject.AddComponent<ScoreKeeper>();

            var arena = new GameObject("Arena").transform;
            Primitive(PrimitiveType.Plane, "Ground", arena, Vector3.zero, new Vector3(2f, 1f, 2f), GroundSlate);
            BuildDepot(arena);
            var parcels = BuildParcels(arena);
            BuildHazards(arena);

            var player = BuildCourier(arena);
            var health = player.GetComponent<PlayerHealth>();
            var cargo = player.GetComponent<CourierInventory>();
            Wire(player, ("m_Game", game), ("m_Cargo", cargo), ("m_Health", health), ("m_Score", score));

            var canvas = BuildCanvas();
            var hud = BuildHud(canvas, clock, score, health, cargo);
            var inventory = BuildInventory(canvas, cargo);
            var menu = BuildMenu(canvas, game);
            var pause = BuildPause(canvas, game);
            var results = BuildResults(canvas, game, score, clock);

            Wire(game,
                ("m_MenuPanel", menu),
                ("m_HudPanel", hud),
                ("m_PausePanel", pause),
                ("m_ResultsPanel", results),
                ("m_InventoryPanel", inventory),
                ("m_Clock", clock),
                ("m_Score", score),
                ("m_Health", health),
                ("m_Cargo", cargo),
                ("m_Player", player),
                ("m_Parcels", parcels));

            // The scene is authored ON the menu. CourierGame.Start restates it, so a scene saved
            // mid-run could never open half-open.
            hud.SetActive(false);
            inventory.gameObject.SetActive(false);
            pause.SetActive(false);
            results.SetActive(false);

            BuildEventSystem();
        }

        // ---- Arena -------------------------------------------------------------------------

        private static void BuildCamera()
        {
            var camera = new GameObject("Main Camera", typeof(Camera), typeof(AudioListener)).GetComponent<Camera>();
            camera.tag = "MainCamera";
            // Framed so the bottom of the screen falls on the start mark and the depot sits just
            // under the top edge: the whole route is on screen at once, with no dead ground.
            camera.transform.SetPositionAndRotation(new Vector3(0f, 18f, -14f), Quaternion.Euler(50f, 0f, 0f));
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = Rgb(0x0E1117);
            camera.fieldOfView = 45f;
            camera.nearClipPlane = 0.3f;
            camera.farClipPlane = 120f;
        }

        private static void BuildSun()
        {
            var light = new GameObject("Sun", typeof(Light)).GetComponent<Light>();
            light.transform.rotation = Quaternion.Euler(52f, -35f, 0f);
            light.type = LightType.Directional;
            light.color = Rgb(0xFFF5E8);
            light.intensity = 1.15f;
            light.shadows = LightShadows.Soft;
        }

        private static void BuildDepot(Transform arena)
        {
            // A flat quad rather than a stretched cube: the drop-off is a painted area, and a box the
            // courier visually stands inside would read as a wall.
            var depot = Primitive(PrimitiveType.Quad, "DeliveryZone", arena,
                new Vector3(0f, 0.02f, 7f), new Vector3(8f, 6f, 1f), DepotGreen);
            depot.transform.localRotation = Quaternion.Euler(90f, 0f, 0f);

            UnityEngine.Object.DestroyImmediate(depot.GetComponent<MeshCollider>());

            var trigger = depot.AddComponent<BoxCollider>();
            trigger.isTrigger = true;

            // Local Z is world UP once the quad is laid flat, so the depth has to go THERE for the
            // trigger to be tall enough to contain the capsule.
            trigger.size = new Vector3(1f, 1f, 3f);

            depot.AddComponent<DeliveryZone>();
        }

        private static ParcelField BuildParcels(Transform arena)
        {
            var field = new GameObject("Parcels").AddComponent<ParcelField>();
            field.transform.SetParent(arena, false);

            // A and B sit on the centre lane between the start mark and the depot, so "hold W" is a
            // whole route: two pickups and a delivery, no steering. C is off-lane, for a human.
            AddParcel(field.transform, "Parcel A", new Vector3(0f, 0.45f, -4f), "A", 120);
            AddParcel(field.transform, "Parcel B", new Vector3(0f, 0.45f, -1f), "B", 80);
            AddParcel(field.transform, "Parcel C", new Vector3(6f, 0.45f, -3f), "C", 150);

            return field;
        }

        private static void AddParcel(Transform parent, string name, Vector3 position, string label, int value)
        {
            var parcel = Primitive(PrimitiveType.Cube, name, parent, position, new Vector3(0.9f, 0.9f, 0.9f), CargoCyan);

            var trigger = parcel.GetComponent<BoxCollider>();
            trigger.isTrigger = true;

            // Wider than the cube it draws: a pickup that needs pixel-accurate steering is a pickup
            // no flow can reproduce.
            trigger.size = new Vector3(1.6f, 3f, 1.6f);

            Wire(parcel.AddComponent<Parcel>(), ("m_Label", label), ("m_Value", value));
        }

        private static void BuildHazards(Transform arena)
        {
            var hazards = new GameObject("Hazards").transform;
            hazards.SetParent(arena, false);

            AddHazard(hazards, "Hazard West", new Vector3(-3.5f, 0.5f, 1f));
            AddHazard(hazards, "Hazard East", new Vector3(3.5f, 0.5f, 1f));
        }

        private static void AddHazard(Transform parent, string name, Vector3 position)
        {
            var hazard = Primitive(PrimitiveType.Cube, name, parent, position, new Vector3(1.4f, 1f, 1.4f), DangerRed);

            var trigger = hazard.GetComponent<BoxCollider>();
            trigger.isTrigger = true;
            trigger.size = new Vector3(1f, 3f, 1f);

            hazard.AddComponent<Hazard>();
        }

        private static CourierPlayer BuildCourier(Transform arena)
        {
            var courier = Primitive(PrimitiveType.Capsule, "Courier", arena,
                new Vector3(0f, 0.9f, -8f), new Vector3(0.9f, 0.9f, 0.9f), CourierAmber);

            var body = courier.AddComponent<Rigidbody>();
            body.isKinematic = true;
            body.useGravity = false;

            courier.AddComponent<PlayerHealth>();
            courier.AddComponent<CourierInventory>();
            return courier.AddComponent<CourierPlayer>();
        }

        // ---- UI ----------------------------------------------------------------------------

        private static Canvas BuildCanvas()
        {
            var canvas = new GameObject("Canvas", typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster))
                .GetComponent<Canvas>();

            canvas.renderMode = RenderMode.ScreenSpaceOverlay;

            var scaler = canvas.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);
            scaler.screenMatchMode = CanvasScaler.ScreenMatchMode.MatchWidthOrHeight;
            scaler.matchWidthOrHeight = 0.5f;

            return canvas;
        }

        private static void BuildEventSystem()
        {
            // The module is added with NO actions asset on purpose: InputSystemUIInputModule.OnEnable
            // assigns the built-in defaults when it has none, and those live in a runtime-created
            // object that cannot be serialized into a scene.
            new GameObject("EventSystem", typeof(EventSystem), typeof(InputSystemUIInputModule));
        }

        private static GameObject BuildHud(Canvas canvas, GameClock clock, ScoreKeeper score, PlayerHealth health, CourierInventory cargo)
        {
            var hud = Layer("HudPanel", canvas.transform);

            var time = Corner(hud, "TimeLabel", new Vector2(0f, 1f), new Vector2(36f, -32f), new Vector2(360f, 40f), 30, Ink, TextAnchor.UpperLeft);
            var points = Corner(hud, "ScoreLabel", new Vector2(0f, 1f), new Vector2(36f, -78f), new Vector2(360f, 34f), 24, Accent, TextAnchor.UpperLeft);
            var hits = Corner(hud, "HealthLabel", new Vector2(0f, 1f), new Vector2(36f, -116f), new Vector2(360f, 34f), 24, Ink, TextAnchor.UpperLeft);
            var carry = Corner(hud, "CarryLabel", new Vector2(0f, 0f), new Vector2(36f, 36f), new Vector2(620f, 34f), 22, MutedInk, TextAnchor.LowerLeft);

            Wire(hud.AddComponent<HudView>(),
                ("m_Clock", clock), ("m_Score", score), ("m_Health", health), ("m_Cargo", cargo),
                ("m_TimeLabel", time), ("m_ScoreLabel", points), ("m_HealthLabel", hits), ("m_CarryLabel", carry));

            return hud;
        }

        private static InventoryPanel BuildInventory(Canvas canvas, CourierInventory cargo)
        {
            var panel = new GameObject("InventoryPanel", typeof(Image));
            var rect = Attach(panel, canvas.transform);
            rect.anchorMin = new Vector2(0.5f, 0f);
            rect.anchorMax = new Vector2(0.5f, 0f);
            rect.pivot = new Vector2(0.5f, 0f);
            rect.anchoredPosition = new Vector2(0f, 110f);
            rect.sizeDelta = new Vector2(340f, 200f);
            panel.GetComponent<Image>().color = CardFace;

            Stacked(rect, "InventoryTitle", 16f, 300f, 28f, "CARGO", 20, MutedInk, TextAnchor.MiddleCenter);

            // The drag layer holds no Graphic, so it blocks no raycast; it is made the LAST child so a
            // chip lifted out of its slot draws above both slots.
            var dragLayer = new GameObject("DragLayer").AddComponent<RectTransform>();
            dragLayer.SetParent(rect, false);
            Stretch(dragLayer);

            var slots = new UnityEngine.Object[2];
            slots[0] = BuildSlot(rect, dragLayer, cargo, 0, -70f);
            slots[1] = BuildSlot(rect, dragLayer, cargo, 1, 70f);
            dragLayer.SetAsLastSibling();

            var inventory = panel.AddComponent<InventoryPanel>();
            Wire(inventory, ("m_Cargo", cargo), ("m_Slots", slots));
            return inventory;
        }

        private static InventorySlot BuildSlot(RectTransform panel, RectTransform dragLayer, CourierInventory cargo, int index, float x)
        {
            var slot = new GameObject("Slot" + index, typeof(Image));
            var rect = Attach(slot, panel);
            rect.anchorMin = new Vector2(0.5f, 1f);
            rect.anchorMax = new Vector2(0.5f, 1f);
            rect.pivot = new Vector2(0.5f, 1f);
            rect.anchoredPosition = new Vector2(x, -56f);
            rect.sizeDelta = new Vector2(120f, 120f);
            slot.GetComponent<Image>().color = SlotFace;

            var chip = new GameObject("Chip", typeof(Image));
            var chipRect = Attach(chip, rect);
            chipRect.anchorMin = new Vector2(0.5f, 0.5f);
            chipRect.anchorMax = new Vector2(0.5f, 0.5f);
            chipRect.pivot = new Vector2(0.5f, 0.5f);
            chipRect.anchoredPosition = Vector2.zero;
            chipRect.sizeDelta = new Vector2(96f, 96f);
            chip.GetComponent<Image>().color = CargoCyan;

            var label = NewText("ChipLabel", chipRect, string.Empty, 44, AccentInk, TextAnchor.MiddleCenter);
            Stretch(label.rectTransform);

            // Two identical frames whose only distinguishing text is the cargo they happen to hold:
            // exactly the case a stable id exists for.
            TestId(slot, "courier.slot." + index);

            var component = slot.AddComponent<InventorySlot>();
            Wire(component,
                ("m_Cargo", cargo),
                ("m_Index", index),
                ("m_Chip", chipRect),
                ("m_ChipImage", chip.GetComponent<Image>()),
                ("m_ChipLabel", label),
                ("m_DragLayer", dragLayer));

            return component;
        }

        private static GameObject BuildMenu(Canvas canvas, CourierGame game)
        {
            var panel = Panel("MenuPanel", canvas.transform, Backdrop);
            var card = CenteredCard(panel, "MenuCard", 560f, 690f);

            Stacked(card, "MenuTitle", 34f, 480f, 58f, "COURIER", 46, Ink, TextAnchor.MiddleCenter);
            Stacked(card, "MenuTagline", 98f, 480f, 30f, "Two parcels at a time. Mind the clock.", 18, MutedInk, TextAnchor.MiddleCenter);

            Stacked(card, "NameCaption", 158f, 460f, 26f, "COURIER NAME", 16, MutedInk, TextAnchor.LowerLeft);
            var nameField = TextField(card, "CourierNameField", 188f, 460f, 52f, "your name here");

            Stacked(card, "DifficultyCaption", 262f, 460f, 26f, "DIFFICULTY", 16, MutedInk, TextAnchor.LowerLeft);
            var difficulty = DifficultyDropdown(card, 292f, 460f, 52f);

            var sound = SoundToggle(card, 368f, 460f, 34f);

            Stacked(card, "VolumeCaption", 428f, 460f, 26f, "VOLUME", 16, MutedInk, TextAnchor.LowerLeft);
            var volume = VolumeSlider(card, 460f, 460f, 26f);

            var summary = Stacked(card, "MenuSummary", 506f, 500f, 30f, string.Empty, 17, Accent, TextAnchor.MiddleCenter);

            var play = PrimaryButton(card, "PlayButton", 556f, 460f, 56f, "PLAY");
            var quit = SecondaryButton(card, "QuitButton", 624f, 460f, 46f, "QUIT");

            // The label is user-facing copy a localiser will change; the id is not.
            TestId(play.gameObject, "courier.menu.play");

            Wire(panel.AddComponent<MenuScreen>(),
                ("m_Game", game),
                ("m_NameField", nameField),
                ("m_Difficulty", difficulty),
                ("m_Sound", sound),
                ("m_Volume", volume),
                ("m_Play", play),
                ("m_Quit", quit),
                ("m_Summary", summary));

            return panel;
        }

        private static GameObject BuildPause(Canvas canvas, CourierGame game)
        {
            var panel = Panel("PausePanel", canvas.transform, Rgba(0x080B10, 0.78f));
            var card = CenteredCard(panel, "PauseCard", 470f, 300f);

            Stacked(card, "PauseTitle", 38f, 400f, 52f, "PAUSED", 38, Ink, TextAnchor.MiddleCenter);
            Stacked(card, "PauseHint", 100f, 400f, 28f, "The clock is stopped. Escape resumes.", 17, MutedInk, TextAnchor.MiddleCenter);

            var resume = PrimaryButton(card, "ResumeButton", 150f, 380f, 56f, "RESUME");
            var menu = SecondaryButton(card, "PauseMenuButton", 218f, 380f, 48f, "BACK TO MENU");

            Wire(panel.AddComponent<PauseScreen>(), ("m_Game", game), ("m_Resume", resume), ("m_Menu", menu));
            return panel;
        }

        private static GameObject BuildResults(Canvas canvas, CourierGame game, ScoreKeeper score, GameClock clock)
        {
            var panel = Panel("ResultsPanel", canvas.transform, Backdrop);
            var card = CenteredCard(panel, "ResultsCard", 680f, 350f);

            var headline = Stacked(card, "ResultsHeadline", 42f, 620f, 56f, "SHIFT OVER", 38, Ink, TextAnchor.MiddleCenter);
            var summary = Stacked(card, "ResultsSummary", 110f, 620f, 34f, string.Empty, 21, Accent, TextAnchor.MiddleCenter);

            var again = PrimaryButton(card, "PlayAgainButton", 186f, 460f, 56f, "PLAY AGAIN");
            var menu = SecondaryButton(card, "ResultsMenuButton", 254f, 460f, 48f, "BACK TO MENU");

            Wire(panel.AddComponent<ResultsScreen>(),
                ("m_Game", game), ("m_Score", score), ("m_Clock", clock),
                ("m_Headline", headline), ("m_Summary", summary),
                ("m_PlayAgain", again), ("m_Menu", menu));

            return panel;
        }

        // ---- uGUI construction helpers -----------------------------------------------------

        private static DefaultControls.Resources UiResources() => new DefaultControls.Resources
        {
            standard = Builtin("UI/Skin/UISprite.psd"),
            background = Builtin("UI/Skin/Background.psd"),
            inputField = Builtin("UI/Skin/InputFieldBackground.psd"),
            knob = Builtin("UI/Skin/Knob.psd"),
            checkmark = Builtin("UI/Skin/Checkmark.psd"),
            dropdown = Builtin("UI/Skin/DropdownArrow.psd"),
            mask = Builtin("UI/Skin/UIMask.psd")
        };

        private static Sprite Builtin(string path) => AssetDatabase.GetBuiltinExtraResource<Sprite>(path);

        /// <summary>A full-screen rect with no pixels of its own: a layout root, not a surface.</summary>
        private static GameObject Layer(string name, Transform parent)
        {
            var layer = new GameObject(name, typeof(RectTransform));
            Stretch(Attach(layer, parent));
            return layer;
        }

        /// <summary>A full-screen surface that also blocks the arena behind it.</summary>
        private static GameObject Panel(string name, Transform parent, Color color)
        {
            var panel = new GameObject(name, typeof(Image));
            Stretch(Attach(panel, parent));
            panel.GetComponent<Image>().color = color;
            return panel;
        }

        private static RectTransform CenteredCard(GameObject panel, string name, float width, float height)
        {
            var card = new GameObject(name, typeof(Image));
            var rect = Attach(card, panel.transform);
            rect.anchorMin = new Vector2(0.5f, 0.5f);
            rect.anchorMax = new Vector2(0.5f, 0.5f);
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.anchoredPosition = Vector2.zero;
            rect.sizeDelta = new Vector2(width, height);
            card.GetComponent<Image>().color = CardFace;
            return rect;
        }

        /// <summary>
        /// Place a child by its distance from the card's TOP edge.
        ///
        /// Every control on every screen is positioned this way, which is what makes the vertical
        /// rhythm consistent by construction instead of by eye.
        /// </summary>
        private static RectTransform Row(RectTransform card, GameObject child, float top, float width, float height)
        {
            var rect = Attach(child, card);
            rect.anchorMin = new Vector2(0.5f, 1f);
            rect.anchorMax = new Vector2(0.5f, 1f);
            rect.pivot = new Vector2(0.5f, 1f);
            rect.anchoredPosition = new Vector2(0f, -top);
            rect.sizeDelta = new Vector2(width, height);
            return rect;
        }

        private static Text Stacked(RectTransform card, string name, float top, float width, float height, string content, int size, Color color, TextAnchor anchor)
        {
            var text = NewText(name, null, content, size, color, anchor);
            Row(card, text.gameObject, top, width, height);
            return text;
        }

        private static Text Corner(GameObject panel, string name, Vector2 anchor, Vector2 offset, Vector2 size, int fontSize, Color color, TextAnchor align)
        {
            var text = NewText(name, panel.transform, string.Empty, fontSize, color, align);
            var rect = text.rectTransform;
            rect.anchorMin = anchor;
            rect.anchorMax = anchor;
            rect.pivot = anchor;
            rect.anchoredPosition = offset;
            rect.sizeDelta = size;
            return text;
        }

        private static InputField TextField(RectTransform card, string name, float top, float width, float height, string placeholder)
        {
            var control = DefaultControls.CreateInputField(UiResources());
            control.name = name;
            Row(card, control, top, width, height);

            var field = control.GetComponent<InputField>();
            field.characterLimit = 14;
            field.image.color = SlotFace;

            Restyle(control.transform.Find("Text (Legacy)").GetComponent<Text>(), 22, Ink, TextAnchor.MiddleLeft);

            var hint = control.transform.Find("Placeholder").GetComponent<Text>();
            Restyle(hint, 22, MutedInk, TextAnchor.MiddleLeft);
            hint.text = placeholder;

            return field;
        }

        private static Dropdown DifficultyDropdown(RectTransform card, float top, float width, float height)
        {
            var control = DefaultControls.CreateDropdown(UiResources());
            control.name = "DifficultyDropdown";
            Row(card, control, top, width, height);

            var dropdown = control.GetComponent<Dropdown>();
            dropdown.image.color = SlotFace;
            dropdown.ClearOptions();
            dropdown.AddOptions(new List<string>
            {
                CourierDifficulty.Relaxed.ToString(),
                CourierDifficulty.Normal.ToString(),
                CourierDifficulty.Rush.ToString()
            });
            dropdown.value = (int)CourierDifficulty.Normal;
            dropdown.RefreshShownValue();

            Restyle(control.transform.Find("Label").GetComponent<Text>(), 22, Ink, TextAnchor.MiddleLeft);
            Restyle(control.transform.Find("Template/Viewport/Content/Item/Item Label").GetComponent<Text>(), 20, Ink, TextAnchor.MiddleLeft);
            control.transform.Find("Template").GetComponent<Image>().color = CardFace;
            control.transform.Find("Template/Viewport/Content/Item/Item Background").GetComponent<Image>().color = SlotFace;
            control.transform.Find("Template/Viewport/Content/Item/Item Checkmark").GetComponent<Image>().color = Accent;

            return dropdown;
        }

        private static Toggle SoundToggle(RectTransform card, float top, float width, float height)
        {
            var control = DefaultControls.CreateToggle(UiResources());
            control.name = "SoundToggle";
            Row(card, control, top, width, height);

            var toggle = control.GetComponent<Toggle>();
            toggle.isOn = true;

            // DefaultControls sizes the box for its own 20px-tall row; this one is 34, so the box is
            // recentred and the label pushed clear of it rather than left hanging off the top edge.
            var box = control.transform.Find("Background").GetComponent<RectTransform>();
            box.anchoredPosition = new Vector2(14f, -height * 0.5f);
            box.sizeDelta = new Vector2(26f, 26f);
            box.GetComponent<Image>().color = SlotFace;
            control.transform.Find("Background/Checkmark").GetComponent<Image>().color = Accent;

            var label = control.transform.Find("Label").GetComponent<Text>();
            Restyle(label, 20, Ink, TextAnchor.MiddleLeft);
            label.text = "Sound";
            label.rectTransform.offsetMin = new Vector2(40f, 0f);

            return toggle;
        }

        private static Slider VolumeSlider(RectTransform card, float top, float width, float height)
        {
            var control = DefaultControls.CreateSlider(UiResources());
            control.name = "VolumeSlider";
            Row(card, control, top, width, height);

            var slider = control.GetComponent<Slider>();
            slider.minValue = 0f;
            slider.maxValue = 1f;
            slider.value = 0.7f;

            control.transform.Find("Background").GetComponent<Image>().color = SlotFace;
            control.transform.Find("Fill Area/Fill").GetComponent<Image>().color = Accent;
            control.transform.Find("Handle Slide Area/Handle").GetComponent<Image>().color = Ink;

            return slider;
        }

        private static Button PrimaryButton(RectTransform card, string name, float top, float width, float height, string label) =>
            NewButton(card, name, top, width, height, label, Accent, AccentInk);

        private static Button SecondaryButton(RectTransform card, string name, float top, float width, float height, string label) =>
            NewButton(card, name, top, width, height, label, Secondary, Ink);

        private static Button NewButton(RectTransform card, string name, float top, float width, float height, string label, Color face, Color ink)
        {
            var control = DefaultControls.CreateButton(UiResources());
            control.name = name;
            Row(card, control, top, width, height);

            var button = control.GetComponent<Button>();
            button.image.color = face;

            // A ColorBlock MULTIPLIES the image colour, so a dead button needs a grey MULTIPLIER
            // rather than a grey colour - which is what makes a disabled Play button read as dead.
            var colors = button.colors;
            colors.normalColor = Color.white;
            colors.highlightedColor = new Color(1.12f, 1.12f, 1.12f, 1f);
            colors.pressedColor = new Color(0.82f, 0.82f, 0.82f, 1f);
            colors.selectedColor = new Color(1.12f, 1.12f, 1.12f, 1f);
            colors.disabledColor = new Color(0.42f, 0.42f, 0.42f, 0.55f);
            button.colors = colors;

            var text = control.transform.Find("Text (Legacy)").GetComponent<Text>();
            Restyle(text, 22, ink, TextAnchor.MiddleCenter);
            text.text = label;
            text.fontStyle = FontStyle.Bold;

            return button;
        }

        private static Text NewText(string name, Transform parent, string content, int size, Color color, TextAnchor anchor)
        {
            var text = new GameObject(name, typeof(Text)).GetComponent<Text>();

            if (parent != null)
                Attach(text.gameObject, parent);

            text.text = content;
            Restyle(text, size, color, anchor);
            text.raycastTarget = false;
            return text;
        }

        /// <summary>
        /// Font, size, colour, alignment — in one place because DefaultControls deliberately does not
        /// assign a font, and a uGUI InputField with a fontless text component refuses to activate.
        /// </summary>
        private static void Restyle(Text text, int size, Color color, TextAnchor anchor)
        {
            text.font = s_Font;
            text.fontSize = size;
            text.color = color;
            text.alignment = anchor;
            text.fontStyle = FontStyle.Normal;
            text.horizontalOverflow = HorizontalWrapMode.Overflow;
            text.verticalOverflow = VerticalWrapMode.Overflow;
        }

        private static RectTransform Attach(GameObject child, Transform parent)
        {
            var rect = child.GetComponent<RectTransform>();
            if (rect == null)
                rect = child.AddComponent<RectTransform>();

            rect.SetParent(parent, false);
            return rect;
        }

        private static void Stretch(RectTransform rect)
        {
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.offsetMin = Vector2.zero;
            rect.offsetMax = Vector2.zero;
        }

        // ---- Shared plumbing ---------------------------------------------------------------

        private static GameObject Primitive(PrimitiveType type, string name, Transform parent, Vector3 position, Vector3 scale, Color color)
        {
            var primitive = GameObject.CreatePrimitive(type);
            primitive.name = name;
            primitive.transform.SetParent(parent, false);
            primitive.transform.localPosition = position;
            primitive.transform.localScale = scale;

            Wire(primitive.AddComponent<PrimitiveTint>(), ("m_Color", color));
            return primitive;
        }

        private static void TestId(GameObject target, string id) =>
            Wire(target.AddComponent<global::UnityFlow.FlowTestId>(), ("m_TestId", id));

        /// <summary>
        /// Assign private <c>[SerializeField]</c> members from the editor.
        ///
        /// Through SerializedObject rather than by making the fields public: the sample's public
        /// surface is what a FLOW reads, and widening a field so a builder could reach it would put
        /// authoring convenience into the API the sample exists to teach.
        /// </summary>
        private static void Wire(UnityEngine.Object target, params (string Field, object Value)[] bindings)
        {
            var serialized = new SerializedObject(target);

            foreach (var (field, value) in bindings)
            {
                var property = serialized.FindProperty(field);
                if (property == null)
                    throw new InvalidOperationException($"{target.GetType().Name} has no serialized field '{field}'.");

                Assign(property, field, value);
            }

            serialized.ApplyModifiedPropertiesWithoutUndo();
        }

        private static void Assign(SerializedProperty property, string field, object value)
        {
            switch (value)
            {
                case UnityEngine.Object[] references:
                    property.arraySize = references.Length;
                    for (var i = 0; i < references.Length; i++)
                        property.GetArrayElementAtIndex(i).objectReferenceValue = references[i];
                    return;

                case UnityEngine.Object reference:
                    property.objectReferenceValue = reference;
                    return;

                case int number:
                    property.intValue = number;
                    return;

                case float number:
                    property.floatValue = number;
                    return;

                case bool flag:
                    property.boolValue = flag;
                    return;

                case string text:
                    property.stringValue = text;
                    return;

                case Color color:
                    property.colorValue = color;
                    return;

                default:
                    throw new InvalidOperationException(
                        $"'{field}' was given a {value?.GetType().Name ?? "null"}, which this builder cannot serialize.");
            }
        }

        /// <summary>
        /// Locate the sample folder from this script's own asset path, so the menu item writes the
        /// scene next to the flows wherever the sample was imported.
        /// </summary>
        private static string SampleRoot()
        {
            const string FileName = nameof(CourierSceneBuilder) + ".cs";

            foreach (var guid in AssetDatabase.FindAssets(nameof(CourierSceneBuilder) + " t:MonoScript"))
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                if (!path.EndsWith("/" + FileName, StringComparison.Ordinal))
                    continue;

                // <sample root>/Editor/CourierSceneBuilder.cs
                return Path.GetDirectoryName(Path.GetDirectoryName(path));
            }

            throw new InvalidOperationException($"{FileName} is not in the AssetDatabase, so the sample folder cannot be located.");
        }

        private static Color Rgb(int hex) => Rgba(hex, 1f);

        private static Color Rgba(int hex, float alpha) => new Color(
            ((hex >> 16) & 0xFF) / 255f,
            ((hex >> 8) & 0xFF) / 255f,
            (hex & 0xFF) / 255f,
            alpha);
    }
}
