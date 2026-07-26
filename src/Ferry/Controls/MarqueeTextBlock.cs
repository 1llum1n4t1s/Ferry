using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

namespace Ferry.Controls;

/// <summary>
/// テキストが横幅に収まらない時だけ左へ流すマーキー表示。収まる時は静止して左寄せ表示する。
/// Avalonia 標準にマーキーが無いため自作。テンプレート(ControlTheme)に依存せず、子 TextBlock を
/// コードで保持して TranslateTransform.X を更新する単純方式
/// （「収まる時は流さない」「実測幅に応じた移動量」を確実に制御でき、Native AOT 安全）。
///
/// 駆動は<b>クラス共通の DispatcherTimer 1 本</b>。転送履歴は行ごとにこのコントロールを持つため、
/// インスタンスごとに DispatcherTimer を立てると流れている行数ぶんのタイマーが同時に走り、
/// Dispatcher のキューを無駄に埋める。アクティブなインスタンスを登録制にして 1 本で回す。
/// </summary>
public class MarqueeTextBlock : Decorator
{
    /// <summary>表示テキスト。</summary>
    public static readonly StyledProperty<string?> TextProperty =
        AvaloniaProperty.Register<MarqueeTextBlock, string?>(nameof(Text));

    /// <summary>スクロール速度 (px/秒)。</summary>
    public static readonly StyledProperty<double> SpeedProperty =
        AvaloniaProperty.Register<MarqueeTextBlock, double>(nameof(Speed), 40);

    /// <summary>フォントサイズ（内部 TextBlock へ転送）。</summary>
    public static readonly StyledProperty<double> FontSizeProperty =
        TextBlock.FontSizeProperty.AddOwner<MarqueeTextBlock>();

    /// <summary>文字色（内部 TextBlock へ転送）。</summary>
    public static readonly StyledProperty<IBrush?> ForegroundProperty =
        TextBlock.ForegroundProperty.AddOwner<MarqueeTextBlock>();

    public string? Text { get => GetValue(TextProperty); set => SetValue(TextProperty, value); }
    public double Speed { get => GetValue(SpeedProperty); set => SetValue(SpeedProperty, value); }
    public double FontSize { get => GetValue(FontSizeProperty); set => SetValue(FontSizeProperty, value); }
    public IBrush? Foreground { get => GetValue(ForegroundProperty); set => SetValue(ForegroundProperty, value); }

    // === クラス共通ティッカー（UI スレッド専用。登録・解除も measure/arrange・detach 経由で UI スレッド） ===

    private const double FrameMs = 33; // ~30fps

    private static readonly List<MarqueeTextBlock> Running = [];
    private static DispatcherTimer? _sharedTimer;

