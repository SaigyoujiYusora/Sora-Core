using System.Buffers.Binary;
using System.Text;
using Sora.Core.Vendor;

namespace Sora.Core;




public sealed record SerializedObject(long Id, int ClassId, object? Data);
public sealed record SerializedDocument(string UnityVersion, string[] ExternalFiles, SerializedObject[] Objects);

public static class SerializedAssets
{
    private sealed record Node(byte Level, string Type, string Name, int Size, int Flags);
    private sealed record TypeDescription(int ClassId, Node[] Nodes);
    private sealed record ObjectLocation(long Id, int Start, int Length, int Type);

    public static SerializedDocument Decode(byte[] bytes)
    {
        Validation.Require(bytes.Length <= DatabaseFile.MaxPayloadBytes && bytes.Length >= 48, "Invalid serialized asset size");
        var header = new Cursor(bytes, true);
        header.Skip(8);
        Validation.Require(header.U32() == 22, "Only serialized asset version 22 is supported");
        header.Skip(4);
        byte endian = header.U8(); Validation.Require(endian <= 1, "Invalid serialized asset endianness");
        header.Skip(3);
        uint metadataSize = header.U32();
        Validation.Require(metadataSize <= 64 * 1024 * 1024, "Serialized metadata exceeds limit");
        ulong fileSize = header.U64(), dataStart = header.U64(); header.Skip(8);
        Validation.Require(fileSize == (ulong)bytes.Length && dataStart >= 48 && dataStart <= fileSize && metadataSize <= dataStart - 48, "Invalid serialized asset sections");
        var metadata = new Cursor(bytes.AsSpan(48, (int)metadataSize).ToArray(), endian == 1);
        string version = metadata.Text(); metadata.Skip(4);
        byte typeTree = metadata.U8(); Validation.Require(typeTree == 1, "Serialized asset has no embedded type tree");
        int typeCount = metadata.Count(23);
        Validation.Require(typeCount <= 4096, "Serialized type limit exceeded");
        int totalNodes = 0;
        var types = new TypeDescription[typeCount];
        for (int type = 0; type < typeCount; type++)
        {
            int classId = metadata.I32(); metadata.Skip(1); metadata.Skip(2);
            if (classId == 114) metadata.Skip(16);
            metadata.Skip(16);
            int nodeCount = metadata.Count(32), stringBytes = metadata.I32();
            totalNodes = checked(totalNodes + nodeCount);
            Validation.Require(totalNodes <= 262144, "Serialized type-tree node limit exceeded");
            Validation.Require(nodeCount > 0 && nodeCount <= 65536 && stringBytes >= 0, "Invalid embedded type tree size");
            var rawNodes = metadata.Take(checked(nodeCount * 32));
            var strings = metadata.Take(stringBytes);
            var nodeReader = new Cursor(rawNodes, endian == 1);
            var nodes = new Node[nodeCount];
            for (int node = 0; node < nodeCount; node++)
            {
                nodeReader.Skip(2); byte level = nodeReader.U8(); nodeReader.Skip(1);
                string typeName = Resolve(nodeReader.U32(), strings);
                string name = Resolve(nodeReader.U32(), strings);
                int size = nodeReader.I32(); nodeReader.Skip(4); int flags = nodeReader.I32(); nodeReader.Skip(8);
                Validation.Require(level <= 63 && (node == 0 ? level == 0 : level > 0 && level <= nodes[node - 1].Level + 1), "Invalid type-tree depth");
                nodes[node] = new(level, typeName, name, size, flags);
            }
            int dependencyCount = metadata.Count(4); metadata.Skip(dependencyCount * 4);
            types[type] = new(classId, nodes);
        }
        int objectCount = metadata.Count(24);
        Validation.Require(objectCount <= 250000, "Serialized object count limit exceeded");
        var locations = new ObjectLocation[objectCount];
        var ids = new HashSet<long>();
        for (int i = 0; i < objectCount; i++)
        {
            metadata.Align(); long id = unchecked((long)metadata.U64()); ulong offset = metadata.U64(); uint length = metadata.U32(); int type = metadata.I32();
            Validation.Require(type >= 0 && type < types.Length && ids.Add(id) && offset <= fileSize - dataStart && length <= fileSize - dataStart - offset, "Invalid serialized object location");
            locations[i] = new(id, checked((int)(dataStart + offset)), (int)length, type);
        }
        int scripts = metadata.Count(12);
        Validation.Require(scripts <= 65536, "Serialized script reference limit exceeded");
        for (int i = 0; i < scripts; i++) { metadata.Skip(4); metadata.Align(); metadata.Skip(8); }
        int externalCount = metadata.Count(22);
        Validation.Require(externalCount <= 65536, "Serialized external reference limit exceeded");
        var externalFiles = new string[externalCount];
        for (int i = 0; i < externalCount; i++) { metadata.Text(); metadata.Skip(20); externalFiles[i] = metadata.Text(); }
        int referenceCount = metadata.Count(23); Validation.Require(referenceCount <= 4096, "Referenced type limit exceeded"); var references = new Dictionary<(string, string, string), Node[]>();
        for (int type = 0; type < referenceCount; type++)
        {
            int classId = metadata.I32(); metadata.Skip(1); short scriptIndex = unchecked((short)metadata.U16());
            if (scriptIndex >= 0) metadata.Skip(16);
            metadata.Skip(16);
            int nodeCount = metadata.Count(32), stringBytes = metadata.I32();
            totalNodes = checked(totalNodes + nodeCount);
            Validation.Require(totalNodes <= 262144, "Serialized type-tree node limit exceeded");
            Validation.Require(nodeCount > 0 && nodeCount <= 65536 && stringBytes >= 0, "Invalid embedded type tree size");
            var rawNodes = metadata.Take(checked(nodeCount * 32));
            var strings = metadata.Take(stringBytes);
            var nodeReader = new Cursor(rawNodes, endian == 1);
            var nodes = new Node[nodeCount];
            for (int node = 0; node < nodeCount; node++)
            {
                nodeReader.Skip(2); byte level = nodeReader.U8(); nodeReader.Skip(1);
                string typeName = Resolve(nodeReader.U32(), strings);
                string name = Resolve(nodeReader.U32(), strings);
                int size = nodeReader.I32(); nodeReader.Skip(4); int flags = nodeReader.I32(); nodeReader.Skip(8);
                Validation.Require(level <= 63 && (node == 0 ? level == 0 : level > 0 && level <= nodes[node - 1].Level + 1), "Invalid type-tree depth");
                nodes[node] = new(level, typeName, name, size, flags);
            }
            string className = metadata.Text(), ns = metadata.Text(), assembly = metadata.Text();
            Validation.Require(references.TryAdd((className, ns, assembly), nodes), "Duplicate referenced serialized type");
        }

        metadata.Text();
        Validation.Require(metadata.Remaining == 0, "Trailing serialized metadata");
        var result = new SerializedObject[objectCount];
        long precedingEnd = (long)dataStart;
        foreach (var location in locations.OrderBy(x => x.Start))
        {
            Validation.Require(location.Start >= precedingEnd, "Serialized object ranges overlap");
            precedingEnd = (long)location.Start + location.Length;
        }
        var budget = new DecodeBudget();
        for (int i = 0; i < objectCount; i++)
        {
            var location = locations[i]; var type = types[location.Type];
            var payload = new Cursor(bytes.AsSpan(location.Start, location.Length).ToArray(), endian == 1);
            object? value = ReadValue(type.Nodes, 0, payload, budget, references);
            Validation.Require(payload.Remaining == 0, "Object payload does not match embedded type tree");
            result[i] = new(location.Id, type.ClassId, value);
        }
        return new(version, externalFiles, result);
    }

