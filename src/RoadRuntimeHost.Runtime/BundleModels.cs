using System.Runtime.Serialization;

namespace RoadRuntimeHost.Runtime
{
    [DataContract]
    internal sealed class Manifest
    {
        [DataMember(Name = "schema_version")] public int SchemaVersion;
        [DataMember(Name = "revision")] public string Revision;
        [DataMember(Name = "roads")] public ManifestRoad[] Roads;
    }

    [DataContract]
    internal sealed class ManifestRoad
    {
        [DataMember(Name = "road_id")] public string RoadId;
        [DataMember(Name = "prefab_name")] public string PrefabName;
        [DataMember(Name = "bundle_path")] public string BundlePath;
        [DataMember(Name = "revision")] public string Revision;
        [DataMember(Name = "structural_signature")] public string StructuralSignature;
    }

    [DataContract]
    internal sealed class RoadBundle
    {
        [DataMember(Name = "schema_version")] public int SchemaVersion;
        [DataMember(Name = "road_id")] public string RoadId;
        [DataMember(Name = "prefab_name")] public string PrefabName;
        [DataMember(Name = "template_name")] public string TemplateName;
        [DataMember(Name = "revision")] public string Revision;
        [DataMember(Name = "structural_signature")] public string StructuralSignature;
        [DataMember(Name = "lanes")] public LaneBundle[] Lanes;
        [DataMember(Name = "modes")] public ModeBundle[] Modes;
    }

    [DataContract]
    internal sealed class LaneBundle
    {
        [DataMember(Name = "lane_id")] public string LaneId;
        [DataMember(Name = "position")] public float Position;
        [DataMember(Name = "width")] public float Width;
        [DataMember(Name = "vertical_offset")] public float VerticalOffset;
        [DataMember(Name = "stop_offset")] public float StopOffset;
        [DataMember(Name = "speed_limit")] public float SpeedLimit;
        [DataMember(Name = "direction")] public string Direction;
        [DataMember(Name = "lane_type")] public string LaneType;
        [DataMember(Name = "vehicle_type")] public string VehicleType;
        [DataMember(Name = "allow_connect")] public bool AllowConnect;
    }

    [DataContract]
    internal sealed class ModeBundle
    {
        [DataMember(Name = "mode")] public string Mode;
        [DataMember(Name = "entries")] public GeometryEntry[] Entries;
    }

    [DataContract]
    internal sealed class GeometryEntry
    {
        [DataMember(Name = "kind")] public string Kind;
        [DataMember(Name = "mesh")] public MeshBundle Mesh;
    }

    [DataContract]
    internal sealed class MeshBundle
    {
        [DataMember(Name = "name")] public string Name;
        [DataMember(Name = "vertices")] public float[] Vertices;
        [DataMember(Name = "normals")] public float[] Normals;
        [DataMember(Name = "uv")] public float[] Uv;
        [DataMember(Name = "triangles")] public int[] Triangles;
        [DataMember(Name = "material")] public MaterialBundle Material;
    }

    [DataContract]
    internal sealed class MaterialBundle
    {
        [DataMember(Name = "name")] public string Name;
        [DataMember(Name = "shader")] public string Shader;
        [DataMember(Name = "color")] public float[] Color;
        [DataMember(Name = "textures")] public NamedValue[] Textures;
        [DataMember(Name = "material_properties")] public NamedValue[] MaterialProperties;
    }

    [DataContract]
    internal sealed class Catalog
    {
        [DataMember(Name = "schema_version")] public int SchemaVersion;
        [DataMember(Name = "revision")] public string Revision;
        [DataMember(Name = "roads")] public CatalogRoad[] Roads;
        [DataMember(Name = "props")] public CatalogProp[] Props;
        [DataMember(Name = "conditions")] public CatalogCondition[] Conditions;
        [DataMember(Name = "test_scenarios")] public TestScenario[] TestScenarios;
    }

    [DataContract]
    internal sealed class CatalogRoad
    {
        [DataMember(Name = "road_id")] public string RoadId;
        [DataMember(Name = "category")] public string Category;
        [DataMember(Name = "ui_priority")] public int UiPriority;
        [DataMember(Name = "test_scenario_id")] public string TestScenarioId;
        [DataMember(Name = "structural_signature")] public string StructuralSignature;
        [DataMember(Name = "lanes")] public LaneBundle[] Lanes;
        [DataMember(Name = "prop_placements")] public PropPlacement[] PropPlacements;
        [DataMember(Name = "geometry_bindings")] public GeometryBinding[] GeometryBindings;
    }

    [DataContract]
    internal sealed class CatalogProp
    {
        [DataMember(Name = "prop_id")] public string PropId;
        [DataMember(Name = "prefab_name")] public string PrefabName;
        [DataMember(Name = "template_name")] public string TemplateName;
        [DataMember(Name = "kind")] public string Kind;
        [DataMember(Name = "shader")] public string Shader;
        [DataMember(Name = "mesh_bundle")] public string MeshBundle;
        [DataMember(Name = "textures")] public NamedValue[] Textures;
        [DataMember(Name = "material_properties")] public NamedValue[] MaterialProperties;
    }

    [DataContract]
    internal sealed class CatalogCondition
    {
        [DataMember(Name = "condition_id")] public string ConditionId;
        [DataMember(Name = "scope")] public string Scope;
        [DataMember(Name = "required")] public NamedValue[] Required;
        [DataMember(Name = "forbidden")] public NamedValue[] Forbidden;
    }

    [DataContract]
    internal sealed class NamedValue
    {
        [DataMember(Name = "name")] public string Name;
        [DataMember(Name = "value_json")] public string ValueJson;
    }

    [DataContract]
    internal sealed class PropPlacement
    {
        [DataMember(Name = "placement_id")] public string PlacementId;
        [DataMember(Name = "lane_id")] public string LaneId;
        [DataMember(Name = "prop_id")] public string PropId;
        [DataMember(Name = "condition_id")] public string ConditionId;
        [DataMember(Name = "position")] public float[] Position;
        [DataMember(Name = "angle")] public float Angle;
        [DataMember(Name = "repeat_distance")] public float RepeatDistance;
        [DataMember(Name = "probability")] public int Probability;
    }

    [DataContract]
    internal sealed class GeometryBinding
    {
        [DataMember(Name = "binding_id")] public string BindingId;
        [DataMember(Name = "mode")] public string Mode;
        [DataMember(Name = "kind")] public string Kind;
        [DataMember(Name = "material_name")] public string MaterialName;
        [DataMember(Name = "order")] public int Order;
        [DataMember(Name = "condition_id")] public string ConditionId;
        [DataMember(Name = "direct_connect")] public bool DirectConnect;
    }

    [DataContract]
    internal sealed class TestScenario
    {
        [DataMember(Name = "scenario_id")] public string ScenarioId;
        [DataMember(Name = "layout")] public string Layout;
        [DataMember(Name = "enabled")] public bool Enabled;
        [DataMember(Name = "origin")] public float[] Origin;
        [DataMember(Name = "spacing")] public float Spacing;
    }

    [DataContract]
    internal sealed class PropMeshFile
    {
        [DataMember(Name = "schema_version")] public int SchemaVersion;
        [DataMember(Name = "prop_id")] public string PropId;
        [DataMember(Name = "revision")] public string Revision;
        [DataMember(Name = "mesh")] public MeshBundle Mesh;
    }
}
