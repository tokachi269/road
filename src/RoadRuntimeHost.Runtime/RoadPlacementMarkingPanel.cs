using System;
using System.Collections.Generic;
using System.Reflection;
using ColossalFramework;
using ColossalFramework.UI;
using UnityEngine;

namespace RoadRuntimeHost.Runtime
{
    internal sealed class RoadPlacementMarkingSelection
    {
        public bool RoadsideLines;
        public string LaneSeparatorStyle;
        public string CenterLineStyle;

        public static RoadPlacementMarkingSelection FromStyle(ImtMarkingStyleBundle style)
        {
            return new RoadPlacementMarkingSelection
            {
                RoadsideLines = style == null || style.RoadsideLines,
                LaneSeparatorStyle = NormalizeLaneSeparator(
                    style == null ? null : style.LaneSeparatorStyle),
                CenterLineStyle = NormalizeCenterLine(
                    style == null ? null : style.CenterLineStyle)
            };
        }

        public RoadPlacementMarkingSelection Copy()
        {
            return new RoadPlacementMarkingSelection
            {
                RoadsideLines = RoadsideLines,
                LaneSeparatorStyle = LaneSeparatorStyle,
                CenterLineStyle = CenterLineStyle
            };
        }

        public void ApplyTo(ImtMarkingStyleBundle style)
        {
            if (style == null) throw new ArgumentNullException("style");
            style.RoadsideLines = RoadsideLines;
            style.LaneSeparatorStyle = NormalizeLaneSeparator(LaneSeparatorStyle);
            style.CenterLineStyle = NormalizeCenterLine(CenterLineStyle);
            style.CenterLineYellow = string.Equals(
                style.CenterLineStyle,
                "SOLID_YELLOW",
                StringComparison.Ordinal);
        }

        internal static string NormalizeLaneSeparator(string value)
        {
            return string.Equals(value, "SOLID_WHITE", StringComparison.OrdinalIgnoreCase)
                ? "SOLID_WHITE"
                : "DASHED_WHITE";
        }

        internal static string NormalizeCenterLine(string value)
        {
            if (string.Equals(value, "SOLID_YELLOW", StringComparison.OrdinalIgnoreCase))
                return "SOLID_YELLOW";
            if (string.Equals(value, "SOLID_WHITE", StringComparison.OrdinalIgnoreCase))
                return "SOLID_WHITE";
            return "DASHED_WHITE";
        }
    }

    internal sealed class RoadPlacementMarkingController
    {
        private readonly object _sync = new object();
        private readonly HashSet<NetInfo> _targets = new HashSet<NetInfo>();
        private RoadPlacementMarkingSelection _selection;
        private RoadPlacementMarkingPanel _panel;
        internal RoadPlacementGeometryController Geometry;

        public RoadPlacementMarkingController()
        {
        }

        public void Register(
            string roadId,
            NetInfo info,
            ImtMarkingStyleBundle defaultStyle)
        {
            if (info == null) return;
            lock (_sync)
            {
                _targets.Add(info);
                if (_selection == null)
                    _selection = RoadPlacementMarkingSelection.FromStyle(defaultStyle);
            }
            if (object.ReferenceEquals(_panel, null)) StartPanel();
        }

        public bool TryGet(NetInfo info, out RoadPlacementMarkingSelection selection)
        {
            selection = null;
            if (info == null) return false;
            lock (_sync)
            {
                if (!_targets.Contains(info) || _selection == null) return false;
                selection = _selection.Copy();
                return true;
            }
        }

        public void SetRoadsideLines(NetInfo info, bool enabled)
        {
            lock (_sync)
            {
                if (_selection != null) _selection.RoadsideLines = enabled;
            }
        }

        public void SetLaneSeparatorStyle(NetInfo info, string style)
        {
            lock (_sync)
            {
                if (_selection != null)
                    _selection.LaneSeparatorStyle =
                        RoadPlacementMarkingSelection.NormalizeLaneSeparator(style);
            }
        }

        public void SetCenterLineStyle(NetInfo info, string style)
        {
            lock (_sync)
            {
                if (_selection != null)
                    _selection.CenterLineStyle =
                        RoadPlacementMarkingSelection.NormalizeCenterLine(style);
            }
        }

        public ImtMarkingStyleBundle Capture(
            NetInfo info,
            ImtMarkingStyleBundle defaultStyle)
        {
            ImtMarkingStyleBundle captured = CopyStyle(defaultStyle);
            RoadPlacementMarkingSelection selection;
            if (TryGet(info, out selection)) selection.ApplyTo(captured);
            return captured;
        }

