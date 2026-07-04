using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using NxtUI.Protos;
using NxtUI.Tests.Protos.Fixtures;

namespace NxtUI.Tests.Protos;

/// <summary>
/// Verifies MessageRegistry's dynamic (schema-driven, no generated C# classes)
/// wire-format decoder against known-good bytes produced by Google.Protobuf's
/// own generated classes and JsonFormatter — the two must agree.
/// </summary>
public class MessageRegistryTests
{
    // Compile-time source path (this file's own directory) — stable regardless
    // of build output location (bin/Debug vs bin/Release, IDE vs `dotnet test`).
    private static string SourceDir([CallerFilePath] string here = "") => Path.GetDirectoryName(here)!;

    private static readonly MessageRegistry AppRegistry =
        MessageRegistry.Create(Path.Combine(SourceDir(), "..", "..", "..", "src", "NxtUI.Protos", "Protos"));

    private static readonly MessageRegistry FixtureRegistry =
        MessageRegistry.Create(Path.Combine(SourceDir(), "Fixtures"));

    // ── Scalars, nested messages, enums, repeated fields ────────────────────

    [Fact]
    public void Decodes_scalars_nested_message_enum_and_repeated_fields_matching_reference()
    {
        var order = new global::NxtUI.Protos.OrderEvent
        {
            OrderId       = "ord-123",
            CustomerId    = "cust-456",
            Status        = global::NxtUI.Protos.OrderStatus.Confirmed,
            TotalAmount   = 199.95,
            Currency      = "EUR",
            CorrelationId = "corr-abc",
            OccurredAt    = Timestamp.FromDateTime(new DateTime(2026, 7, 2, 10, 30, 0, DateTimeKind.Utc)),
        };
        order.Lines.Add(new global::NxtUI.Protos.OrderLine { Sku = "SKU-1", Quantity = 2, Price = 49.99 });
        order.Lines.Add(new global::NxtUI.Protos.OrderLine { Sku = "SKU-2", Quantity = 1, Price = 99.97 });

        var ok = AppRegistry.TryParseToJson("OrderEvent", order.ToByteArray(), out var json);

        ok.Should().BeTrue();
        var node = JsonNode.Parse(json!)!;
        node["orderId"]!.GetValue<string>().Should().Be("ord-123");
        node["customerId"]!.GetValue<string>().Should().Be("cust-456");
        node["status"]!.GetValue<string>().Should().Be("ORDER_STATUS_CONFIRMED");
        node["totalAmount"]!.GetValue<double>().Should().Be(199.95);
        node["currency"]!.GetValue<string>().Should().Be("EUR");
        node["correlationId"]!.GetValue<string>().Should().Be("corr-abc");
        node["occurredAt"]!.GetValue<string>().Should().Be("2026-07-02T10:30:00Z");

        var lines = node["lines"]!.AsArray();
        lines.Should().HaveCount(2);
        lines[0]!["sku"]!.GetValue<string>().Should().Be("SKU-1");
        lines[0]!["quantity"]!.GetValue<int>().Should().Be(2);
        lines[0]!["price"]!.GetValue<double>().Should().Be(49.99);
        lines[1]!["sku"]!.GetValue<string>().Should().Be("SKU-2");
    }

    [Fact]
    public void Unknown_enum_value_falls_back_to_raw_number()
    {
        // PaymentStatus only defines 0-5; write an out-of-range value directly
        // via ProtoMsg's raw payload to simulate a schema-ahead-of-us producer.
        var payment = new global::NxtUI.Protos.PaymentEvent { PaymentId = "p1", Status = (global::NxtUI.Protos.PaymentStatus)99 };
        AppRegistry.TryParseToJson("PaymentEvent", payment.ToByteArray(), out var json).Should().BeTrue();

        JsonNode.Parse(json!)!["status"]!.GetValue<string>().Should().Be("99");
    }

    // ── Map fields ────────────────────────────────────────────────────────

    [Fact]
    public void Decodes_map_field_as_json_object_not_array_of_pairs()
    {
        var msg = new global::NxtUI.Protos.ProtoMsg
        {
            TypeUrl = "type.googleapis.com/nxtui.OrderEvent",
            Payload = ByteString.CopyFrom([1, 2, 3]),
        };
        msg.Metadata["source"]  = "unit-test";
        msg.Metadata["version"] = "1";

        AppRegistry.TryParseToJson("ProtoMsg", msg.ToByteArray(), out var json).Should().BeTrue();

        var node = JsonNode.Parse(json!)!;
        node["typeUrl"]!.GetValue<string>().Should().Be("type.googleapis.com/nxtui.OrderEvent");
        node["payload"]!.GetValue<string>().Should().Be("byte[3]");
        var metadata = node["metadata"]!.AsObject();
        metadata["source"]!.GetValue<string>().Should().Be("unit-test");
        metadata["version"]!.GetValue<string>().Should().Be("1");
    }

