using System;
using System.Threading;
using System.Threading.Tasks;
using Velopack;

namespace Ferry.Models;

/// <summary>
/// Velopack による更新情報を保持するモデル。
/// </summary>
public sealed class VelopackUpdate(UpdateManager manager, UpdateInfo updateInfo)
{
    /// <summary>リリースのタグ名（例: v1.0.6）。</summary>
    public string TagName => $"v{updateInfo.TargetFullRelease.Version}";

    /// <summary>バージョン文字列。</summary>
    public string VersionString => updateInfo.TargetFullRelease.Version.ToString();

    /// <summary>更新パッケージを非同期でダウンロードする。</summary>
    public async Task DownloadAsync(Action<int> onProgress, CancellationToken token)
    {
        await manager.DownloadUpdatesAsync(updateInfo, onProgress, cancelToken: token);
    }

    /// <summary>ダウンロード済みの更新を適用してアプリケーションを再起動する。</summary>
    public void ApplyAndRestart()
    {
        manager.ApplyUpdatesAndRestart(updateInfo);
    }
}

/// <summary>最新版で更新なし。</summary>
public sealed class AlreadyUpToDate;

/// <summary>更新チェックに失敗した。</summary>
public sealed class SelfUpdateFailed(Exception exception)
{
    public string Reason => exception.InnerException?.Message ?? exception.Message;
}
