using System;
using System.IO;
using System.Runtime.Serialization.Json;

namespace RoadRuntimeHost.Runtime
{
    internal static class JsonFiles
    {
        public static T Read<T>(string path) where T : class
        {
            for (int attempt = 0; attempt != 3; ++attempt)
            {
                try
                {
                    using (FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                    {
                        DataContractJsonSerializer serializer = new DataContractJsonSerializer(typeof(T));
                        return serializer.ReadObject(stream) as T;
                    }
                }
                catch (IOException error)
                {
                    DiagnosticLog.Warn("IO", "json_read_retry", "JSON file is temporarily unavailable; read will be retried", "path", path, "data_type", typeof(T).FullName, "attempt", (attempt + 1).ToString(), "error", error.Message);
                    if (attempt == 2) throw;
                }
            }
            throw new InvalidOperationException("unreachable");
        }
    }
}
