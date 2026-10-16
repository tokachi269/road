using System;
using System.Reflection;
using System.Reflection.Emit;
using System.IO;

// Source-linked game module; independent of the preview Host assembly.
namespace RoadRuntimeHost.Runtime
{
    // Only the installed AR API is consumed here. No AR assembly reference is
    // required to load the host, and no reflection/boxing occurs per lane/frame.
    internal sealed class AdaptiveRoadsBridge
    {
        internal delegate long ReadFlags(int index);
        internal delegate void WriteFlags(int index, long mask, long value);
        private readonly ReadFlags _readSegment;
        private readonly WriteFlags _writeSegment, _writeEnd;
        private readonly long _bit0, _bit1, _endBit;
        private readonly MethodInfo _ensure, _segmentMetadata, _nodeMetadata, _recalculate;
        public readonly Assembly Assembly;

        public AdaptiveRoadsBridge(Assembly assembly)
        {
            Assembly = assembly;
            Type manager = assembly.GetType("AdaptiveRoads.Manager.NetworkExtensionManager", true);
            PropertyInfo instance = manager.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static);
            FieldInfo segments = manager.GetField("SegmentBuffer"), ends = manager.GetField("SegmentEndBuffer");
            FieldInfo flags = segments.FieldType.GetElementType().GetField("m_flags");
            FieldInfo endFlags = ends.FieldType.GetElementType().GetField("m_flags");
            _bit0 = CustomBit(flags.FieldType, "Custom0"); _bit1 = CustomBit(flags.FieldType, "Custom1");
            _endBit = CustomBit(endFlags.FieldType, "Custom0");
            _readSegment = BuildReader(instance, segments, flags);
            _writeSegment = BuildWriter(instance, segments, flags);
            _writeEnd = BuildWriter(instance, ends, endFlags);
            Type extension = assembly.GetType("AdaptiveRoads.Manager.NetInfoExtionsion", true);
            Type metadata = assembly.GetType("AdaptiveRoads.Manager.Extensions", true);
            _ensure = extension.GetMethod("EnsureExtended", new Type[] { typeof(NetInfo), typeof(bool) });
            _segmentMetadata = metadata.GetMethod("GetOrCreateMetaData", new Type[] { typeof(NetInfo.Segment) });
            _nodeMetadata = metadata.GetMethod("GetOrCreateMetaData", new Type[] { typeof(NetInfo.Node) });
            _recalculate = metadata.GetMethod("RecalculateMetaData", new Type[] { typeof(NetInfo) });
            if (_ensure == null || _segmentMetadata == null || _nodeMetadata == null || _recalculate == null)
                throw new InvalidDataException("Adaptive Roads metadata API is incompatible");
        }

        public static AdaptiveRoadsBridge FindLoaded()
        {
            foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
                if (assembly.GetType("AdaptiveRoads.Manager.NetworkExtensionManager", false) != null) {
                    ColossalFramework.Plugins.PluginManager.PluginInfo plugin = ColossalFramework.Singleton<ColossalFramework.Plugins.PluginManager>
                        .instance.FindPluginInfo(assembly);
                    if (plugin == null || !plugin.isEnabled) return null;
                    return new AdaptiveRoadsBridge(assembly);
                }
            return null;
        }

        private static long CustomBit(Type type, string name)
        {
            if (!type.IsEnum || Enum.GetUnderlyingType(type) != typeof(long))
                throw new InvalidDataException("Adaptive Roads flags must be Int64 enums");
            long value = Convert.ToInt64(Enum.Parse(type, name));
            if (value <= 0 || (value & (value-1)) != 0) throw new InvalidDataException("Invalid AR custom bit");
            return value;
        }

