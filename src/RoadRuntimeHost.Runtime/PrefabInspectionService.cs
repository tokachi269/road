using System;
using System.Globalization;
using System.IO;
using System.Text;

namespace RoadRuntimeHost.Runtime
{
    internal sealed class PrefabInspectionRequest
    {
        public int SchemaVersion;
        public string RequestId;
        public string Command;
        public string NameContains;
        public string PrefabName;
        public string Section;
        public int LaneIndex;
        public int Offset;
        public int Limit;
    }

    internal sealed class PrefabInspectionService
    {
        internal const int MaxRequestBytes = 16 * 1024;
        internal const int MaxResultCount = 20;
        internal const int MaxResponseBytes = 64 * 1024;

        private readonly string _requestPath;
        private readonly string _responsePath;
        private string _processedVersion;

        public PrefabInspectionService(string previewPath)
        {
            _requestPath = Path.Combine(previewPath, "inspect.request.json");
            _responsePath = Path.Combine(previewPath, "inspect.response.json");
        }

        public void Poll(bool force)
        {
            if (!File.Exists(_requestPath)) return;
            string version = FileVersion(_requestPath);
            if (!force && string.Equals(version, _processedVersion, StringComparison.Ordinal)) return;
            _processedVersion = version;

            string requestId = string.Empty;
            try
            {
                FileInfo file = new FileInfo(_requestPath);
                if (file.Length > MaxRequestBytes) throw new InvalidDataException("inspection request exceeds 16 KiB");
                PrefabInspectionRequest request = JsonFiles.Read<PrefabInspectionRequest>(_requestPath);
                requestId = request == null ? string.Empty : request.RequestId ?? string.Empty;
                WriteResponse(Execute(request), requestId);
                DiagnosticLog.Info("SUCCESS", "prefab_inspection_completed", "Prefab inspection request completed", "request_id", requestId, "command", request == null ? string.Empty : request.Command ?? string.Empty);
            }
            catch (Exception error)
            {
                WriteResponse(ErrorResponse(requestId, "invalid_request", error.Message), requestId);
                DiagnosticLog.Error(DiagnosticLog.Classify(error), "prefab_inspection_failed", "Prefab inspection request failed; it will not be retried until the request file changes", error, "request_id", requestId, "request_path", _requestPath);
            }
        }

        internal static string ValidateRequestForSmoke(string json)
        {
            PrefabInspectionRequest request = JsonFiles.ReadValue<PrefabInspectionRequest>(json);
            ValidateRequest(request);
            return request.Command + "|" + NormalizeLimit(request.Limit);
        }

        private static string Execute(PrefabInspectionRequest request)
        {
            ValidateRequest(request);
            return request.Command == "find_net" ? FindNet(request) : InspectNet(request);
        }

        private static void ValidateRequest(PrefabInspectionRequest request)
        {
            if (request == null) throw new InvalidDataException("inspection request is missing");
            if (request.SchemaVersion != 1) throw new InvalidDataException("unsupported inspection request schema");
            if (string.IsNullOrEmpty(request.RequestId) || request.RequestId.Length > 128) throw new InvalidDataException("request_id is required and must be at most 128 characters");
            if (request.Offset < 0) throw new InvalidDataException("offset must not be negative");
            if (request.Command == "find_net")
            {
                if ((request.NameContains ?? string.Empty).Length > 256) throw new InvalidDataException("name_contains must be at most 256 characters");
                return;
            }
            if (request.Command != "inspect_net") throw new InvalidDataException("command must be find_net or inspect_net; bulk dump is not supported");
            if (string.IsNullOrEmpty(request.PrefabName) || request.PrefabName.Length > 512) throw new InvalidDataException("prefab_name is required and must be at most 512 characters");
            string section = request.Section ?? string.Empty;
            if (section != "summary" && section != "lanes" && section != "lane_props" && section != "segments" && section != "nodes") throw new InvalidDataException("section must be summary, lanes, lane_props, segments, or nodes");
            if (section == "lane_props" && request.LaneIndex < 0) throw new InvalidDataException("lane_index must not be negative");
        }

        private static string FindNet(PrefabInspectionRequest request)
        {
            string filter = request.NameContains ?? string.Empty;
            int limit = NormalizeLimit(request.Limit);
            StringBuilder items = new StringBuilder();
            int skipped = 0;
            int matched = 0;
            bool truncated = false;
            int count = PrefabCollection<NetInfo>.LoadedCount();
            for (int index = 0; index < count; ++index)
            {
                NetInfo info = PrefabCollection<NetInfo>.GetLoaded((uint)index);
                if (info == null || info.name == null || info.name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0) continue;
                if (skipped < request.Offset) { ++skipped; continue; }
                if (matched == limit) { truncated = true; break; }
                if (matched != 0) items.Append(',');
                items.Append('{');
                JsonProperty(items, "name", info.name, false);
                JsonProperty(items, "lane_count", info.m_lanes == null ? 0 : info.m_lanes.Length, true);
                items.Append('}');
                ++matched;
            }
            return OkResponse(request, truncated, items, matched);
        }

