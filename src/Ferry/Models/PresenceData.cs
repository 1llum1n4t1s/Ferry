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
}
