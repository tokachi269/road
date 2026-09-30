using System;
using System.Reflection;
using CitiesHarmony.API;
using HarmonyLib;
using IMT.Manager;
using IMT.Tools;
using IMT.UI;
using IMT.UI.Panel;
using IMT.Utilities;
using ModsCommon;
using ModsCommon.UI;

namespace RoadRuntimeHost.Runtime
{
    internal static class ImtRestoreDefaultsHook
    {
        private const string PatchTypeName =
            "RoadRuntimeHost.Runtime.ImtRestoreDefaultsPatch";
        private static readonly object Sync = new object();
        private static Func<bool, ushort, bool> _canRestore;
        private static Func<bool, ushort, bool> _restore;
        private static bool _startAttempted;

        public static void Start(
            Func<bool, ushort, bool> canRestore,
            Func<bool, ushort, bool> restore)
        {
            lock (Sync)
            {
                _canRestore = canRestore;
                _restore = restore;
            }
            if (_startAttempted) return;
            _startAttempted = true;
            InvokePatch("Start");
        }

        public static void Stop()
        {
            lock (Sync)
            {
                _canRestore = null;
                _restore = null;
            }
            if (_startAttempted) InvokePatch("Stop");
            _startAttempted = false;
        }

        internal static bool CanRestore(Marking marking)
        {
            if (marking == null) return false;
            Func<bool, ushort, bool> handler;
            lock (Sync) handler = _canRestore;
            return handler != null
                && handler(marking.Type == MarkingType.Node, marking.Id);
        }

        internal static bool Restore(Marking marking)
        {
            if (marking == null) return false;
            Func<bool, ushort, bool> handler;
            lock (Sync) handler = _restore;
            return handler != null
                && handler(marking.Type == MarkingType.Node, marking.Id);
        }

        private static void InvokePatch(string methodName)
        {
            try
            {
                Type patchType = typeof(ImtRestoreDefaultsHook).Assembly.GetType(
                    PatchTypeName, true);
                MethodInfo method = patchType.GetMethod(
                    methodName,
                    BindingFlags.Static | BindingFlags.Public
                        | BindingFlags.NonPublic);
                if (method == null)
                    throw new MissingMethodException(PatchTypeName, methodName);
                method.Invoke(null, null);
            }
            catch (TargetInvocationException error)
            {
                ReportLoadFailure(error.InnerException ?? error);
            }
            catch (Exception error)
            {
                ReportLoadFailure(error);
            }
        }

        private static void ReportLoadFailure(Exception error)
        {
            DiagnosticLog.Error(
                "MOD_DEPENDENCY",
                "imt_restore_ui_hook_load_failed",
                "Road defaults remain restorable by runtime regeneration, but the IMT header action could not be loaded",
                error);
        }
    }

    internal static class ImtRestoreDefaultsPatch
    {
        private static readonly string PatchId =
            "RoadRuntimeHost.ImtRestoreDefaults."
            + typeof(ImtRestoreDefaultsPatch).Module.ModuleVersionId.ToString("N");
        private static Harmony _harmony;
        private static MethodInfo _fillContent;
        private static MethodInfo _refresh;
        private static HeaderButtonInfo<HeaderButton> _restoreButton;
        private static PanelHeader _buttonHeader;
        private static bool _stopped = true;

        public static void Start()
        {
            _stopped = false;
            HarmonyHelper.DoOnHarmonyReady(Install);
        }

        public static void Stop()
        {
            _stopped = true;
            if (_restoreButton != null)
            {
                _restoreButton.Visible = false;
                if (_buttonHeader != null) _buttonHeader.Refresh();
            }
            if (_harmony != null)
            {
                if (_fillContent != null)
                    _harmony.Unpatch(
                        _fillContent, HarmonyPatchType.All, PatchId);
                if (_refresh != null)
                    _harmony.Unpatch(_refresh, HarmonyPatchType.All, PatchId);
                DiagnosticLog.Info(
                    "MOD",
                    "imt_restore_ui_hook_removed",
                    "IMT road-default restore action was disabled");
            }
            _harmony = null;
            _fillContent = null;
            _refresh = null;
            _restoreButton = null;
            _buttonHeader = null;
        }

