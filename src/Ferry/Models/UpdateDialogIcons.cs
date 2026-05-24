using Avalonia;
using Avalonia.Media;
using VelopackUpdateDialog;

namespace Ferry.Models;

/// <summary>
/// VelopackUpdateDialog.Avalonia のベクタアイコンを Ferry のリソースへ接続する。
/// Komorebi と同じパターンだが、Ferry は専用のベクタアイコンセットを持たないため、
/// 該当キーが見つからなければ空の <see cref="Geometry"/> を返してダイアログ全体の描画失敗を避ける。
/// 将来アイコンセットを追加する場合は Icons.SoftwareUpdate / Icons.Info / Icons.Pull /
/// Icons.File.Ignore / Icons.Error をリソースに登録すれば自動的に反映される。
/// </summary>
public sealed class UpdateDialogIcons : IUpdateDialogIcons
{
    /// <summary>シングルトン インスタンス。</summary>
    public static readonly UpdateDialogIcons Instance = new();

    /// <inheritdoc />
    public Geometry SoftwareUpdate => GetGeometry("Icons.SoftwareUpdate");

    /// <inheritdoc />
    public Geometry Info => GetGeometry("Icons.Info");

    /// <inheritdoc />
    public Geometry Download => GetGeometry("Icons.Pull");

    /// <inheritdoc />
    public Geometry Ignore => GetGeometry("Icons.File.Ignore");

    /// <inheritdoc />
    public Geometry Error => GetGeometry("Icons.Error");

    /// <summary>
    /// リソースキーから <see cref="Geometry"/>（実体は StreamGeometry）を取得する。
    /// 未登録キーの場合は空の Geometry を返してダイアログ全体の描画失敗を避ける。
    /// Avalonia 12 では <c>Application.FindResource</c> が直接公開されないため
    /// <see cref="IResourceNode.TryGetResource"/> を使う。
    /// </summary>
    private static Geometry GetGeometry(string key)
    {
        if (Application.Current is { } app
            && app.TryGetResource(key, app.ActualThemeVariant, out var value)
            && value is Geometry geo)
        {
            return geo;
        }

        return s_empty;
    }

    private static readonly Geometry s_empty = Geometry.Parse("M0,0");
}
