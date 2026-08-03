using System;
using Newtonsoft.Json.Linq;

namespace CadArchSpec.EditorBridge
{
    public sealed class EditorMessage
    {
        public int ProtocolVersion { get; set; } = 1;
        public string MessageId { get; set; } = Guid.NewGuid().ToString("D");
        public string Type { get; set; } = string.Empty;
        public JObject Payload { get; set; } = new JObject();
    }
}