        private static void Install()
        {
            if (_stopped) return;
            try
            {
                Assembly imtAssembly = typeof(PanelHeader).Assembly;
                Version version = imtAssembly.GetName().Version;
                if (version == null || version.Major != 1 || version.Minor != 15)
                    throw new NotSupportedException(
                        "IMT restore UI supports 1.15.x only; loaded "
                        + (version == null ? "<unknown>" : version.ToString()));

                _fillContent = typeof(PanelHeader).GetMethod(
                    "FillContent",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                _refresh = typeof(PanelHeader).GetMethod(
                    "Refresh",
                    BindingFlags.Instance | BindingFlags.Public);
                if (_fillContent == null || _refresh == null)
                    throw new MissingMethodException(
                        typeof(PanelHeader).FullName,
                        "FillContent/Refresh");

                MethodInfo fillPostfix = typeof(ImtRestoreDefaultsPatch).GetMethod(
                    "FillContentPostfix",
                    BindingFlags.Static | BindingFlags.NonPublic);
                MethodInfo refreshPrefix = typeof(ImtRestoreDefaultsPatch).GetMethod(
                    "RefreshPrefix",
                    BindingFlags.Static | BindingFlags.NonPublic);
                if (fillPostfix == null || refreshPrefix == null)
                    throw new MissingMethodException(
                        typeof(ImtRestoreDefaultsPatch).FullName,
                        "FillContentPostfix/RefreshPrefix");

                _harmony = new Harmony(PatchId);
                _harmony.Unpatch(_fillContent, HarmonyPatchType.All, PatchId);
                _harmony.Unpatch(_refresh, HarmonyPatchType.All, PatchId);
                _harmony.Patch(
                    _fillContent, null, new HarmonyMethod(fillPostfix));
                _harmony.Patch(
                    _refresh, new HarmonyMethod(refreshPrefix));

                if (SingletonItem<IntersectionMarkingToolPanel>.Exist)
                {
                    IntersectionMarkingToolPanel panel =
                        SingletonItem<IntersectionMarkingToolPanel>.Instance;
                    PanelHeader header = Traverse.Create(panel)
                        .Field("Header").GetValue<PanelHeader>();
                    AddRestoreButton(header, true);
                }

                DiagnosticLog.Info(
                    "SUCCESS",
                    "imt_restore_ui_hook_installed",
                    "Installed a road-default restore action in the IMT marking header",
                    "imt_version", version.ToString());
            }
            catch (Exception error)
            {
                _harmony = null;
                _fillContent = null;
                _refresh = null;
                DiagnosticLog.Error(
                    "MOD_COMPATIBILITY",
                    "imt_restore_ui_hook_install_failed",
                    "The IMT road-default restore action was not installed because the version-gated UI integration was incompatible",
                    error);
            }
        }

        private static void FillContentPostfix(PanelHeader __instance)
        {
            if (_stopped) return;
            AddRestoreButton(__instance, false);
        }

        private static void AddRestoreButton(
            PanelHeader header,
            bool refresh)
        {
            if (header == null || ReferenceEquals(header, _buttonHeader)) return;
            if (_restoreButton != null) _restoreButton.Visible = false;

            BaseHeaderContent content = Traverse.Create(header)
                .Property("Content").GetValue<BaseHeaderContent>();
            if (content == null)
                throw new InvalidOperationException(
                    "IMT panel header content was not available");

            _restoreButton = new HeaderButtonInfo<HeaderButton>(
                "Restore road defaults",
                HeaderButtonState.Main,
                IMTTextures.Atlas,
                IMTTextures.ResetHeaderButton,
                "Restore road defaults",
                RestoreClick);
            _restoreButton.Visible = CanRestoreCurrent();
            _buttonHeader = header;
            content.AddButton(_restoreButton, refresh);
        }

        private static void RefreshPrefix()
        {
            if (_restoreButton != null)
                _restoreButton.Visible = !_stopped && CanRestoreCurrent();
        }

        private static bool CanRestoreCurrent()
        {
            IntersectionMarkingTool tool =
                SingletonTool<IntersectionMarkingTool>.Instance;
            return tool != null
                && ImtRestoreDefaultsHook.CanRestore(tool.Marking);
        }

        private static void RestoreClick()
        {
            IntersectionMarkingTool tool =
                SingletonTool<IntersectionMarkingTool>.Instance;
            Marking marking = tool == null ? null : tool.Marking;
            if (!ImtRestoreDefaultsHook.CanRestore(marking)) return;

            YesNoMessageBox messageBox = MessageBox.Show<YesNoMessageBox>();
            messageBox.CaptionText = "Restore road defaults";
            messageBox.MessageText =
                "Replace all IMT markings on the selected "
                + marking.Type.ToString().ToLowerInvariant()
                + " with the current generated road defaults?\n"
                + "Manual IMT edits on it will be removed.";
            messageBox.OnButton1Click = delegate
            {
                bool restored = ImtRestoreDefaultsHook.Restore(marking);
                if (restored && tool.Panel != null) tool.Panel.UpdatePanel();
                return restored;
            };
        }
    }
}
