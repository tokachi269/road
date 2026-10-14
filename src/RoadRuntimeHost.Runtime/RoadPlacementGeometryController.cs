using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading;
using UnityEngine;

namespace RoadRuntimeHost.Runtime
{
    // Protocol order is the compiled option key order. No metre values or
    // topology rules are calculated here; available keys come from domain.
    internal sealed class PlacementOptionSelection
    {
        public string Key { get; private set; }
        public PlacementOptionSelection(string key) { Commit(key); }

        public string Propose(string[] available, int dimension, string value)
        {
            if (dimension < 0 || dimension >= 5 || string.IsNullOrEmpty(value) || value.Contains("|"))
                throw new InvalidDataException("Invalid placement option");
            string[] parts = Key.Split('|');
            parts[dimension] = value;
            string proposed = string.Join("|", parts);
            foreach (string key in available) if (string.Equals(key, proposed, StringComparison.Ordinal)) return proposed;
            throw new InvalidDataException("This placement combination is not compiled for the selected Base Road");
        }

        public void Commit(string key)
        {
            if (string.IsNullOrEmpty(key) || key.Split('|').Length != 5)
                throw new InvalidDataException("Placement option key must have five parts");
            foreach (string part in key.Split('|')) if (part.Length == 0) throw new InvalidDataException("Empty placement option");
            Key = key;
        }
    }

    internal sealed class RoadPlacementGeometryController
    {
        private sealed class Binding
        {
            public string RoadId;
            public PlacementVariantLibrary Library;
            public CatalogRoad Catalog;
            public ImtMarkingStyleBundle Style;
            public NetInfo Basic;
            public string Revision;
        }

        private sealed class Target
        {
            public Binding Binding;
            public string Mode;
            public string Key;
            public string Identity;
            public bool Shared;
            public string SelectionRevision;
        }

        private sealed class PendingTransition
        {
            public NetInfo ToolInfo;
            public NetInfo ReverseBasic;
            public string ToKey;
            public volatile bool Placed;
        }

        private readonly PrefabUpdater _updater;
        private readonly ImtPreviewService _markings;
        private readonly Dictionary<NetInfo, Target> _targets = new Dictionary<NetInfo, Target>();
        private readonly object _targetSync = new object();
        private PlacementOptionSelection _selection;
        private readonly Dictionary<string, string> _menuRevisions = new Dictionary<string, string>(StringComparer.Ordinal);
        private int _revision;
        private volatile PendingTransition _pendingTransition;
        public int Revision { get { return Interlocked.CompareExchange(ref _revision, 0, 0); } }

        public RoadPlacementGeometryController(PrefabUpdater updater, ImtPreviewService markings)
        {
            _updater = updater;
            _markings = markings;
        }

        public void Register(string root, string roadId, NetInfo basic, CatalogRoad catalog, ImtMarkingStyleBundle style,
            string indexPath, string revision, bool force)
        {
            string relative = string.IsNullOrEmpty(indexPath) ? "placement/" + roadId + "/index.json" : indexPath;
            string previous;
            if (!force && _menuRevisions.TryGetValue(roadId, out previous) && previous == (revision ?? string.Empty)) return;
            if (!File.Exists(Path.Combine(root, relative))) return;
            PlacementVariantLibrary library = new PlacementVariantLibrary(root, roadId, relative, revision);
            Binding binding = new Binding { RoadId = roadId, Library = library, Basic = basic, Catalog = catalog,
                Style = style, Revision = revision ?? string.Empty };
            if (_selection == null) _selection = new PlacementOptionSelection(library.DefaultKey);
            List<NetInfo> variants = new List<NetInfo>();
            foreach (KeyValuePair<NetInfo, Target> item in _targets)
            {
                if (item.Value.Binding.RoadId != roadId) continue;
                item.Value.Binding = binding;
                if (item.Value.Mode == "basic" && item.Value.Identity != null) variants.Add(item.Key);
            }
            foreach (NetInfo variant in variants) _markings.RegisterPlacement(roadId, variant, style);
            RegisterModes(basic, binding, null, null);
            _menuRevisions[roadId] = revision ?? string.Empty;
            Interlocked.Increment(ref _revision);
        }

