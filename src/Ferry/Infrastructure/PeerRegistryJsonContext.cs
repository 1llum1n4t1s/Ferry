using System.Collections.Generic;
using System.Text.Json.Serialization;
using Ferry.Models;

namespace Ferry.Infrastructure;

/// <summary>
/// ペアリング情報 JSON の AOT 互換シリアライゼーションコンテキスト。
/// </summary>
[JsonSerializable(typeof(List<PairedPeer>))]
internal partial class PeerRegistryJsonContext : JsonSerializerContext;
