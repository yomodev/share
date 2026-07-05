using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using Google.Protobuf.Reflection;
using NxtUI.Core.Services.Kafka;

namespace NxtUI.Protos;

/// <summary>
/// Dynamically deserialises protobuf messages using .proto schema files parsed
/// at runtime — no generated C# classes involved. Field descriptors drive a
/// hand-rolled wire-format walk (varint/fixed32/fixed64/length-delimited)
/// straight into a JSON object graph.
///
/// Schema files are parsed once and cached for the process lifetime (see
/// KafkaSettings.ProtoSchemaFolder). Restart the app to pick up .proto edits.
/// </summary>
public sealed class MessageRegistry : IMessageRegistry
{
    // Keyed by short message name (e.g. "OrderEvent") — the public lookup API,
    // matching topic-deserializer config and legacy call sites.
    private readonly Dictionary<string, DescriptorProto> _byShortName =
        new(StringComparer.OrdinalIgnoreCase);

    // Keyed by fully-qualified name with leading dot (e.g. ".nxtui.OrderEvent")
    // — how FieldDescriptorProto.TypeName references nested/cross-file types.
    private readonly Dictionary<string, DescriptorProto> _messagesByFqn = new();
    private readonly Dictionary<string, EnumDescriptorProto> _enumsByFqn = new();

    private MessageRegistry() { }

    /// <summary>Parses every .proto file in <paramref name="folder"/> (default: the folder bundled with this assembly).</summary>
    public static MessageRegistry Create(string? folder = null)
    {
        folder ??= Path.Combine(AppContext.BaseDirectory, "Protos");

        var registry = new MessageRegistry();
        var set = new FileDescriptorSet();

        if (Directory.Exists(folder))
        {
            foreach (var path in Directory.GetFiles(folder, "*.proto"))
            {
                using var reader = new StreamReader(path);
                set.Add(Path.GetFileName(path), true, reader);
            }
        }

        set.Process();
        var errors = set.GetErrors();
        if (errors.Length > 0)
            throw new InvalidOperationException(
                "Failed to parse .proto schema files: " +
                string.Join("; ", errors.Select(e => $"{e.File}:{e.LineNumber} {e.Message}")));

        foreach (var file in set.Files)
            registry.IndexFile(file);

        return registry;
    }

    private void IndexFile(FileDescriptorProto file)
    {
        var prefix = string.IsNullOrEmpty(file.Package) ? "" : "." + file.Package;
        foreach (var msg in file.MessageTypes)
            IndexMessage(msg, prefix, isTopLevel: true);
        foreach (var en in file.EnumTypes)
            _enumsByFqn[prefix + "." + en.Name] = en;
    }

    private void IndexMessage(DescriptorProto msg, string parentFqn, bool isTopLevel)
    {
        var fqn = parentFqn + "." + msg.Name;
        _messagesByFqn[fqn] = msg;

        // Map-entry synthetic types and other nested types aren't useful as
        // top-level lookup targets — only index true top-level message names.
        if (isTopLevel && msg.Options?.MapEntry != true)
            _byShortName.TryAdd(msg.Name, msg);

        foreach (var nested in msg.NestedTypes)
            IndexMessage(nested, fqn, isTopLevel: false);
        foreach (var en in msg.EnumTypes)
            _enumsByFqn[fqn + "." + en.Name] = en;
    }

    /// <inheritdoc/>
    public IReadOnlyCollection<string> RegisteredTypes => _byShortName.Keys;

    /// <inheritdoc/>
    public bool TryParseToJson(string typeName, byte[] bytes, out string? json)
    {
        if (!_byShortName.TryGetValue(typeName, out var descriptor))
        {
            json = null;
            return false;
        }
        try
        {
            var obj = DecodeMessage(descriptor, bytes);
            json = obj.ToJsonString();
            return true;
        }
        catch
        {
            json = null;
            return false;
        }
    }

    // ── Wire format decode ──────────────────────────────────────────────────