        private void RegisterModes(NetInfo basic, Binding binding, string key, string identity, bool shared = false)
        {
            lock (_targetSync) {
                if (basic == null) return;
                _targets[basic] = new Target { Binding = binding, Mode = "basic", Key = key, Identity = identity, Shared = shared,
                    SelectionRevision = binding.Revision };
                RoadAI ai = basic.m_netAI as RoadAI;
                if (ai == null) return;
                AddMode(ai.m_elevatedInfo, binding, "elevated", key, identity, shared);
                AddMode(ai.m_bridgeInfo, binding, "bridge", key, identity, shared);
                AddMode(ai.m_slopeInfo, binding, "slope", key, identity, shared);
                AddMode(ai.m_tunnelInfo, binding, "tunnel", key, identity, shared);
            }
        }

        private void AddMode(NetInfo info, Binding binding, string mode, string key, string identity, bool shared)
        {
            if (info != null) _targets[info] = new Target { Binding = binding, Mode = mode, Key = key, Identity = identity, Shared = shared,
                SelectionRevision = binding.Revision };
        }

        public bool TryGetMenu(NetInfo info, out PlacementVariantChoice[] choices, out string key, out string roadId)
        {
            choices = null; key = null; roadId = null;
            Target target;
            if (info == null || !_targets.TryGetValue(info, out target) || _selection == null) return false;
            choices = target.Binding.Library.GetChoices();
            key = _selection.Key;
            roadId = target.Binding.RoadId;
            return true;
        }

        public bool TrySelect(NetInfo selected, int dimension, string value, out NetInfo result, out string error)
        {
            result = selected; error = null;
            try
            {
                Target target;
                if (selected == null || !_targets.TryGetValue(selected, out target))
                    throw new InvalidDataException("Selected road has no compiled placement menu");
                PlacementVariantChoice[] choices = target.Binding.Library.GetChoices();
                string[] keys = new string[choices.Length];
                for (int i = 0; i < keys.Length; ++i) keys[i] = choices[i].Key;
                string proposed = _selection.Propose(keys, dimension, value);
                result = ApplySelected(selected, target, proposed);
                return true;
            }
            catch (Exception failure)
            {
                error = failure.Message;
                DiagnosticLog.Error(DiagnosticLog.Classify(failure), "placement_variant_select_failed",
                    "Placement options and the tool prefab were not changed", failure);
                return false;
            }
        }

        // Explicit user operation, not an automatic one-way fallback. It also
        // lets users leave a combination with multiple unavailable dimensions.
        public bool TryUseDefault(NetInfo selected, out NetInfo result, out string error)
        {
            result = selected; error = null;
            try
            {
                Target target;
                if (selected == null || !_targets.TryGetValue(selected, out target))
                    throw new InvalidDataException("Selected road has no compiled placement menu");
                result = ApplySelected(selected, target, target.Binding.Library.DefaultKey);
                return true;
            }
            catch (Exception failure) { error = failure.Message; return false; }
        }

