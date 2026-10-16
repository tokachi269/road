using System;

namespace RoadRuntimeHost.Runtime
{
    internal sealed class PreviewPlacementPrefabs : IPlacementPrefabFactory
    {
        private readonly PrefabUpdater _updater;
        private readonly Func<string, CatalogRoad> _catalog;

        public PreviewPlacementPrefabs(PrefabUpdater updater, Func<string, CatalogRoad> catalog)
        { _updater = updater; _catalog = catalog; }

        public NetInfo GetOrCreateSharedHeight(SharedHeightFamily family, string roadId)
        { return _updater.GetOrCreateSharedHeight(family, _catalog(roadId)); }
    }
}
