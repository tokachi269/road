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
        public ImtMarkingStyleBundle ImtMarkingStyle;
        public string Revision;
        public string StructuralSignature;
        public LaneBundle[] Lanes;
        public ModeBundle[] Modes;
    }
    internal sealed class ImtMarkingStyleBundle
    {
        public float[] WhiteColor;
        public float[] YellowColor;
        public bool CenterLineYellow;
        public float Texture;
        public float[] Cracks;
        public float[] Voids;
        public float CrosswalkWidth;
        public float CrosswalkDashLength;
        public float CrosswalkGapLength;
        public float CrosswalkOffset;
        public float StopLineWidth;
        public float LineWidth;
        public float DashLength;
        public float DashGap;
    }
    internal sealed class LaneBundle
    {
        public string LaneId;
        public float Position;
        public float Width;
        public float VerticalOffset;
        public float StopOffset;
        public float SpeedLimit;
        public string Direction;
        public string LaneType;
        public string VehicleType;
        public bool AllowConnect;
    }
    internal sealed class ModeBundle
    {
        public string Mode;
        public GeometryEntry[] Entries;
    }
    internal sealed class GeometryEntry
    {
        public string Kind;
        public MeshBundle Mesh;
    }
    internal sealed class MeshBundle
    {
        public string Name;
        public float[] Vertices;
        public float[] Normals;
        public float[] Uv;
        public int[] Triangles;
        public MaterialBundle Material;
    }
    internal sealed class MaterialBundle
    {
        public string Name;
        public string Shader;
        public float[] Color;
        public NamedValue[] Textures;
        public PackedTextureBundle[] PackedTextures;
        public NamedValue[] MaterialProperties;
        public float[] MainTextureScale;
    }
    internal sealed class PackedTextureBundle
    {
        public string Name;
        public string Packing;
        public NamedValue[] Sources;
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
    internal sealed class NamedValue
    {
        public string Name;
        public string ValueJson;
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
