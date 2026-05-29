namespace Ferry;

/// <summary>
/// アプリケーションバージョンの static 定数。
/// N-9: Native AOT で <see cref="System.Reflection.AssemblyInformationalVersionAttribute"/> が削除される可能性があるため
/// リフレクションに頼らず compile-time 定数として持つ。`/vava` ワークフロー実行時に
/// <c>Directory.Build.props</c> の <c>&lt;Version&gt;</c> と同期する。
/// </summary>
internal static class AppVersion
{
    /// <summary>セマンティックバージョン（Directory.Build.props と同期）。</summary>
    public const string Value = "1.0.40";
}
