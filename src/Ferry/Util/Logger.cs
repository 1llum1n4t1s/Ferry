using System;
using System.IO;
using System.Linq;
using SuperLightLogger;

namespace Ferry.Util;

/// <summary>
/// ログレベルを表す列挙型
/// </summary>
public enum LogLevel
{
    Debug,
    Info,
    Warning,
    Error
}

/// <summary>
/// ログ初期化設定
/// </summary>
public sealed class LoggerConfig
{
    /// <summary>ログ出力ディレクトリ</summary>
    public required string LogDirectory { get; init; }

    /// <summary>ログファイル名のプレフィックス（例: "Ferry"）</summary>
    public required string FilePrefix { get; init; }

    /// <summary>ローリングサイズ上限（MB）</summary>
    public int MaxSizeMB { get; init; } = 10;

    /// <summary>アーカイブファイルの最大保持数</summary>
    public int MaxArchiveFiles { get; init; } = 10;

    /// <summary>ログファイルの保持日数（0以下の場合は削除しない）</summary>
    public int RetentionDays { get; init; } = 7;
}

/// <summary>
/// SuperLightLogger（log4net 互換シム + 内蔵 File Target）を使用した汎用ログ出力クラス。
/// </summary>
public static class Logger
{
    private static ILog? _logger;
    private static bool _isConfigured;
    private static string _appName = "Ferry";

    /// <summary>
    /// 最小ログレベル（これ以上のレベルのログのみ出力）
    /// </summary>
    private static readonly LogLevel MinLogLevel =
#if DEBUG
        LogLevel.Debug;
#else
        // Release でも接続フォールバックの各段（成功・経路確定・状態遷移）を診断できるよう
        // Info 以上を出力する。IP 等の PII は MaskIp で伏せて記録する
        LogLevel.Info;
#endif

    /// <summary>
    /// ロガーを初期化する
    /// </summary>
    /// <param name="config">ログ設定</param>
    public static void Initialize(LoggerConfig config)
    {
        if (_isConfigured) return;

        _appName = config.FilePrefix;

        if (!Directory.Exists(config.LogDirectory))
        {
            Directory.CreateDirectory(config.LogDirectory);
        }

        // SuperLightLogger の内蔵 File Target で NLog 同等のローリング設定を構築する。
        // builder の最小レベルは Trace に設定し、Ferry 側の MinLogLevel で実フィルタを行う
        // （SuperLightLogger は Microsoft.Extensions.Logging を内部利用するが、ここでは
        //  Ferry.Util.LogLevel と MEL.LogLevel の名前衝突を避けるため文字列ベース API を使う）。
        LogManager.Configure(builder =>
        {
            builder.AddSuperLightFile(opt =>
            {
                opt.FileName = Path.Combine(config.LogDirectory, $"{config.FilePrefix}_${{date:format=yyyyMMdd}}.log");
                opt.Layout = "${longdate} [${level:uppercase=true}] ${message}${onexception:${newline}${exception:format=tostring}}";
                opt.ArchiveAboveSize = (long)config.MaxSizeMB * 1024L * 1024L;
                opt.MaxArchiveFiles = config.MaxArchiveFiles;
            });
            builder.SetMinimumLevel("Trace");
        });

        _logger = LogManager.GetLogger(config.FilePrefix);
        _isConfigured = true;

        Log("Logger initialized with SuperLightLogger (RollingFile)", LogLevel.Debug);

        // 過去のバグで作成された不要な "0" ファイルを削除
        CleanupStaleFile(Path.Combine(config.LogDirectory, "0"));

        // 保持期間を超えた古いログファイルを削除
        CleanupOldLogFiles(config.LogDirectory, config.FilePrefix, config.RetentionDays);
    }