    private static void Register(MarqueeTextBlock item)
    {
        if (!Running.Contains(item)) Running.Add(item);
        // ⚠ 早期 return しない: 全インスタンスが不可視になって OnSharedTick がタイマーを
        //   止めた後、再表示で Register が呼ばれたときに再始動できなくなる (#C-22)。
        if (_sharedTimer is not null) return;

        _sharedTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(FrameMs) };
        _sharedTimer.Tick += OnSharedTick;
        _sharedTimer.Start();
    }

    private static void StopSharedTimer()
    {
        if (_sharedTimer is null) return;
        _sharedTimer.Stop();
        _sharedTimer.Tick -= OnSharedTick;
        _sharedTimer = null;
    }

    private static void Unregister(MarqueeTextBlock item)
    {
        if (!Running.Remove(item) || Running.Count > 0) return;

        // 流れている行が無くなったらタイマーごと止める（アイドル時に 30fps を回さない）
        StopSharedTimer();
    }

    private static void OnSharedTick(object? sender, EventArgs e)
    {
        // rere レビュー #C-22: 画面に出ていないインスタンスは進めない。
        // Avalonia 12 は IsEffectivelyVisible の変更通知を public に公開していないため
        // （IsEffectivelyVisibleChanged は非 public）、登録の出し入れではなく tick 側で判定する。
        // TransferView は MainWindow の Panel 内で IsVisible を切り替えるだけでビジュアルツリーから
        // 外れず、その間 measure/arrange も走らないので Recompute 経由の解除も起きない。
        // 旧実装はこの状態（設定タブ表示中・トレイ格納中）で 30fps の transform 更新を
        // 回し続けており、「アイドル時に 30fps を回さない」という設計目標が
        // 最も長時間続くアイドル状態でだけ達成できていなかった。
        var anyVisible = false;

        // Advance 内で Unregister されうるので後ろから回す
        for (var i = Running.Count - 1; i >= 0; i--)
        {
            var item = Running[i];
            if (!item.IsEffectivelyVisible) continue;
            anyVisible = true;
            item.Advance();
        }

        // 全部が不可視ならタイマーを止める。再表示時は arrange → Recompute → Register で
        // 再始動する（Register がタイマー再作成を早期 return しないのはこのため）。
        if (!anyVisible) StopSharedTimer();
    }

    private readonly TextBlock _text;
    private readonly TranslateTransform _xform = new();
    private double _offset;     // 現在の X（0 以下）
    private double _distance;   // 流す総距離（オーバーフロー分 + 余白）
    private double _pauseLeft;  // 端での静止残り秒

    private const double EdgePauseSeconds = 1.2; // 端で一旦止める時間

    public MarqueeTextBlock()
    {
        ClipToBounds = true;
        _text = new TextBlock
        {
            TextWrapping = TextWrapping.NoWrap,
            VerticalAlignment = VerticalAlignment.Center,
            RenderTransform = _xform,
        };
        Child = _text;
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.Property == TextProperty)
        {
            _text.Text = Text;
            InvalidateMeasure();
        }
        else if (e.Property == FontSizeProperty)
        {
            _text.FontSize = FontSize;
        }
        else if (e.Property == ForegroundProperty)
        {
            _text.Foreground = Foreground;
        }
    }


    protected override Size MeasureOverride(Size availableSize)
    {
        // 自然幅（オーバーフロー判定用）を得るため無限幅で測る。
        _text.Measure(new Size(double.PositiveInfinity, availableSize.Height));
        var w = double.IsInfinity(availableSize.Width) ? _text.DesiredSize.Width : availableSize.Width;
        return new Size(w, _text.DesiredSize.Height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        // 子は自然幅で配置（NoWrap）。はみ出しは ClipToBounds でクリップする。
        _text.Arrange(new Rect(0, 0, _text.DesiredSize.Width, finalSize.Height));
        Recompute(finalSize.Width);
        return finalSize;
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        // 行が履歴から消えた / 仮想化でリサイクルされた時に共有ティッカーへ残さない
        Unregister(this);
    }

    private void Recompute(double viewW)
    {
        var textW = _text.DesiredSize.Width;
        var distance = textW - viewW + 8; // 末尾まで見せる + 余白

        // #C-22: 画面に出ていない間は流さない（登録もしない）
        if (textW <= viewW || viewW <= 0 || Speed <= 0 || !IsEffectivelyVisible)
        {
            _distance = 0;
            _offset = 0;
            _xform.X = 0;
            Unregister(this); // 収まる → 静止（普通の左寄せ表示）
            return;
        }

        // 幅が変わっていない再 arrange（スクロールやサイズ無変化の再レイアウト）では
        // 流れている途中の位置を保つ。ここでリセットすると常に先頭へ巻き戻ってしまう。
        if (Math.Abs(distance - _distance) > 0.5)
        {
            _distance = distance;
            _offset = 0;
            _xform.X = 0;
            _pauseLeft = EdgePauseSeconds; // まず先頭で少し止めてから流す
        }

        Register(this);
    }

    /// <summary>共有ティッカーから 1 フレーム分呼ばれる。</summary>
    private void Advance()
    {
        // Speed <= 0 を与えられた場合、移動量が 0 か負になり _offset が _distance に到達できず
        // 回り続ける（または逆走する）ので、無効値なら登録を外す。
        if (_distance <= 0 || Speed <= 0)
        {
            Unregister(this);
            return;
        }

        if (_pauseLeft > 0)
        {
            _pauseLeft -= FrameMs / 1000.0;
            return;
        }

        _offset -= Speed * (FrameMs / 1000.0);
        if (_offset <= -_distance)
        {
            // 末尾まで流した → 先頭へ戻して少し静止、ループ
            _offset = 0;
            _xform.X = 0;
            _pauseLeft = EdgePauseSeconds;
            return;
        }
        _xform.X = _offset;
    }
}
