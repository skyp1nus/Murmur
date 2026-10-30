using System.Text.Json.Serialization;

namespace Murmur.Storage;

/// <summary>Persisted shape of a screen segment (millisecond integers keep the JSON compact and stable).</summary>
internal sealed record ScreenSegmentDto(int Index, long OffsetMs, long DurationMs);

/// <summary>Persisted shape of a citation of a saved answer (the moment in milliseconds).</summary>
internal sealed record CitationDto(string Session, long AtMs, string Label, string Snippet);

/// <summary>Source-generated JSON metadata: no reflection, Native AOT safe.</summary>
[JsonSerializable(typeof(ScreenSegmentDto[]))]
[JsonSerializable(typeof(CitationDto[]))]
internal sealed partial class StorageJsonContext : JsonSerializerContext;
