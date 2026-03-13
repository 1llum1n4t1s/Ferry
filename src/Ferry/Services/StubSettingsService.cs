using System.Threading.Tasks;
using Ferry.Models;

namespace Ferry.Services;

/// <summary>
/// 設定サービスのスタブ実装。
/// </summary>
public sealed class StubSettingsService : ISettingsService
{
    public AppSettings Settings { get; } = new()
    {
        FirebaseDatabaseUrl = "https://ferry-edf09-default-rtdb.firebaseio.com",
        BridgePageUrl = "https://ferry-edf09.web.app",
    };

    public Task LoadAsync() => Task.CompletedTask;
    public Task SaveAsync() => Task.CompletedTask;
}