    /// <summary>
    /// 過去のバグで作成された不要ファイルを削除する
    /// </summary>
    private static void CleanupStaleFile(string filePath)
    {
        try
        {
            if (File.Exists(filePath))
            {
                File.Delete(filePath);
                Log($"不要なファイルを削除しました: {Path.GetFileName(filePath)}", LogLevel.Debug);
            }
        }
        catch (Exception ex)
        {
            Log($"不要ファイルの削除に失敗しました: {filePath} - {ex.Message}", LogLevel.Warning);
        }
    }

    /// <summary>
    /// 保持期間を超えた古いログファイルを削除する
    /// </summary>
    private static void CleanupOldLogFiles(string logDirectory, string filePrefix, int retentionDays)
    {
        if (retentionDays <= 0) return;

        try
        {
            var cutoffDate = DateTime.Now.Date.AddDays(-retentionDays);
            // ローテーション形式は SuperLightLogger 実装次第で複数あり得るので、
            //   - `Ferry_20260206.log`     （日付ごとの最新）
            //   - `Ferry_20260206.log.1`   （サイズ超過時のアーカイブ）
            //   - `Ferry_20260206_000.log` （別命名形式）
            // のすべてを拾うため、拡張子を限定せず `Ferry_*` で検索する
            var prefix = $"{filePrefix}_";
            var logFiles = Directory.GetFiles(logDirectory, $"{prefix}*");

            foreach (var file in logFiles)
            {
                try
                {
                    var fileName = Path.GetFileName(file);
                    if (!fileName.StartsWith(prefix, StringComparison.Ordinal)) continue;
                    if (fileName.Length < prefix.Length + 8) continue;

                    // M-17 + P-10: Substring の string 確保を AsSpan で回避 + InvariantCulture 明示
                    var datePart = fileName.AsSpan(prefix.Length, 8);
                    if (DateTime.TryParseExact(datePart, "yyyyMMdd", System.Globalization.CultureInfo.InvariantCulture,
                            System.Globalization.DateTimeStyles.None, out var fileDate)
                        && fileDate < cutoffDate)
                    {
                        File.Delete(file);
                        Log($"古いログファイルを削除しました: {fileName}", LogLevel.Debug);
                    }
                }
                catch (Exception ex)
                {
                    Log($"ログファイルの削除に失敗しました: {Path.GetFileName(file)} - {ex.Message}", LogLevel.Warning);
                }
            }
        }
        catch (Exception ex)
        {
            Log($"ログファイルのクリーンアップ中にエラーが発生しました: {ex.Message}", LogLevel.Warning);
        }
    }

    /// <summary>
    /// ログを出力する
    /// </summary>
    /// <param name="message">ログメッセージ</param>
    /// <param name="level">ログレベル（デフォルト: Info）</param>
    public static void Log(string message, LogLevel level = LogLevel.Info)
    {
        if (level < MinLogLevel || _logger == null)
            return;

        Dispatch(_logger, level, message);
    }

    /// <summary>
    /// ログ出力用に IP アドレスの末尾を伏せる（PII 保護）。
    /// IPv4: "1.2.3.4" → "1.2.3.x"、"1.2.3.4:5678" → "1.2.3.x:5678"
    /// IPv6: "2403:3a00:202:1134:49:212:230:244" → "2403:3a00:202:1134:49:212:x:x"
    /// IPv6 ブラケット形式: "[2001:db8::1]:8080" → "[2001:db8::x:x]:8080"
    /// </summary>
    public static string MaskIp(string? ipOrEndpoint)
    {
        if (string.IsNullOrEmpty(ipOrEndpoint)) return ipOrEndpoint ?? "";

        // IPv6 ブラケット形式: [::1]:port
        if (ipOrEndpoint[0] == '[')
        {
            var closeBracket = ipOrEndpoint.IndexOf(']');
            if (closeBracket > 1)
            {
                var ipv6 = ipOrEndpoint[1..closeBracket];
                var rest = ipOrEndpoint[(closeBracket + 1)..]; // ":port" or ""
                return "[" + MaskIpv6(ipv6) + "]" + rest;
            }
        }

        // IPv6（ブラケットなし）: コロンが 2 個以上ある → IPv4:port の単一コロンと区別。
        // M-6 + P-9: LINQ Count(デリゲート割当 + 全走査) を 2 段 IndexOf（早期終了）に
        var firstColon = ipOrEndpoint.IndexOf(':');
        if (firstColon >= 0 && ipOrEndpoint.IndexOf(':', firstColon + 1) >= 0)
        {
            return MaskIpv6(ipOrEndpoint);
        }

        // IPv4 または IPv4:port
        var colon = ipOrEndpoint.LastIndexOf(':');
        var ip = colon >= 0 ? ipOrEndpoint[..colon] : ipOrEndpoint;
        var port = colon >= 0 ? ipOrEndpoint[colon..] : "";

        var parts = ip.Split('.');
        if (parts.Length != 4) return ipOrEndpoint; // 不正形式はそのまま返す
        parts[3] = "x";
        return string.Join('.', parts) + port;
    }

