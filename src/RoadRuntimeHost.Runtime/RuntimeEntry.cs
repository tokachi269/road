using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using UnityEngine;

namespace RoadRuntimeHost.Runtime
{
    public sealed class RuntimeEntry
    {
        private sealed class AppliedRoad
        {
            public string Revision;
            public string StructuralSignature;
            public string TestSignature;
            public NetInfo Info;
        }

        private readonly Dictionary<string, AppliedRoad> _roads = new Dictionary<string, AppliedRoad>();
        private string _previewPath;
        private string _manifestRevision;
        private string _catalogRevision;
        private float _elapsed;
        private bool _stopped;
        private Catalog _catalog;
        private PrefabUpdater _updater;
        private TestLayoutManager _layouts;
        private bool _reportedMissingCatalog;
        private bool _reportedMissingManifest;

        public static string ValidatePreview(string previewPath)
        {
            return ValidatePreview(previewPath, null);
        }

        public static string ValidatePreview(string previewPath, string logPath)
        {
            if (!string.IsNullOrEmpty(logPath)) DiagnosticLog.Configure(logPath);
            string root = Path.GetFullPath(previewPath);
            DiagnosticLog.Info("DATA", "preview_validation_begin", "Validating compiled preview files", "preview_path", root);
            try
            {
                Catalog catalog = JsonFiles.Read<Catalog>(Path.Combine(root, "catalog.json"));
                Manifest manifest = JsonFiles.Read<Manifest>(Path.Combine(root, "manifest.json"));
                if (catalog == null || catalog.SchemaVersion != 1) throw new InvalidDataException("invalid catalog");
                if (manifest == null || manifest.SchemaVersion != 1) throw new InvalidDataException("invalid manifest");
                int roads = 0;
                foreach (ManifestRoad entry in manifest.Roads ?? new ManifestRoad[0])
                {
                    if (Path.IsPathRooted(entry.BundlePath) || entry.BundlePath.Contains("..")) throw new InvalidDataException("unsafe bundle path");
                    RoadBundle bundle = JsonFiles.Read<RoadBundle>(Path.Combine(root, entry.BundlePath));
                    if (bundle == null || bundle.SchemaVersion != 1 || bundle.Revision != entry.Revision) throw new InvalidDataException("invalid road bundle " + entry.RoadId);
                    if (bundle.Modes == null || bundle.Modes.Length == 0) throw new InvalidDataException("road bundle has no modes " + entry.RoadId);
                    ++roads;
                }
                string result = "catalog=" + (catalog.Roads == null ? 0 : catalog.Roads.Length) + ", bundles=" + roads;
                DiagnosticLog.Info("SUCCESS", "preview_validation_success", "Compiled preview files are internally consistent", "summary", result);
                return result;
            }
            catch (Exception error)
            {
                DiagnosticLog.Error(DiagnosticLog.Classify(error), "preview_validation_failed", "Compiled preview validation failed", error, "preview_path", root);
                throw;
            }
        }

        public void Start(string previewPath, string loadMode, string logPath)
        {
            DiagnosticLog.Configure(logPath);
            _previewPath = Path.GetFullPath(previewPath);
            Directory.CreateDirectory(_previewPath);
            _updater = new PrefabUpdater(_previewPath);
            _layouts = new TestLayoutManager();
            _stopped = false;
            DiagnosticLog.Info("MOD", "runtime_start", "Hot runtime started", "preview_path", _previewPath, "load_mode", loadMode, "log_path", logPath);
            Poll(true);
        }

        public void Tick(float realTimeDelta, float simulationTimeDelta)
        {
            if (_stopped) return;
            _elapsed += realTimeDelta;
            if (_elapsed < 1f) return;
            _elapsed = 0f;
            Poll(false);
        }

        public void Stop()
        {
            if (_stopped) return;
            _stopped = true;
            if (_layouts != null) _layouts.ReleaseAll();
            int appliedRoadCount = _roads.Count;
            _roads.Clear();
            DiagnosticLog.Info("MOD", "runtime_stop", "Hot runtime stopped", "applied_road_count", appliedRoadCount.ToString());
        }

