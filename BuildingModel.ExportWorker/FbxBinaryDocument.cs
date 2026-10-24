using System.Text;

namespace BatchPdfPublisher.BuildingModel.Export;

// Narrow FBX 7500 container editor for our own Assimp output. Array/compressed
// payloads are opaque: presentation edits must not decode or retessellate them.
internal sealed class FbxBinaryDocument
{
    internal sealed record Property(byte[] Bytes, object? Value)
    {
        internal static Property Of(object value)
        {
            using var data = new MemoryStream(); using var writer = new BinaryWriter(data, Encoding.UTF8, true);
            switch (value)
            {
                case string text:
                    var bytes = Encoding.UTF8.GetBytes(text); writer.Write((byte)'S'); writer.Write(bytes.Length); writer.Write(bytes); break;
                case long number: writer.Write((byte)'L'); writer.Write(number); break;
                case int number: writer.Write((byte)'I'); writer.Write(number); break;
                case double number: writer.Write((byte)'D'); writer.Write(number); break;
                default: throw new ArgumentException("Unsupported FBX presentation property.");
            }
            return new(data.ToArray(), value);
        }
    }
    internal sealed class Node
    {
        internal string Name;
        internal List<Property> Properties = new();
        internal List<Node> Children = new();
        internal bool Terminated;
        internal Node(string name, params object[] values) { Name = name; Properties.AddRange(values.Select(Property.Of)); }
        internal Node One(string name) => Children.Single(n => n.Name == name);
        internal long Id => (long)Properties[0].Value!;
        internal string ObjectName => ((string)Properties[1].Value!).Split('\0')[0];
    }
    private static readonly byte[] Magic = "Kaydara FBX Binary  \0\x1a\0"u8.ToArray();
    internal List<Node> Nodes = new();
    private byte[] _footerCode = Array.Empty<byte>(), _footerSuffix = Array.Empty<byte>();

    internal static FbxBinaryDocument Read(string path, CancellationToken cancellation)
    {
        using var stream = File.OpenRead(path); using var reader = new BinaryReader(stream, Encoding.UTF8, true);
        if (stream.Length > 1024L * 1024 * 1024 || !reader.ReadBytes(23).SequenceEqual(Magic) || reader.ReadUInt32() != 7500)
            throw new InvalidDataException("Unexpected FBX container/version.");
        var result = new FbxBinaryDocument(); var count = 0;
        Node? ReadNode(int depth)
        {
            cancellation.ThrowIfCancellationRequested();
            if (depth > 64 || ++count > 4_000_000 || stream.Position + 25 > stream.Length) throw new InvalidDataException("FBX node limit exceeded.");
            var end = reader.ReadUInt64(); var properties = reader.ReadUInt64(); var length = reader.ReadUInt64(); var nameLength = reader.ReadByte();
            if (end == 0)
            {
                if (properties != 0 || length != 0 || nameLength != 0) throw new InvalidDataException("Invalid FBX terminator.");
                return null;
            }
            if (end > (ulong)stream.Length || end <= (ulong)stream.Position || properties > 1_000_000 || length > end)
                throw new InvalidDataException("Invalid FBX node range.");
            var node = new Node(Encoding.UTF8.GetString(reader.ReadBytes(nameLength)));
            var propertyEnd = checked(stream.Position + (long)length);
            if (propertyEnd > (long)end) throw new InvalidDataException("Invalid FBX properties range.");
            for (ulong i = 0; i < properties; i++)
            {
                var start = stream.Position; object? value = null; var type = (char)reader.ReadByte();
                switch (type)
                {
                    case 'Y': value = reader.ReadInt16(); break;
                    case 'C': value = reader.ReadByte(); break;
                    case 'I': value = reader.ReadInt32(); break;
                    case 'L': value = reader.ReadInt64(); break;
                    case 'F': value = reader.ReadSingle(); break;
                    case 'D': value = reader.ReadDouble(); break;
                    case 'S': case 'R':
                        var size = reader.ReadUInt32();
                        if (size > propertyEnd - stream.Position) throw new InvalidDataException("Invalid FBX string length.");
                        if (type == 'S') value = Encoding.UTF8.GetString(reader.ReadBytes((int)size)); else stream.Position += size;
                        break;
                    case 'f': case 'd': case 'l': case 'i': case 'b': case 'c':
                        reader.ReadUInt32(); var encoding = reader.ReadUInt32(); var bytes = reader.ReadUInt32();
                        if (encoding > 1 || bytes > propertyEnd - stream.Position) throw new InvalidDataException("Invalid FBX array length.");
                        stream.Position += bytes; break;
                    default: throw new InvalidDataException("Unexpected FBX property type.");
                }
                var after = stream.Position;
                if (after > propertyEnd) throw new InvalidDataException("FBX property exceeds node range.");
                stream.Position = start; node.Properties.Add(new(reader.ReadBytes(checked((int)(after - start))), value));
            }
            if (stream.Position != propertyEnd) throw new InvalidDataException("FBX property count mismatch.");
            while (stream.Position < (long)end)
            {
                var child = ReadNode(depth + 1);
                if (child == null) { node.Terminated = true; break; }
                node.Children.Add(child);
            }
            if (stream.Position != (long)end) throw new InvalidDataException("FBX child range mismatch.");
            return node;
        }
        while (true) { var node = ReadNode(0); if (node == null) break; result.Nodes.Add(node); }
        var footer = reader.ReadBytes(checked((int)(stream.Length - stream.Position)));
        if (footer.Length < 160 || footer.Length > 176 || BitConverter.ToUInt32(footer, footer.Length - 140) != 7500)
            throw new InvalidDataException("Unexpected FBX footer.");
        result._footerCode = footer[..16]; result._footerSuffix = footer[^144..];
        return result;
    }

    internal void Write(string path, CancellationToken cancellation)
    {
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write);
        using var writer = new BinaryWriter(stream, Encoding.UTF8, true);
        writer.Write(Magic); writer.Write(7500);
        void WriteNode(Node node)
        {
            cancellation.ThrowIfCancellationRequested();
            var start = stream.Position; var name = Encoding.UTF8.GetBytes(node.Name);
            if (name.Length > 255) throw new InvalidDataException("FBX node name too long.");
            writer.Write(0UL); writer.Write((ulong)node.Properties.Count); writer.Write((ulong)node.Properties.Sum(p => (long)p.Bytes.Length)); writer.Write((byte)name.Length);
            writer.Write(name);
            foreach (var property in node.Properties) writer.Write(property.Bytes);
            foreach (var child in node.Children) WriteNode(child);
            if (node.Terminated || node.Children.Count > 0) writer.Write(new byte[25]);
            var end = stream.Position; stream.Position = start; writer.Write((ulong)end); stream.Position = end;
        }
        foreach (var node in Nodes) WriteNode(node);
        writer.Write(new byte[25]); writer.Write(_footerCode);
        // Assimp emits a full 16-byte pad when the footer code is already aligned.
        writer.Write(new byte[16 - stream.Position % 16]); writer.Write(_footerSuffix);
    }
}
