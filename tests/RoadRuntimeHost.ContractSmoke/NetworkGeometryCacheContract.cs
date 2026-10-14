using System;
using System.Collections;
using System.Reflection;
using System.Runtime.Serialization;
using UnityEngine;

namespace RoadRuntimeHost.ContractSmoke
{
    internal static class NetworkGeometryCacheContract
    {
        private static int _created;
        public static Mesh Factory(object source)
        {
            ++_created;
            return (Mesh)FormatterServices.GetUninitializedObject(typeof(Mesh));
        }

        public static void Validate(Assembly assembly, string root)
        {
            Type cacheType = assembly.GetType("RoadRuntimeHost.Runtime.NetworkGeometryCache", true);
            Type meshType = assembly.GetType("RoadRuntimeHost.Runtime.MeshBundle", true);
            ConstructorInfo constructor = cacheType.GetConstructors()[0];
            Delegate factory = Delegate.CreateDelegate(constructor.GetParameters()[0].ParameterType,
                typeof(NetworkGeometryCacheContract).GetMethod("Factory"));
            object cache = constructor.Invoke(new object[] { factory });
            MethodInfo get = cacheType.GetMethod("Get");
            object mesh = NewMesh(meshType);
            _created = 0;
            object first = get.Invoke(cache, new object[] { mesh, false, -1f, 2f, 0.25f });
            for (int i = 0; i < 100; ++i)
            {
                object equivalent = NewMesh(meshType);
                meshType.GetField("Name").SetValue(equivalent, "another road or height state");
                object same = get.Invoke(cache, new object[] { equivalent, false, -1f, 2f, 0.25f });
                if (!ReferenceEquals(first, same)) throw new InvalidOperationException("Identical network geometry allocated another mesh");
            }
            if (_created != 1) throw new InvalidOperationException("Mesh generation grew on repeated requests");
            // Every independently meaningful buffer or native bound input must
            // split the resource. No approximate float comparison or welding.
            foreach (string field in new string[] { "Vertices", "Normals", "Uv" })
            {
                object changed = NewMesh(meshType);
                float[] values = (float[])meshType.GetField(field).GetValue(changed);
                values[0] += 0.01f;
                RequireDifferent(get, cache, changed, first, false, -1f, 2f, 0.25f);
            }
            object triangles = NewMesh(meshType);
            meshType.GetField("Triangles").SetValue(triangles, new int[] { 0, 2, 1 });
            RequireDifferent(get, cache, triangles, first, false, -1f, 2f, 0.25f);
            RequireDifferent(get, cache, mesh, first, true, -1f, 2f, 0.25f);
            RequireDifferent(get, cache, mesh, first, false, -2f, 2f, 0.25f);
            RequireDifferent(get, cache, mesh, first, false, -1f, 3f, 0.25f);
            RequireDifferent(get, cache, mesh, first, false, -1f, 2f, 0.5f);
            object node = get.Invoke(cache, new object[] { mesh, true, -1f, 2f, 0.25f });
            object sameNode = get.Invoke(cache, new object[] { mesh, true, -1f, 2f, 0.5f });
            if (!ReferenceEquals(node, sameNode)) throw new InvalidOperationException("Node mesh split on an unused slope input");
            if (_created != 9) throw new InvalidOperationException("Network geometry resource matrix lost an independent case");

            // Real Blender entries, without comparing against a key generated
            // by production: duplicate each entire input sequence explicitly.
            Type library = assembly.GetType("RoadRuntimeHost.Runtime.PlacementVariantLibrary", true);
            object menu = Activator.CreateInstance(library, new object[] { root, "jp-1x2l", "placement/jp-1x2l/index.json" });
            object family = library.GetMethod("ResolveSharedHeight").Invoke(menu,
                new object[] { "NARROW|CENTERED|NONE|NONE|DEPRESSED" });
            cache = constructor.Invoke(new object[] { factory }); _created = 0;
            ArrayList resources = new ArrayList();
            foreach (string state in new string[] { "Depressed", "Flush", "Rise", "Fall" })
            {
                object snapshot = family.GetType().GetField(state).GetValue(family);
                foreach (object mode in (IEnumerable)snapshot.GetType().GetField("Modes").GetValue(snapshot))
                    foreach (object entry in (IEnumerable)mode.GetType().GetField("Entries").GetValue(mode))
                    {
                        object source = entry.GetType().GetField("Mesh").GetValue(entry);
                        bool isNode = (string)entry.GetType().GetField("Kind").GetValue(entry) == "node";
                        resources.Add(new object[] { source, isNode, -1f, 2f, 0.25f });
                        get.Invoke(cache, (object[])resources[resources.Count-1]);
                    }
            }
            int unique = _created;
            for (int repeat = 0; repeat < 10; ++repeat)
                foreach (object[] request in resources) get.Invoke(cache, request);
            if (_created != unique || unique >= resources.Count)
                throw new InvalidOperationException("Compiled height entries did not share duplicate geometry or grew on reuse");
            Console.WriteLine("NETWORK_GEOMETRY_CACHE_OK compiled_entries=" + resources.Count
                + " unique_mesh_requests=" + unique + " repeated_growth=0 native_bounds_separated");
        }

        private static object NewMesh(Type type)
        {
            object value = Activator.CreateInstance(type);
            type.GetField("Vertices").SetValue(value, new float[] { 0, 0, 0, 1, 0, 0, 0, 0, 1 });
            type.GetField("Normals").SetValue(value, new float[] { 0, 1, 0, 0, 1, 0, 0, 1, 0 });
            type.GetField("Uv").SetValue(value, new float[] { 0, 0, 1, 0, 0, 1 });
            type.GetField("Triangles").SetValue(value, new int[] { 0, 1, 2 });
            return value;
        }

        private static void RequireDifferent(MethodInfo get, object cache, object source, object first,
            bool node, float min, float max, float slope)
        {
            object different = get.Invoke(cache, new object[] { source, node, min, max, slope });
            if (ReferenceEquals(first, different)) throw new InvalidOperationException("Distinct geometry or native bounds shared the same mutable mesh");
        }
    }
}
