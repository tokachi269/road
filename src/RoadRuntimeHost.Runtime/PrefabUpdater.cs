using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using ColossalFramework;
using UnityEngine;

namespace RoadRuntimeHost.Runtime
{
    internal sealed class PrefabUpdater
    {
        private sealed class PackedTextureState
        {
            public string Packing;
            public Texture2D Texture;
            public Dictionary<string, Texture2D> Sources;
        }

        private readonly string _previewPath;
        private readonly Dictionary<string, PropInfo> _props = new Dictionary<string, PropInfo>();
        private readonly Dictionary<string, CatalogCondition> _conditions = new Dictionary<string, CatalogCondition>();
        private readonly HashSet<string> _unsupportedConditionNamespaces = new HashSet<string>();
        private readonly Dictionary<string, Texture2D> _textures = new Dictionary<string, Texture2D>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, PackedTextureState> _packedTextures = new Dictionary<string, PackedTextureState>(StringComparer.Ordinal);

        public PrefabUpdater(string previewPath)
        {
            _previewPath = previewPath;
        }

        public int ReloadTextures()
        {
            int count = 0;
            foreach (KeyValuePair<string, Texture2D> item in _textures)
            {
                if (!File.Exists(item.Key))
                    throw new DiagnosticException("DATA_MISSING", "texture_file_missing", "Texture file does not exist: " + item.Key);
                if (!item.Value.LoadImage(File.ReadAllBytes(item.Key)))
                    throw new InvalidDataException("Texture image could not be decoded: " + item.Key);
                item.Value.wrapMode = TextureWrapMode.Repeat;
                ++count;
            }
            foreach (PackedTextureState packed in _packedTextures.Values) RebuildPackedTexture(packed);
            DiagnosticLog.Info("SUCCESS", "textures_reloaded", "Shared runtime textures were reloaded in place", "texture_count", count.ToString(), "packed_texture_count", _packedTextures.Count.ToString());
            return count;
        }

        public void ApplyProps(Catalog catalog)
        {
            _conditions.Clear();
            if (catalog != null && catalog.Conditions != null)
                foreach (CatalogCondition condition in catalog.Conditions) _conditions[condition.ConditionId] = condition;
            if (catalog == null || catalog.Props == null) return;
            foreach (CatalogProp definition in catalog.Props)
            {
                try { ApplyProp(definition); }
                catch (Exception error)
                {
                    DiagnosticLog.Error(DiagnosticLog.Classify(error), "prop_apply_failed", "Prop definition failed; roads referencing it may omit the placement", error, "prop_id", definition.PropId ?? string.Empty, "prefab_name", definition.PrefabName ?? string.Empty, "mesh_bundle", definition.MeshBundle ?? string.Empty);
                }
            }
        }

        public NetInfo ApplyRoad(RoadBundle bundle, CatalogRoad catalogRoad)
        {
            NetInfo template = PrefabCollection<NetInfo>.FindLoaded(bundle.TemplateName);
            if (template == null) throw new DiagnosticException("CS1_ENVIRONMENT", "road_template_not_loaded", "Road template is not loaded: " + bundle.TemplateName);

            Dictionary<string, NetInfo> modes = new Dictionary<string, NetInfo>();
            if (bundle.Modes == null) throw new InvalidDataException("Road bundle has no modes: " + bundle.RoadId);
            foreach (ModeBundle mode in bundle.Modes)
            {
                NetInfo modeTemplate = ModeTemplate(template, mode.Mode);
                if (modeTemplate == null)
                {
                    DiagnosticLog.Warn("CS1_ENVIRONMENT", "road_mode_template_missing", "Template does not expose the requested mode; that mode is skipped", "road_id", bundle.RoadId ?? string.Empty, "template", bundle.TemplateName ?? string.Empty, "mode", mode.Mode ?? string.Empty);
                    continue;
                }
                string name = ModeName(bundle.PrefabName, mode.Mode);
                NetInfo info = FindOrCloneNet(name, modeTemplate, bundle.RoadId + "." + mode.Mode);
                info.m_halfWidth = bundle.HalfWidth;
                info.m_pavementWidth = bundle.PavementWidth;
                ApplyLanes(info, bundle.Lanes, catalogRoad);
                ApplyGeometry(info, mode.Mode, mode.Entries, catalogRoad);
                if (catalogRoad != null)
                {
                    SetUiCategory(info, catalogRoad.Category);
                    info.m_UIPriority = catalogRoad.UiPriority;
                }
                info.InitializePrefab();
                modes[mode.Mode] = info;
                DiagnosticLog.Info("SUCCESS", "road_mode_applied", "Road mode prefab was updated", "road_id", bundle.RoadId ?? string.Empty, "mode", mode.Mode ?? string.Empty, "prefab_name", name, "half_width", info.m_halfWidth.ToString(System.Globalization.CultureInfo.InvariantCulture), "pavement_width", info.m_pavementWidth.ToString(System.Globalization.CultureInfo.InvariantCulture), "lane_count", (info.m_lanes == null ? 0 : info.m_lanes.Length).ToString(), "segment_entry_count", (info.m_segments == null ? 0 : info.m_segments.Length).ToString(), "node_entry_count", (info.m_nodes == null ? 0 : info.m_nodes.Length).ToString());
            }
            NetInfo basic;
            if (!modes.TryGetValue("basic", out basic)) throw new InvalidDataException("Road bundle has no usable basic mode");
            LinkModes(basic, modes);
            RefreshExistingInstances(modes.Values);
            return basic;
        }