        private static string InspectNet(PrefabInspectionRequest request)
        {
            NetInfo info = PrefabCollection<NetInfo>.FindLoaded(request.PrefabName);
            if (info == null) return ErrorResponse(request.RequestId, "prefab_not_found", "No loaded NetInfo has the exact requested name");
            if (request.Section == "summary") return Summary(request, info);
            if (request.Section == "lanes") return Lanes(request, info);
            if (request.Section == "lane_props") return LaneProps(request, info);
            if (request.Section == "segments") return Segments(request, info);
            return Nodes(request, info);
        }

        private static string Summary(PrefabInspectionRequest request, NetInfo info)
        {
            StringBuilder item = new StringBuilder("{");
            JsonProperty(item, "name", info.name, false);
            JsonProperty(item, "half_width", info.m_halfWidth, true);
            JsonProperty(item, "pavement_width", info.m_pavementWidth, true);
            JsonProperty(item, "min_corner_offset", info.m_minCornerOffset, true);
            JsonProperty(item, "max_corner_offset", info.m_maxCornerOffset, true);
            JsonProperty(item, "segment_length", info.m_segmentLength, true);
            JsonProperty(item, "max_slope", info.m_maxSlope, true);
            JsonProperty(item, "max_build_angle", info.m_maxBuildAngle, true);
            JsonProperty(item, "max_turn_angle", info.m_maxTurnAngle, true);
            JsonProperty(item, "max_prop_distance", info.m_maxPropDistance, true);
            JsonProperty(item, "lane_count", info.m_lanes == null ? 0 : info.m_lanes.Length, true);
            JsonProperty(item, "segment_entry_count", info.m_segments == null ? 0 : info.m_segments.Length, true);
            JsonProperty(item, "node_entry_count", info.m_nodes == null ? 0 : info.m_nodes.Length, true);
            item.Append('}');
            return OkResponse(request, false, item, 1);
        }

        private static string Lanes(PrefabInspectionRequest request, NetInfo info)
        {
            NetInfo.Lane[] source = info.m_lanes ?? new NetInfo.Lane[0];
            return PagedResponse(request, source.Length, delegate(int index, StringBuilder item)
            {
                NetInfo.Lane lane = source[index];
                JsonProperty(item, "index", index, false);
                JsonProperty(item, "position", lane.m_position, true);
                JsonProperty(item, "width", lane.m_width, true);
                JsonProperty(item, "vertical_offset", lane.m_verticalOffset, true);
                JsonProperty(item, "stop_offset", lane.m_stopOffset, true);
                JsonProperty(item, "speed_limit", lane.m_speedLimit, true);
                JsonProperty(item, "direction", lane.m_direction.ToString(), true);
                JsonProperty(item, "final_direction", lane.m_finalDirection.ToString(), true);
                JsonProperty(item, "lane_type", lane.m_laneType.ToString(), true);
                JsonProperty(item, "vehicle_type", lane.m_vehicleType.ToString(), true);
                JsonProperty(item, "stop_type", lane.m_stopType.ToString(), true);
                JsonProperty(item, "allow_connect", lane.m_allowConnect, true);
                JsonProperty(item, "use_terrain_height", lane.m_useTerrainHeight, true);
                JsonProperty(item, "center_platform", lane.m_centerPlatform, true);
                JsonProperty(item, "elevated", lane.m_elevated, true);
                JsonProperty(item, "prop_count", lane.m_laneProps == null || lane.m_laneProps.m_props == null ? 0 : lane.m_laneProps.m_props.Length, true);
            });
        }