        public void Stop()
        {
            lock (_sync)
            {
                _targets.Clear();
                _selection = null;
            }
            if (!object.ReferenceEquals(_panel, null))
            {
                DestroyPanel(_panel);
                _panel = null;
            }
        }

        private void StartPanel()
        {
            if (!SimulationManager.exists) return;
            try
            {
                UIView view = UIView.GetAView();
                if (view == null) return;
                _panel = view.AddUIComponent(typeof(RoadPlacementMarkingPanel))
                    as RoadPlacementMarkingPanel;
                if (_panel == null)
                    throw new InvalidOperationException("Road placement panel could not be created");
                _panel.Bind(this);
                DiagnosticLog.Info(
                    "SUCCESS",
                    "road_placement_marking_panel_created",
                    "Created the road-tool marking panel for newly placed segments");
            }
            catch (Exception error)
            {
                _panel = null;
                DiagnosticLog.Error(
                    "MOD_COMPATIBILITY",
                    "road_placement_marking_panel_create_failed",
                    "The road-tool marking panel could not be created",
                    error);
            }
        }

        private static void DestroyPanel(RoadPlacementMarkingPanel panel)
        {
            PropertyInfo gameObjectProperty = typeof(Component).GetProperty("gameObject");
            MethodInfo destroy = typeof(UnityEngine.Object).GetMethod(
                "Destroy",
                BindingFlags.Static | BindingFlags.Public,
                null,
                new Type[] { typeof(UnityEngine.Object) },
                null);
            if (gameObjectProperty == null || destroy == null) return;
            object gameObject = gameObjectProperty.GetValue(panel, null);
            destroy.Invoke(null, new object[] { gameObject });
        }

        internal static ImtMarkingStyleBundle CopyStyle(ImtMarkingStyleBundle source)
        {
            if (source == null) source = new ImtMarkingStyleBundle();
            return new ImtMarkingStyleBundle
            {
                WhiteColor = Copy(source.WhiteColor),
                YellowColor = Copy(source.YellowColor),
                CenterLineYellow = source.CenterLineYellow,
                RoadsideLines = source.RoadsideLines,
                LaneSeparatorStyle = source.LaneSeparatorStyle,
                CenterLineStyle = source.CenterLineStyle,
                Texture = source.Texture,
                Cracks = Copy(source.Cracks),
                Voids = Copy(source.Voids),
                CrosswalkWidth = source.CrosswalkWidth,
                CrosswalkDashLength = source.CrosswalkDashLength,
                CrosswalkGapLength = source.CrosswalkGapLength,
                CrosswalkOffset = source.CrosswalkOffset,
                StopLineWidth = source.StopLineWidth,
                LineWidth = source.LineWidth,
                DashLength = source.DashLength,
                DashGap = source.DashGap
            };
        }

        private static float[] Copy(float[] values)
        {
            return values == null ? null : (float[])values.Clone();
        }
    }

    internal sealed class RoadPlacementMarkingPanelWatcher : MonoBehaviour
    {
        private RoadPlacementMarkingPanel _panel;

        public void Bind(RoadPlacementMarkingPanel panel)
        {
            _panel = panel;
        }

        public void Update()
        {
            if (_panel != null) _panel.RefreshSelectedTool();
        }
    }

    internal sealed class RoadPlacementMarkingPanel : UIPanel
    {
        private const float PanelWidth = 430f;
        private const float PanelHeight = 482f;
        private static readonly Color32 SelectedColor = new Color32(92, 146, 72, 255);
        private static readonly Color32 NormalColor = new Color32(75, 85, 96, 255);

        private RoadPlacementMarkingController _controller;
        private NetInfo _currentInfo;
        private NetInfo _lastObservedInfo;
        private UILabel _roadName;
        private UIButton _roadsideOn;
        private UIButton _roadsideOff;
        private UIButton _laneDashed;
        private UIButton _laneSolid;
        private UIButton _centerYellow;
        private UIButton _centerSolid;
        private UIButton _centerDashed;
        private readonly UIButton[] _geometryButtons = new UIButton[5];
        private readonly List<UIComponent> _geometryComponents = new List<UIComponent>();
        private UILabel _geometryStatus;
        private UIButton _heightTransitionButton;
        private UIButton _sectionResetButton;
        private string _geometryError;
        private int _lastGeometryRevision = -1;

