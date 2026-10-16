namespace RoadRuntimeHost.Runtime
{
    internal sealed class ImtMarkingStyleBundle
    {
        public float[] WhiteColor;
        public float[] YellowColor;
        public bool CenterLineYellow;
        public bool RoadsideLines = true;
        public string LaneSeparatorStyle = "DASHED_WHITE";
        public string CenterLineStyle = "DASHED_WHITE";
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
    internal sealed class NamedValue
    {
        public string Name;
        public string ValueJson;
    }
}
