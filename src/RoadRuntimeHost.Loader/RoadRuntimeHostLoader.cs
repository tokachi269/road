using ICities;

namespace RoadRuntimeHost.Loader
{
    public sealed class RoadRuntimeHostMod : IUserMod
    {
        public string Name { get { return "Road Runtime Host"; } }
        public string Description { get { return "Hot-reloadable in-map road, prop and decal preview host"; } }
    }

    public sealed class RoadRuntimeHostLoading : LoadingExtensionBase
    {
        public override void OnLevelLoaded(LoadMode mode)
        {
            HotReloadCoordinator.Instance.Start(mode.ToString());
        }

        public override void OnLevelUnloading()
        {
            HotReloadCoordinator.Instance.Stop();
        }

        public override void OnReleased()
        {
            HotReloadCoordinator.Instance.Stop();
        }
    }

    public sealed class RoadRuntimeHostThreading : ThreadingExtensionBase
    {
        public override void OnUpdate(float realTimeDelta, float simulationTimeDelta)
        {
            HotReloadCoordinator.Instance.Tick(realTimeDelta, simulationTimeDelta);
        }
    }
}
