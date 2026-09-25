using System;
using System.IO;
using System.Reflection;
using ColossalFramework.Plugins;
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
        private bool _reportedMissingPointer;
        private string _rootPath;
        private string _rootPathSource;
        private string _assemblyLocation;
        private string _pluginPath;
        private Exception _pluginPathError;

        public static HotReloadCoordinator Instance { get { return _instance; } }

        private string RootPath
        {
            get
            {
                if (_rootPath == null) ResolveRootPath();
                return _rootPath;
            }
        }

        public void Start(string loadMode)
        {
            _loadMode = loadMode;
            string logPath = Path.Combine(Path.Combine(RootPath, "logs"), "RoadRuntimeHost.jsonl");
            DiagnosticLog.Configure(logPath);
            DiagnosticLog.Info(
                "MOD", "loader_start", "Stable loader started",
                "root", RootPath,
                "root_source", _rootPathSource,
                "plugin_path", _pluginPath ?? string.Empty,
                "assembly_location", _assemblyLocation ?? string.Empty,
                "load_mode", loadMode,
                "log_path", logPath);
            if (_pluginPathError != null)
            {
                DiagnosticLog.Warn(
                    "MOD", "plugin_path_lookup_failed",
                    "PluginManager could not resolve the mod directory; the assembly location fallback was used",
                    "exception_type", _pluginPathError.GetType().FullName,
                    "exception", _pluginPathError.Message,
                    "assembly_location", _assemblyLocation ?? string.Empty);
            }
            Directory.CreateDirectory(Path.Combine(RootPath, "runtime"));
            ReloadIfChanged(true);
        }

        private void ResolveRootPath()
        {
            Assembly assembly = Assembly.GetExecutingAssembly();
            _assemblyLocation = assembly.Location ?? string.Empty;
            try
            {
                PluginManager.PluginInfo plugin = PluginManager.instance.FindPluginInfo(assembly);
                _pluginPath = plugin == null ? string.Empty : plugin.modPath;
            }
            catch (Exception error)
            {
                _pluginPath = string.Empty;
                _pluginPathError = error;
            }
            _rootPath = ResolveRootPath(_pluginPath, _assemblyLocation, out _rootPathSource);
        }

        internal static string ResolveRootPath(string pluginPath, string assemblyLocation)
        {
            string source;
            return ResolveRootPath(pluginPath, assemblyLocation, out source);
        }

        private static string ResolveRootPath(string pluginPath, string assemblyLocation, out string source)
        {
            string resolved = NormalizeExistingDirectory(pluginPath);
            if (resolved != null)
            {
                source = "plugin_manager";
                return resolved;
            }

            if (!string.IsNullOrEmpty(assemblyLocation))
            {
                try
                {
                    string directory = Path.GetDirectoryName(Path.GetFullPath(assemblyLocation));
                    resolved = NormalizeExistingDirectory(directory);
                    if (resolved != null)
                    {
                        source = "assembly_location";
                        return resolved;
                    }
                }
                catch (ArgumentException) { }
                catch (NotSupportedException) { }
                catch (PathTooLongException) { }
            }

            throw new InvalidOperationException(
                "RoadRuntimeHost could not resolve its mod directory from PluginManager or Assembly.Location; "
                + "plugin_path='" + (pluginPath ?? string.Empty) + "', assembly_location='"
                + (assemblyLocation ?? string.Empty) + "'");
        }

        private static string NormalizeExistingDirectory(string path)
        {
            if (string.IsNullOrEmpty(path)) return null;
            try
            {
                string fullPath = Path.GetFullPath(path);
                return Directory.Exists(fullPath) ? fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) : null;
            }
            catch (ArgumentException) { return null; }
            catch (NotSupportedException) { return null; }
            catch (PathTooLongException) { return null; }
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
                catch (Exception error)
                {
                    Exception actual = Unwrap(error);
                    DiagnosticLog.Error(DiagnosticLog.Classify(actual), "runtime_tick_failed", "Hot runtime Tick failed", actual, "runtime_token", _runtimeToken ?? string.Empty);
                }
            }
        }

        public void Stop()
        {
            DiagnosticLog.Info("MOD", "loader_stop", "Stable loader is stopping", "runtime_token", _runtimeToken ?? string.Empty);
            StopRuntime(_runtime, _stop);
            _runtime = null;
            _tick = null;
            _stop = null;
            _runtimeToken = null;
        }

        private void ReloadIfChanged(bool force)
        {
            string pointer = Path.Combine(RootPath, "runtime.current");
            if (!File.Exists(pointer))
            {
                if (!_reportedMissingPointer)
                {
                    DiagnosticLog.Warn("DATA_MISSING", "runtime_pointer_missing", "runtime.current does not exist; no hot runtime can be loaded", "path", pointer);
                    _reportedMissingPointer = true;
                }
                return;
            }
            _reportedMissingPointer = false;
            string token;
            try { token = File.ReadAllText(pointer).Trim(); }
            catch (IOException error)
            {
                DiagnosticLog.Warn("IO", "runtime_pointer_read_retry", "runtime.current could not be read and will be retried", "path", pointer, "error", error.Message);
                return;
            }
            if (!force && string.Equals(token, _runtimeToken, StringComparison.Ordinal)) return;
            if (token.Length == 0 || Path.IsPathRooted(token) || token.Contains(".."))
            {
                DiagnosticLog.Error("DATA_INVALID", "runtime_pointer_invalid", "runtime.current contains an unsafe or empty value", null, "path", pointer, "value", token);
                return;
            }
            string dll = Path.GetFullPath(Path.Combine(RootPath, token));
            string runtimeRoot = Path.GetFullPath(Path.Combine(RootPath, "runtime")) + Path.DirectorySeparatorChar;
            if (!dll.StartsWith(runtimeRoot, StringComparison.OrdinalIgnoreCase))
            {
                DiagnosticLog.Error("DATA_INVALID", "runtime_path_escaped", "Resolved runtime DLL is outside the runtime directory", null, "token", token, "resolved_path", dll);
                return;
            }
            if (!File.Exists(dll))
            {
                DiagnosticLog.Warn("DATA_MISSING", "runtime_dll_missing", "runtime.current points to a DLL that does not exist yet", "token", token, "path", dll);
                return;
            }

            try
            {
                DiagnosticLog.Info("MOD", "runtime_load_begin", "Loading hot runtime candidate", "token", token, "path", dll);
                Assembly assembly = Assembly.Load(File.ReadAllBytes(dll));
                Type type = assembly.GetType(RuntimeTypeName, true);
                object candidate = Activator.CreateInstance(type);
                MethodInfo start = RequireMethod(type, "Start", typeof(string), typeof(string), typeof(string));
                MethodInfo tick = RequireMethod(type, "Tick", typeof(float), typeof(float));
                MethodInfo stop = RequireMethod(type, "Stop");
                string previewPath = ReadPreviewPath();
                string logPath = Path.Combine(Path.Combine(RootPath, "logs"), "RoadRuntimeHost.jsonl");
                start.Invoke(candidate, new object[] { previewPath, _loadMode, logPath });

                object previous = _runtime;
                MethodInfo previousStop = _stop;
                _runtime = candidate;
                _tick = tick;
                _stop = stop;
                _runtimeToken = token;
                StopRuntime(previous, previousStop);
                DiagnosticLog.Info("SUCCESS", "runtime_activated", "Hot runtime candidate activated", "token", token, "preview_path", previewPath);
                Debug.Log("RoadRuntimeHost: activated " + token + "; details: " + logPath);
            }
            catch (Exception error)
            {
                Exception actual = Unwrap(error);
                DiagnosticLog.Error(DiagnosticLog.Classify(actual), "runtime_activation_failed", "Hot runtime candidate could not be activated; previous runtime remains active", actual, "token", token, "path", dll);
            }
        }

        private string ReadPreviewPath()
        {
            string pathFile = Path.Combine(RootPath, "preview.path");
            if (File.Exists(pathFile))
            {
                string configured = File.ReadAllText(pathFile).Trim();
                if (configured.Length != 0)
                {
                    string resolved = Path.GetFullPath(configured);
                    DiagnosticLog.Info("DATA", "preview_path_resolved", "Using configured preview directory", "config_path", pathFile, "preview_path", resolved);
                    return resolved;
                }
                DiagnosticLog.Warn("DATA_INVALID", "preview_path_empty", "preview.path is empty; using the bundled preview directory", "path", pathFile);
            }
            string fallback = Path.Combine(RootPath, "preview");
            DiagnosticLog.Info("DATA", "preview_path_default", "Using bundled preview directory", "preview_path", fallback);
            return fallback;
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
            catch (Exception error)
            {
                Exception actual = Unwrap(error);
                DiagnosticLog.Error(DiagnosticLog.Classify(actual), "runtime_stop_failed", "Hot runtime Stop failed", actual);
            }
        }

        private static Exception Unwrap(Exception error)
        {
            TargetInvocationException invocation = error as TargetInvocationException;
            return invocation != null && invocation.InnerException != null ? invocation.InnerException : error;
        }
    }
}
