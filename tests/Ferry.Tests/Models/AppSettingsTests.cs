namespace Ferry.Tests.Models;

/// <summary>
/// AppSettings の DeviceId 確定と保存互換性を検証する。
/// </summary>
public class AppSettingsTests
{
    [Fact]
    public void AppConstantsの接続先URLが非空で期待スキームであること()
    {
        // rere #D-004: 接続先 URL は settings から AppConstants(const) へ移行。空文字化や URL タイポの回帰を固定化。
        // CF 単独完結 Step 6: Firebase 系定数は撤去し、CF 経路の定数のみ検証する。
        Assert.StartsWith("https://", Ferry.AppConstants.CfApiBaseUrl);
        Assert.StartsWith("https://", Ferry.AppConstants.CfBridgePageUrl);
        Assert.StartsWith("wss://", Ferry.AppConstants.RelayUrl);
    }

    [Fact]
    public void 旧settingsの撤去済みキーを含むJSONも例外なくロードできること()
    {
        // rere #D-004 / CF 単独完結 Step 6: AppSettings から削除したキー（FirebaseDatabaseUrl / BridgePageUrl /
        // dual-path の UseCloudflareSignaling / MigratedToCloudflareDefault）が旧 settings.json に残っていても、
        // 未知キーとして無視され DeviceId 等は正しく読めること（次回 SaveAsync で自然に消える）。
        // 実際のロード経路（SettingsService が AppSettingsJsonContext で読む）を通して検証する。
        var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"ferry_test_{System.Guid.NewGuid():N}.json");
        System.IO.File.WriteAllText(path, "{\"DeviceId\":\"abc123\",\"DisplayName\":\"OldPC\",\"FirebaseDatabaseUrl\":\"https://old.example.com\",\"BridgePageUrl\":\"https://old.web.app\",\"UseCloudflareSignaling\":false,\"MigratedToCloudflareDefault\":true}");
        try
        {
            using var svc = new Ferry.Services.SettingsService(path);
            Assert.Equal("abc123", svc.Settings.DeviceId);
            Assert.Equal("OldPC", svc.Settings.DisplayName);
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }

    [Fact]
    public void 新規インストールは初回Saveでsettingsjsonが生成されDeviceIdが確定すること()
    {
        // settings.json が存在しない初回起動: 初回 Save でファイルが生成され DeviceId が永続化される。
        // WindowX/Y は「位置未設定」を double.NaN で表す。既定の System.Text.Json は NaN を書けず ArgumentException になるため、
        // AppSettingsJsonContext で AllowNamedFloatingPointLiterals を有効化している。初回 Save が落ちて永続化されない
        // 回帰を固定化する（NaN センチネルの round-trip が IsNaN 判定＝位置復元のゲートと整合すること）。
        var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"ferry_test_{System.Guid.NewGuid():N}.json");
        try
        {
            using var svc = new Ferry.Services.SettingsService(path);
            Assert.True(System.IO.File.Exists(path)); // 初回 Save で生成される
            // "N" フォーマット: ハイフンなし 32 文字の GUID
            Assert.True(Guid.TryParseExact(svc.Settings.DeviceId, "N", out _),
                $"DeviceId '{svc.Settings.DeviceId}' は GUID の N フォーマットではない");

            // 再ロードしても同じ DeviceId が読め、NaN 座標も保たれる（永続化が成立している）
            using var reloaded = new Ferry.Services.SettingsService(path);
            Assert.Equal(svc.Settings.DeviceId, reloaded.Settings.DeviceId);
            Assert.True(double.IsNaN(reloaded.Settings.WindowX) && double.IsNaN(reloaded.Settings.WindowY));
        }
        finally
        {
            if (System.IO.File.Exists(path)) System.IO.File.Delete(path);
        }
    }
}