        private static string LaneProps(PrefabInspectionRequest request, NetInfo info)
        {
            NetInfo.Lane[] lanes = info.m_lanes ?? new NetInfo.Lane[0];
            if (request.LaneIndex >= lanes.Length) return ErrorResponse(request.RequestId, "lane_not_found", "lane_index is outside the requested prefab's lane array");
            NetLaneProps.Prop[] source = lanes[request.LaneIndex].m_laneProps == null || lanes[request.LaneIndex].m_laneProps.m_props == null ? new NetLaneProps.Prop[0] : lanes[request.LaneIndex].m_laneProps.m_props;
            return PagedResponse(request, source.Length, delegate(int index, StringBuilder item)
            {
                NetLaneProps.Prop prop = source[index];
                JsonProperty(item, "index", index, false);
                JsonProperty(item, "prop", prop.m_prop == null ? string.Empty : prop.m_prop.name, true);
                JsonProperty(item, "final_prop", prop.m_finalProp == null ? string.Empty : prop.m_finalProp.name, true);
                JsonProperty(item, "max_render_distance", prop.m_finalProp == null ? 0f : prop.m_finalProp.m_maxRenderDistance, true);
                JsonProperty(item, "tree", prop.m_tree == null ? string.Empty : prop.m_tree.name, true);
                JsonProperty(item, "position_x", prop.m_position.x, true);
                JsonProperty(item, "position_y", prop.m_position.y, true);
                JsonProperty(item, "position_z", prop.m_position.z, true);
                JsonProperty(item, "angle", prop.m_angle, true);
                JsonProperty(item, "segment_offset", prop.m_segmentOffset, true);
                JsonProperty(item, "repeat_distance", prop.m_repeatDistance, true);
                JsonProperty(item, "min_length", prop.m_minLength, true);
                JsonProperty(item, "corner_angle", prop.m_cornerAngle, true);
                JsonProperty(item, "probability", prop.m_probability, true);
                JsonProperty(item, "lane_flags_required", prop.m_flagsRequired.ToString(), true);
                JsonProperty(item, "lane_flags_forbidden", prop.m_flagsForbidden.ToString(), true);
                JsonProperty(item, "start_node_flags_required", prop.m_startFlagsRequired.ToString(), true);
                JsonProperty(item, "start_node_flags_forbidden", prop.m_startFlagsForbidden.ToString(), true);
                JsonProperty(item, "end_node_flags_required", prop.m_endFlagsRequired.ToString(), true);
                JsonProperty(item, "end_node_flags_forbidden", prop.m_endFlagsForbidden.ToString(), true);
            });
        }

        private static string Segments(PrefabInspectionRequest request, NetInfo info)
        {
            NetInfo.Segment[] source = info.m_segments ?? new NetInfo.Segment[0];
            return PagedResponse(request, source.Length, delegate(int index, StringBuilder item)
            {
                NetInfo.Segment entry = source[index];
                JsonProperty(item, "index", index, false);
                JsonProperty(item, "mesh", entry.m_mesh == null ? string.Empty : entry.m_mesh.name, true);
                JsonProperty(item, "material", entry.m_material == null ? string.Empty : entry.m_material.name, true);
                JsonProperty(item, "forward_required", entry.m_forwardRequired.ToString(), true);
                JsonProperty(item, "forward_required2", entry.m_forwardRequired2.ToString(), true);
                JsonProperty(item, "forward_forbidden", entry.m_forwardForbidden.ToString(), true);
                JsonProperty(item, "forward_forbidden2", entry.m_forwardForbidden2.ToString(), true);
                JsonProperty(item, "backward_required", entry.m_backwardRequired.ToString(), true);
                JsonProperty(item, "backward_required2", entry.m_backwardRequired2.ToString(), true);
                JsonProperty(item, "backward_forbidden", entry.m_backwardForbidden.ToString(), true);
                JsonProperty(item, "backward_forbidden2", entry.m_backwardForbidden2.ToString(), true);
                JsonProperty(item, "empty_transparent", entry.m_emptyTransparent, true);
                JsonProperty(item, "disable_bend_nodes", entry.m_disableBendNodes, true);
                JsonProperty(item, "preserve_uvs", entry.m_preserveUVs, true);
                JsonProperty(item, "generate_tangents", entry.m_generateTangents, true);
            });
        }

        private static string Nodes(PrefabInspectionRequest request, NetInfo info)
        {
            NetInfo.Node[] source = info.m_nodes ?? new NetInfo.Node[0];
            return PagedResponse(request, source.Length, delegate(int index, StringBuilder item)
            {
                NetInfo.Node entry = source[index];
                JsonProperty(item, "index", index, false);
                JsonProperty(item, "mesh", entry.m_mesh == null ? string.Empty : entry.m_mesh.name, true);
                JsonProperty(item, "material", entry.m_material == null ? string.Empty : entry.m_material.name, true);
                JsonProperty(item, "flags_required", entry.m_flagsRequired.ToString(), true);
                JsonProperty(item, "flags_required2", entry.m_flagsRequired2.ToString(), true);
                JsonProperty(item, "flags_forbidden", entry.m_flagsForbidden.ToString(), true);
                JsonProperty(item, "flags_forbidden2", entry.m_flagsForbidden2.ToString(), true);
                JsonProperty(item, "connect_group", entry.m_connectGroup.ToString(), true);
                JsonProperty(item, "direct_connect", entry.m_directConnect, true);
                JsonProperty(item, "empty_transparent", entry.m_emptyTransparent, true);
                JsonProperty(item, "tags_required", entry.m_tagsRequired ?? new string[0], true);
                JsonProperty(item, "tags_forbidden", entry.m_tagsForbidden ?? new string[0], true);
                JsonProperty(item, "forbid_any_tags", entry.m_forbidAnyTags, true);
            });
        }

