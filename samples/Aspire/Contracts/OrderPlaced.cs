using System.Text.Json.Serialization;

namespace Dispatchly.Sample.Aspire.Contracts;

public sealed record OrderPlaced(string OrderId);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(OrderPlaced))]
public sealed partial class OrderJsonContext : JsonSerializerContext;