    private static string Resolve(uint offset, byte[] strings)
    {
        if ((offset & 0x80000000) != 0)
            return CommonString.StringBuffer.TryGetValue(offset & 0x7fffffff, out var value) ? value : throw new InvalidDataException("Unknown common type-tree string");
        Validation.Require(offset < strings.Length, "Type-tree string offset out of bounds");
        var cursor = new Cursor(strings, false) { Position = (int)offset }; return cursor.Text();
    }

    private static int End(Node[] nodes, int index)
    {
        int end = index + 1;
        while (end < nodes.Length && nodes[end].Level > nodes[index].Level) end++;
        return end;
    }

    private sealed class DecodeBudget
    {
        public int Elements = 8_000_000;
        private int bytes = 128 * 1024 * 1024;
        public void TakeBytes(int count)
        {
            Validation.Require(count >= 0 && count <= bytes, "Serialized raw data byte budget exceeded");
            bytes -= count;
        }
    }

    private static object? ReadValue(Node[] nodes, int index, Cursor data, DecodeBudget budget, Dictionary<(string,string,string),Node[]> references, bool managedPayload = false, int depth = 0)
    {
        Validation.Require(depth <= 128, "Serialized managed reference nesting limit exceeded");
        Validation.Require(--budget.Elements >= 0, "Serialized object element budget exceeded");
        var node = nodes[index]; object? value;
        // A referenced type tree may include the host registry schema, but its payload is stored only on the host.
        if(node.Type == "ManagedReferencesRegistry" && managedPayload) return null;
        if (node.Type == "ReferencedObject")
        {
            var fields = new Dictionary<string, object?>(StringComparer.Ordinal);
            int end = End(nodes,index);
            for(int child=index+1;child<end;child=End(nodes,child)) {
                object? field;
                if(nodes[child].Type == "ReferencedObjectData") {
                    var identity=(Dictionary<string,object?>)fields["type"]!;
                    var key=((string)identity["class"]!, (string)identity["ns"]!, (string)identity["asm"]!);
                    if((long)fields["rid"]! is -1 or -2) field=null;
                    else {
                        Validation.Require(references.TryGetValue(key,out var schema), "Unresolved managed reference type: "+key);
                        field=ReadValue(schema!,0,data,budget,references,true,depth + 1);
                    }
                } else field=ReadValue(nodes,child,data,budget,references,managedPayload,depth + 1);
                Validation.Require(fields.TryAdd(nodes[child].Name,field),"Duplicate reference field");
            }
            value=fields;
        }
        else if (node.Type == "string")
        {
            int count = data.Count(1); budget.TakeBytes(count); value = new UTF8Encoding(false, true).GetString(data.Take(count)); data.Align();
        }
        else if (node.Type == "TypelessData")
        {
            int count = data.Count(1); budget.TakeBytes(count); value = data.Take(count);
        }
        else if (node.Type == "Array")
        {
            Validation.Require(index + 2 < nodes.Length && nodes[index + 1].Name == "size", "Invalid type-tree array schema");
            int count = data.Count(1);
            if (nodes[index + 2].Type is "UInt8" or "char") { budget.TakeBytes(count); value = data.Take(count); }
            else
            {
                Validation.Require(count <= budget.Elements, "Serialized array element budget exceeded");
                var array = new object?[count];
                for (int item = 0; item < count; item++) array[item] = ReadValue(nodes, index + 2, data, budget, references, managedPayload, depth + 1);
                value = array;
            }
        }
        else
        {
            value = node.Type switch
            {
                "bool" => data.U8() != 0,
                "char" or "UInt8" => data.U8(), "SInt8" => unchecked((sbyte)data.U8()),
                "short" or "SInt16" => unchecked((short)data.U16()), "unsigned short" or "UInt16" => data.U16(),
                "int" or "SInt32" => data.I32(), "unsigned int" or "UInt32" or "Type*" => data.U32(),
                "long long" or "SInt64" => unchecked((long)data.U64()), "unsigned long long" or "UInt64" or "FileSize" => data.U64(),
                "float" => BitConverter.Int32BitsToSingle(data.I32()), "double" => BitConverter.Int64BitsToDouble(unchecked((long)data.U64())),
                _ => null
            };
            if (value is null)
            {
                int end = End(nodes, index);
                if (end == index + 1)
                {
                    Validation.Require(node.Size >= 0, "Unsupported leaf type: " + node.Type); budget.TakeBytes(node.Size); value = data.Take(node.Size);
                }
                else
                {
                    var fields = new Dictionary<string, object?>(StringComparer.Ordinal);
                    for (int child = index + 1; child < end; child = End(nodes, child))
                        Validation.Require(fields.TryAdd(nodes[child].Name, ReadValue(nodes, child, data, budget, references, managedPayload, depth + 1)), "Duplicate type-tree field");
                    if (node.Type == "ManagedReferencesRegistry")
                        Validation.Require(fields.TryGetValue("version", out var registryVersion) && registryVersion is int v && v == 2, "Only managed reference registry version 2 is supported");
                    value = fields;
                }
            }
        }
        if ((node.Flags & 0x4000) != 0) data.Align();
        return value;
    }