        public bool TryArmHeightTransition(NetInfo selected, out NetInfo result, out string error)
        {
            result = selected; error = null;
            try
            {
                SharedHeightHook.RequireReady();
                if (!PlacementHeightTransitionHook.Ready || !NetSegmentCreationHook.Ready)
                    throw new InvalidOperationException("Height transition hooks are not ready");
                Target target;
                if (selected == null || !_targets.TryGetValue(selected, out target) || _selection == null)
                    throw new InvalidDataException("Selected road has no compiled placement menu");
                if (_pendingTransition != null) throw new InvalidOperationException("A transition is already armed; select a normal section to cancel");
                string fromKey = _selection.Key;
                string[] parts = fromKey.Split('|');
                parts[4] = parts[4] == "DEPRESSED" ? "FLUSH" : "DEPRESSED";
                string toKey = string.Join("|", parts);
                SharedHeightFamily family = target.Binding.Library.ResolveSharedHeight(fromKey);
                NetInfo basic = _updater.GetOrCreateSharedHeight(family, target.Binding.Catalog);
                NetInfo mode = GetMode(basic, target.Mode);
                if (mode == null) throw new InvalidDataException("Transition is missing the selected mode");
                _markings.RegisterPlacement(target.Binding.RoadId, basic, target.Binding.Style);
                RegisterModes(basic, target.Binding, fromKey, family.Identity, true);
                SelectToolPrefab(selected, mode);
                _pendingTransition = new PendingTransition { ToolInfo = mode, ReverseBasic = basic, ToKey = toKey };
                Interlocked.Increment(ref _revision);
                result = mode;
                return true;
            }
            catch (Exception failure)
            {
                error = failure.Message;
                DiagnosticLog.Error(DiagnosticLog.Classify(failure), "height_transition_arm_failed", "Transition was not armed", failure);
                return false;
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public NetInfo IsTransitionArmed(NetInfo info)
        {
            PendingTransition pending = _pendingTransition;
            if (pending == null || pending.Placed || !ReferenceEquals(info, pending.ToolInfo)) return null;
            NetTool tool = ToolsModifierControl.toolController == null ? null : ToolsModifierControl.toolController.CurrentTool as NetTool;
            return tool != null && tool.m_mode != NetTool.Mode.Upgrade && ReferenceEquals(tool.Prefab, info) ? pending.ReverseBasic : null;
        }

        public void OnTransitionPlaced(NetInfo info)
        {
            PendingTransition pending = _pendingTransition;
            if (pending == null) return;
            if (!ReferenceEquals(pending.ToolInfo, info)) {
                lock (_targetSync) {
                    Target armed, actual;
                    if (!_targets.TryGetValue(pending.ToolInfo, out armed) || !_targets.TryGetValue(info, out actual)
                        || !armed.Shared || !actual.Shared || armed.Identity != actual.Identity) return;
                }
            }
            pending.Placed = true;
            Interlocked.Increment(ref _revision);
        }

        public bool HasArmedTransition { get { return _pendingTransition != null; } }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public int SharedPlacementCode(NetInfo info)
        {
            Target target, toolTarget;
            NetTool tool = ToolsModifierControl.toolController == null ? null : ToolsModifierControl.toolController.CurrentTool as NetTool;
            lock (_targetSync) {
                if (tool == null || info == null || !_targets.TryGetValue(info, out target) || !target.Shared
                    || tool.Prefab == null || !_targets.TryGetValue(tool.Prefab, out toolTarget)
                    || !toolTarget.Shared || toolTarget.Identity != target.Identity || _selection == null) return -1;
            }
            bool flush = _selection.Key.EndsWith("|FLUSH", StringComparison.Ordinal);
            PendingTransition pending = _pendingTransition;
            if (pending != null) {
                if (pending.Placed) flush = pending.ToKey.EndsWith("|FLUSH", StringComparison.Ordinal);
                else if (tool.m_mode != NetTool.Mode.Upgrade) return flush ? 3 : 2;
            }
            return flush ? 1 : 0;
        }

        private NetInfo ApplySelected(NetInfo selected, Target target, string proposed)
        {
            SharedHeightHook.RequireReady();
            if (target.Shared && target.Key == proposed && _pendingTransition == null
                && target.SelectionRevision == target.Binding.Revision) return selected;
            SharedHeightFamily family = target.Binding.Library.ResolveSharedHeight(proposed);
            NetInfo basic = _updater.GetOrCreateSharedHeight(family, target.Binding.Catalog);
            NetInfo mode = GetMode(basic, target.Mode);
            if (mode == null) throw new InvalidDataException("Placement variant is missing the selected network mode");
            // Registration is event-driven and does not create any segments.
            _markings.RegisterPlacement(target.Binding.RoadId, basic, target.Binding.Style);
            RegisterModes(basic, target.Binding, proposed, family.Identity, true);
            SelectToolPrefab(selected, mode);
            _selection.Commit(proposed);
            _pendingTransition = null;
            DiagnosticLog.Info("SUCCESS", "placement_variant_selected", "Selected an immutable road section for the next placement",
                "road_id", target.Binding.RoadId, "identity", family.Identity, "choice", proposed);
            return mode;
        }

        public bool EnsureSelection(NetInfo info, out NetInfo result, out string error)
        {
            result = info; error = null;
            PendingTransition pending = _pendingTransition;
            if (pending != null)
            {
                if (ReferenceEquals(info, pending.ToolInfo))
                {
                    if (!pending.Placed) return true;
                    try { result = ApplySelected(info, _targets[info], pending.ToKey); return true; }
                    catch (Exception failure) { error = failure.Message; return false; }
                }
                _pendingTransition = null;
            }
            if (_selection == null || info == null || !_targets.ContainsKey(info)) return true;
            return TrySelect(info, 0, _selection.Key.Split('|')[0], out result, out error);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void SelectToolPrefab(NetInfo expected, NetInfo value)
        {
            NetTool tool = ToolsModifierControl.toolController == null ? null : ToolsModifierControl.toolController.CurrentTool as NetTool;
            if (tool == null || !ReferenceEquals(tool.Prefab, expected))
                throw new InvalidOperationException("Road tool selection changed before placement variant registration completed");
            tool.Prefab = value;
        }

        private static NetInfo GetMode(NetInfo basic, string mode)
        {
            return PrefabUpdater.ModeTemplate(basic, mode);
        }
    }
}
