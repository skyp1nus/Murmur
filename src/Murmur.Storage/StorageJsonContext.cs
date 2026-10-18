using System.Text.Json.Serialization;

namespace Murmur.Storage;

/// <summary>Persisted shape of a screen segment (millisecond integers keep the JSON compact and stable).</summary>
internal sealed record ScreenSegmentDto(int Index, long OffsetMs, long DurationMs);

/// <summary>Source-generated JSON metadata: no reflection, Native AOT safe.</summary>
[JsonSerializable(typeof(ScreenSegmentDto[]))]
internal sealed partial class StorageJsonContext : JsonSerializerContext;