    private sealed class Cursor(byte[] bytes, bool big)
    {
        public int Position { get; set; }
        public int Remaining => bytes.Length - Position;
        public byte[] Take(int length) { Validation.Require(length >= 0 && length <= Remaining, "Serialized read exceeds section"); var result = bytes.AsSpan(Position, length).ToArray(); Position += length; return result; }
        public void Skip(int length) { Validation.Require(length >= 0 && length <= Remaining, "Serialized skip exceeds section"); Position += length; }
        public void Align() => Skip((-Position) & 3);
        public byte U8() { Validation.Require(Remaining >= 1, "Truncated serialized byte"); return bytes[Position++]; }
        public ushort U16() { byte[] value = Take(2); return big ? BinaryPrimitives.ReadUInt16BigEndian(value) : BinaryPrimitives.ReadUInt16LittleEndian(value); }
        public uint U32() { byte[] value = Take(4); return big ? BinaryPrimitives.ReadUInt32BigEndian(value) : BinaryPrimitives.ReadUInt32LittleEndian(value); }
        public int I32() => unchecked((int)U32());
        public ulong U64() { byte[] value = Take(8); return big ? BinaryPrimitives.ReadUInt64BigEndian(value) : BinaryPrimitives.ReadUInt64LittleEndian(value); }
        public int Count(int minimumBytes) { int count = I32(); Validation.Require(count >= 0 && count <= Remaining / minimumBytes, "Invalid serialized count"); return count; }
        public string Text()
        {
            int end = Array.IndexOf(bytes, (byte)0, Position);
            Validation.Require(end >= Position && end - Position <= 32768, "Invalid serialized string");
            string value = new UTF8Encoding(false, true).GetString(bytes, Position, end - Position); Position = end + 1; return value;
        }
    }
}




