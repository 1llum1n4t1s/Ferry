using System.Collections.Generic;
using System.Text.Json.Serialization;
using Ferry.Models;

namespace Ferry.Infrastructure;

/// <summary>
/// AppSettings JSON の AOT 互換シリアライゼーションコンテキスト。
/// HashSet&lt;string&gt; など新しい型を含む。
/// </summary>
/// <remarks>
/// NumberHandling=AllowNamedFloatingPointLiterals: <see cref="AppSettings.WindowX"/>/<see cref="AppSettings.WindowY"/>
/// は「位置未設定」を <c>double.NaN</c> センチネルで表すが、既定の System.Text.Json は NaN/Infinity を
/// シリアライズできず ArgumentException を投げる。これが無いと、ウィンドウ位置がまだ NaN の初回起動時に
/// <see cref="Ferry.Services.SettingsService"/> の同期 Save が静かに失敗し settings.json が永続化されない
/// （DeviceId / 移行フラグ等が初回保存で落ちる）。NaN を <c>"NaN"</c> 文字列として round-trip させて防ぐ
/// （実数値の表現は不変なので既存 settings.json への影響なし。読み戻しは double.IsNaN 判定と整合）。
/// </remarks>
[JsonSourceGenerationOptions(NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals)]
[JsonSerializable(typeof(AppSettings))]
[JsonSerializable(typeof(HashSet<string>))]
[JsonSerializable(typeof(List<string>))]
internal partial class AppSettingsJsonContext : JsonSerializerContext;
