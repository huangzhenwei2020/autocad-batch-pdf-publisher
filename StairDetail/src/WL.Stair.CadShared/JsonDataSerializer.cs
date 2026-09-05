using System;

namespace WL.Stair.CadShared
{
    internal sealed class JsonDataSerializer
    {
#if NET8_0_OR_GREATER
        private readonly System.Text.Json.JsonSerializerOptions _options = new System.Text.Json.JsonSerializerOptions
        {
            IncludeFields = true,
            PropertyNameCaseInsensitive = true,
            WriteIndented = false
        };

        internal string Serialize(object value)
        {
            if (value == null) return "null";
            return System.Text.Json.JsonSerializer.Serialize(value, value.GetType(), _options);
        }

        internal T Deserialize<T>(string json)
        {
            return System.Text.Json.JsonSerializer.Deserialize<T>(json, _options);
        }
#else
        private readonly System.Web.Script.Serialization.JavaScriptSerializer _serializer =
            new System.Web.Script.Serialization.JavaScriptSerializer { MaxJsonLength = int.MaxValue };

        internal string Serialize(object value) { return _serializer.Serialize(value); }
        internal T Deserialize<T>(string json) { return _serializer.Deserialize<T>(json); }
#endif
    }
}
