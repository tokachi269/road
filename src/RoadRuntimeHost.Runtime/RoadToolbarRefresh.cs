using System;
using UnityEngine;

namespace RoadRuntimeHost.Runtime
{
    internal static class RoadToolbarRefresh
    {
        public static void Refresh()
        {
            int groupPanelCount = 0;
            int roadPanelCount = 0;
            try
            {
                UnityEngine.Object[] groupPanels =
                    UnityEngine.Object.FindObjectsOfType(typeof(RoadsGroupPanel));
                foreach (UnityEngine.Object value in groupPanels)
                {
                    RoadsGroupPanel panel = value as RoadsGroupPanel;
                    if (panel == null) continue;
                    panel.RefreshPanel();
                    ++groupPanelCount;
                }

                UnityEngine.Object[] roadPanels =
                    UnityEngine.Object.FindObjectsOfType(typeof(RoadsPanel));
                foreach (UnityEngine.Object value in roadPanels)
                {
                    RoadsPanel panel = value as RoadsPanel;
                    if (panel == null) continue;
                    panel.RefreshPanel();
                    ++roadPanelCount;
                }

                DiagnosticLog.Info(
                    "SUCCESS",
                    "road_toolbar_refreshed",
                    "Refreshed the road toolbar after applying road prefabs",
                    "group_panel_count", groupPanelCount.ToString(),
                    "road_panel_count", roadPanelCount.ToString());
            }
            catch (Exception error)
            {
                DiagnosticLog.Error(
                    "CS1_ENVIRONMENT",
                    "road_toolbar_refresh_failed",
                    "The road toolbar could not be refreshed after applying road prefabs",
                    error,
                    "group_panel_count", groupPanelCount.ToString(),
                    "road_panel_count", roadPanelCount.ToString());
            }
        }
    }
}
