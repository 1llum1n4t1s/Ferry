namespace Ferry.Models;

/// <summary>
/// 宛先リスト（ListBox）に差し込むセクション見出し。ピアアイテム（<see cref="PairedPeer"/>）と
/// 同じ <c>ObservableCollection&lt;object&gt;</c> に混在させ、DataTemplate を型で出し分けて表示する。
/// 見出し行は選択・フォーカス不可（MainWindow の ContainerPrepared で抑止）。
///
/// record（値等価）にしているのは、宛先リストの差分更新（ConnectionViewModel.ReconcileVisiblePeers）が
/// 再構築のたびに新インスタンスとなる見出しを Icon/Label の値で同一視し、不要な削除・再追加を避けるため。
/// </summary>
public sealed record PeerListSection
{
    /// <summary>見出しアイコン（📌 / 🟢 / ⚪ など）。</summary>
    public required string Icon { get; init; }

    /// <summary>見出しラベル（ローカライズ済みテキスト）。</summary>
    public required string Label { get; init; }
}
