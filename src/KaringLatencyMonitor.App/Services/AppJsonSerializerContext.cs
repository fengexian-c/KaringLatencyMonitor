using System.Text.Json.Serialization;
using KaringLatencyMonitor.App.Models;

namespace KaringLatencyMonitor.App.Services;

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(AppSettings))]
internal partial class AppJsonSerializerContext : JsonSerializerContext;
