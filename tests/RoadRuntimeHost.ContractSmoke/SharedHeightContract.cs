using System;
using System.Collections;
using System.IO;
using System.Reflection;
using System.Runtime.Serialization;
using UnityEngine;

namespace RoadRuntimeHost.ContractSmoke
{
    internal static class SharedHeightContract
    {
        [Flags] public enum Flags : long { Other = 0x40, Custom0 = 0x1000000, Custom1 = 0x2000000, High = 0x1000000000 }
        public struct Cell { public Flags m_flags; }
        public sealed class Manager
        {
            public static Manager Current = new Manager();
            public static Manager Instance { get { return Current; } }
            public Cell[] SegmentBuffer = new Cell[32];
        }
        private static int _factoryCalls, _nativeModeRequests;
        public static NetInfo Factory(object family, object catalog)
        {
            ++_factoryCalls;
            object source = family.GetType().GetField("Depressed").GetValue(family);
            foreach (object mode in (IEnumerable)source.GetType().GetField("Modes").GetValue(source)) ++_nativeModeRequests;
            // A managed stand-in for the native engine registration port. No
            // Unity constructor or real prefab collection is invoked offline.
            return (NetInfo)FormatterServices.GetUninitializedObject(typeof(NetInfo));
        }

        public static void Validate(Assembly assembly, string root)
        {
            ValidateFlags(assembly);
            Type familyType = assembly.GetType("RoadRuntimeHost.Runtime.SharedHeightFamily", true);
            MethodInfo endpoint = familyType.GetMethod("EndpointFlush");
            // Literal physical endpoint states, independent of the production
            // state decoder. Drawing inversion reverses authored endpoints.
            bool[][] expected = { new bool[] { false, false }, new bool[] { true, true },
                new bool[] { false, true }, new bool[] { true, false } };
            for (int code = 0; code < 4; ++code)
                foreach (bool invert in new bool[] { false, true })
                    foreach (bool start in new bool[] { true, false }) {
                        int position = start ? 0 : 1; if (invert) position = 1-position;
                        bool actual = (bool)endpoint.Invoke(null, new object[] { code, start, invert });
                        if (actual != expected[code][position]) throw new InvalidOperationException("Shared height endpoint matrix disagrees with physical states");
                    }
            Type library = assembly.GetType("RoadRuntimeHost.Runtime.PlacementVariantLibrary", true);
            object menu = Activator.CreateInstance(library, new object[] { root, "jp-1x2l", "placement/jp-1x2l/index.json" });
            const string lower = "NARROW|CENTERED|NONE|NONE|DEPRESSED", flush = "NARROW|CENTERED|NONE|NONE|FLUSH";
            MethodInfo resolve = library.GetMethod("ResolveSharedHeight");
            object family = resolve.Invoke(menu, new object[] { lower });
            object otherLevel = resolve.Invoke(menu, new object[] { flush });
            string identity = (string)familyType.GetField("Identity").GetValue(family);
            if (identity != (string)familyType.GetField("Identity").GetValue(otherLevel))
                throw new InvalidOperationException("Height selection changed the native family identity");
            object[] profiles = { familyType.GetField("Depressed").GetValue(family), familyType.GetField("Flush").GetValue(family),
                familyType.GetField("Rise").GetValue(family), familyType.GetField("Fall").GetValue(family) };
            foreach (object profile in profiles)
                if (identity == (string)profile.GetType().GetField("Identity").GetValue(profile))
                    throw new InvalidOperationException("Native shared identity collides with an ordinary/transition snapshot");
            Type updater = assembly.GetType("RoadRuntimeHost.Runtime.PrefabUpdater", true);
            ConstructorInfo constructor = null;
            foreach (ConstructorInfo candidate in updater.GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                if (candidate.GetParameters().Length == 2) constructor = candidate;
            Type factoryType = constructor.GetParameters()[1].ParameterType;
            Delegate factory = Delegate.CreateDelegate(factoryType, typeof(SharedHeightContract).GetMethod("Factory"));
            object instance = constructor.Invoke(new object[] { root, factory });
            MethodInfo get = updater.GetMethod("GetOrCreateSharedHeight", BindingFlags.Instance | BindingFlags.NonPublic);
            _factoryCalls = 0; _nativeModeRequests = 0;
            object first = null;
            for (int i = 0; i < 100; ++i) {
                object registered = get.Invoke(instance, new object[] { i % 2 == 0 ? family : otherLevel, null });
                if (first == null) first = registered;
                if (!ReferenceEquals(first, registered)) throw new InvalidOperationException("Selection toggles allocated another height family");
            }
            if (_factoryCalls != 1 || _nativeModeRequests != 5)
                throw new InvalidOperationException("Repeated height choices did not share the single five-mode native registration request");
            // A cached native prefab must not bypass the AR capability gate.
            // Seed a production updater without calling Unity registration.
            object native = Activator.CreateInstance(updater, new object[] { root });
            IDictionary cache = (IDictionary)updater.GetField("_sharedHeights", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(native);
            cache.Add(identity, first);
            bool refused = false;
            try { get.Invoke(native, new object[] { family, null }); }
            catch (TargetInvocationException failure) {
                if (!(failure.InnerException is InvalidOperationException)) throw;
                refused = true;
            }
            if (!refused) throw new InvalidOperationException("Cached shared prefab bypassed the unavailable AR consumer gate");
            Console.WriteLine("SHARED_HEIGHT_RESOURCE_CONTRACT_OK selections=100 family_requests=1 native_mode_requests=5");
        }

        private static void ValidateFlags(Assembly assembly)
        {
            Type bridge = assembly.GetType("RoadRuntimeHost.Runtime.AdaptiveRoadsBridge", true);
            PropertyInfo instance = typeof(Manager).GetProperty("Instance");
            FieldInfo buffer = typeof(Manager).GetField("SegmentBuffer"), flags = typeof(Cell).GetField("m_flags");
            Delegate read = (Delegate)bridge.GetMethod("BuildReader", BindingFlags.Static | BindingFlags.NonPublic)
                .Invoke(null, new object[] { instance, buffer, flags });
            Delegate write = (Delegate)bridge.GetMethod("BuildWriter", BindingFlags.Static | BindingFlags.NonPublic)
                .Invoke(null, new object[] { instance, buffer, flags });
            Manager.Current = new Manager();
            Manager.Current.SegmentBuffer[3].m_flags = Flags.Other | Flags.High;
            Manager.Current.SegmentBuffer[9].m_flags = Flags.Custom1 | Flags.High;
            long untouched = (long)Manager.Current.SegmentBuffer[9].m_flags;
            const long mask = 0x3000000;
            long[] literalStates = { 0, 0x1000000, 0x2000000, 0x3000000 };
            foreach (long state in literalStates) {
                write.DynamicInvoke(3, mask, state);
                long actual = (long)read.DynamicInvoke(3);
                if (actual != (0x40L | 0x1000000000L | state)
                    || (long)read.DynamicInvoke(9) != untouched)
                    throw new InvalidOperationException("Individual flags changed another segment or other AR bits");
            }
            Manager previous = Manager.Current;
            Manager.Current = new Manager();
            write.DynamicInvoke(3, mask, 0x1000000L);
            if ((long)read.DynamicInvoke(3) != 0x1000000L
                || previous.SegmentBuffer[3].m_flags != (Flags.Other | Flags.High | Flags.Custom0 | Flags.Custom1))
                throw new InvalidOperationException("Flag access retained stale buffers across map load");
            Console.WriteLine("AR_INDIVIDUAL_FLAGS_CONTRACT_OK foreign_bits_preserved buffers_rebound");
        }
    }
}
