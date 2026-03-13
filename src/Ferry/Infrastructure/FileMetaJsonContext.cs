using System.Text.Json.Serialization;

namespace Ferry.Infrastructure;

/// <summary>
/// ファイルメタデータ JSON の AOT 互換シリアライゼーションコンテキスト。
/// </summary>
[JsonSerializable(typeof(FileMeta))]
public partial class FileMetaJsonContext : JsonSerializerContext;

/// <summary>
/// ファイルメタデータの JSON 構造。
/// </summary>
public sealed class FileMeta
{
    public string FileName { get; set; } = string.Empty;
    public long FileSize { get; set; }
    public int TotalChunks { get; set; }
    public string Sha256 { get; set; } = string.Empty;

    /// <summary>転送セッション ID（レジューム照合用）。</summary>
    public string TransferId { get; set; } = string.Empty;
}