    /// <summary>
    /// IPv6 アドレスの末尾 2 hextet を伏せる。`::` 圧縮表記の空ブロックはスキップする。
    /// </summary>
    private static string MaskIpv6(string ipv6)
    {
        var hextets = ipv6.Split(':');
        if (hextets.Length < 2) return ipv6;

        // 末尾から非空 hextet を 2 つだけ "x" に置換する（`::` の空要素は飛ばす）
        var masked = 0;
        for (var i = hextets.Length - 1; i >= 0 && masked < 2; i--)
        {
            if (!string.IsNullOrEmpty(hextets[i]))
            {
                hextets[i] = "x";
                masked++;
            }
        }
        return string.Join(':', hextets);
    }

    /// <summary>
    /// 例外情報を含むログを出力する（常にErrorレベル）
    /// </summary>
    /// <param name="message">ログメッセージ</param>
    /// <param name="exception">例外オブジェクト</param>
    public static void LogException(string message, Exception exception)
    {
        // log4net 互換の引数順は (message, exception)。NLog の (exception, message) とは順序が逆
        _logger?.Error(message, exception);
    }

    /// <summary>
    /// アプリケーション起動時のログを出力する（Debugレベル）
    /// </summary>
    /// <param name="args">コマンドライン引数</param>
    public static void LogStartup(string[] args)
    {
        if (LogLevel.Debug < MinLogLevel) return;

        var message =
            $"""
            === {_appName} 起動ログ ===
            起動時刻: {DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}
            実行ファイルパス: {Environment.ProcessPath}
            コマンドライン引数の数: {args.Length}
            コマンドライン引数:
            {string.Join(Environment.NewLine, args.Select((a, i) => $"  [{i}]: {a}"))}
            """;

        if (_logger != null)
        {
            _logger.Debug(message);
        }
        else
        {
            // Initialize 失敗時のフォールバック。起動情報を黙って捨てないよう stderr に出す
            try { Console.Error.WriteLine($"[{_appName}/LogStartup] {message}"); } catch { /* 出力先がないなら諦める */ }
        }
    }

    /// <summary>
    /// ロガーを明示的に終了する（バッファのフラッシュなど）
    /// </summary>
    public static void Dispose()
    {
        LogManager.Shutdown();
        // Shutdown 後にログを呼ばれてもダングリングしないよう参照をクリア
        _logger = null;
        _isConfigured = false;
    }

    /// <summary>
    /// 自前の <see cref="LogLevel"/> を SuperLightLogger の <see cref="ILog"/> メソッド呼び出しに振り分ける
    /// </summary>
    private static void Dispatch(ILog log, LogLevel level, string message)
    {
        switch (level)
        {
            case LogLevel.Debug: log.Debug(message); break;
            case LogLevel.Info: log.Info(message); break;
            case LogLevel.Warning: log.Warn(message); break;
            case LogLevel.Error: log.Error(message); break;
        }
    }
}
