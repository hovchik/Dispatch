namespace Dispatch.Application.Grpc;

/// <summary>Decodes serialized <c>google.protobuf.FileDescriptorProto</c> messages (as returned by server reflection).</summary>
public static class DescriptorReader
{
    public static FileDesc ReadFile(ReadOnlySpan<byte> data)
    {
        var file = new FileDesc();
        var reader = new ProtoReader(data);
        while (!reader.End)
        {
            var (field, wire) = reader.ReadTag();
            switch (field)
            {
                case 1: file.Name = reader.ReadString(); break;
                case 2: file.Package = reader.ReadString(); break;
                case 3: file.Dependencies.Add(reader.ReadString()); break;
                case 4: file.MessageTypes.Add(ReadMessage(reader.ReadLengthDelimited())); break;
                case 5: file.EnumTypes.Add(ReadEnum(reader.ReadLengthDelimited())); break;
                case 6: file.Services.Add(ReadService(reader.ReadLengthDelimited())); break;
                case 12: file.Syntax = reader.ReadString(); break;
                case 14: // edition (enum)
                    reader.ReadVarint();
                    file.Syntax = "editions";
                    break;
                default: reader.Skip(wire, field); break;
            }
        }
        return file;
    }

    private static MessageDesc ReadMessage(ReadOnlySpan<byte> data)
    {
        var message = new MessageDesc();
        var reader = new ProtoReader(data);
        while (!reader.End)
        {
            var (field, wire) = reader.ReadTag();
            switch (field)
            {
                case 1: message.Name = reader.ReadString(); break;
                case 2: message.Fields.Add(ReadField(reader.ReadLengthDelimited())); break;
                case 3: message.NestedTypes.Add(ReadMessage(reader.ReadLengthDelimited())); break;
                case 4: message.EnumTypes.Add(ReadEnum(reader.ReadLengthDelimited())); break;
                case 8: message.Oneofs.Add(ReadName(reader.ReadLengthDelimited())); break;
                case 7: message.IsMapEntry = ReadBoolOption(reader.ReadLengthDelimited(), 7); break;
                default: reader.Skip(wire, field); break;
            }
        }
        return message;
    }

    private static FieldDesc ReadField(ReadOnlySpan<byte> data)
    {
        var f = new FieldDesc();
        var reader = new ProtoReader(data);
        while (!reader.End)
        {
            var (field, wire) = reader.ReadTag();
            switch (field)
            {
                case 1: f.Name = reader.ReadString(); break;
                case 3: f.Number = (int)reader.ReadVarint(); break;
                case 4: f.Label = (ProtoLabel)reader.ReadVarint(); break;
                case 5: f.Type = (ProtoType)reader.ReadVarint(); break;
                case 6: f.TypeName = reader.ReadString(); break;
                case 7: f.DefaultValue = reader.ReadString(); break;
                case 8:
                    var options = reader.ReadLengthDelimited();
                    if (HasOption(options, 2))
                        f.Packed = ReadBoolOption(options, 2);
                    break;
                case 9: f.OneofIndex = (int)reader.ReadVarint(); break;
                case 10: f.JsonName = reader.ReadString(); break;
                case 17: f.Proto3Optional = reader.ReadVarint() != 0; break;
                default: reader.Skip(wire, field); break;
            }
        }
        return f;
    }

    private static EnumDesc ReadEnum(ReadOnlySpan<byte> data)
    {
        var e = new EnumDesc();
        var reader = new ProtoReader(data);
        while (!reader.End)
        {
            var (field, wire) = reader.ReadTag();
            switch (field)
            {
                case 1: e.Name = reader.ReadString(); break;
                case 2:
                    var valueReader = new ProtoReader(reader.ReadLengthDelimited());
                    string name = "";
                    var number = 0;
                    while (!valueReader.End)
                    {
                        var (vf, vw) = valueReader.ReadTag();
                        if (vf == 1) name = valueReader.ReadString();
                        else if (vf == 2) number = (int)valueReader.ReadVarint();
                        else valueReader.Skip(vw, vf);
                    }
                    e.Values.Add((name, number));
                    break;
                default: reader.Skip(wire, field); break;
            }
        }
        return e;
    }

    private static ServiceDesc ReadService(ReadOnlySpan<byte> data)
    {
        var service = new ServiceDesc();
        var reader = new ProtoReader(data);
        while (!reader.End)
        {
            var (field, wire) = reader.ReadTag();
            switch (field)
            {
                case 1: service.Name = reader.ReadString(); break;
                case 2:
                    var m = new MethodDesc();
                    var methodReader = new ProtoReader(reader.ReadLengthDelimited());
                    while (!methodReader.End)
                    {
                        var (mf, mw) = methodReader.ReadTag();
                        switch (mf)
                        {
                            case 1: m.Name = methodReader.ReadString(); break;
                            case 2: m.InputType = methodReader.ReadString(); break;
                            case 3: m.OutputType = methodReader.ReadString(); break;
                            case 5: m.ClientStreaming = methodReader.ReadVarint() != 0; break;
                            case 6: m.ServerStreaming = methodReader.ReadVarint() != 0; break;
                            default: methodReader.Skip(mw, mf); break;
                        }
                    }
                    service.Methods.Add(m);
                    break;
                default: reader.Skip(wire, field); break;
            }
        }
        return service;
    }

    private static string ReadName(ReadOnlySpan<byte> data)
    {
        var reader = new ProtoReader(data);
        while (!reader.End)
        {
            var (field, wire) = reader.ReadTag();
            if (field == 1)
                return reader.ReadString();
            reader.Skip(wire, field);
        }
        return "";
    }

    private static bool HasOption(ReadOnlySpan<byte> data, int number)
    {
        var reader = new ProtoReader(data);
        while (!reader.End)
        {
            var (field, wire) = reader.ReadTag();
            if (field == number)
                return true;
            reader.Skip(wire, field);
        }
        return false;
    }

    private static bool ReadBoolOption(ReadOnlySpan<byte> data, int number)
    {
        var reader = new ProtoReader(data);
        var value = false;
        while (!reader.End)
        {
            var (field, wire) = reader.ReadTag();
            if (field == number && wire == WireType.Varint)
                value = reader.ReadVarint() != 0;
            else
                reader.Skip(wire, field);
        }
        return value;
    }
}