    private JsonObject DecodeMessage(DescriptorProto desc, ReadOnlySpan<byte> data)
    {
        var obj = new JsonObject();
        var pos = 0;

        while (pos < data.Length)
        {
            var tag = ReadVarint(data, ref pos);
            var fieldNum = (int)(tag >> 3);
            var wireType = (int)(tag & 7);
            var field = FindField(desc, fieldNum);

            object? value = wireType switch
            {
                0 => DecodeVarintTyped(field, ReadVarint(data, ref pos)),
                1 => DecodeFixed64Typed(field, ReadUInt64LE(data, ref pos)),
                5 => DecodeFixed32Typed(field, ReadUInt32LE(data, ref pos)),
                2 => DecodeLengthDelimited(field, ReadLengthDelimited(data, ref pos)),
                _ => null, // groups (3/4) and anything else — not supported, message is truncated here
            };

            if (wireType is not (0 or 1 or 2 or 5)) break;
            if (field is null) continue; // unknown field number — skip, already consumed

            ApplyField(obj, field, value);
        }

        return obj;
    }

    private void ApplyField(JsonObject obj, FieldDescriptorProto field, object? value)
    {
        var name = JsonFieldName(field);

        // proto3 maps are encoded as `repeated MapEntry { key = 1; value = 2; }`
        // — reassemble into a real JSON object instead of an array of pairs.
        if (field.label == FieldDescriptorProto.Label.LabelRepeated &&
            value is MapEntry entry)
        {
            var mapObj = obj[name] as JsonObject ?? (JsonObject)(obj[name] = new JsonObject());
            mapObj[entry.Key] = entry.Value;
            return;
        }

        if (field.label == FieldDescriptorProto.Label.LabelRepeated)
        {
            var arr = obj[name] as JsonArray ?? (JsonArray)(obj[name] = new JsonArray());
            if (value is JsonArray packed) // packed scalar repeated field decoded in one shot
                foreach (var item in packed.ToList())
                    arr.Add(item?.DeepClone());
            else
                arr.Add(ToJsonNode(value));
            return;
        }

        obj[name] = ToJsonNode(value);
    }

    private sealed record MapEntry(string Key, JsonNode? Value);

    private FieldDescriptorProto? FindField(DescriptorProto desc, int number) =>
        desc.Fields.FirstOrDefault(f => f.Number == number);

    private static string JsonFieldName(FieldDescriptorProto field) =>
        !string.IsNullOrEmpty(field.JsonName) ? field.JsonName : ToCamelCase(field.Name);

