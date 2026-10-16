using System;

namespace RoadRuntimeHost.Runtime
{
    // Implemented by a preview prefab provider today; an asset-backed provider
    // can supply the same compiled family without loading the development Host.
    internal interface IPlacementPrefabFactory
    {
        NetInfo GetOrCreateSharedHeight(SharedHeightFamily family, string roadId);
    }

    internal sealed class RoadGameSession : IDisposable
    {
        private ImtPreviewService _markings;
        private RoadPlacementGeometryController _geometry;
        private readonly IPlacementPrefabFactory _prefabs;
        private bool _active;

        public RoadGameSession(IPlacementPrefabFactory prefabs)
        {
            if (prefabs == null) throw new ArgumentNullException("prefabs");
            _prefabs = prefabs;
        }

        public void Start()
        {
            if (_active) return;
            _active = true;
            try
            {
                _markings = new ImtPreviewService();
                _geometry = new RoadPlacementGeometryController(_prefabs, _markings);
                SharedHeightHook.Start(_geometry.SharedPlacementCode, _geometry.OnTransitionPlaced);
                PlacementHeightTransitionHook.Start(_geometry.IsTransitionArmed, _geometry.OnTransitionPlaced);
                _markings.BindPlacementGeometry(_geometry);
            }
            catch { Dispose(); throw; }
        }

        public void RegisterRoad(string roadId, NetInfo info, ImtMarkingStyleBundle style)
        {
            RequireStarted();
            _markings.ApplyRoad(roadId, info, style);
        }

        public void RegisterPlacementMenu(string root, string roadId, NetInfo info,
            ImtMarkingStyleBundle style, string indexPath, string revision, bool force)
        {
            RequireStarted();
            _geometry.Register(root, roadId, info, style, indexPath, revision, force);
        }

        private void RequireStarted()
        {
            if (_markings == null) throw new InvalidOperationException("Game session is not started");
        }

        public void Dispose()
        {
            if (!_active) return;
            _active = false;
            try { if (_markings != null) _markings.Stop(); }
            finally
            {
                try { PlacementHeightTransitionHook.Stop(); }
                finally { SharedHeightHook.Stop(); _markings = null; _geometry = null; }
            }
        }
    }
}
