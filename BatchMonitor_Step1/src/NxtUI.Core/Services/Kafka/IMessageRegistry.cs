namespace NxtUI.Core.Services.Kafka;

/// <summary>
/// Resolves proto type names to parsers and serialises parsed messages as JSON.
/// Implemented by <c>NxtUI.Protos.MessageRegistry</c>; abstracted here so
/// NxtUI.Core does not take a hard dependency on the generated proto assemblies.
/// </summary>
public interface IMessageRegistry
{
    bool TryParseToJson(string typeName, byte[] bytes, out string? json);
    IReadOnlyCollection<string> RegisteredTypes { get; }
}