        private void ApplyProp(CatalogProp definition)
        {
            PropInfo info = PrefabCollection<PropInfo>.FindLoaded(definition.PrefabName);
            if (info == null)
            {
                if (string.IsNullOrEmpty(definition.TemplateName))
                {
                    DiagnosticLog.Warn("DATA_MISSING", "prop_template_name_missing", "New prop requires template_name and was not registered", "prop_id", definition.PropId ?? string.Empty, "prefab_name", definition.PrefabName ?? string.Empty);
                    return;
                }
                PropInfo template = PrefabCollection<PropInfo>.FindLoaded(definition.TemplateName);
                if (template == null)
                {
                    DiagnosticLog.Warn("CS1_ENVIRONMENT", "prop_template_not_loaded", "Prop template is not loaded and the prop was not registered", "prop_id", definition.PropId ?? string.Empty, "template", definition.TemplateName ?? string.Empty);
                    return;
                }
                GameObject clone = UnityEngine.Object.Instantiate(template.gameObject) as GameObject;
                clone.name = definition.PrefabName;
                info = clone.GetComponent<PropInfo>();
                PrefabCollection<PropInfo>.InitializePrefabs("RoadRuntimeHost." + definition.PropId, info, null);
                PrefabCollection<PropInfo>.BindPrefabs();
            }
            if (!string.IsNullOrEmpty(definition.MeshBundle))
            {
                string path = SafePreviewPath(definition.MeshBundle);
                PropMeshFile meshFile = JsonFiles.Read<PropMeshFile>(path);
                info.m_mesh = BuildMesh(meshFile.Mesh);
                info.m_material = BuildMaterial(meshFile.Mesh.Material, info.m_material);
                info.m_lodMesh = info.m_mesh;
                info.m_lodMaterial = info.m_material;
            }
            if (!string.IsNullOrEmpty(definition.Shader))
            {
                Shader shader = Shader.Find(definition.Shader);
                if (shader == null) throw new DiagnosticException("CS1_ENVIRONMENT", "prop_shader_not_found", "Shader is not available: " + definition.Shader);
                if (info.m_material == null) info.m_material = new Material(shader);
                info.m_material.shader = shader;
            }
            if (string.Equals(definition.Kind, "DECAL", StringComparison.OrdinalIgnoreCase))
            {
                info.m_isDecal = true;
                info.m_requireHeightMap = true;
                info.m_createRuining = false;
            }
            ApplyTextures(info.m_material, definition.Textures);
            ApplyMaterialProperties(info.m_material, definition.MaterialProperties);
            info.InitializePrefab();
            _props[definition.PropId] = info;
            DiagnosticLog.Info("SUCCESS", "prop_apply_success", "Prop definition applied", "prop_id", definition.PropId ?? string.Empty, "prefab_name", definition.PrefabName ?? string.Empty, "kind", definition.Kind ?? string.Empty);
        }

        private void ApplyLanes(NetInfo info, LaneBundle[] definitions, CatalogRoad catalogRoad)
        {
            if (definitions == null) definitions = new LaneBundle[0];
            Dictionary<string, List<PropPlacement>> placements = new Dictionary<string, List<PropPlacement>>();
            if (catalogRoad != null && catalogRoad.PropPlacements != null)
            {
                foreach (PropPlacement placement in catalogRoad.PropPlacements)
                {
                    List<PropPlacement> values;
                    if (!placements.TryGetValue(placement.LaneId, out values))
                    {
                        values = new List<PropPlacement>();
                        placements.Add(placement.LaneId, values);
                    }
                    values.Add(placement);
                }
            }
            NetInfo.Lane[] lanes = new NetInfo.Lane[definitions.Length];
            for (int index = 0; index != definitions.Length; ++index)
            {
                LaneBundle source = definitions[index];
                NetInfo.Lane lane = new NetInfo.Lane();
                lane.m_position = source.Position;
                lane.m_width = source.Width;
                lane.m_verticalOffset = source.VerticalOffset;
                lane.m_stopOffset = source.StopOffset;
                lane.m_speedLimit = source.SpeedLimit;
                lane.m_direction = ParseDirection(source.Direction);
                lane.m_finalDirection = lane.m_direction;
                lane.m_laneType = ParseLaneType(source.LaneType);
                lane.m_vehicleType = ParseVehicleType(source.VehicleType);
                lane.m_allowConnect = source.AllowConnect;
                lane.m_laneProps = ScriptableObject.CreateInstance<NetLaneProps>();
                lane.m_laneProps.name = info.name + "." + source.LaneId;
                lane.m_laneProps.m_props = BuildLaneProps(placements, source.LaneId);
                lanes[index] = lane;
            }
            info.m_lanes = lanes;
            info.m_sortedLanes = new int[lanes.Length];
            for (int index = 0; index != lanes.Length; ++index) info.m_sortedLanes[index] = index;
            Array.Sort(info.m_sortedLanes, delegate(int left, int right) { return lanes[left].m_position.CompareTo(lanes[right].m_position); });
        }

