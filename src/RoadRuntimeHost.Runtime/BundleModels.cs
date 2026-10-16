namespace RoadRuntimeHost.Runtime
{
    internal sealed class Manifest
    {
        public int SchemaVersion;
        public string Revision;
        public string TextureRevision;
        public ManifestRoad[] Roads;
    }
    internal sealed class ManifestRoad
    {
        public string RoadId;
        public string PrefabName;
        public string BundlePath;
        public string Revision;
        public string StructuralSignature;
        public string PlacementIndex;
        public string PlacementRevision;
    }
    internal sealed class RoadBundle
    {
        public int SchemaVersion;
        public string RoadId;
        public string PrefabName;
        public string TemplateName;
        public float HalfWidth;
        public float PavementWidth;
        public float NodeMinCornerOffset = float.NaN;
        // Placement snapshots set this explicitly. Legacy preview bundles
        // keep the inherited template value when this field is absent.
        public float SurfaceLevel = float.NaN;
        public ImtMarkingStyleBundle ImtMarkingStyle;
        public string Revision;
        public string StructuralSignature;
        public LaneBundle[] Lanes;
        public ModeBundle[] Modes;
    }
    internal sealed class Catalog
    {
        public int SchemaVersion;
        public string Revision;
        public CatalogRoad[] Roads;
        public CatalogProp[] Props;
        public CatalogCondition[] Conditions;
        public TestScenario[] TestScenarios;
    }
    internal sealed class CatalogRoad
    {
        public string RoadId;
        public string Category;
        public int UiPriority;
        public string TestScenarioId;
        public string StructuralSignature;
        public LaneBundle[] Lanes;
        public PropPlacement[] PropPlacements;
        public GeometryBinding[] GeometryBindings;
    }
    internal sealed class CatalogProp
    {
        public string PropId;
        public string PrefabName;
        public string TemplateName;
        public string Kind;
        public string Shader;
        public string MeshBundle;
        public NamedValue[] Textures;
        public NamedValue[] MaterialProperties;
    }
    internal sealed class CatalogCondition
    {
        public string ConditionId;
        public string Scope;
        public NamedValue[] Required;
        public NamedValue[] Forbidden;
    }
    internal sealed class PropPlacement
    {
        public string PlacementId;
        public string LaneId;
        public string PropId;
        public string ConditionId;
        public float[] Position;
        public float Angle;
        public float RepeatDistance;
        public int Probability;
    }
    internal sealed class GeometryBinding
    {
        public string BindingId;
        public string Mode;
        public string Kind;
        public string MaterialName;
        public int Order;
        public string ConditionId;
        public bool DirectConnect;
    }
    internal sealed class TestScenario
    {
        public string ScenarioId;
        public string Layout;
        public bool Enabled;
        public float[] Origin;
        public float Spacing;
    }
    internal sealed class PropMeshFile
    {
        public int SchemaVersion;
        public string PropId;
        public string Revision;
        public MeshBundle Mesh;
    }
}
