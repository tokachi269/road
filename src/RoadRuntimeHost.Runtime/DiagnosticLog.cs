using System;
using System.IO;
using System.Text;
using System.Threading;
using UnityEngine;

namespace RoadRuntimeHost.Runtime
{
    internal sealed class DiagnosticException : Exception
    {
        public readonly string Category;
        public readonly string EventName;
        public DiagnosticException(string category, string eventName, string message) : base(message) { Category = category; EventName = eventName; }
    }

    internal static class DiagnosticLog
    {
        private static readonly Mutex FileMutex = new Mutex(false, "RoadRuntimeHost.DiagnosticLog");
        private static string _path;
        private static bool _writeFailureReported;

        public static void Configure(string path) { _path = path; _writeFailureReported = false; }
        public static void Info(string category, string eventName, string message, params string[] context) { Write("INFO", category, eventName, message, null, context); }
        public static void Warn(string category, string eventName, string message, params string[] context)
        {
            Write("WARN", category, eventName, message, null, context);
            try { Debug.LogWarning("RoadRuntimeHost [" + category + "/" + eventName + "]: " + message); }
            catch { }
        }
        public static void Error(string category, string eventName, string message, Exception error, params string[] context)
        {
            Write("ERROR", category, eventName, message, error, context);
            try { Debug.LogError("RoadRuntimeHost [" + category + "/" + eventName + "]: " + message + (error == null ? string.Empty : " (" + error.GetType().Name + ": " + error.Message + ")")); }
            catch { }
        }
        public static string Classify(Exception error)
        {
            DiagnosticException diagnostic = error as DiagnosticException;
            if (diagnostic != null) return diagnostic.Category;
            FileNotFoundException missingAssembly = error as FileNotFoundException;
            if (missingAssembly != null && !string.IsNullOrEmpty(missingAssembly.FileName)
                && missingAssembly.FileName.IndexOf(", Version=", StringComparison.OrdinalIgnoreCase) >= 0)
                return "MOD_DEPENDENCY";
            if (error is FileLoadException || error is BadImageFormatException) return "MOD_DEPENDENCY";
            if (error is FileNotFoundException || error is DirectoryNotFoundException) return "DATA_MISSING";
            if (error is InvalidDataException) return "DATA_INVALID";
            if (error is MissingMethodException || error is TypeLoadException || error is MissingMemberException) return "MOD_CONTRACT";
            if (error is FormatException || error is InvalidCastException || error is ArgumentException || error.GetType().FullName == "System.Runtime.Serialization.SerializationException") return "CONTRACT_TYPE";
            if (error is IOException) return "IO";
            return "MOD";
        }
        private static void Write(string level, string category, string eventName, string message, Exception error, string[] context)
        {
            if (string.IsNullOrEmpty(_path)) return;
            bool locked = false;
            try
            {
                locked = FileMutex.WaitOne(1000, false);
                if (!locked) return;
                StringBuilder line = new StringBuilder();
                line.Append("{\"ts\":\"").Append(Escape(DateTime.UtcNow.ToString("o"))).Append("\"");
                line.Append(",\"level\":\"").Append(level).Append("\"");
                line.Append(",\"component\":\"runtime\"");
                line.Append(",\"category\":\"").Append(Escape(category)).Append("\"");
                line.Append(",\"event\":\"").Append(Escape(eventName)).Append("\"");
                line.Append(",\"message\":\"").Append(Escape(message)).Append("\"");
                if (context != null && context.Length != 0)
                {
                    line.Append(",\"context\":{");
                    for (int index = 0; index + 1 < context.Length; index += 2)
                    {
                        if (index != 0) line.Append(',');
                        line.Append('\"').Append(Escape(context[index])).Append("\":\"").Append(Escape(context[index + 1])).Append('\"');
                    }
                    line.Append('}');
                }
                if (error != null)
                {
                    line.Append(",\"exception_type\":\"").Append(Escape(error.GetType().FullName)).Append("\"");
                    line.Append(",\"exception\":\"").Append(Escape(error.ToString())).Append("\"");
                }
                line.Append("}\r\n");
                byte[] bytes = Encoding.UTF8.GetBytes(line.ToString());
                using (FileStream stream = new FileStream(_path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete))
                    stream.Write(bytes, 0, bytes.Length);
            }
            catch (Exception writeError)
            {
                if (!_writeFailureReported)
                {
                    _writeFailureReported = true;
                    Debug.LogError("RoadRuntimeHost: dedicated log write failed: " + writeError);
                }
            }
            finally { if (locked) FileMutex.ReleaseMutex(); }
        }
        private static string Escape(string value)
        {
            if (value == null) return string.Empty;
            StringBuilder escaped = new StringBuilder(value.Length + 16);
            foreach (char character in value)
            {
                switch (character)
                {
                    case '\\': escaped.Append("\\\\"); break;
                    case '\"': escaped.Append("\\\""); break;
                    case '\b': escaped.Append("\\b"); break;
                    case '\f': escaped.Append("\\f"); break;
                    case '\n': escaped.Append("\\n"); break;
                    case '\r': escaped.Append("\\r"); break;
                    case '\t': escaped.Append("\\t"); break;
                    default:
                        if (character < 32) escaped.Append("\\u").Append(((int)character).ToString("x4"));
                        else escaped.Append(character);
                        break;
                }
            }
            return escaped.ToString();
        }
    }
}