        public override void Awake()
        {
            base.Awake();
            name = "RoadRuntimeHostRoadPlacementMarkings";
            atlas = UIView.GetAView().defaultAtlas;
            backgroundSprite = "MenuPanel2";
            color = new Color32(255, 255, 255, 245);
            size = new Vector2(PanelWidth, PanelHeight);
            canFocus = true;

            AddLabel("Road placement", 14f, 12f, 400f, 24f, 1.0f);
            _roadName = AddLabel(string.Empty, 14f, 36f, 400f, 20f, 0.78f);

            AddLabel("Roadside lines", 14f, 68f, 118f, 26f, 0.82f);
            _roadsideOn = AddButton("On", 140f, 66f, 90f, delegate
            {
                if (_currentInfo != null) _controller.SetRoadsideLines(_currentInfo, true);
                RefreshSelection();
            });
            _roadsideOff = AddButton("Off", 236f, 66f, 90f, delegate
            {
                if (_currentInfo != null) _controller.SetRoadsideLines(_currentInfo, false);
                RefreshSelection();
            });

            AddLabel("Lane separators", 14f, 104f, 118f, 26f, 0.82f);
            _laneDashed = AddButton("White dashed", 140f, 102f, 132f, delegate
            {
                if (_currentInfo != null)
                    _controller.SetLaneSeparatorStyle(_currentInfo, "DASHED_WHITE");
                RefreshSelection();
            });
            _laneSolid = AddButton("White solid", 278f, 102f, 132f, delegate
            {
                if (_currentInfo != null)
                    _controller.SetLaneSeparatorStyle(_currentInfo, "SOLID_WHITE");
                RefreshSelection();
            });

            AddLabel("Center line", 14f, 140f, 118f, 26f, 0.82f);
            _centerYellow = AddButton("Yellow solid", 140f, 138f, 88f, delegate
            {
                if (_currentInfo != null)
                    _controller.SetCenterLineStyle(_currentInfo, "SOLID_YELLOW");
                RefreshSelection();
            });
            _centerSolid = AddButton("White solid", 234f, 138f, 82f, delegate
            {
                if (_currentInfo != null)
                    _controller.SetCenterLineStyle(_currentInfo, "SOLID_WHITE");
                RefreshSelection();
            });
            _centerDashed = AddButton("White dashed", 322f, 138f, 88f, delegate
            {
                if (_currentInfo != null)
                    _controller.SetCenterLineStyle(_currentInfo, "DASHED_WHITE");
                RefreshSelection();
            });
            _geometryComponents.Add(AddLabel("Cross-section — next road only", 14f, 180f, 400f, 22f, 0.82f));
            string[] labels = { "Total width", "Traffic divider", "Median", "Roadside width", "Road level" };
            for (int index = 0; index < labels.Length; ++index)
            {
                int dimension = index;
                _geometryComponents.Add(AddLabel(labels[index], 14f, 208f + index * 34f, 118f, 26f, 0.82f));
                _geometryButtons[index] = AddButton(string.Empty, 140f, 206f + index * 34f, 270f,
                    delegate { CycleGeometry(dimension); });
                _geometryComponents.Add(_geometryButtons[index]);
            }
            _geometryStatus = AddLabel(string.Empty, 14f, 380f, 400f, 28f, 0.68f);
            _geometryStatus.wordWrap = true;
            _geometryComponents.Add(_geometryStatus);
            _heightTransitionButton = AddButton("Transition to other road level (once)", 140f, 410f, 270f, delegate
            {
                if (_controller == null || _controller.Geometry == null) return;
                NetInfo resolved; string error;
                if (_controller.Geometry.TryArmHeightTransition(_currentInfo, out resolved, out error)) _currentInfo = resolved;
                _geometryError = error;
                RefreshGeometry();
            });
            _geometryComponents.Add(_heightTransitionButton);
            _sectionResetButton = AddButton("Use Base Road default section", 140f, 444f, 270f, delegate
            {
                if (_controller == null || _controller.Geometry == null) return;
                NetInfo resolved; string error;
                if (_controller.Geometry.HasArmedTransition)
                {
                    PlacementVariantChoice[] choices; string key, roadId;
                    _controller.Geometry.TryGetMenu(_currentInfo, out choices, out key, out roadId);
                    if (_controller.Geometry.TrySelect(_currentInfo, 0, key.Split('|')[0], out resolved, out error)) _currentInfo = resolved;
                }
                else if (_controller.Geometry.TryUseDefault(_currentInfo, out resolved, out error)) _currentInfo = resolved;
                _geometryError = error;
                RefreshGeometry();
            });
            _geometryComponents.Add(_sectionResetButton);
            Hide();
        }

