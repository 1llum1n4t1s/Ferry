using System.Collections.Generic;
using System.Text.Json.Serialization;
using Ferry.Models;

namespace Ferry.Infrastructure;

/// <summary>
/// チャット履歴 JSON の AOT 互換シリアライゼーションコンテキスト。
/// </summary>
[JsonSerializable(typeof(List<ChatMessage>))]
internal partial class ChatMessageJsonContext : JsonSerializerContext;