        private void Poll(bool force)
        {
            bool catalogChanged = ReloadCatalog(force);
            string manifestPath = Path.Combine(_previewPath, "manifest.json");
            if (!File.Exists(manifestPath))
            {
                if (!_reportedMissingManifest)
                {
                    DiagnosticLog.Warn("DATA_MISSING", "manifest_missing", "manifest.json is missing; road updates are paused", "path", manifestPath);
                    _reportedMissingManifest = true;
                }
                return;
            }
            _reportedMissingManifest = false;
            try
            {
                Manifest manifest = JsonFiles.Read<Manifest>(manifestPath);
                if (manifest == null || manifest.SchemaVersion != 1) throw new InvalidDataException("unsupported manifest schema");
                ManifestRoad[] entries = manifest.Roads ?? new ManifestRoad[0];
                if (!force && !catalogChanged && string.Equals(manifest.Revision, _manifestRevision, StringComparison.Ordinal) && !HasPendingRoad(entries)) return;
                DiagnosticLog.Info("DATA", "manifest_changed", "Applying changed manifest", "revision", manifest.Revision ?? string.Empty, "road_count", entries.Length.ToString(), "catalog_changed", catalogChanged.ToString());
                foreach (ManifestRoad entry in entries)
                {
                    AppliedRoad current;
                    bool hasCurrent = _roads.TryGetValue(entry.RoadId, out current);
                    if (!force && !catalogChanged && hasCurrent && string.Equals(current.Revision, entry.Revision, StringComparison.Ordinal)) continue;
                    Apply(entry, current);
                }
                _manifestRevision = manifest.Revision;
                DiagnosticLog.Info("SUCCESS", "manifest_applied", "Manifest update completed", "revision", manifest.Revision ?? string.Empty, "road_count", entries.Length.ToString());
            }
            catch (Exception error)
            {
                DiagnosticLog.Error(DiagnosticLog.Classify(error), "manifest_apply_failed", "Manifest could not be read or applied; it will be retried", error, "path", manifestPath);
            }
        }

        private bool HasPendingRoad(ManifestRoad[] entries)
        {
            foreach (ManifestRoad entry in entries)
            {
                AppliedRoad current;
                if (!_roads.TryGetValue(entry.RoadId, out current) || !string.Equals(current.Revision, entry.Revision, StringComparison.Ordinal)) return true;
            }
            return false;
        }

        private bool ReloadCatalog(bool force)
        {
            string path = Path.Combine(_previewPath, "catalog.json");
            if (!File.Exists(path))
            {
                if (!_reportedMissingCatalog)
                {
                    DiagnosticLog.Warn("DATA_MISSING", "catalog_missing", "catalog.json is missing; catalog updates are paused", "path", path);
                    _reportedMissingCatalog = true;
                }
                return false;
            }
            _reportedMissingCatalog = false;
            try
            {
                Catalog next = JsonFiles.Read<Catalog>(path);
                if (next == null || next.SchemaVersion != 1) throw new InvalidDataException("unsupported catalog schema");
                if (!force && string.Equals(next.Revision, _catalogRevision, StringComparison.Ordinal)) return false;
                _updater.ApplyProps(next);
                _catalog = next;
                _catalogRevision = next.Revision;
                DiagnosticLog.Info("SUCCESS", "catalog_applied", "Catalog update completed", "revision", next.Revision ?? string.Empty, "road_count", (next.Roads == null ? 0 : next.Roads.Length).ToString(), "prop_count", (next.Props == null ? 0 : next.Props.Length).ToString());
                return true;
            }
            catch (Exception error)
            {
                DiagnosticLog.Error(DiagnosticLog.Classify(error), "catalog_apply_failed", "Catalog could not be read or applied; it will be retried", error, "path", path);
                return false;
            }
        }