        private delegate void ItemWriter(int index, StringBuilder item);

        private static string PagedResponse(PrefabInspectionRequest request, int total, ItemWriter writeItem)
        {
            int start = Math.Min(request.Offset, total);
            int end = Math.Min(total, start + NormalizeLimit(request.Limit));
            StringBuilder items = new StringBuilder();
            for (int index = start; index < end; ++index)
            {
                if (index != start) items.Append(',');
                items.Append('{');
                writeItem(index, items);
                items.Append('}');
            }
            return OkResponse(request, end < total, items, end - start);
        }

        private static int NormalizeLimit(int value)
        {
            return value <= 0 ? MaxResultCount : Math.Min(value, MaxResultCount);
        }

        private static string OkResponse(PrefabInspectionRequest request, bool truncated, StringBuilder items, int count)
        {
            StringBuilder json = ResponsePrefix(request.RequestId, "ok");
            JsonProperty(json, "command", request.Command, true);
            JsonProperty(json, "section", request.Section ?? string.Empty, true);
            JsonProperty(json, "count", count, true);
            JsonProperty(json, "truncated", truncated, true);
            json.Append(",\"items\":[").Append(items).Append("]}");
            return json.ToString();
        }

        private static string ErrorResponse(string requestId, string code, string message)
        {
            StringBuilder json = ResponsePrefix(requestId, "error");
            JsonProperty(json, "error_code", code, true);
            JsonProperty(json, "message", message ?? string.Empty, true);
            json.Append('}');
            return json.ToString();
        }

        private static StringBuilder ResponsePrefix(string requestId, string status)
        {
            StringBuilder json = new StringBuilder("{");
            JsonProperty(json, "schema_version", 1, false);
            JsonProperty(json, "request_id", requestId ?? string.Empty, true);
            JsonProperty(json, "status", status, true);
            return json;
        }

        private void WriteResponse(string json, string requestId)
        {
            if (Encoding.UTF8.GetByteCount(json) > MaxResponseBytes)
                json = ErrorResponse(requestId, "response_too_large", "Inspection response exceeded 64 KiB; narrow the request or page it with offset");
            string temp = _responsePath + ".tmp";
            File.WriteAllText(temp, json, new UTF8Encoding(false));
            if (File.Exists(_responsePath)) File.Delete(_responsePath);
            File.Move(temp, _responsePath);
        }

        private static string FileVersion(string path)
        {
            FileInfo file = new FileInfo(path);
            return file.Exists ? file.Length + "|" + file.LastWriteTimeUtc.Ticks : string.Empty;
        }

        private static void JsonProperty(StringBuilder json, string name, string value, bool comma)
        {
            if (comma) json.Append(',');
            JsonString(json, name); json.Append(':'); JsonString(json, value ?? string.Empty);
        }

        private static void JsonProperty(StringBuilder json, string name, int value, bool comma)
        {
            if (comma) json.Append(',');
            JsonString(json, name); json.Append(':').Append(value.ToString(CultureInfo.InvariantCulture));
        }

        private static void JsonProperty(StringBuilder json, string name, float value, bool comma)
        {
            if (comma) json.Append(',');
            JsonString(json, name); json.Append(':').Append(value.ToString("R", CultureInfo.InvariantCulture));
        }

        private static void JsonProperty(StringBuilder json, string name, bool value, bool comma)
        {
            if (comma) json.Append(',');
            JsonString(json, name); json.Append(value ? ":true" : ":false");
        }

        private static void JsonProperty(StringBuilder json, string name, string[] values, bool comma)
        {
            if (comma) json.Append(',');
            JsonString(json, name); json.Append(":").Append('[');
            for (int index = 0; index < values.Length; ++index)
            {
                if (index != 0) json.Append(',');
                JsonString(json, values[index] ?? string.Empty);
            }
            json.Append(']');
        }

        private static void JsonString(StringBuilder json, string value)
        {
            json.Append('"');
            foreach (char character in value)
            {
                switch (character)
                {
                    case '"': json.Append("\\\""); break;
                    case '\\': json.Append("\\\\"); break;
                    case '\b': json.Append("\\b"); break;
                    case '\f': json.Append("\\f"); break;
                    case '\n': json.Append("\\n"); break;
                    case '\r': json.Append("\\r"); break;
                    case '\t': json.Append("\\t"); break;
                    default:
                        if (character < 32) json.Append("\\u").Append(((int)character).ToString("x4"));
                        else json.Append(character);
                        break;
                }
            }
            json.Append('"');
        }
    }
}