        // Compiled getters fetch the current manager every call. AR replaces
        // its buffers on map load; retaining a previous buffer would leak state.
        internal static ReadFlags BuildReader(PropertyInfo instance, FieldInfo buffer, FieldInfo flags)
        {
            RequireAccess(instance, buffer, flags);
            DynamicMethod method = new DynamicMethod("ReadARFlags", typeof(long), new Type[] { typeof(int) },
                typeof(AdaptiveRoadsBridge).Module, true);
            ILGenerator il = method.GetILGenerator();
            il.Emit(OpCodes.Call, instance.GetGetMethod()); il.Emit(OpCodes.Ldfld, buffer);
            il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Ldelema, buffer.FieldType.GetElementType());
            il.Emit(OpCodes.Ldfld, flags); il.Emit(OpCodes.Conv_I8); il.Emit(OpCodes.Ret);
            return (ReadFlags)method.CreateDelegate(typeof(ReadFlags));
        }
        internal static WriteFlags BuildWriter(PropertyInfo instance, FieldInfo buffer, FieldInfo flags)
        {
            RequireAccess(instance, buffer, flags);
            DynamicMethod method = new DynamicMethod("WriteARFlags", typeof(void),
                new Type[] { typeof(int), typeof(long), typeof(long) }, typeof(AdaptiveRoadsBridge).Module, true);
            ILGenerator il = method.GetILGenerator();
            il.Emit(OpCodes.Call, instance.GetGetMethod()); il.Emit(OpCodes.Ldfld, buffer);
            il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Ldelema, buffer.FieldType.GetElementType());
            il.Emit(OpCodes.Dup); il.Emit(OpCodes.Ldfld, flags);
            il.Emit(OpCodes.Ldarg_1); il.Emit(OpCodes.Not); il.Emit(OpCodes.And);
            il.Emit(OpCodes.Ldarg_2); il.Emit(OpCodes.Ldarg_1); il.Emit(OpCodes.And);
            il.Emit(OpCodes.Or); il.Emit(OpCodes.Stfld, flags); il.Emit(OpCodes.Ret);
            return (WriteFlags)method.CreateDelegate(typeof(WriteFlags));
        }
        private static void RequireAccess(PropertyInfo instance, FieldInfo buffer, FieldInfo flags)
        {
            if (instance == null || instance.GetGetMethod() == null || buffer == null || flags == null
                || !buffer.FieldType.IsArray || !buffer.FieldType.GetElementType().IsValueType
                || Enum.GetUnderlyingType(flags.FieldType) != typeof(long))
                throw new InvalidDataException("Adaptive Roads flag buffer API is incompatible");
        }

        public int ReadCode(ushort segmentId)
        {
            long flags = _readSegment(segmentId);
            return ((flags & _bit0) != 0 ? 1 : 0) | ((flags & _bit1) != 0 ? 2 : 0);
        }
        public void WriteCode(ushort segmentId, int code)
        {
            if (segmentId == 0 || code < 0 || code > 3) throw new InvalidDataException("Invalid individual height state");
            _writeSegment(segmentId, _bit0 | _bit1, ((code & 1) != 0 ? _bit0 : 0) | ((code & 2) != 0 ? _bit1 : 0));
        }
        public void WriteEndpoint(ushort segmentId, bool start, bool flush)
        {
            if (segmentId == 0) throw new InvalidDataException("Invalid segment endpoint");
            _writeEnd(segmentId * 2 + (start ? 0 : 1), _endBit, flush ? _endBit : 0);
        }

        public void Configure(NetInfo info, int[] segmentCodes, bool[] nodeFlush)
        {
            if (segmentCodes.Length != info.m_segments.Length || nodeFlush.Length != info.m_nodes.Length)
                throw new InvalidDataException("Shared height selectors disagree with geometry");
            _ensure.Invoke(null, new object[] { info, false });
            for (int i = 0; i < segmentCodes.Length; ++i)
            {
                int code = segmentCodes[i];
                object metadata = _segmentMetadata.Invoke(null, new object[] { info.m_segments[i] });
                long required = ((code & 1) != 0 ? _bit0 : 0) | ((code & 2) != 0 ? _bit1 : 0);
                SetPair(metadata, "Forward", required, (_bit0 | _bit1) ^ required);
                SetPair(metadata, "Backward", required, (_bit0 | _bit1) ^ required);
            }
            for (int i = 0; i < nodeFlush.Length; ++i)
            {
                object metadata = _nodeMetadata.Invoke(null, new object[] { info.m_nodes[i] });
                SetPair(metadata, "SegmentEndFlags", nodeFlush[i] ? _endBit : 0, nodeFlush[i] ? 0 : _endBit);
            }
            _recalculate.Invoke(null, new object[] { info });
        }
        private static void SetPair(object metadata, string name, long required, long forbidden)
        {
            FieldInfo field = metadata.GetType().GetField(name);
            object pair = field.GetValue(metadata);
            FieldInfo r = pair.GetType().GetField("Required"), f = pair.GetType().GetField("Forbidden");
            r.SetValue(pair, Enum.ToObject(r.FieldType, required)); f.SetValue(pair, Enum.ToObject(f.FieldType, forbidden));
            field.SetValue(metadata, pair);
        }
    }
}