        private NetLaneProps.Prop[] BuildLaneProps(Dictionary<string, List<PropPlacement>> placements, string laneId)
        {
            List<PropPlacement> source;
            if (!placements.TryGetValue(laneId, out source)) return new NetLaneProps.Prop[0];
            List<NetLaneProps.Prop> result = new List<NetLaneProps.Prop>();
            foreach (PropPlacement placement in source)
            {
                PropInfo prop;
                if (!_props.TryGetValue(placement.PropId, out prop))
                {
                    DiagnosticLog.Warn("DATA_MISSING", "prop_placement_unresolved", "Placement references a prop that was not applied", "lane_id", laneId ?? string.Empty, "prop_id", placement.PropId ?? string.Empty);
                    continue;
                }
                NetLaneProps.Prop value = new NetLaneProps.Prop();
                value.m_prop = prop;
                value.m_finalProp = prop;
                float[] position = placement.Position ?? new float[0];
                value.m_position = new Vector3(
                    position.Length > 0 ? position[0] : 0f,
                    position.Length > 1 ? position[1] : 0f,
                    position.Length > 2 ? position[2] : 0f);
                value.m_angle = placement.Angle;
                value.m_repeatDistance = placement.RepeatDistance;
                value.m_probability = placement.Probability;
                ApplyCondition(value, placement.ConditionId);
                result.Add(value);
            }
            return result.ToArray();
        }

        private void ApplyCondition(NetLaneProps.Prop target, string conditionId)
        {
            if (string.IsNullOrEmpty(conditionId)) return;
            CatalogCondition condition;
            if (!_conditions.TryGetValue(conditionId, out condition))
            {
                DiagnosticLog.Warn("DATA_MISSING", "condition_reference_unresolved", "Prop placement references an unknown condition", "condition_id", conditionId);
                return;
            }
            ApplyConditionValues(target, condition.Required, true);
            ApplyConditionValues(target, condition.Forbidden, false);
        }

        private void ApplyConditionValues(NetLaneProps.Prop target, NamedValue[] values, bool required)
        {
            if (values == null) return;
            foreach (NamedValue value in values)
            {
                string[] flags = DecodeJsonStrings(value.ValueJson);
                if (flags.Length == 0) continue;
                string combined = string.Join(",", flags);
                if (string.Equals(value.Name, "vanilla.lane", StringComparison.OrdinalIgnoreCase))
                {
                    NetLane.Flags parsed = (NetLane.Flags)Enum.Parse(typeof(NetLane.Flags), combined, true);
                    if (required) target.m_flagsRequired |= parsed; else target.m_flagsForbidden |= parsed;
                }
                else if (string.Equals(value.Name, "vanilla.start_node", StringComparison.OrdinalIgnoreCase))
                {
                    NetNode.Flags parsed = (NetNode.Flags)Enum.Parse(typeof(NetNode.Flags), combined, true);
                    if (required) target.m_startFlagsRequired |= parsed; else target.m_startFlagsForbidden |= parsed;
                }
                else if (string.Equals(value.Name, "vanilla.end_node", StringComparison.OrdinalIgnoreCase))
                {
                    NetNode.Flags parsed = (NetNode.Flags)Enum.Parse(typeof(NetNode.Flags), combined, true);
                    if (required) target.m_endFlagsRequired |= parsed; else target.m_endFlagsForbidden |= parsed;
                }
                else if (_unsupportedConditionNamespaces.Add(value.Name))
                {
                    DiagnosticLog.Warn("MOD_CONTRACT", "condition_namespace_unsupported", "Condition is preserved in data but this runtime has no adapter for its namespace", "condition_name", value.Name ?? string.Empty);
                }
            }
        }