    private static string ToCamelCase(string snake)
    {
        var parts = snake.Split('_', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return snake;
        var sb = new StringBuilder(parts[0]);
        for (var i = 1; i < parts.Length; i++)
            sb.Append(char.ToUpperInvariant(parts[i][0])).Append(parts[i][1..]);
        return sb.ToString();
    }

    // ── Scalar decoding per FieldDescriptorProto.Type ───────────────────────

    private object? DecodeVarintTyped(FieldDescriptorProto? field, ulong raw) => field?.type switch
    {
        FieldDescriptorProto.Type.TypeBool => raw != 0,
        FieldDescriptorProto.Type.TypeInt32 => unchecked((int)raw),
        FieldDescriptorProto.Type.TypeInt64 => unchecked((long)raw),
        FieldDescriptorProto.Type.TypeUint32 => unchecked((uint)raw),
        FieldDescriptorProto.Type.TypeUint64 => raw,
        FieldDescriptorProto.Type.TypeSint32 => ZigZagDecode32((uint)raw),
        FieldDescriptorProto.Type.TypeSint64 => ZigZagDecode64(raw),
        FieldDescriptorProto.Type.TypeEnum => EnumName(field.TypeName, unchecked((int)raw)),
        null => (long)raw, // unknown field, best-effort raw value
        _ => (long)raw,
    };

    private object? DecodeFixed64Typed(FieldDescriptorProto? field, ulong raw) => field?.type switch
    {
        FieldDescriptorProto.Type.TypeDouble => BitConverter.Int64BitsToDouble(unchecked((long)raw)),
        FieldDescriptorProto.Type.TypeSfixed64 => unchecked((long)raw),
        _ => raw, // TypeFixed64 or unknown
    };

    private object? DecodeFixed32Typed(FieldDescriptorProto? field, uint raw) => field?.type switch
    {
        FieldDescriptorProto.Type.TypeFloat => BitConverter.Int32BitsToSingle(unchecked((int)raw)),
        FieldDescriptorProto.Type.TypeSfixed32 => unchecked((int)raw),
        _ => raw, // TypeFixed32 or unknown
    };

    private object? DecodeLengthDelimited(FieldDescriptorProto? field, ReadOnlySpan<byte> bytes)
    {
        switch (field?.type)
        {
            case FieldDescriptorProto.Type.TypeString:
                return Encoding.UTF8.GetString(bytes);

            case FieldDescriptorProto.Type.TypeBytes:
                // Raw bytes are opaque and often large — surfacing the base64 blob in
                // the JSON viewer is noise, not signal. Show just the length instead.
                return $"byte[{bytes.Length}]";

            case FieldDescriptorProto.Type.TypeMessage:
                var nested = ResolveMessage(field.TypeName);
                if (nested is null) return null;
                if (nested.Options?.MapEntry == true)
                {
                    var entryObj = DecodeMessage(nested, bytes);
                    return new MapEntry(entryObj["key"]?.ToString() ?? "", entryObj["value"]?.DeepClone());
                }
                if (TryDecodeWellKnown(field.TypeName, nested, bytes, out var wellKnown))
                    return wellKnown;
                return DecodeMessage(nested, bytes);

            case null:
                // Unknown field with no schema info — keep as base64 for inspection.
                return Convert.ToBase64String(bytes);

            default:
                // Repeated scalar field encoded packed (wire type 2 instead of its
                // normal scalar wire type) — decode as a run of values.
                if (field.label == FieldDescriptorProto.Label.LabelRepeated)
                    return DecodePacked(field, bytes);
                return Convert.ToBase64String(bytes);
        }
    }

    private JsonArray DecodePacked(FieldDescriptorProto field, ReadOnlySpan<byte> bytes)
    {
        var arr = new JsonArray();
        var pos = 0;
        while (pos < bytes.Length)
        {
            object? value = field.type switch
            {
                FieldDescriptorProto.Type.TypeFloat or FieldDescriptorProto.Type.TypeSfixed32
                    => DecodeFixed32Typed(field, ReadUInt32LE(bytes, ref pos)),
                FieldDescriptorProto.Type.TypeDouble or FieldDescriptorProto.Type.TypeSfixed64
                    => DecodeFixed64Typed(field, ReadUInt64LE(bytes, ref pos)),
                _ => DecodeVarintTyped(field, ReadVarint(bytes, ref pos)),
            };
            arr.Add(ToJsonNode(value));
        }
        return arr;
    }

    private DescriptorProto? ResolveMessage(string fqn) =>
        _messagesByFqn.TryGetValue(fqn, out var d) ? d : null;

    private static readonly HashSet<string> WrapperTypeNames = new(StringComparer.Ordinal)
    {
        ".google.protobuf.StringValue", ".google.protobuf.BytesValue",
        ".google.protobuf.Int32Value",  ".google.protobuf.Int64Value",
        ".google.protobuf.UInt32Value", ".google.protobuf.UInt64Value",
        ".google.protobuf.FloatValue",  ".google.protobuf.DoubleValue",
        ".google.protobuf.BoolValue",
    };

    /// <summary>
    /// Renders well-known wrapper/Timestamp/Duration types the way proto3 JSON
    /// mapping does — as plain scalars/strings — instead of as nested objects.
    /// Wrapper types (Int32Value, StringValue, ...) are how proto3 represents a
    /// nullable primitive: a message field that's either absent (null) or
    /// present with one "value" field.
    /// </summary>
    private bool TryDecodeWellKnown(string typeName, DescriptorProto nested, ReadOnlySpan<byte> bytes, out JsonNode? result)
    {
        if (typeName == ".google.protobuf.Timestamp")
        {
            var obj = DecodeMessage(nested, bytes);
            var seconds = obj["seconds"]?.GetValue<long>() ?? 0L;
            var nanos = obj["nanos"]?.GetValue<int>() ?? 0;
            var dt = DateTimeOffset.FromUnixTimeSeconds(seconds).UtcDateTime.AddTicks(nanos / 100);
            result = JsonValue.Create(dt.ToString("yyyy-MM-ddTHH:mm:ss.FFFFFFFZ", CultureInfo.InvariantCulture));
            return true;
        }

        if (typeName == ".google.protobuf.Duration")
        {
            var obj = DecodeMessage(nested, bytes);
            var seconds = obj["seconds"]?.GetValue<long>() ?? 0L;
            var nanos = obj["nanos"]?.GetValue<int>() ?? 0;
            var total = seconds + nanos / 1_000_000_000.0;
            result = JsonValue.Create(total.ToString("0.#########", CultureInfo.InvariantCulture) + "s");
            return true;
        }

        if (WrapperTypeNames.Contains(typeName))
        {
            // The wrapper message being present at all means "not null" — but
            // proto3 omits zero-value scalars from the wire, so an absent inner
            // "value" field here means the wrapped value IS the type's default
            // (0 / "" / false), not that the whole field should render as null.
            var obj = DecodeMessage(nested, bytes);
            var inner = obj["value"];
            result = inner is not null ? inner.DeepClone() : DefaultWrapperValue(typeName);
            return true;
        }

        result = null;
        return false;
    }

    private static JsonNode? DefaultWrapperValue(string typeName) => typeName switch
    {
        ".google.protobuf.StringValue" => JsonValue.Create(""),
        ".google.protobuf.BytesValue" => JsonValue.Create("byte[0]"),
        ".google.protobuf.BoolValue" => JsonValue.Create(false),
        ".google.protobuf.FloatValue" => JsonValue.Create(0f),
        ".google.protobuf.DoubleValue" => JsonValue.Create(0d),
        ".google.protobuf.Int32Value" => JsonValue.Create(0),
        ".google.protobuf.Int64Value" => JsonValue.Create(0L),
        ".google.protobuf.UInt32Value" => JsonValue.Create(0u),
        ".google.protobuf.UInt64Value" => JsonValue.Create(0UL),
        _ => null,
    };

    private string EnumName(string fqn, int number)
    {
        if (_enumsByFqn.TryGetValue(fqn, out var e))
        {
            var match = e.Values.FirstOrDefault(v => v.Number == number);
            if (match is not null) return match.Name;

            // No exact match — proto has no official "flags enum" concept, but declaring
            // power-of-two values and OR-ing them on the wire is a common convention for
            // bitmask fields. Decompose into the declared flags that are actually set;
            // if some bits aren't covered by any declared value, fall through to the raw
            // number rather than silently reporting a partial/misleading combination.
            if (number != 0)
            {
                var setFlags = e.Values
                    .Where(v => v.Number != 0 && (number & v.Number) == v.Number)
                    .OrderBy(v => v.Number)
                    .ToList();

                var covered = setFlags.Aggregate(0, (acc, v) => acc | v.Number);
                if (setFlags.Count > 0 && covered == number)
                    return string.Join(" | ", setFlags.Select(v => v.Name));
            }
        }
        return number.ToString(); // unknown enum value — fall back to the raw number
    }

    private static JsonNode? ToJsonNode(object? value) => value switch
    {
        null => null,
        JsonNode node => node,
        bool b => JsonValue.Create(b),
        string s => JsonValue.Create(s),
        int i => JsonValue.Create(i),
        long l => JsonValue.Create(l),
        uint ui => JsonValue.Create(ui),
        ulong ul => JsonValue.Create(ul),
        double d => JsonValue.Create(d),
        float f => JsonValue.Create(f),
        _ => JsonValue.Create(value.ToString()),
    };

    // ── Low-level wire readers ───────────────────────────────────────────────

    private static ulong ReadVarint(ReadOnlySpan<byte> data, ref int pos)
    {
        ulong result = 0;
        var shift = 0;
        while (true)
        {
            var b = data[pos++];
            result |= (ulong)(b & 0x7F) << shift;
            if ((b & 0x80) == 0) break;
            shift += 7;
        }
        return result;
    }

    private static uint ReadUInt32LE(ReadOnlySpan<byte> data, ref int pos)
    {
        var v = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(data[pos..]);
        pos += 4;
        return v;
    }

    private static ulong ReadUInt64LE(ReadOnlySpan<byte> data, ref int pos)
    {
        var v = System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(data[pos..]);
        pos += 8;
        return v;
    }

    private static ReadOnlySpan<byte> ReadLengthDelimited(ReadOnlySpan<byte> data, ref int pos)
    {
        var len = (int)ReadVarint(data, ref pos);
        var slice = data.Slice(pos, len);
        pos += len;
        return slice;
    }

    private static int ZigZagDecode32(uint n) => (int)(n >> 1) ^ -(int)(n & 1);
    private static long ZigZagDecode64(ulong n) => (long)(n >> 1) ^ -(long)(n & 1);
}
