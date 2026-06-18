namespace Ferry.Models;

/// <summary>
/// Firebase の presence ノードに書き込むプレゼンスデータ（LastSeen + DisplayName）。
/// rere #B1-001: presence 抽象（<see cref="Ferry.Services.IPresenceService"/>）を Infrastructure 非依存に
/// するため、純 DTO として Models 層に置く。シリアライズはプロパティ名ベースなので移設は互換。
/// </summary>
public sealed class PresenceData
{
    public long LastSeen { get; set; }
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>
    /// rere #D-001(a) Phase B / Q5: 自端末のアプリ Version 文字列（例: "1.0.62"）。
    /// rules 厳格化 deploy (Step 8) の直前に両 PC が新版を書いているか機械確認するための
    /// マーカー。rules で `length &lt;= 16` を validate するため Semver 形式に収まる。
    /// 旧クライアントは このフィールドを書かないので null/empty で配信。
    /// </summary>
    public string Version { get; set; } = string.Empty;
}