        private void ApplyGeometry(NetInfo info, string mode, GeometryEntry[] entries, CatalogRoad catalogRoad)
        {
            List<NetInfo.Segment> segments = new List<NetInfo.Segment>();
            List<NetInfo.Node> nodes = new List<NetInfo.Node>();
            NetInfo.Segment segmentTemplate = info.m_segments != null && info.m_segments.Length != 0 ? info.m_segments[0] : new NetInfo.Segment();
            NetInfo.Node nodeTemplate = info.m_nodes != null && info.m_nodes.Length != 0 ? info.m_nodes[0] : new NetInfo.Node();
            if (entries != null)
            {
                foreach (GeometryEntry entry in entries)
                {
                    if (entry == null || entry.Mesh == null) continue;
                    Mesh mesh = BuildMesh(entry.Mesh);
                    if (string.Equals(entry.Kind, "node", StringComparison.OrdinalIgnoreCase))
                    {
                        NetInfo.Node value = CopyNode(nodeTemplate);
                        value.m_mesh = value.m_nodeMesh = mesh;
                        value.m_material = value.m_nodeMaterial = BuildMaterial(entry.Mesh.Material, nodeTemplate.m_material);
                        value.m_lodMesh = mesh;
                        value.m_lodMaterial = value.m_material;
                        GeometryBinding binding = FindGeometryBinding(catalogRoad, mode, "node", entry.Mesh.Material != null ? entry.Mesh.Material.Name : null);
                        if (binding != null)
                        {
                            value.m_directConnect = binding.DirectConnect;
                            ApplyNodeCondition(value, binding.ConditionId);
                        }
                        nodes.Add(value);
                    }
                    else
                    {
                        NetInfo.Segment value = CopySegment(segmentTemplate);
                        value.m_mesh = value.m_segmentMesh = mesh;
                        value.m_material = value.m_segmentMaterial = BuildMaterial(entry.Mesh.Material, segmentTemplate.m_material);
                        value.m_lodMesh = mesh;
                        value.m_lodMaterial = value.m_material;
                        GeometryBinding binding = FindGeometryBinding(catalogRoad, mode, "segment", entry.Mesh.Material != null ? entry.Mesh.Material.Name : null);
                        if (binding != null) ApplySegmentCondition(value, binding.ConditionId);
                        segments.Add(value);
                    }
                }
            }
            info.m_segments = segments.ToArray();
            info.m_nodes = nodes.ToArray();
        }