        public void Bind(RoadPlacementMarkingController controller)
        {
            _controller = controller;
            RoadPlacementMarkingPanelWatcher watcher =
                gameObject.AddComponent<RoadPlacementMarkingPanelWatcher>();
            watcher.Bind(this);
        }

        public void RefreshSelectedTool()
        {
            PositionPanel();
            NetInfo selected = null;
            ToolController toolController = ToolsModifierControl.toolController;
            NetTool netTool = toolController == null
                ? null
                : toolController.CurrentTool as NetTool;
            if (netTool != null) selected = netTool.Prefab;

            // Only react once to a newly selected prefab, not every frame after
            // a rejected snapshot. No segment scanning or geometry polling.
            int geometryRevision = _controller == null || _controller.Geometry == null ? -1 : _controller.Geometry.Revision;
            bool menuChanged = geometryRevision != _lastGeometryRevision;
            if ((!ReferenceEquals(_lastObservedInfo, selected) || menuChanged) && _controller != null && _controller.Geometry != null)
            {
                NetInfo resolved; string error;
                if (_controller.Geometry.EnsureSelection(selected, out resolved, out error))
                    selected = resolved;
                _geometryError = error;
            }
            _lastGeometryRevision = geometryRevision;

            RoadPlacementMarkingSelection ignored;
            bool generatedRoad =
                _controller != null && _controller.TryGet(selected, out ignored);
            if (!ReferenceEquals(_lastObservedInfo, selected))
            {
                _lastObservedInfo = selected;
                DiagnosticLog.Info(
                    "DATA",
                    "road_placement_tool_prefab_changed",
                    "Observed a road-tool prefab selection change",
                    "prefab_name", selected == null ? string.Empty : selected.name,
                    "generated_road", generatedRoad ? "true" : "false");
            }

            if (!generatedRoad)
            {
                NetInfo previous = _currentInfo;
                _currentInfo = null;
                if (isVisible)
                {
                    Hide();
                    DiagnosticLog.Info(
                        "SUCCESS",
                        "road_placement_marking_panel_hidden",
                        "Hid the road-tool marking panel because the selected tool is not a generated road",
                        "previous_prefab", previous == null ? string.Empty : previous.name);
                }
                return;
            }

            if (!ReferenceEquals(_currentInfo, selected) || menuChanged)
            {
                _currentInfo = selected;
                _roadName.text = selected.name;
                RefreshSelection();
                RefreshGeometry();
                DiagnosticLog.Info(
                    "SUCCESS",
                    "road_placement_marking_panel_target_selected",
                    "Selected a generated road for placement-time marking controls",
                    "prefab_name", selected.name);
            }
            if (!isVisible)
            {
                Show(true);
                DiagnosticLog.Info(
                    "SUCCESS",
                    "road_placement_marking_panel_shown",
                    "Showed the road-tool marking panel for a generated road",
                    "prefab_name", selected.name);
            }
        }

        private void PositionPanel()
        {
            UIView view = UIView.GetAView();
            if (view == null) return;
            relativePosition = new Vector3(
                Math.Max(8f, view.fixedWidth - PanelWidth - 12f),
                Math.Max(8f, view.fixedHeight - height - 170f));
        }

        private UILabel AddLabel(
            string text,
            float x,
            float y,
            float width,
            float height,
            float scale)
        {
            UILabel label = AddUIComponent<UILabel>();
            label.text = text;
            label.textScale = scale;
            label.textColor = new Color32(235, 235, 235, 255);
            label.autoSize = false;
            label.size = new Vector2(width, height);
            label.relativePosition = new Vector3(x, y);
            label.verticalAlignment = UIVerticalAlignment.Middle;
            return label;
        }

