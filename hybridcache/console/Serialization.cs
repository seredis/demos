// Serialization.cs - how a Product becomes bytes. Left alone, both implementations fall back to
// reflection-based System.Text.Json, which a trimmed / native-AOT build does not have. HybridCache
// lets the application supply the serializer instead: an IHybridCacheSerializer<T> registered in
// DI is picked up by the stock implementation and by StackExchange.Redis.HybridCache alike (see
// Setup.cs), so this file is, like CoreUsage.cs, identical for both modes.
//
// The serializer here is still System.Text.Json, but source-generated: the same JSON bytes as the
// default, with no reflection at run time.

using System.Buffers;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Caching.Hybrid;

namespace HybridCacheDemo;

[JsonSerializable(typeof(Product))]
sealed partial class ProductJsonContext : JsonSerializerContext;

sealed class ProductSerializer : IHybridCacheSerializer<Product>
{
    public Product Deserialize(ReadOnlySequence<byte> source)
    {
        var reader = new Utf8JsonReader(source);
        return JsonSerializer.Deserialize(ref reader, ProductJsonContext.Default.Product)!;
    }

    public void Serialize(Product value, IBufferWriter<byte> target)
    {
        using var writer = new Utf8JsonWriter(target);
        JsonSerializer.Serialize(writer, value, ProductJsonContext.Default.Product);
    }
}
