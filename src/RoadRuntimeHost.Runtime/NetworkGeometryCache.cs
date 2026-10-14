using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using UnityEngine;

namespace RoadRuntimeHost.Runtime
{
    // Only Runtime-owned network meshes. Native NetInfo initialization mutates
    // their bounds, so equal vertex buffers alone are not a sharing contract.
    // Materials and prop meshes do not belong to this pool.
    internal sealed class NetworkGeometryCache
    {
        private readonly Dictionary<string, Mesh> _meshes = new Dictionary<string, Mesh>(StringComparer.Ordinal);
        private readonly Func<MeshBundle, Mesh> _create;
        public int Count { get { return _meshes.Count; } }

        public NetworkGeometryCache(Func<MeshBundle, Mesh> create)
        {
            if (create == null) throw new ArgumentNullException("create");
            _create = create;
        }

        public Mesh Get(MeshBundle source, bool node, float minHeight, float maxHeight, float maxSlope)
        {
            string key = Key(source, node, minHeight, maxHeight, maxSlope);
            Mesh mesh;
            if (_meshes.TryGetValue(key, out mesh)) return mesh;
            mesh = _create(source);
            if (ReferenceEquals(mesh, null)) throw new InvalidDataException("Network mesh creation returned no resource");
            _meshes.Add(key, mesh);
            return mesh;
        }

        private static string Key(MeshBundle source, bool node, float minHeight, float maxHeight, float maxSlope)
        {
            if (source == null) throw new ArgumentNullException("source");
            if (!Finite(minHeight) || !Finite(maxHeight) || !Finite(maxSlope))
                throw new InvalidDataException("Native network bounds inputs must be finite");
            using (SHA256 hash = SHA256.Create())
            {
                // Stream into the hash, not a second full mesh byte buffer.
                using (CryptoStream stream = new CryptoStream(Stream.Null, hash, CryptoStreamMode.Write))
                {
                    BinaryWriter writer = new BinaryWriter(stream);
                    writer.Write("rr-network-mesh-v1");
                    writer.Write(node); writer.Write(minHeight); writer.Write(maxHeight);
                    // InitNodeInfo does not read maxSlope. InitSegmentInfo does.
                    if (!node) writer.Write(maxSlope);
                    Write(writer, source.Vertices); Write(writer, source.Normals); Write(writer, source.Uv);
                    writer.Write(source.Triangles == null ? -1 : source.Triangles.Length);
                    if (source.Triangles != null) foreach (int index in source.Triangles) writer.Write(index);
                    writer.Flush(); stream.FlushFinalBlock();
                    StringBuilder text = new StringBuilder(64);
                    foreach (byte value in hash.Hash) text.Append(value.ToString("x2"));
                    return text.ToString();
                }
            }
        }

        private static void Write(BinaryWriter writer, float[] values)
        {
            writer.Write(values == null ? -1 : values.Length);
            if (values != null) foreach (float value in values) writer.Write(value);
        }
        private static bool Finite(float value) { return !float.IsNaN(value) && !float.IsInfinity(value); }
    }
}