        private void Apply(ManifestRoad entry, AppliedRoad current)
        {
            Stopwatch stopwatch = Stopwatch.StartNew();
            try
            {
                string path = SafeBundlePath(entry.BundlePath);
                DiagnosticLog.Info("DATA", "road_apply_begin", "Applying road bundle", "road_id", entry.RoadId ?? string.Empty, "revision", entry.Revision ?? string.Empty, "bundle_path", path);
                RoadBundle bundle = JsonFiles.Read<RoadBundle>(path);
                if (bundle == null || bundle.SchemaVersion != 1) throw new InvalidDataException("unsupported road bundle schema: " + entry.RoadId);
                if (!string.Equals(bundle.RoadId, entry.RoadId, StringComparison.Ordinal)
                    || !string.Equals(bundle.Revision, entry.Revision, StringComparison.Ordinal))
                    throw new InvalidDataException("manifest/bundle revision mismatch: " + entry.RoadId);
                CatalogRoad catalogRoad = FindCatalogRoad(entry.RoadId);
                if (catalogRoad == null) DiagnosticLog.Warn("DATA_MISSING", "road_catalog_row_missing", "Road bundle has no matching catalog row; bundle lane snapshot will be used", "road_id", entry.RoadId ?? string.Empty);
                if (catalogRoad != null && catalogRoad.Lanes != null) bundle.Lanes = catalogRoad.Lanes;
                NetInfo info = _updater.ApplyRoad(bundle, catalogRoad);
                string effectiveStructuralSignature = catalogRoad != null && !string.IsNullOrEmpty(catalogRoad.StructuralSignature)
                    ? catalogRoad.StructuralSignature : bundle.StructuralSignature;
                TestScenario scenario = FindScenario(catalogRoad != null ? catalogRoad.TestScenarioId : null);
                string testSignature = ScenarioSignature(scenario);
                bool structuralChanged = current == null
                    || !string.Equals(current.StructuralSignature, effectiveStructuralSignature, StringComparison.Ordinal);
                bool testChanged = current == null || !string.Equals(current.TestSignature, testSignature, StringComparison.Ordinal);
                AppliedRoad applied = current ?? new AppliedRoad();
                applied.Revision = bundle.Revision;
                applied.StructuralSignature = effectiveStructuralSignature;
                applied.TestSignature = testSignature;
                applied.Info = info;
                _roads[entry.RoadId] = applied;
                if (structuralChanged || testChanged)
                {
                    if (scenario != null && scenario.Enabled)
                        _layouts.Rebuild(entry.RoadId, info, scenario, catalogRoad != null ? catalogRoad.UiPriority : 0);
                    else _layouts.Release(entry.RoadId);
                }
                DiagnosticLog.Info("SUCCESS", "road_apply_success", "Road bundle applied", "road_id", entry.RoadId ?? string.Empty, "revision", entry.Revision ?? string.Empty, "mode_count", (bundle.Modes == null ? 0 : bundle.Modes.Length).ToString(), "lane_count", (bundle.Lanes == null ? 0 : bundle.Lanes.Length).ToString(), "structural_changed", structuralChanged.ToString(), "test_changed", testChanged.ToString(), "elapsed_ms", stopwatch.ElapsedMilliseconds.ToString());
            }
            catch (Exception error)
            {
                DiagnosticException diagnostic = error as DiagnosticException;
                DiagnosticLog.Error(DiagnosticLog.Classify(error), "road_apply_failed", "Road bundle failed and remains pending for retry", error, "road_id", entry.RoadId ?? string.Empty, "revision", entry.Revision ?? string.Empty, "bundle_path", entry.BundlePath ?? string.Empty, "cause_event", diagnostic == null ? string.Empty : diagnostic.EventName, "elapsed_ms", stopwatch.ElapsedMilliseconds.ToString());
            }
        }

        private CatalogRoad FindCatalogRoad(string roadId)
        {
            if (_catalog == null || _catalog.Roads == null) return null;
            foreach (CatalogRoad road in _catalog.Roads)
                if (string.Equals(road.RoadId, roadId, StringComparison.Ordinal)) return road;
            return null;
        }

        private TestScenario FindScenario(string scenarioId)
        {
            if (string.IsNullOrEmpty(scenarioId) || _catalog == null || _catalog.TestScenarios == null) return null;
            foreach (TestScenario scenario in _catalog.TestScenarios)
                if (string.Equals(scenario.ScenarioId, scenarioId, StringComparison.Ordinal)) return scenario;
            return null;
        }

        private static string ScenarioSignature(TestScenario scenario)
        {
            if (scenario == null) return string.Empty;
            float[] origin = scenario.Origin ?? new float[0];
            return scenario.ScenarioId + "|" + scenario.Layout + "|" + scenario.Enabled + "|"
                + scenario.Spacing.ToString(System.Globalization.CultureInfo.InvariantCulture) + "|"
                + string.Join(",", Array.ConvertAll(origin, delegate(float value) { return value.ToString(System.Globalization.CultureInfo.InvariantCulture); }));
        }

        private string SafeBundlePath(string relative)
        {
            if (string.IsNullOrEmpty(relative) || Path.IsPathRooted(relative) || relative.Contains(".."))
                throw new InvalidDataException("unsafe bundle path: " + relative);
            string root = _previewPath + Path.DirectorySeparatorChar;
            string path = Path.GetFullPath(Path.Combine(root, relative));
            if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("bundle path escapes preview root");
            return path;
        }
    }
}
