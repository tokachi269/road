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
        private readonly Dictionary<NetInfo, RoadPlacementMarkingSelection> _selections =
            new Dictionary<NetInfo, RoadPlacementMarkingSelection>();
        private readonly Dictionary<string, RoadPlacementMarkingSelection> _roadSelections =
            new Dictionary<string, RoadPlacementMarkingSelection>(StringComparer.Ordinal);
        private RoadPlacementMarkingPanel _panel;

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
                string key = string.IsNullOrEmpty(roadId)
                    ? "prefab:" + info.name
                    : roadId;
                RoadPlacementMarkingSelection selection;
                if (!_roadSelections.TryGetValue(key, out selection))
                {
                    selection = RoadPlacementMarkingSelection.FromStyle(defaultStyle);
                    _roadSelections.Add(key, selection);
                }
                _selections[info] = selection;
            }
            if (object.ReferenceEquals(_panel, null)) StartPanel();
        }

        public bool TryGet(NetInfo info, out RoadPlacementMarkingSelection selection)
        {
            selection = null;
            if (info == null) return false;
            lock (_sync)
            {
                RoadPlacementMarkingSelection current;
                if (!_selections.TryGetValue(info, out current)) return false;
                selection = current.Copy();
                return true;
            }
        }

        public void SetRoadsideLines(NetInfo info, bool enabled)
        {
            lock (_sync)
            {
                RoadPlacementMarkingSelection selection;
                if (_selections.TryGetValue(info, out selection))
                    selection.RoadsideLines = enabled;
            }
        }

        public void SetLaneSeparatorStyle(NetInfo info, string style)
        {
            lock (_sync)
            {
                RoadPlacementMarkingSelection selection;
                if (_selections.TryGetValue(info, out selection))
                    selection.LaneSeparatorStyle =
                        RoadPlacementMarkingSelection.NormalizeLaneSeparator(style);
            }
        }

        public void SetCenterLineStyle(NetInfo info, string style)
        {
            lock (_sync)
            {
                RoadPlacementMarkingSelection selection;
                if (_selections.TryGetValue(info, out selection))
                    selection.CenterLineStyle =
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
                _selections.Clear();
                _roadSelections.Clear();
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

    internal sealed class RoadPlacementMarkingPanel : UIPanel
    {
        private const float PanelWidth = 430f;
        private const float PanelHeight = 190f;
        private static readonly Color32 SelectedColor = new Color32(92, 146, 72, 255);
        private static readonly Color32 NormalColor = new Color32(75, 85, 96, 255);

        private RoadPlacementMarkingController _controller;
        private NetInfo _currentInfo;
        private UILabel _roadName;
        private UIButton _roadsideOn;
        private UIButton _roadsideOff;
        private UIButton _laneDashed;
        private UIButton _laneSolid;
        private UIButton _centerYellow;
        private UIButton _centerSolid;
        private UIButton _centerDashed;

        public override void Awake()
        {
            base.Awake();
            name = "RoadRuntimeHostRoadPlacementMarkings";
            atlas = UIView.GetAView().defaultAtlas;
            backgroundSprite = "MenuPanel2";
            color = new Color32(255, 255, 255, 245);
            size = new Vector2(PanelWidth, PanelHeight);
            canFocus = true;

            AddLabel("New road lines", 14f, 12f, 400f, 24f, 1.0f);
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
            Hide();
        }

        public void Bind(RoadPlacementMarkingController controller)
        {
            _controller = controller;
        }

        public override void Update()
        {
            PositionPanel();
            NetInfo selected = null;
            ToolController toolController = ToolsModifierControl.toolController;
            NetTool netTool = toolController == null
                ? null
                : toolController.CurrentTool as NetTool;
            if (netTool != null) selected = netTool.Prefab;

            RoadPlacementMarkingSelection ignored;
            if (_controller == null || !_controller.TryGet(selected, out ignored))
            {
                _currentInfo = null;
                if (isVisible) Hide();
                return;
            }

            if (!ReferenceEquals(_currentInfo, selected))
            {
                _currentInfo = selected;
                _roadName.text = selected.name;
                RefreshSelection();
            }
            if (!isVisible) Show();
        }

        private void PositionPanel()
        {
            UIView view = UIView.GetAView();
            if (view == null) return;
            relativePosition = new Vector3(
                Math.Max(8f, view.fixedWidth - PanelWidth - 12f),
                Math.Max(8f, view.fixedHeight - PanelHeight - 170f));
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

        private static void Select(UIButton button, bool selected)
        {
            button.color = selected ? SelectedColor : NormalColor;
            button.focusedColor = button.color;
        }
    }
}