        private UIButton AddButton(
            string text,
            float x,
            float y,
            float width,
            MouseEventHandler handler)
        {
            UIButton button = AddUIComponent<UIButton>();
            button.text = text;
            button.textScale = 0.72f;
            button.textColor = new Color32(240, 240, 240, 255);
            button.hoveredTextColor = Color.white;
            button.pressedTextColor = Color.white;
            button.normalBgSprite = "ButtonMenu";
            button.hoveredBgSprite = "ButtonMenuHovered";
            button.pressedBgSprite = "ButtonMenuPressed";
            button.focusedBgSprite = "ButtonMenu";
            button.size = new Vector2(width, 28f);
            button.relativePosition = new Vector3(x, y);
            button.eventClicked += handler;
            return button;
        }

        private void RefreshSelection()
        {
            RoadPlacementMarkingSelection selection;
            if (_controller == null || !_controller.TryGet(_currentInfo, out selection)) return;
            Select(_roadsideOn, selection.RoadsideLines);
            Select(_roadsideOff, !selection.RoadsideLines);
            Select(_laneDashed, selection.LaneSeparatorStyle == "DASHED_WHITE");
            Select(_laneSolid, selection.LaneSeparatorStyle == "SOLID_WHITE");
            Select(_centerYellow, selection.CenterLineStyle == "SOLID_YELLOW");
            Select(_centerSolid, selection.CenterLineStyle == "SOLID_WHITE");
            Select(_centerDashed, selection.CenterLineStyle == "DASHED_WHITE");
        }

        private void RefreshGeometry()
        {
            PlacementVariantChoice[] choices; string key, roadId;
            bool visible = _controller != null && _controller.Geometry != null
                && _controller.Geometry.TryGetMenu(_currentInfo, out choices, out key, out roadId);
            foreach (UIComponent component in _geometryComponents) component.isVisible = visible;
            height = visible ? PanelHeight : 190f;
            if (!visible) return;
            _controller.Geometry.TryGetMenu(_currentInfo, out choices, out key, out roadId);
            string[] parts = key.Split('|');
            _heightTransitionButton.text = _controller.Geometry.HasArmedTransition ? "Transition armed"
                : parts[4] == "DEPRESSED" ? "Transition: D30 to D0 (once)" : "Transition: D0 to D30 (once)";
            _heightTransitionButton.isEnabled = !_controller.Geometry.HasArmedTransition;
            _sectionResetButton.text = _controller.Geometry.HasArmedTransition ? "Cancel transition (keep section)" : "Use Base Road default section";
            for (int i = 0; i < parts.Length; ++i) _geometryButtons[i].text = OptionLabel(parts[i]) + "  >";
            PlacementVariantChoice chosen = null;
            foreach (PlacementVariantChoice choice in choices) if (choice.Key == key) chosen = choice;
            _roadName.text = roadId;
            _geometryStatus.text = _geometryError ?? (_controller.Geometry.HasArmedTransition ? "Next placement: one height transition, then the target level"
                : chosen == null ? "This combination is unavailable for this Base Road"
                : "Actual width: " + chosen.TotalWidth.ToString("0.##") + " m | traffic divider: "
                  + (chosen.TrafficBoundary.HasValue ? chosen.TrafficBoundary.Value.ToString("0.##") + " m" : "one-way"));
            _geometryStatus.tooltip = _geometryStatus.text;
        }

        private void CycleGeometry(int dimension)
        {
            if (_controller == null || _controller.Geometry == null) return;
            PlacementVariantChoice[] choices; string key, roadId;
            if (!_controller.Geometry.TryGetMenu(_currentInfo, out choices, out key, out roadId)) return;
            List<string> values = new List<string>();
            foreach (PlacementVariantChoice choice in choices)
            {
                string value = choice.Key.Split('|')[dimension];
                if (!values.Contains(value)) values.Add(value);
            }
            string current = key.Split('|')[dimension];
            int nextIndex = (values.IndexOf(current) + 1) % values.Count;
            NetInfo resolved; string error;
            if (_controller.Geometry.TrySelect(_currentInfo, dimension, values[nextIndex], out resolved, out error))
                _currentInfo = resolved;
            _geometryError = error;
            RefreshGeometry();
        }

        private static string OptionLabel(string value)
        {
            if (value == "OFFSET_LEFT") return "Offset left";
            if (value == "OFFSET_RIGHT") return "Offset right";
            if (value == "DEPRESSED") return "Depressed";
            if (value == "FLUSH") return "Flush";
            return value.Length == 0 ? value : value.Substring(0, 1) + value.Substring(1).ToLowerInvariant();
        }

        private static void Select(UIButton button, bool selected)
        {
            button.color = selected ? SelectedColor : NormalColor;
            button.focusedColor = button.color;
        }
    }
}
