using System;
using System.Collections.Generic;
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

        public static string ValidatePreview(string previewPath)
        {
            string root = Path.GetFullPath(previewPath);
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
            return "catalog=" + (catalog.Roads == null ? 0 : catalog.Roads.Length) + ", bundles=" + roads;
        }

        public void Start(string previewPath, string loadMode)
        {
            _previewPath = Path.GetFullPath(previewPath);
            Directory.CreateDirectory(_previewPath);
            _updater = new PrefabUpdater(_previewPath);
            _layouts = new TestLayoutManager();
            _stopped = false;
            Debug.Log("RoadRuntimeHost runtime started for " + loadMode + " at " + _previewPath);
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
            _roads.Clear();
            Debug.Log("RoadRuntimeHost runtime stopped");
        }

        private void Poll(bool force)
        {
            bool catalogChanged = ReloadCatalog(force);
            string manifestPath = Path.Combine(_previewPath, "manifest.json");
            if (!File.Exists(manifestPath)) return;
            try
            {
                Manifest manifest = JsonFiles.Read<Manifest>(manifestPath);
                if (manifest == null || manifest.SchemaVersion != 1) throw new InvalidDataException("unsupported manifest schema");
                if (!force && !catalogChanged && string.Equals(manifest.Revision, _manifestRevision, StringComparison.Ordinal)) return;
                ManifestRoad[] entries = manifest.Roads ?? new ManifestRoad[0];
                foreach (ManifestRoad entry in entries)
                {
                    AppliedRoad current;
                    bool hasCurrent = _roads.TryGetValue(entry.RoadId, out current);
                    if (!force && !catalogChanged && hasCurrent && string.Equals(current.Revision, entry.Revision, StringComparison.Ordinal)) continue;
                    Apply(entry, current);
                }
                _manifestRevision = manifest.Revision;
            }
            catch (Exception error)
            {
                Debug.LogException(error);
            }
        }

        private bool ReloadCatalog(bool force)
        {
            string path = Path.Combine(_previewPath, "catalog.json");
            if (!File.Exists(path)) return false;
            try
            {
                Catalog next = JsonFiles.Read<Catalog>(path);
                if (next == null || next.SchemaVersion != 1) throw new InvalidDataException("unsupported catalog schema");
                if (!force && string.Equals(next.Revision, _catalogRevision, StringComparison.Ordinal)) return false;
                _updater.ApplyProps(next);
                _catalog = next;
                _catalogRevision = next.Revision;
                return true;
            }
            catch (Exception error)
            {
                Debug.LogException(error);
                return false;
            }
        }

        private void Apply(ManifestRoad entry, AppliedRoad current)
        {
            try
            {
                string path = SafeBundlePath(entry.BundlePath);
                RoadBundle bundle = JsonFiles.Read<RoadBundle>(path);
                if (bundle == null || bundle.SchemaVersion != 1) throw new InvalidDataException("unsupported road bundle schema: " + entry.RoadId);
                if (!string.Equals(bundle.RoadId, entry.RoadId, StringComparison.Ordinal)
                    || !string.Equals(bundle.Revision, entry.Revision, StringComparison.Ordinal))
                    throw new InvalidDataException("manifest/bundle revision mismatch: " + entry.RoadId);
                CatalogRoad catalogRoad = FindCatalogRoad(entry.RoadId);
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
                Debug.Log("RoadRuntimeHost applied " + entry.RoadId + " " + entry.Revision.Substring(0, Math.Min(12, entry.Revision.Length)));
            }
            catch (Exception error)
            {
                Debug.LogError("RoadRuntimeHost failed road " + entry.RoadId);
                Debug.LogException(error);
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
