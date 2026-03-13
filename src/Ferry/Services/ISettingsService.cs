using System.Threading.Tasks;
using Ferry.Models;

namespace Ferry.Services;

/// <summary>
/// アプリケーション設定の読み書きサービス。
/// </summary>
public interface ISettingsService
{
    /// <summary>現在の設定。</summary>
    AppSettings Settings { get; }

    /// <summary>設定をファイルから読み込む。</summary>
    Task LoadAsync();

    /// <summary>設定をファイルに保存する。</summary>
    Task SaveAsync();
}
