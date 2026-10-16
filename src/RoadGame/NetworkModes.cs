using System;

namespace RoadRuntimeHost.Runtime
{
    internal static class NetworkModes
    {
        public static NetInfo Find(NetInfo basic, string mode)
        {
            if (basic == null) return null;
            if (string.Equals(mode, "basic", StringComparison.OrdinalIgnoreCase)) return basic;
            RoadAI ai = basic.m_netAI as RoadAI;
            if (ai == null) return null;
            if (string.Equals(mode, "elevated", StringComparison.OrdinalIgnoreCase)) return ai.m_elevatedInfo;
            if (string.Equals(mode, "bridge", StringComparison.OrdinalIgnoreCase)) return ai.m_bridgeInfo;
            if (string.Equals(mode, "slope", StringComparison.OrdinalIgnoreCase)) return ai.m_slopeInfo;
            if (string.Equals(mode, "tunnel", StringComparison.OrdinalIgnoreCase)) return ai.m_tunnelInfo;
            return null;
        }
    }
}
