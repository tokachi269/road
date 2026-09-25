using System;
using System.IO;
using System.Reflection;
using UnityEngine;

namespace RoadRuntimeHost.Loader
{
    internal sealed class HotReloadCoordinator
    {
        private const string RuntimeTypeName = "RoadRuntimeHost.Runtime.RuntimeEntry";
        private static readonly HotReloadCoordinator _instance = new HotReloadCoordinator();

        private object _runtime;
        private MethodInfo _tick;
        private MethodInfo _stop;
        private string _runtimeToken;
        private string _loadMode;
        private float _watchElapsed;

        public static HotReloadCoordinator Instance { get { return _instance; } }

        private string RootPath
        {
            get { return Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location); }
        }

        public void Start(string loadMode)
        {
            _loadMode = loadMode;
            Directory.CreateDirectory(Path.Combine(RootPath, "runtime"));
            ReloadIfChanged(true);
        }

        public void Tick(float realTimeDelta, float simulationTimeDelta)
        {
            _watchElapsed += realTimeDelta;
            if (_watchElapsed >= 1f)
            {
                _watchElapsed = 0f;
                ReloadIfChanged(false);
            }
            if (_runtime != null && _tick != null)
            {
                try { _tick.Invoke(_runtime, new object[] { realTimeDelta, simulationTimeDelta }); }
                catch (Exception error) { Debug.LogException(Unwrap(error)); }
            }
        }

        public void Stop()
        {
            StopRuntime(_runtime, _stop);
            _runtime = null;
            _tick = null;
            _stop = null;
            _runtimeToken = null;
        }

        private void ReloadIfChanged(bool force)
        {
            string pointer = Path.Combine(RootPath, "runtime.current");
            if (!File.Exists(pointer)) return;
            string token;
            try { token = File.ReadAllText(pointer).Trim(); }
            catch (IOException) { return; }
            if (!force && string.Equals(token, _runtimeToken, StringComparison.Ordinal)) return;
            if (token.Length == 0 || Path.IsPathRooted(token) || token.Contains(".."))
            {
                Debug.LogError("RoadRuntimeHost: invalid runtime.current value");
                return;
            }
            string dll = Path.GetFullPath(Path.Combine(RootPath, token));
            string runtimeRoot = Path.GetFullPath(Path.Combine(RootPath, "runtime")) + Path.DirectorySeparatorChar;
            if (!dll.StartsWith(runtimeRoot, StringComparison.OrdinalIgnoreCase) || !File.Exists(dll)) return;

            try
            {
                Assembly assembly = Assembly.Load(File.ReadAllBytes(dll));
                Type type = assembly.GetType(RuntimeTypeName, true);
                object candidate = Activator.CreateInstance(type);
                MethodInfo start = RequireMethod(type, "Start", typeof(string), typeof(string));
                MethodInfo tick = RequireMethod(type, "Tick", typeof(float), typeof(float));
                MethodInfo stop = RequireMethod(type, "Stop");
                string previewPath = ReadPreviewPath();
                start.Invoke(candidate, new object[] { previewPath, _loadMode });

                object previous = _runtime;
                MethodInfo previousStop = _stop;
                _runtime = candidate;
                _tick = tick;
                _stop = stop;
                _runtimeToken = token;
                StopRuntime(previous, previousStop);
                Debug.Log("RoadRuntimeHost: activated " + token);
            }
            catch (Exception error)
            {
                Debug.LogException(Unwrap(error));
            }
        }

        private string ReadPreviewPath()
        {
            string pathFile = Path.Combine(RootPath, "preview.path");
            if (File.Exists(pathFile))
            {
                string configured = File.ReadAllText(pathFile).Trim();
                if (configured.Length != 0) return Path.GetFullPath(configured);
            }
            return Path.Combine(RootPath, "preview");
        }

        private static MethodInfo RequireMethod(Type type, string name, params Type[] arguments)
        {
            MethodInfo method = type.GetMethod(name, BindingFlags.Public | BindingFlags.Instance, null, arguments, null);
            if (method == null) throw new MissingMethodException(type.FullName, name);
            return method;
        }

        private static void StopRuntime(object runtime, MethodInfo stop)
        {
            if (runtime == null || stop == null) return;
            try { stop.Invoke(runtime, null); }
            catch (Exception error) { Debug.LogException(Unwrap(error)); }
        }

        private static Exception Unwrap(Exception error)
        {
            TargetInvocationException invocation = error as TargetInvocationException;
            return invocation != null && invocation.InnerException != null ? invocation.InnerException : error;
        }
    }
}
