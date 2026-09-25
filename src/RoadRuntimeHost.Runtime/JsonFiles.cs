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
                catch (IOException)
                {
                    if (attempt == 2) throw;
                }
            }
            throw new InvalidOperationException("unreachable");
        }
    }
}