    [Fact]
    public void Bytes_field_renders_as_length_placeholder_not_base64_blob()
    {
        var msg = new global::NxtUI.Protos.ProtoMsg
        {
            TypeUrl = "type.googleapis.com/nxtui.OrderEvent",
            Payload = ByteString.CopyFrom(new byte[128]),
        };

        AppRegistry.TryParseToJson("ProtoMsg", msg.ToByteArray(), out var json).Should().BeTrue();
        JsonNode.Parse(json!)!["payload"]!.GetValue<string>().Should().Be("byte[128]");
    }

    // ── Lookup failure paths ─────────────────────────────────────────────────

    [Fact]
    public void Unregistered_type_name_returns_false()
    {
        var ok = AppRegistry.TryParseToJson("NoSuchMessageType", [1, 2, 3], out var json);
        ok.Should().BeFalse();
        json.Should().BeNull();
    }

    [Fact]
    public void Truncated_garbage_bytes_return_false_instead_of_throwing()
    {
        // A length-delimited tag claiming more bytes than actually follow.
        byte[] garbage = [0x0A, 0xFF, 0xFF, 0xFF, 0x7F];
        var ok = AppRegistry.TryParseToJson("OrderEvent", garbage, out var json);
        ok.Should().BeFalse();
        json.Should().BeNull();
    }

    [Fact]
    public void RegisteredTypes_includes_all_top_level_app_messages()
    {
        AppRegistry.RegisteredTypes.Should().Contain(["OrderEvent", "OrderLine", "PaymentEvent", "ProtoMsg"]);
    }

    // ── Well-known types: Timestamp / Duration / nullable-primitive wrappers ─

    [Fact]
    public void Wrapper_types_render_as_plain_scalars_not_nested_objects()
    {
        var msg = new WellKnownTest
        {
            Nickname = "yomo",
            Retries  = 0,          // present but zero-valued — must be 0, not null
            BigCount = 42,
            Active   = true,
            Score    = 3.5,
        };

        FixtureRegistry.TryParseToJson("WellKnownTest", msg.ToByteArray(), out var json).Should().BeTrue();
        var node = JsonNode.Parse(json!)!;

        node["nickname"]!.GetValue<string>().Should().Be("yomo");
        node["retries"]!.GetValue<int>().Should().Be(0);
        node["bigCount"]!.GetValue<long>().Should().Be(42);
        node["active"]!.GetValue<bool>().Should().BeTrue();
        node["score"]!.GetValue<double>().Should().Be(3.5);
    }

    [Fact]
    public void Wrapper_fields_left_entirely_unset_are_absent_from_json()
    {
        var msg = new WellKnownTest(); // nothing set at all

        FixtureRegistry.TryParseToJson("WellKnownTest", msg.ToByteArray(), out var json).Should().BeTrue();
        var node = JsonNode.Parse(json!)!.AsObject();

        node.ContainsKey("nickname").Should().BeFalse();
        node.ContainsKey("retries").Should().BeFalse();
        node.ContainsKey("active").Should().BeFalse();
    }

    [Fact]
    public void Timestamp_renders_as_iso8601_string()
    {
        var msg = new WellKnownTest
        {
            CreatedAt = Timestamp.FromDateTime(new DateTime(2026, 7, 2, 10, 30, 0, DateTimeKind.Utc)),
        };

        FixtureRegistry.TryParseToJson("WellKnownTest", msg.ToByteArray(), out var json).Should().BeTrue();
        JsonNode.Parse(json!)!["createdAt"]!.GetValue<string>().Should().Be("2026-07-02T10:30:00Z");
    }

    [Fact]
    public void Duration_renders_as_seconds_suffixed_string()
    {
        var msg = new WellKnownTest
        {
            Timeout = Duration.FromTimeSpan(TimeSpan.FromSeconds(1.5)),
        };

        FixtureRegistry.TryParseToJson("WellKnownTest", msg.ToByteArray(), out var json).Should().BeTrue();
        JsonNode.Parse(json!)!["timeout"]!.GetValue<string>().Should().Be("1.5s");
    }
}
