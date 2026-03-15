using System.Text.Json.Serialization;
using Ferry.Models;

namespace Ferry.Infrastructure;

/// <summary>
/// AppSettings JSON の AOT 互換シリアライゼーションコンテキスト。
/// </summary>
[JsonSerializable(typeof(AppSettings))]
internal partial class AppSettingsJsonContext : JsonSerializerContext;
