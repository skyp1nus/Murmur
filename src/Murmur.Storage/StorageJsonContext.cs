using System.Text.Json.Serialization;

namespace Murmur.Storage;

/// <summary>Persisted shape of a screen segment (millisecond integers keep the JSON compact and stable).</summary>
internal sealed record ScreenSegmentDto(int Index, long OffsetMs, long DurationMs);

/// <summary>Persisted shape of a summary line: the text and the moment it cites in milliseconds (<c>null</c> = none).</summary>
internal sealed record SummaryItemDto(string Text, long? AtMs);

/// <summary>Source-generated JSON metadata: no reflection, Native AOT safe.</summary>
[JsonSerializable(typeof(ScreenSegmentDto[]))]
[JsonSerializable(typeof(SummaryItemDto[]))]
internal sealed partial class StorageJsonContext : JsonSerializerContext;