        private static GeometryBinding FindGeometryBinding(CatalogRoad road, string mode, string kind, string materialName)
        {
            if (road == null || road.GeometryBindings == null) return null;
            foreach (GeometryBinding binding in road.GeometryBindings)
            {
                if (string.Equals(binding.Mode, mode, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(binding.Kind, kind, StringComparison.OrdinalIgnoreCase)
                    && (string.IsNullOrEmpty(binding.MaterialName) || string.Equals(binding.MaterialName, materialName, StringComparison.Ordinal)))
                    return binding;
            }
            return null;
        }

        private void ApplySegmentCondition(NetInfo.Segment target, string conditionId)
        {
            CatalogCondition condition;
            if (string.IsNullOrEmpty(conditionId)) return;
            if (!_conditions.TryGetValue(conditionId, out condition))
            {
                DiagnosticLog.Warn("DATA_MISSING", "condition_reference_unresolved", "Segment binding references an unknown condition", "condition_id", conditionId);
                return;
            }
            ApplySegmentConditionValues(target, condition.Required, true);
            ApplySegmentConditionValues(target, condition.Forbidden, false);
        }

        private void ApplySegmentConditionValues(NetInfo.Segment target, NamedValue[] values, bool required)
        {
            if (values == null) return;
            foreach (NamedValue value in values)
            {
                string[] flags = DecodeJsonStrings(value.ValueJson);
                if (flags.Length == 0) continue;
                string combined = string.Join(",", flags);
                if (string.Equals(value.Name, "vanilla.segment.forward", StringComparison.OrdinalIgnoreCase))
                {
                    NetSegment.Flags parsed = (NetSegment.Flags)Enum.Parse(typeof(NetSegment.Flags), combined, true);
                    if (required) target.m_forwardRequired |= parsed; else target.m_forwardForbidden |= parsed;
                }
                else if (string.Equals(value.Name, "vanilla.segment.backward", StringComparison.OrdinalIgnoreCase))
                {
                    NetSegment.Flags parsed = (NetSegment.Flags)Enum.Parse(typeof(NetSegment.Flags), combined, true);
                    if (required) target.m_backwardRequired |= parsed; else target.m_backwardForbidden |= parsed;
                }
                else if (_unsupportedConditionNamespaces.Add(value.Name))
                    DiagnosticLog.Warn("MOD_CONTRACT", "condition_namespace_unsupported", "Condition is preserved in data but this runtime has no adapter for its namespace", "condition_name", value.Name ?? string.Empty);
            }
        }

        private void ApplyNodeCondition(NetInfo.Node target, string conditionId)
        {
            CatalogCondition condition;
            if (string.IsNullOrEmpty(conditionId)) return;
            if (!_conditions.TryGetValue(conditionId, out condition))
            {
                DiagnosticLog.Warn("DATA_MISSING", "condition_reference_unresolved", "Node binding references an unknown condition", "condition_id", conditionId);
                return;
            }
            ApplyNodeConditionValues(target, condition.Required, true);
            ApplyNodeConditionValues(target, condition.Forbidden, false);
        }

        private void ApplyNodeConditionValues(NetInfo.Node target, NamedValue[] values, bool required)
        {
            if (values == null) return;
            foreach (NamedValue value in values)
            {
                string[] flags = DecodeJsonStrings(value.ValueJson);
                if (flags.Length == 0) continue;
                if (string.Equals(value.Name, "vanilla.node", StringComparison.OrdinalIgnoreCase))
                {
                    NetNode.Flags parsed = (NetNode.Flags)Enum.Parse(typeof(NetNode.Flags), string.Join(",", flags), true);
                    if (required) target.m_flagsRequired |= parsed; else target.m_flagsForbidden |= parsed;
                }
                else if (_unsupportedConditionNamespaces.Add(value.Name))
                    DiagnosticLog.Warn("MOD_CONTRACT", "condition_namespace_unsupported", "Condition is preserved in data but this runtime has no adapter for its namespace", "condition_name", value.Name ?? string.Empty);
            }
        }

        private static Mesh BuildMesh(MeshBundle source)
        {
            if (source == null) throw new InvalidDataException("mesh object is missing");
            if (source.Vertices == null || source.Vertices.Length % 3 != 0) throw new InvalidDataException("mesh vertices must be xyz triples");
            int count = source.Vertices.Length / 3;
            Vector3[] vertices = new Vector3[count];
            Vector3[] normals = new Vector3[count];
            Vector2[] uv = new Vector2[count];
            for (int index = 0; index != count; ++index)
            {
                vertices[index] = new Vector3(source.Vertices[index * 3], source.Vertices[index * 3 + 1], source.Vertices[index * 3 + 2]);
                if (source.Normals != null && source.Normals.Length == source.Vertices.Length)
                    normals[index] = new Vector3(source.Normals[index * 3], source.Normals[index * 3 + 1], source.Normals[index * 3 + 2]);
                if (source.Uv != null && source.Uv.Length == count * 2)
                    uv[index] = new Vector2(source.Uv[index * 2], source.Uv[index * 2 + 1]);
            }
            Mesh mesh = new Mesh();
            mesh.name = source.Name;
            mesh.vertices = vertices;
            mesh.uv = uv;
            mesh.triangles = source.Triangles ?? new int[0];
            if (source.Normals != null && source.Normals.Length == source.Vertices.Length) mesh.normals = normals;
            else mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            return mesh;
        }

        private Material BuildMaterial(MaterialBundle source, Material fallback)
        {
            Shader shader = source != null ? Shader.Find(source.Shader) : null;
            if (source != null && !string.IsNullOrEmpty(source.Shader) && shader == null)
                throw new DiagnosticException("CS1_ENVIRONMENT", "material_shader_not_found", "Shader is not available: " + source.Shader);
            Material material = fallback != null ? new Material(fallback) : new Material(shader != null ? shader : Shader.Find("Custom/Net/Road"));
            if (shader != null) material.shader = shader;
            if (source != null)
            {
                material.name = source.Name;
                if (source.Color != null && source.Color.Length >= 4)
                    material.color = new Color(source.Color[0], source.Color[1], source.Color[2], source.Color[3]);
                ApplyTextures(material, source.Textures);
                ApplyPackedTextures(material, source.PackedTextures);
                ApplyMainTextureScale(material, source.MainTextureScale);
            }
            return material;
        }

        private static void ApplyMainTextureScale(Material material, float[] scale)
        {
            if (material == null || scale == null) return;
            if (scale.Length != 2 || scale[0] <= 0f || scale[1] <= 0f
                || float.IsNaN(scale[0]) || float.IsNaN(scale[1])
                || float.IsInfinity(scale[0]) || float.IsInfinity(scale[1]))
                throw new InvalidDataException("main_texture_scale must contain two finite positive values");
            material.mainTextureScale = new Vector2(scale[0], scale[1]);
        }

        private void ApplyTextures(Material material, NamedValue[] textures)
        {
            if (material == null || textures == null) return;
            foreach (NamedValue texture in textures)
            {
                string path = DecodeJsonString(texture.ValueJson);
                if (!string.IsNullOrEmpty(path) && !Path.IsPathRooted(path)) path = SafePreviewPath(path);
                if (string.IsNullOrEmpty(path))
                {
                    DiagnosticLog.Warn("DATA_MISSING", "texture_path_missing", "Texture binding has no path", "property", texture.Name ?? string.Empty);
                    continue;
                }
                if (!File.Exists(path))
                {
                    DiagnosticLog.Warn("DATA_MISSING", "texture_file_missing", "Texture file does not exist", "property", texture.Name ?? string.Empty, "path", path);
                    continue;
                }
                Texture2D image = LoadTexture(path, texture.Name);
                material.SetTexture(texture.Name, image);
            }
        }

        private Texture2D LoadTexture(string path, string propertyName)
        {
            Texture2D image;
            if (_textures.TryGetValue(path, out image)) return image;
            image = new Texture2D(2, 2, TextureFormat.ARGB32, true);
            image.name = Path.GetFileNameWithoutExtension(path);
            if (!image.LoadImage(File.ReadAllBytes(path)))
                throw new InvalidDataException("Texture image could not be decoded: " + path);
            image.wrapMode = TextureWrapMode.Repeat;
            _textures[path] = image;
            DiagnosticLog.Info("SUCCESS", "texture_loaded", "Runtime texture was loaded into the shared cache", "property", propertyName ?? string.Empty, "path", path, "width", image.width.ToString(), "height", image.height.ToString());
            return image;
        }

        private void ApplyPackedTextures(Material material, PackedTextureBundle[] definitions)
        {
            if (material == null || definitions == null) return;
            foreach (PackedTextureBundle definition in definitions)
            {
                if (definition == null || string.IsNullOrEmpty(definition.Name)
                    || string.IsNullOrEmpty(definition.Packing)) continue;
                Dictionary<string, Texture2D> sources = new Dictionary<string, Texture2D>(StringComparer.OrdinalIgnoreCase);
                List<string> keyParts = new List<string>();
                foreach (NamedValue source in definition.Sources ?? new NamedValue[0])
                {
                    string path = DecodeJsonString(source.ValueJson);
                    if (!string.IsNullOrEmpty(path) && !Path.IsPathRooted(path)) path = SafePreviewPath(path);
                    if (string.IsNullOrEmpty(path) || !File.Exists(path))
                    {
                        DiagnosticLog.Warn("DATA_MISSING", "packed_texture_source_missing", "Packed texture source file does not exist", "property", definition.Name, "packing", definition.Packing, "source", source.Name ?? string.Empty, "path", path ?? string.Empty);
                        continue;
                    }
                    sources[source.Name] = LoadTexture(path, definition.Name + "." + source.Name);
                    keyParts.Add(source.Name + "=" + path);
                }
                if (sources.Count == 0) continue;
                keyParts.Sort(StringComparer.Ordinal);
                string key = definition.Packing + "|" + string.Join("|", keyParts.ToArray());
                PackedTextureState state;
                if (!_packedTextures.TryGetValue(key, out state))
                {
                    state = new PackedTextureState { Packing = definition.Packing, Sources = sources };
                    RebuildPackedTexture(state);
                    state.Texture.name = definition.Packing.ToLowerInvariant() + "_runtime";
                    _packedTextures[key] = state;
                    DiagnosticLog.Info("SUCCESS", "packed_texture_created", "CS1 runtime texture channels were packed", "property", definition.Name, "packing", definition.Packing, "source_count", sources.Count.ToString(), "width", state.Texture.width.ToString(), "height", state.Texture.height.ToString());
                }
                material.SetTexture(definition.Name, state.Texture);
            }
        }

        private static void RebuildPackedTexture(PackedTextureState state)
        {
            int width = 0;
            int height = 0;
            foreach (Texture2D source in state.Sources.Values)
            {
                if (width == 0)
                {
                    width = source.width;
                    height = source.height;
                }
                else if (source.width != width || source.height != height)
                    throw new InvalidDataException("Packed texture sources must have identical dimensions");
            }
            if (width == 0 || height == 0) throw new InvalidDataException("Packed texture has no sources");
            Color32[] alpha = SourcePixels(state, "a");
            Color32[] pavement = SourcePixels(state, "p");
            Color32[] road = SourcePixels(state, "r");
            Color32[] normal = SourcePixels(state, "n");
            Color32[] specular = SourcePixels(state, "s");
            Color32[] pixels;
            if (string.Equals(state.Packing, "APR", StringComparison.Ordinal))
                pixels = PackAprPixels(alpha, pavement, road, width * height);
            else if (string.Equals(state.Packing, "XYS", StringComparison.Ordinal))
                pixels = PackXysPixels(normal, specular, width * height);
            else throw new InvalidDataException("Unsupported packed texture kind: " + state.Packing);
            if (state.Texture == null)
                state.Texture = new Texture2D(width, height, TextureFormat.ARGB32, true);
            else if (state.Texture.width != width || state.Texture.height != height)
                throw new InvalidDataException("Packed texture dimensions changed during hot reload");
            state.Texture.SetPixels32(pixels);
            state.Texture.Apply(true, false);
            state.Texture.wrapMode = TextureWrapMode.Repeat;
        }

        private static Color32[] SourcePixels(PackedTextureState state, string name)
        {
            Texture2D source;
            return state.Sources.TryGetValue(name, out source) ? source.GetPixels32() : null;
        }

        private static Color32[] PackAprPixels(Color32[] alpha, Color32[] pavement, Color32[] road, int length)
        {
            Color32[] result = new Color32[length];
            for (int index = 0; index != length; ++index)
                result[index] = new Color32(
                    (byte)(255 - (alpha == null ? 255 : alpha[index].r)),
                    (byte)(255 - (pavement == null ? 0 : pavement[index].r)),
                    road == null ? (byte)0 : road[index].r,
                    255);
            return result;
        }

        private static Color32[] PackXysPixels(Color32[] normal, Color32[] specular, int length)
        {
            Color32[] result = new Color32[length];
            for (int index = 0; index != length; ++index)
                result[index] = new Color32(
                    normal == null ? (byte)128 : normal[index].r,
                    normal == null ? (byte)128 : normal[index].g,
                    (byte)(255 - (specular == null ? 0 : specular[index].r)),
                    255);
            return result;
        }

        private static string DecodeJsonString(string json)
        {
            if (string.IsNullOrEmpty(json)) return null;
            return JsonFiles.ReadValue<string>(json);
        }

        private static string[] DecodeJsonStrings(string json)
        {
            if (string.IsNullOrEmpty(json)) return new string[0];
            return JsonFiles.ReadValue<string[]>(json);
        }

        private static void ApplyMaterialProperties(Material material, NamedValue[] properties)
        {
            if (material == null || properties == null) return;
            foreach (NamedValue property in properties)
            {
                string json = property.ValueJson == null ? string.Empty : property.ValueJson.Trim();
                if (json.StartsWith("[", StringComparison.Ordinal))
                {
                    float[] values = JsonFiles.ReadValue<float[]>(json);
                    if (values.Length == 4) material.SetVector(property.Name, new Vector4(values[0], values[1], values[2], values[3]));
                    else if (values.Length == 2) material.SetVector(property.Name, new Vector4(values[0], values[1], 0f, 0f));
                    else throw new InvalidDataException("material vector property must have 2 or 4 values: " + property.Name);
                }
                else
                {
                    float value;
                    if (string.Equals(json, "true", StringComparison.OrdinalIgnoreCase)) value = 1f;
                    else if (string.Equals(json, "false", StringComparison.OrdinalIgnoreCase)) value = 0f;
                    else value = float.Parse(json, System.Globalization.CultureInfo.InvariantCulture);
                    material.SetFloat(property.Name, value);
                }
            }
        }

        private static NetInfo FindOrCloneNet(string name, NetInfo template, string collection)
        {
            NetInfo existing = PrefabCollection<NetInfo>.FindLoaded(name);
            if (existing != null) return existing;
            GameObject clone = UnityEngine.Object.Instantiate(template.gameObject) as GameObject;
            clone.name = name;
            NetInfo info = clone.GetComponent<NetInfo>();
            PrefabCollection<NetInfo>.InitializePrefabs("RoadRuntimeHost." + collection, info, null);
            PrefabCollection<NetInfo>.BindPrefabs();
            return info;
        }

        private string SafePreviewPath(string relative)
        {
            if (Path.IsPathRooted(relative) || relative.Contains("..")) throw new InvalidDataException("unsafe preview-relative path: " + relative);
            string root = Path.GetFullPath(_previewPath) + Path.DirectorySeparatorChar;
            string path = Path.GetFullPath(Path.Combine(root, relative));
            if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("path escapes preview root: " + relative);
            return path;
        }

        private static string ModeName(string name, string mode)
        {
            return string.Equals(mode, "basic", StringComparison.OrdinalIgnoreCase) ? name : name + " [" + mode + "]";
        }

        private static NetInfo ModeTemplate(NetInfo basic, string mode)
        {
            if (string.Equals(mode, "basic", StringComparison.OrdinalIgnoreCase)) return basic;
            RoadAI ai = basic.m_netAI as RoadAI;
            if (ai == null) return null;
            if (string.Equals(mode, "elevated", StringComparison.OrdinalIgnoreCase)) return ai.m_elevatedInfo;
            if (string.Equals(mode, "bridge", StringComparison.OrdinalIgnoreCase)) return ai.m_bridgeInfo;
            if (string.Equals(mode, "slope", StringComparison.OrdinalIgnoreCase)) return ai.m_slopeInfo;
            if (string.Equals(mode, "tunnel", StringComparison.OrdinalIgnoreCase)) return ai.m_tunnelInfo;
            return null;
        }

        private static void LinkModes(NetInfo basic, Dictionary<string, NetInfo> modes)
        {
            RoadAI ai = basic.m_netAI as RoadAI;
            if (ai == null) return;
            NetInfo value;
            if (modes.TryGetValue("elevated", out value)) ai.m_elevatedInfo = value;
            if (modes.TryGetValue("bridge", out value)) ai.m_bridgeInfo = value;
            if (modes.TryGetValue("slope", out value)) ai.m_slopeInfo = value;
            if (modes.TryGetValue("tunnel", out value)) ai.m_tunnelInfo = value;
        }

        private static void RefreshExistingInstances(IEnumerable<NetInfo> infos)
        {
            List<NetInfo> targets = new List<NetInfo>(infos);
            SimulationManager.instance.AddAction(delegate
            {
                try
                {
                    int segmentCount = 0;
                    int nodeCount = 0;
                    NetManager manager = NetManager.instance;
                    for (ushort id = 1; id < manager.m_segments.m_size; ++id)
                    {
                        NetInfo info = manager.m_segments.m_buffer[id].Info;
                        if (targets.Contains(info)) { manager.UpdateSegmentRenderer(id, true); ++segmentCount; }
                    }
                    for (ushort id = 1; id < manager.m_nodes.m_size; ++id)
                    {
                        NetInfo info = manager.m_nodes.m_buffer[id].Info;
                        if (targets.Contains(info)) { manager.UpdateNodeRenderer(id, true); ++nodeCount; }
                    }
                    DiagnosticLog.Info("SUCCESS", "existing_instances_refreshed", "Existing road renderers were marked for refresh without deleting the roads", "segment_count", segmentCount.ToString(), "node_count", nodeCount.ToString());
                }
                catch (Exception error) { DiagnosticLog.Error("CS1_ENVIRONMENT", "existing_instances_refresh_failed", "Existing road renderers could not be refreshed", error, "prefab_count", targets.Count.ToString()); }
            });
        }

        private static NetInfo.Direction ParseDirection(string value)
        {
            return (NetInfo.Direction)Enum.Parse(typeof(NetInfo.Direction), NormalizeEnum(value), true);
        }

        private static NetInfo.LaneType ParseLaneType(string value)
        {
            return (NetInfo.LaneType)Enum.Parse(typeof(NetInfo.LaneType), NormalizeEnum(value), true);
        }

        private static VehicleInfo.VehicleType ParseVehicleType(string value)
        {
            string normalized = NormalizeEnum(value);
            if (string.Equals(normalized, "None", StringComparison.OrdinalIgnoreCase)) return VehicleInfo.VehicleType.None;
            return (VehicleInfo.VehicleType)Enum.Parse(typeof(VehicleInfo.VehicleType), normalized, true);
        }

        private static string NormalizeEnum(string value)
        {
            if (string.IsNullOrEmpty(value)) return "None";
            return value.Replace("_", string.Empty).Replace(" ", string.Empty);
        }

        private static void SetUiCategory(PrefabInfo info, string category)
        {
            FieldInfo field = typeof(PrefabInfo).GetField("m_UICategory", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (field != null) field.SetValue(info, category);
        }

        private static NetInfo.Segment CopySegment(NetInfo.Segment source)
        {
            NetInfo.Segment value = new NetInfo.Segment();
            value.m_forwardRequired = source.m_forwardRequired;
            value.m_forwardRequired2 = source.m_forwardRequired2;
            value.m_forwardForbidden = source.m_forwardForbidden;
            value.m_forwardForbidden2 = source.m_forwardForbidden2;
            value.m_backwardRequired = source.m_backwardRequired;
            value.m_backwardRequired2 = source.m_backwardRequired2;
            value.m_backwardForbidden = source.m_backwardForbidden;
            value.m_backwardForbidden2 = source.m_backwardForbidden2;
            value.m_emptyTransparent = source.m_emptyTransparent;
            value.m_disableBendNodes = source.m_disableBendNodes;
            value.m_lodMesh = source.m_lodMesh;
            value.m_lodMaterial = source.m_lodMaterial;
            value.m_lodRenderDistance = source.m_lodRenderDistance;
            value.m_requireSurfaceMaps = source.m_requireSurfaceMaps;
            value.m_requireHeightMap = source.m_requireHeightMap;
            value.m_requireWindSpeed = source.m_requireWindSpeed;
            value.m_preserveUVs = source.m_preserveUVs;
            value.m_generateTangents = source.m_generateTangents;
            value.m_layer = source.m_layer;
            return value;
        }

        private static NetInfo.Node CopyNode(NetInfo.Node source)
        {
            NetInfo.Node value = new NetInfo.Node();
            value.m_flagsRequired = source.m_flagsRequired;
            value.m_flagsRequired2 = source.m_flagsRequired2;
            value.m_flagsForbidden = source.m_flagsForbidden;
            value.m_flagsForbidden2 = source.m_flagsForbidden2;
            value.m_connectGroup = source.m_connectGroup;
            value.m_directConnect = source.m_directConnect;
            value.m_emptyTransparent = source.m_emptyTransparent;
            value.m_lodMesh = source.m_lodMesh;
            value.m_lodMaterial = source.m_lodMaterial;
            value.m_tagsRequired = source.m_tagsRequired;
            value.m_tagsForbidden = source.m_tagsForbidden;
            value.m_forbidAnyTags = source.m_forbidAnyTags;
            value.m_minSameTags = source.m_minSameTags;
            value.m_maxSameTags = source.m_maxSameTags;
            value.m_minOtherTags = source.m_minOtherTags;
            value.m_maxOtherTags = source.m_maxOtherTags;
            value.m_lodRenderDistance = source.m_lodRenderDistance;
            value.m_requireSurfaceMaps = source.m_requireSurfaceMaps;
            value.m_requireWindSpeed = source.m_requireWindSpeed;
            value.m_preserveUVs = source.m_preserveUVs;
            value.m_generateTangents = source.m_generateTangents;
            value.m_layer = source.m_layer;
            return value;
        }
    }
}
