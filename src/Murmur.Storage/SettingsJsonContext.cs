using System.Text.Json.Serialization;
using Murmur.Core.Settings;

namespace Murmur.Storage;

/// <summary>
/// Source-generated JSON metadata for <c>settings.json</c>: no reflection, Native AOT safe. Property names are camelCase,
/// enums are written as strings (so a hand-edited file stays readable), reading ignores the case of names and skips
/// members it does not know.
/// </summary>
[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    PropertyNameCaseInsensitive = true,
    UseStringEnumConverter = true,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Skip)]
[JsonSerializable(typeof(AppSettings))]
internal sealed partial class SettingsJsonContext : JsonSerializerContext;
