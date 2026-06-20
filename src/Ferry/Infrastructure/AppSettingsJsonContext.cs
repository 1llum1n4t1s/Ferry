using System.Collections.Generic;
using System.Text.Json.Serialization;
using Ferry.Models;

namespace Ferry.Infrastructure;

/// <summary>
/// AppSettings JSON の AOT 互換シリアライゼーションコンテキスト。
/// HashSet&lt;string&gt; など新しい型を含む。
/// </summary>
[JsonSerializable(typeof(AppSettings))]
[JsonSerializable(typeof(HashSet<string>))]
[JsonSerializable(typeof(List<string>))]
internal partial class AppSettingsJsonContext : JsonSerializerContext;
