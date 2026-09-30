using System;
using System.Reflection;

namespace RoadRuntimeHost.Runtime
{
    // TM:PE is optional. Keep every API reference behind reflection so the
    // Runtime Host still loads when TM:PE is not installed or is disabled.
    internal sealed class TmpeTrafficPolicy
    {
        private readonly Action<ushort, bool> _modified;
        private object _junctionRestrictions;
        private object _trafficLights;
        private object _trafficPriority;
        private MethodInfo _isCrossingAllowed;
        private MethodInfo _isEnteringBlockedJunctionAllowed;
        private MethodInfo _hasTrafficLight;
        private MethodInfo _getPrioritySign;
        private object _notifier;
        private System.Reflection.EventInfo _modifiedEvent;
        private Delegate _modifiedHandler;

        public bool IsAvailable { get { return _junctionRestrictions != null; } }

        public TmpeTrafficPolicy(Action<ushort, bool> modified)
        {
            _modified = modified;
            TryAttach();
        }

        public void Stop()
        {
            if (_notifier != null && _modifiedEvent != null && _modifiedHandler != null)
            {
                try { _modifiedEvent.RemoveEventHandler(_notifier, _modifiedHandler); }
                catch { }
            }
            _notifier = null;
            _modifiedEvent = null;
            _modifiedHandler = null;
        }

        public bool? IsPedestrianCrossingAllowed(ushort segmentId, bool startNode)
        {
            if (_junctionRestrictions == null || _isCrossingAllowed == null) return null;
            return Convert.ToBoolean(_isCrossingAllowed.Invoke(
                _junctionRestrictions, new object[] { segmentId, startNode }));
        }

        public bool? IsEnteringBlockedJunctionAllowed(ushort segmentId, bool startNode)
        {
            if (_junctionRestrictions == null || _isEnteringBlockedJunctionAllowed == null) return null;
            return Convert.ToBoolean(_isEnteringBlockedJunctionAllowed.Invoke(
                _junctionRestrictions, new object[] { segmentId, startNode }));
        }

        public bool? HasTrafficLight(ushort nodeId)
        {
            if (_trafficLights == null || _hasTrafficLight == null) return null;
            return Convert.ToBoolean(_hasTrafficLight.Invoke(
                _trafficLights, new object[] { nodeId }));
        }

        public bool? HasStopSign(ushort segmentId, bool startNode)
        {
            if (_trafficPriority == null || _getPrioritySign == null) return null;
            object value = _getPrioritySign.Invoke(
                _trafficPriority, new object[] { segmentId, startNode });
            return value != null && string.Equals(
                value.ToString(), "Stop", StringComparison.OrdinalIgnoreCase);
        }

        private void TryAttach()
        {
            try
            {
                Assembly api = null;
                foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
                {
                    if (assembly.GetName().Name == "TMPE.API")
                    {
                        api = assembly;
                        break;
                    }
                }
                if (api == null) return;

                Type implementations = api.GetType("TrafficManager.API.Implementations", true);
                object factory = RequireStaticProperty(implementations, "ManagerFactory");
                _junctionRestrictions = RequireProperty(factory, "JunctionRestrictionsManager");
                _trafficLights = RequireProperty(factory, "TrafficLightManager");
                _trafficPriority = RequireProperty(factory, "TrafficPriorityManager");
                _isCrossingAllowed = RequireMethod(
                    _junctionRestrictions, "IsPedestrianCrossingAllowed",
                    typeof(ushort), typeof(bool));
                _isEnteringBlockedJunctionAllowed = RequireMethod(
                    _junctionRestrictions, "IsEnteringBlockedJunctionAllowed",
                    typeof(ushort), typeof(bool));
                _hasTrafficLight = RequireMethod(
                    _trafficLights, "HasTrafficLight", typeof(ushort));
                _getPrioritySign = RequireMethod(
                    _trafficPriority, "GetPrioritySign",
                    typeof(ushort), typeof(bool));

                _notifier = RequireStaticProperty(implementations, "Notifier");
                _modifiedEvent = _notifier.GetType().GetEvent("EventModified");
                if (_modifiedEvent != null)
                {
                    Type eventArgument = _modifiedEvent.EventHandlerType.GetGenericArguments()[0];
                    MethodInfo attach = typeof(TmpeTrafficPolicy).GetMethod(
                        "AttachModifiedHandler",
                        BindingFlags.Instance | BindingFlags.NonPublic);
                    attach.MakeGenericMethod(eventArgument).Invoke(this, null);
                }

                DiagnosticLog.Info(
                    "SUCCESS",
                    "tmpe_policy_ready",
                    "TM:PE crossing, blocked-junction, traffic-light, and stop-sign policy is available",
                    "tmpe_api_version", api.GetName().Version == null
                        ? string.Empty : api.GetName().Version.ToString(),
                    "event_subscription", (_modifiedHandler != null).ToString());
            }
            catch (Exception error)
            {
                _junctionRestrictions = null;
                _trafficLights = null;
                _trafficPriority = null;
                DiagnosticLog.Warn(
                    "MOD_COMPATIBILITY",
                    "tmpe_policy_unavailable",
                    "TM:PE policy could not be read; vanilla node flags will be used",
                    "reason", error.GetBaseException().Message);
            }
        }

        private void AttachModifiedHandler<T>()
        {
            Action<T> handler = delegate(T eventArgs)
            {
                NotifyModified(eventArgs);
            };
            _modifiedHandler = handler;
            _modifiedEvent.AddEventHandler(_notifier, handler);
        }

        private void NotifyModified(object eventArgs)
        {
            if (_modified == null || eventArgs == null) return;
            FieldInfo instanceField = eventArgs.GetType().GetField("InstanceID");
            object instance = instanceField == null ? null : instanceField.GetValue(eventArgs);
            if (instance == null) return;
            PropertyInfo nodeProperty = instance.GetType().GetProperty("NetNode");
            PropertyInfo segmentProperty = instance.GetType().GetProperty("NetSegment");
            ushort nodeId = nodeProperty == null
                ? (ushort)0 : Convert.ToUInt16(nodeProperty.GetValue(instance, null));
            if (nodeId != 0)
            {
                _modified(nodeId, false);
                return;
            }
            ushort segmentId = segmentProperty == null
                ? (ushort)0 : Convert.ToUInt16(segmentProperty.GetValue(instance, null));
            if (segmentId != 0) _modified(segmentId, true);
        }

        private static object RequireStaticProperty(Type type, string name)
        {
            PropertyInfo property = type.GetProperty(
                name, BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            if (property == null) throw new MissingMemberException(type.FullName, name);
            object value = property.GetValue(null, null);
            if (value == null) throw new InvalidOperationException(type.FullName + "." + name + " returned null");
            return value;
        }

        private static object RequireProperty(object target, string name)
        {
            PropertyInfo property = target.GetType().GetProperty(
                name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (property == null) throw new MissingMemberException(target.GetType().FullName, name);
            object value = property.GetValue(target, null);
            if (value == null) throw new InvalidOperationException(target.GetType().FullName + "." + name + " returned null");
            return value;
        }

        private static MethodInfo RequireMethod(object target, string name, params Type[] parameters)
        {
            MethodInfo method = target.GetType().GetMethod(
                name,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                null,
                parameters,
                null);
            if (method == null) throw new MissingMethodException(target.GetType().FullName, name);
            return method;
        }
    }
}
