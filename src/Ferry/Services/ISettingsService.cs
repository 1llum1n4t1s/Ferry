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

    /// <summary>
    /// Windows レジストリへ自動起動を登録/解除する（Windows 以外では no-op）。
    /// 具象 SettingsService への is キャストを VM から除去するためインターフェース境界に持ち上げ（N-21）。
    /// </summary>
    void SetAutoStart(bool enabled);
}
