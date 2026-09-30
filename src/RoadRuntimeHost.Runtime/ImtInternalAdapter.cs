using System;
using System.Reflection;
using IMT.API;

namespace RoadRuntimeHost.Runtime
{
    // IMT 1.15's public API cannot retrieve an existing crosswalk provider.
    // Keep this adapter limited to that compatibility gap and a one-time
    // recalculation after the pre-dash wall hook is installed. It must not own
    // or continuously rewrite IMT geometry.
    internal sealed class ImtInternalAdapter
    {
        public ulong GetCrosswalkLineId(ICrosswalkData crosswalk)
        {
            return Convert.ToUInt64(PublicProperty(crosswalk, "Id"));
        }

        public ICrosswalkData GetCrosswalk(
            INodeMarkingData marking,
            ICrosswalkPointData start,
            ICrosswalkPointData end)
        {
            Assembly assembly = marking.GetType().Assembly;
            RequireSupportedVersion(assembly);
            object internalMarking = PrivateProperty(marking, "Marking");
            object internalStart = PrivateProperty(start, "Point");
            object internalEnd = PrivateProperty(end, "Point");
            Type lineType = assembly.GetType("IMT.Manager.MarkingCrosswalkLine", true);
            MethodInfo tryGetLine = FindGenericTryGetLine(internalMarking.GetType()).MakeGenericMethod(lineType);
            object[] arguments = new object[] { internalStart, internalEnd, null };
            if (!(bool)tryGetLine.Invoke(internalMarking, arguments) || arguments[2] == null)
                throw new InvalidOperationException("IMT reported an existing crosswalk but its native line was not found");

            object internalCrosswalk = PublicProperty(arguments[2], "Crosswalk");
            Type providerType = assembly.GetType("IMT.Utilities.API.CrosswalkDataProvider", true);
            object provider = Activator.CreateInstance(
                providerType,
                new object[] { start.DataProvider, internalCrosswalk });
            ICrosswalkData result = provider as ICrosswalkData;
            if (result == null)
                throw new InvalidOperationException("IMT crosswalk provider did not implement ICrosswalkData");
            return result;
        }

        public void RestoreNativeCrosswalk(
            INodeMarkingData marking,
            ICrosswalkData crosswalk)
        {
            RequireSupportedVersion(crosswalk.GetType().Assembly);
            object internalCrosswalkLine = PrivateProperty(crosswalk, "Line");
            object internalCrosswalk = PublicProperty(internalCrosswalkLine, "Crosswalk");
            object internalMarking = PrivateProperty(marking, "Marking");

            // Rebuild exclusively from IMT's persisted state. The version-gated
            // pre-dash hook may adjust the generated trajectory, but this adapter
            // never changes points, borders, styles, or final decal polygons.
            Invoke(internalCrosswalk, "Update", true);
            InvokeNoArguments(internalMarking, "RecalculateAllStyleData");
        }

        private static MethodInfo FindGenericTryGetLine(Type markingType)
        {
            foreach (MethodInfo method in markingType.GetMethods(
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
            {
                if (!method.IsGenericMethodDefinition || method.Name != "TryGetLine") continue;
                ParameterInfo[] parameters = method.GetParameters();
                if (parameters.Length == 3
                    && parameters[0].ParameterType.FullName == "IMT.Manager.MarkingPoint"
                    && parameters[1].ParameterType.FullName == "IMT.Manager.MarkingPoint")
                    return method;
            }
            throw new MissingMethodException(markingType.FullName, "TryGetLine<LineType>(MarkingPoint, MarkingPoint, out LineType)");
        }

        private static void RequireSupportedVersion(Assembly assembly)
        {
            Version version = assembly.GetName().Version;
            if (version == null || version.Major != 1 || version.Minor != 15)
                throw new NotSupportedException(
                    "IMT internal adapter supports 1.15.x only; loaded "
                    + (version == null ? "<unknown>" : version.ToString()));
        }

        private static object PrivateProperty(object target, string name)
        {
            Type current = target.GetType();
            while (current != null)
            {
                PropertyInfo property = current.GetProperty(
                    name,
                    BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                if (property != null) return property.GetValue(target, null);
                current = current.BaseType;
            }
            throw new MissingMemberException(target.GetType().FullName, name);
        }

        private static object PublicProperty(object target, string name)
        {
            PropertyInfo property = target.GetType().GetProperty(
                name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (property == null) throw new MissingMemberException(target.GetType().FullName, name);
            return property.GetValue(target, null);
        }

        private static object Invoke(object target, string name, object argument)
        {
            MethodInfo method = target.GetType().GetMethod(
                name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                null, new Type[] { argument.GetType() }, null);
            if (method == null) throw new MissingMethodException(target.GetType().FullName, name);
            return method.Invoke(target, new object[] { argument });
        }

        private static object InvokeNoArguments(object target, string name)
        {
            MethodInfo method = target.GetType().GetMethod(
                name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                null, Type.EmptyTypes, null);
            if (method == null) throw new MissingMethodException(target.GetType().FullName, name);
            return method.Invoke(target, new object[0]);
        }
    }
}
