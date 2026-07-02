using Google.Protobuf;
using NxtUI.Core.Services.Kafka;

namespace NxtUI.Protos;

/// <summary>
/// Maps proto type names to their parsers and serialises parsed messages to JSON.
/// Register as singleton and inject into <see cref="TopicDeserializerPipeline"/>.
/// </summary>
public sealed class MessageRegistry : IMessageRegistry
{
    private readonly Dictionary<string, MessageParser> _parsers =
        new(StringComparer.OrdinalIgnoreCase);

    private static readonly JsonFormatter _formatter =
        new(JsonFormatter.Settings.Default.WithFormatEnumsAsIntegers(false));

    private MessageRegistry() { }

    public static MessageRegistry CreateDefault()
    {
        var r = new MessageRegistry();
        r.Add("ProtoMsg",      ProtoMsg.Parser);
        r.Add("OrderEvent",    OrderEvent.Parser);
        r.Add("PaymentEvent",  PaymentEvent.Parser);
        return r;
    }

    private void Add(string name, MessageParser parser) => _parsers[name] = parser;

    /// <inheritdoc/>
    public bool TryParseToJson(string typeName, byte[] bytes, out string? json)
    {
        if (!_parsers.TryGetValue(typeName, out var parser))
        {
            json = null;
            return false;
        }
        try
        {
            var msg = parser.ParseFrom(bytes);
            json = _formatter.Format(msg);
            return true;
        }
        catch
        {
            json = null;
            return false;
        }
    }

    /// <inheritdoc/>
    public IReadOnlyCollection<string> RegisteredTypes => _parsers.Keys;
}
