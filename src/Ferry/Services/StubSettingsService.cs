using System.Threading.Tasks;
using Ferry.Models;

namespace Ferry.Services;

/// <summary>
/// 設定サービスのスタブ実装。
/// </summary>
public sealed class StubSettingsService : ISettingsService
{
    // rere #D-004: 接続先 URL は AppConstants 固定になり AppSettings から撤去された。
    public AppSettings Settings { get; } = new();

    public Task LoadAsync() => Task.CompletedTask;
    public Task SaveAsync() => Task.CompletedTask;
    public void SetAutoStart(bool enabled) { /* スタブは no-op */ }
}
