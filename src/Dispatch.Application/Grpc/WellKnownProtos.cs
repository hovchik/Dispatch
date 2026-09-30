namespace Dispatch.Application.Grpc;

/// <summary>
/// Built-in sources for google/protobuf well-known types and commonly imported option-only files
/// (descriptor.proto, google/api annotations), so .proto files importing them parse without extra setup.
/// </summary>
public static class WellKnownProtos
{
    public static readonly IReadOnlyDictionary<string, string> Sources = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["google/protobuf/empty.proto"] = """
            syntax = "proto3";
            package google.protobuf;
            message Empty {}
            """,
        ["google/protobuf/timestamp.proto"] = """
            syntax = "proto3";
            package google.protobuf;
            message Timestamp { int64 seconds = 1; int32 nanos = 2; }
            """,
        ["google/protobuf/duration.proto"] = """
            syntax = "proto3";
            package google.protobuf;
            message Duration { int64 seconds = 1; int32 nanos = 2; }
            """,
        ["google/protobuf/wrappers.proto"] = """
            syntax = "proto3";
            package google.protobuf;
            message DoubleValue { double value = 1; }
            message FloatValue { float value = 1; }
            message Int64Value { int64 value = 1; }
            message UInt64Value { uint64 value = 1; }
            message Int32Value { int32 value = 1; }
            message UInt32Value { uint32 value = 1; }
            message BoolValue { bool value = 1; }
            message StringValue { string value = 1; }
            message BytesValue { bytes value = 1; }
            """,
        ["google/protobuf/struct.proto"] = """
            syntax = "proto3";
            package google.protobuf;
            message Struct { map<string, Value> fields = 1; }
            message Value {
              oneof kind {
                NullValue null_value = 1;
                double number_value = 2;
                string string_value = 3;
                bool bool_value = 4;
                Struct struct_value = 5;
                ListValue list_value = 6;
              }
            }
            enum NullValue { NULL_VALUE = 0; }
            message ListValue { repeated Value values = 1; }
            """,
        ["google/protobuf/any.proto"] = """
            syntax = "proto3";
            package google.protobuf;
            message Any { string type_url = 1; bytes value = 2; }
            """,
        ["google/protobuf/field_mask.proto"] = """
            syntax = "proto3";
            package google.protobuf;
            message FieldMask { repeated string paths = 1; }
            """,
        ["google/protobuf/descriptor.proto"] = """
            syntax = "proto2";
            package google.protobuf;
            message FileOptions {}
            message MessageOptions {}
            message FieldOptions {}
            message OneofOptions {}
            message EnumOptions {}
            message EnumValueOptions {}
            message ServiceOptions {}
            message MethodOptions {}
            """,
        ["google/api/annotations.proto"] = """
            syntax = "proto3";
            package google.api;
            import "google/api/http.proto";
            import "google/protobuf/descriptor.proto";
            """,
        ["google/api/http.proto"] = """
            syntax = "proto3";
            package google.api;
            message Http { repeated HttpRule rules = 1; }
            message HttpRule {
              string selector = 1;
              oneof pattern { string get = 2; string put = 3; string post = 4; string delete = 5; string patch = 6; }
              string body = 7;
              string response_body = 12;
              repeated HttpRule additional_bindings = 11;
            }
            """,
        ["google/api/field_behavior.proto"] = """
            syntax = "proto3";
            package google.api;
            import "google/protobuf/descriptor.proto";
            enum FieldBehavior { FIELD_BEHAVIOR_UNSPECIFIED = 0; OPTIONAL = 1; REQUIRED = 2; OUTPUT_ONLY = 3; INPUT_ONLY = 4; IMMUTABLE = 5; }
            """,
        ["google/api/client.proto"] = """
            syntax = "proto3";
            package google.api;
            import "google/protobuf/descriptor.proto";
            """
    };

    /// <summary>All built-in files, parsed (added to every schema so well-known types always resolve).</summary>
    public static IReadOnlyList<FileDesc> Parsed() =>
        Sources.Where(s => s.Key.StartsWith("google/protobuf/", StringComparison.Ordinal))
            .Select(s => ProtoParser.Parse(s.Value, s.Key))
            .ToList();
}
