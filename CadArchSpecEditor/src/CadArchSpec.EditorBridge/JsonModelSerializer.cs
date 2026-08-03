using System;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using Newtonsoft.Json.Serialization;

namespace CadArchSpec.EditorBridge
{
    public sealed class JsonModelSerializer
    {
        private readonly JsonSerializerSettings _settings;

        public JsonModelSerializer()
        {
            _settings = new JsonSerializerSettings
            {
                ContractResolver = new CamelCasePropertyNamesContractResolver(),
                Formatting = Formatting.Indented,
                DateFormatString = "yyyy-MM-ddTHH:mm:ss.FFFFFFFK",
                NullValueHandling = NullValueHandling.Include
            };
            _settings.Converters.Add(new StringEnumConverter(new CamelCaseNamingStrategy()));
        }

        public string Serialize<T>(T value)
        {
            if (value == null) throw new ArgumentNullException(nameof(value));
            return JsonConvert.SerializeObject(value, _settings);
        }

        public T Deserialize<T>(string json)
        {
            if (string.IsNullOrWhiteSpace(json)) throw new ArgumentException("JSON 内容不能为空。", nameof(json));
            var value = JsonConvert.DeserializeObject<T>(json, _settings);
            if (value == null) throw new JsonSerializationException("JSON 未能生成目标对象。");
            return value;
        }
    }
}
