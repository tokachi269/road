using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace RoadRuntimeHost.Runtime
{
    internal sealed class PlacementVariantIndex
    {
        public int SchemaVersion;
        public string BaseRoadId;
        public PlacementVariantChoice[] Choices;
        public string DefaultKey;
        public PlacementHeightTransitionChoice[] HeightTransitions;
    }

    internal sealed class PlacementHeightTransitionChoice
    {
        public string FromKey;
        public string ToKey;
        public string Identity;
        public string BundlePath;
        public string ContentSha256;
    }

    internal sealed class PlacementHeightTransitionSnapshot
    {
        public int SchemaVersion;
        public string BaseRoadId;
        public string Identity;
        public string GeometryIdentity;
        public string StartIdentity;
        public string EndIdentity;
        public ModeBundle[] Modes;
    }

    internal sealed class ResolvedHeightTransition
    {
        public readonly PlacementVariantSnapshot Start;
        public readonly PlacementVariantSnapshot End;
        public readonly PlacementHeightTransitionSnapshot Segment;

        public ResolvedHeightTransition(PlacementVariantSnapshot start, PlacementVariantSnapshot end,
            PlacementHeightTransitionSnapshot segment)
        {
            Start = start; End = end; Segment = segment;
        }
    }

    internal sealed class PlacementVariantChoice
    {
        public string Key;
        public string Identity;
        public string BundlePath;
        public string ContentSha256;
        // Display metadata compiled by the section owner, not Runtime presets.
        public float TotalWidth = float.NaN;
        public float RoadwayZ = float.NaN;
        public float? TrafficBoundary;
    }

    internal sealed class PlacementSectionBundle
    {
        public float TotalWidth = float.NaN;
        public float CurbLeft = float.NaN;
        public float CurbRight = float.NaN;
        public float SidewalkLeft = float.NaN;
        public float SidewalkRight = float.NaN;
        public float RoadsideLeft = float.NaN;
        public float RoadsideRight = float.NaN;
        public float? TrafficBoundary;
        public float? MedianLeft;
        public float? MedianRight;
        public float RoadwayZ = float.NaN;
    }

    internal sealed class PlacementVariantSnapshot
    {
        public int SchemaVersion;
        public string BaseRoadId;
        public string Identity;
        public string GeometryIdentity;
        public string TemplateName;
        public float NodeMinCornerOffset = float.NaN;
        public float NodeReferencePavementWidth = float.NaN;
        public PlacementSectionBundle Section;
        public LaneBundle[] Lanes;
        public ModeBundle[] Modes;
    }

    // This is a consumer of compiled snapshots, not a second preset or layout
    // resolver. Reading an index never creates prefabs or reads every geometry.
    internal sealed class PlacementVariantLibrary
    {
        private readonly string _root;
        private readonly string _baseRoadId;
        private readonly Dictionary<string, PlacementVariantChoice> _choices =
            new Dictionary<string, PlacementVariantChoice>(StringComparer.Ordinal);
        private readonly Dictionary<string, string> _loaded =
            new Dictionary<string, string>(StringComparer.Ordinal);
        private readonly Dictionary<string, PlacementHeightTransitionChoice> _heightTransitions =
            new Dictionary<string, PlacementHeightTransitionChoice>(StringComparer.Ordinal);

        public int LoadedCount { get { return _loaded.Count; } }
        public string DefaultKey { get; private set; }

        public SharedHeightFamily ResolveSharedHeight(string key)
        {
            string[] parts = key.Split('|');
            if (parts.Length != 5) throw new InvalidDataException("Shared height option has invalid dimensions");
            parts[4] = "DEPRESSED"; string depressed = string.Join("|", parts);
            parts[4] = "FLUSH"; string flush = string.Join("|", parts);
            return new SharedHeightFamily(ResolveHeightTransition(depressed, flush), ResolveHeightTransition(flush, depressed));
        }

        public PlacementVariantLibrary(string root, string baseRoadId, string indexPath)
            : this(root, baseRoadId, indexPath, null) { }

        public PlacementVariantLibrary(string root, string baseRoadId, string indexPath, string expectedHash)
        {
            _root = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            _baseRoadId = baseRoadId;
            string path = SafePath(indexPath);
            FileInfo indexFile = new FileInfo(path);
            if (!indexFile.Exists || indexFile.Length > 1024 * 1024)
                throw new InvalidDataException("Placement menu is missing or too large");
            byte[] indexBytes = File.ReadAllBytes(path);
            if (!string.IsNullOrEmpty(expectedHash))
            {
                string hash;
                using (SHA256 sha = SHA256.Create()) hash = BitConverter.ToString(sha.ComputeHash(indexBytes)).Replace("-", "").ToLowerInvariant();
                if (!string.Equals(hash, expectedHash, StringComparison.Ordinal))
                    throw new InvalidDataException("Placement manifest/index revision mismatch");
            }
            PlacementVariantIndex index = JsonFiles.ReadValue<PlacementVariantIndex>(new UTF8Encoding(false, true).GetString(indexBytes));
            if (index == null || index.SchemaVersion != 2
                || !string.Equals(index.BaseRoadId, baseRoadId, StringComparison.Ordinal)
                || index.Choices == null || index.Choices.Length == 0 || index.Choices.Length > 512)
                throw new InvalidDataException("Invalid placement index for " + baseRoadId);
            Dictionary<string, PlacementVariantChoice> identities = new Dictionary<string, PlacementVariantChoice>();
            foreach (PlacementVariantChoice choice in index.Choices)
            {
                if (choice == null || !IsOptionKey(choice.Key)
                    || !IsHash(choice.Identity) || !IsHash(choice.ContentSha256) || _choices.ContainsKey(choice.Key)
                    || !Finite(choice.TotalWidth) || choice.TotalWidth <= 0f || !Finite(choice.RoadwayZ)
                    || (choice.TrafficBoundary.HasValue && !Finite(choice.TrafficBoundary.Value)))
                    throw new InvalidDataException("Invalid or repeated placement choice");
                SafePath(choice.BundlePath);
                PlacementVariantChoice other;
                if (identities.TryGetValue(choice.Identity, out other)
                    && (!string.Equals(other.BundlePath, choice.BundlePath, StringComparison.Ordinal)
                        || !string.Equals(other.ContentSha256, choice.ContentSha256, StringComparison.Ordinal)
                        || !Near(other.TotalWidth, choice.TotalWidth) || !Near(other.RoadwayZ, choice.RoadwayZ)
                        || other.TrafficBoundary != choice.TrafficBoundary))
                    throw new InvalidDataException("One placement identity has conflicting snapshots");
                identities[choice.Identity] = choice;
                _choices.Add(choice.Key, choice);
            }
            if (string.IsNullOrEmpty(index.DefaultKey) || !_choices.ContainsKey(index.DefaultKey))
                throw new InvalidDataException("Placement menu default is not compiled");
            DefaultKey = index.DefaultKey;
            PlacementHeightTransitionChoice[] edges = index.HeightTransitions ?? new PlacementHeightTransitionChoice[0];
            if (edges.Length > 512) throw new InvalidDataException("Placement transition menu is too large");
            Dictionary<string, PlacementHeightTransitionChoice> edgeIdentities = new Dictionary<string, PlacementHeightTransitionChoice>();
            foreach (PlacementHeightTransitionChoice edge in edges)
            {
                if (edge == null || !IsOptionKey(edge.FromKey) || !IsOptionKey(edge.ToKey)
                    || !_choices.ContainsKey(edge.FromKey) || !_choices.ContainsKey(edge.ToKey)
                    || !IsHash(edge.Identity) || !IsHash(edge.ContentSha256))
                    throw new InvalidDataException("Invalid height transition reference");
                string[] from = edge.FromKey.Split('|'), to = edge.ToKey.Split('|');
                for (int i = 0; i < 4; ++i)
                    if (from[i] != to[i]) throw new InvalidDataException("Height transition changes horizontal options");
                if (!((from[4] == "DEPRESSED" && to[4] == "FLUSH") || (from[4] == "FLUSH" && to[4] == "DEPRESSED")))
                    throw new InvalidDataException("Height transition must connect the two ordinary levels");
                SafePath(edge.BundlePath);
                string edgeKey = edge.FromKey + "\n" + edge.ToKey;
                if (_heightTransitions.ContainsKey(edgeKey)) throw new InvalidDataException("Repeated height transition");
                PlacementHeightTransitionChoice other;
                if (edgeIdentities.TryGetValue(edge.Identity, out other)
                    && (edge.BundlePath != other.BundlePath || edge.ContentSha256 != other.ContentSha256
                        || _choices[edge.FromKey].Identity != _choices[other.FromKey].Identity
                        || _choices[edge.ToKey].Identity != _choices[other.ToKey].Identity))
                    throw new InvalidDataException("One height transition identity has conflicting endpoints");
                if (identities.ContainsKey(edge.Identity)) throw new InvalidDataException("Ordinary and transition identities overlap");
                edgeIdentities[edge.Identity] = edge;
                _heightTransitions.Add(edgeKey, edge);
            }
        }

        public PlacementVariantChoice[] GetChoices()
        {
            // No file access and no prefab creation here. Return independent
            // DTOs so UI state cannot alter the compiled menu or its identity.
            List<string> keys = new List<string>(_choices.Keys);
            keys.Sort(StringComparer.Ordinal);
            PlacementVariantChoice[] result = new PlacementVariantChoice[keys.Count];
            for (int i = 0; i < keys.Count; ++i)
            {
                PlacementVariantChoice source = _choices[keys[i]];
                result[i] = new PlacementVariantChoice {
                    Key = source.Key, Identity = source.Identity, BundlePath = source.BundlePath,
                    ContentSha256 = source.ContentSha256, TotalWidth = source.TotalWidth,
                    RoadwayZ = source.RoadwayZ, TrafficBoundary = source.TrafficBoundary
                };
            }
            return result;
        }

        public PlacementVariantSnapshot Resolve(string key)
        {
            PlacementVariantChoice choice;
            if (!_choices.TryGetValue(key ?? string.Empty, out choice))
                throw new InvalidDataException("Placement choice is not compiled: " + key);
            string json;
            if (!_loaded.TryGetValue(choice.Identity, out json))
            {
                json = ReadSnapshot(choice.BundlePath, choice.ContentSha256);
                PlacementVariantSnapshot candidate = JsonFiles.ReadValue<PlacementVariantSnapshot>(json);
                Require(candidate, _baseRoadId, choice.Identity);
                if (!Near(candidate.Section.TotalWidth, choice.TotalWidth)
                    || !Near(candidate.Section.RoadwayZ, choice.RoadwayZ)
                    || candidate.Section.TrafficBoundary != choice.TrafficBoundary)
                    throw new InvalidDataException("Placement menu metadata disagrees with the physical snapshot");
                _loaded.Add(choice.Identity, json);
            }
            // Callers get independent mutable DTOs. A consumer mutation cannot
            // change the snapshot later used by an already placed road.
            return JsonFiles.ReadValue<PlacementVariantSnapshot>(json);
        }

        public ResolvedHeightTransition ResolveHeightTransition(string fromKey, string toKey)
        {
            PlacementHeightTransitionChoice edge;
            if (!_heightTransitions.TryGetValue((fromKey ?? string.Empty) + "\n" + (toKey ?? string.Empty), out edge))
                throw new InvalidDataException("Height transition is not compiled for these endpoints");
            PlacementVariantSnapshot start = Resolve(fromKey), end = Resolve(toKey);
            string json;
            if (!_loaded.TryGetValue(edge.Identity, out json))
            {
                json = ReadSnapshot(edge.BundlePath, edge.ContentSha256);
                PlacementHeightTransitionSnapshot segment = JsonFiles.ReadValue<PlacementHeightTransitionSnapshot>(json);
                if (segment == null || segment.Identity != edge.Identity)
                    throw new InvalidDataException("Height transition identity mismatch");
                RequireHeightTransition(segment, start, end);
                _loaded.Add(edge.Identity, json);
            }
            return new ResolvedHeightTransition(start, end, JsonFiles.ReadValue<PlacementHeightTransitionSnapshot>(json));
        }

        private string ReadSnapshot(string relative, string expectedHash)
        {
            string path = SafePath(relative);
            FileInfo file = new FileInfo(path);
            if (!file.Exists || file.Length > 32 * 1024 * 1024)
                throw new InvalidDataException("Placement snapshot is missing or too large: " + relative);
            byte[] bytes = File.ReadAllBytes(path);
            string hash;
            using (SHA256 sha = SHA256.Create()) hash = BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant();
            if (!string.Equals(hash, expectedHash, StringComparison.Ordinal))
                throw new InvalidDataException("Placement snapshot content hash mismatch");
            return DecodeSnapshot(bytes, path.EndsWith(".json.gz", StringComparison.OrdinalIgnoreCase));
        }

        internal static void RequireHeightTransition(PlacementHeightTransitionSnapshot value,
            PlacementVariantSnapshot start, PlacementVariantSnapshot end)
        {
            if (value == null || value.SchemaVersion != 1 || start == null || end == null
                || !IsHash(value.Identity) || string.IsNullOrEmpty(value.GeometryIdentity)
                || value.BaseRoadId != start.BaseRoadId || value.BaseRoadId != end.BaseRoadId
                || value.StartIdentity != start.Identity || value.EndIdentity != end.Identity
                || value.StartIdentity == value.EndIdentity)
                throw new InvalidDataException("Height transition endpoint identities disagree");
            Require(start, value.BaseRoadId, value.StartIdentity);
            Require(end, value.BaseRoadId, value.EndIdentity);
            PlacementSectionBundle a = start.Section, b = end.Section;
            if (!((Near(a.RoadwayZ, -0.3f) && Near(b.RoadwayZ, 0f)) || (Near(a.RoadwayZ, 0f) && Near(b.RoadwayZ, -0.3f))))
                throw new InvalidDataException("Height transition levels are not D30/D0");
            if (!Near(a.TotalWidth, b.TotalWidth) || !Near(a.CurbLeft, b.CurbLeft) || !Near(a.CurbRight, b.CurbRight)
                || !Near(a.SidewalkLeft, b.SidewalkLeft) || !Near(a.SidewalkRight, b.SidewalkRight)
                || !Near(a.RoadsideLeft, b.RoadsideLeft) || !Near(a.RoadsideRight, b.RoadsideRight)
                || a.TrafficBoundary != b.TrafficBoundary || a.MedianLeft != b.MedianLeft || a.MedianRight != b.MedianRight
                || start.TemplateName != end.TemplateName || !Near(start.NodeMinCornerOffset, end.NodeMinCornerOffset)
                || !Near(start.NodeReferencePavementWidth, end.NodeReferencePavementWidth)
                || start.Lanes.Length != end.Lanes.Length)
                throw new InvalidDataException("Height transition changes the horizontal section or template");
            for (int i = 0; i < start.Lanes.Length; ++i)
            {
                LaneBundle left = start.Lanes[i], right = end.Lanes[i];
                if (left.LaneId != right.LaneId || !Near(left.Position, right.Position) || !Near(left.Width, right.Width)
                    || left.Direction != right.Direction || left.LaneType != right.LaneType || left.VehicleType != right.VehicleType
                    || !Near(left.SpeedLimit, right.SpeedLimit) || !Near(left.StopOffset, right.StopOffset)
                    || left.AllowConnect != right.AllowConnect)
                    throw new InvalidDataException("Height transition changes lane topology or metadata");
            }
            RequireModes(value.Modes, false);
        }

        internal static string DecodeSnapshot(byte[] bytes, bool compressed)
        {
            const int limit = 32 * 1024 * 1024;
            if (bytes == null || bytes.Length > limit) throw new InvalidDataException("Placement snapshot exceeds the size limit");
            if (!compressed) return new UTF8Encoding(false, true).GetString(bytes);
            using (MemoryStream input = new MemoryStream(bytes, false))
            using (GZipStream gzip = new GZipStream(input, CompressionMode.Decompress))
            using (MemoryStream output = new MemoryStream())
            {
                byte[] buffer = new byte[8192];
                int count;
                while ((count = gzip.Read(buffer, 0, buffer.Length)) != 0)
                {
                    if (output.Length + count > limit) throw new InvalidDataException("Expanded placement snapshot exceeds the size limit");
                    output.Write(buffer, 0, count);
                }
                return new UTF8Encoding(false, true).GetString(output.ToArray());
            }
        }

        private string SafePath(string relative)
        {
            if (string.IsNullOrEmpty(relative) || Path.IsPathRooted(relative) || relative.Contains("..")
                || !relative.Replace('\\', '/').StartsWith("placement/", StringComparison.Ordinal))
                throw new InvalidDataException("Unsafe placement path");
            string path = Path.GetFullPath(Path.Combine(_root, relative));
            if (!path.StartsWith(_root, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Placement path escapes preview root");
            return path;
        }

        private static bool IsHash(string value)
        {
            if (value == null || value.Length != 64) return false;
            foreach (char c in value) if (!(c >= '0' && c <= '9') && !(c >= 'a' && c <= 'f')) return false;
            return true;
        }

        private static bool IsOptionKey(string value)
        {
            if (string.IsNullOrEmpty(value) || value.Length > 160) return false;
            string[] parts = value.Split('|');
            if (parts.Length != 5) return false;
            foreach (string part in parts) {
                if (part.Length == 0) return false;
                foreach (char c in part) if (!(c >= 'A' && c <= 'Z') && c != '_') return false;
            }
            return true;
        }

        private static bool Finite(float value) { return !float.IsNaN(value) && !float.IsInfinity(value); }
        private static bool Near(float a, float b) { return Math.Abs(a - b) <= 0.0001f; }

        internal static void Require(PlacementVariantSnapshot value, string baseRoadId, string identity)
        {
            if (value == null || (value.SchemaVersion != 1 && value.SchemaVersion != 2) || !IsHash(identity)
                || !string.Equals(value.Identity, identity, StringComparison.Ordinal)
                || !string.Equals(value.BaseRoadId, baseRoadId, StringComparison.Ordinal)
                || string.IsNullOrEmpty(value.GeometryIdentity) || string.IsNullOrEmpty(value.TemplateName)
                || !Finite(value.NodeMinCornerOffset) || value.NodeMinCornerOffset < 0f)
                throw new InvalidDataException("Placement snapshot identity or schema mismatch");
            PlacementSectionBundle s = value.Section;
            if (s == null || !Finite(s.TotalWidth) || s.TotalWidth < 16f || !Near(s.TotalWidth % 16f, 0f)
                || !Finite(s.CurbLeft) || !Finite(s.CurbRight) || s.CurbLeft >= s.CurbRight
                || !Finite(s.SidewalkLeft) || !Finite(s.SidewalkRight) || s.SidewalkLeft < 0f || s.SidewalkRight < 0f
                || !Finite(s.RoadsideLeft) || !Finite(s.RoadsideRight) || s.RoadsideLeft < 0f || s.RoadsideRight < 0f
                || !Finite(s.RoadwayZ) || !Near(s.CurbLeft + s.TotalWidth * 0.5f, s.SidewalkLeft)
                || !Near(s.TotalWidth * 0.5f - s.CurbRight, s.SidewalkRight)
                || s.MedianLeft.HasValue != s.MedianRight.HasValue)
                throw new InvalidDataException("Placement physical section is inconsistent");
            if (value.SchemaVersion >= 2 && (!Finite(value.NodeReferencePavementWidth)
                || value.NodeReferencePavementWidth <= 0f || value.NodeReferencePavementWidth >= s.TotalWidth * 0.5f
                || !Near(value.NodeReferencePavementWidth, (s.SidewalkLeft + s.SidewalkRight) * 0.5f)))
                throw new InvalidDataException("Placement node reference disagrees with its compiled section");
            if ((s.TrafficBoundary.HasValue && !Finite(s.TrafficBoundary.Value))
                || (s.MedianLeft.HasValue && (!Finite(s.MedianLeft.Value) || !Finite(s.MedianRight.Value)
                    || s.MedianLeft.Value >= s.MedianRight.Value)))
                throw new InvalidDataException("Placement boundary is not finite");
            List<LaneBundle> vehicles = new List<LaneBundle>();
            HashSet<string> ids = new HashSet<string>();
            if (value.Lanes == null) throw new InvalidDataException("Placement lanes are missing");
            foreach (LaneBundle lane in value.Lanes)
            {
                if (lane == null || string.IsNullOrEmpty(lane.LaneId) || !ids.Add(lane.LaneId)
                    || !Finite(lane.Position) || !Finite(lane.Width) || lane.Width <= 0f
                    || !Finite(lane.VerticalOffset) || !Finite(lane.SpeedLimit) || !Finite(lane.StopOffset)
                    || lane.Position - lane.Width * 0.5f < -s.TotalWidth * 0.5f - 0.0001f
                    || lane.Position + lane.Width * 0.5f > s.TotalWidth * 0.5f + 0.0001f)
                    throw new InvalidDataException("Placement lane geometry is invalid");
                if (string.Equals(lane.LaneType, "VEHICLE", StringComparison.Ordinal))
                {
                    if (!string.Equals(lane.VehicleType, "CAR", StringComparison.Ordinal)
                        || (lane.Direction != "FORWARD" && lane.Direction != "BACKWARD")
                        || !Near(lane.VerticalOffset, s.RoadwayZ))
                        throw new InvalidDataException("Placement vehicle metadata or height disagrees");
                    vehicles.Add(lane);
                }
                else if (lane.LaneType != "PEDESTRIAN" || lane.VehicleType != "NONE"
                         || lane.Direction != "BOTH" || !Near(lane.VerticalOffset, 0f))
                    throw new InvalidDataException("Unsupported placement lane metadata");
            }
            if (vehicles.Count == 0) throw new InvalidDataException("Placement has no vehicle lanes");
            if (!Near(vehicles[0].Position - vehicles[0].Width * 0.5f - s.CurbLeft, s.RoadsideLeft)
                || !Near(s.CurbRight - vehicles[vehicles.Count - 1].Position - vehicles[vehicles.Count - 1].Width * 0.5f, s.RoadsideRight))
                throw new InvalidDataException("Placement roadside does not match lane endpoints");
            int opposing = 0;
            for (int i = 1; i < vehicles.Count; ++i)
            {
                LaneBundle left = vehicles[i - 1], right = vehicles[i];
                float a = left.Position + left.Width * 0.5f, b = right.Position - right.Width * 0.5f;
                if (left.Direction == right.Direction)
                {
                    if (!Near(a, b)) throw new InvalidDataException("Same-direction lanes have an unexplained gap");
                }
                else
                {
                    ++opposing;
                    if (!s.TrafficBoundary.HasValue || !Near((a + b) * 0.5f, s.TrafficBoundary.Value)
                        || (s.MedianLeft.HasValue && (!Near(a, s.MedianLeft.Value) || !Near(b, s.MedianRight.Value)))
                        || (!s.MedianLeft.HasValue && !Near(a, b)))
                        throw new InvalidDataException("Traffic boundary and actual lane topology disagree");
                }
            }
            if (opposing > 1 || (opposing == 0 && (s.TrafficBoundary.HasValue || s.MedianLeft.HasValue)))
                throw new InvalidDataException("One-way or interleaved placement has an opposing boundary");
            RequireModes(value.Modes, true);
        }

        private static void RequireModes(ModeBundle[] definitions, bool requireNodes)
        {
            HashSet<string> modes = new HashSet<string>();
            foreach (ModeBundle mode in definitions ?? new ModeBundle[0])
            {
                if (mode == null || !modes.Add(mode.Mode) || (mode.Mode != "basic" && mode.Mode != "elevated"
                    && mode.Mode != "bridge" && mode.Mode != "slope" && mode.Mode != "tunnel"))
                    throw new InvalidDataException("Invalid placement mode");
                bool segment = false, node = false;
                foreach (GeometryEntry entry in mode.Entries ?? new GeometryEntry[0])
                {
                    if (entry == null || entry.Mesh == null || (entry.Kind != "segment" && !(requireNodes && entry.Kind == "node")))
                        throw new InvalidDataException("Invalid placement geometry entry");
                    MeshBundle mesh = entry.Mesh;
                    if (mesh.Vertices == null || mesh.Vertices.Length < 9 || mesh.Vertices.Length % 3 != 0
                        || mesh.Normals == null || mesh.Normals.Length != mesh.Vertices.Length || mesh.Uv == null
                        || mesh.Uv.Length * 3 != mesh.Vertices.Length * 2 || mesh.Triangles == null || mesh.Triangles.Length == 0
                        || mesh.Triangles.Length % 3 != 0 || mesh.Material == null || string.IsNullOrEmpty(mesh.Material.Shader))
                        throw new InvalidDataException("Placement mesh arrays are incomplete");
                    foreach (float coordinate in mesh.Vertices) if (!Finite(coordinate)) throw new InvalidDataException("Non-finite placement vertex");
                    foreach (float normal in mesh.Normals) if (!Finite(normal)) throw new InvalidDataException("Non-finite placement normal");
                    foreach (float uv in mesh.Uv) if (!Finite(uv)) throw new InvalidDataException("Non-finite placement UV");
                    foreach (int index in mesh.Triangles) if (index < 0 || index >= mesh.Vertices.Length / 3) throw new InvalidDataException("Placement triangle index is invalid");
                    segment |= entry.Kind == "segment";
                    node |= entry.Kind == "node";
                }
                if (!segment || (requireNodes && !node)) throw new InvalidDataException("Placement mode is incomplete");
            }
            if (modes.Count != 5) throw new InvalidDataException("Placement snapshot must contain all five modes");
        }
    }
}
