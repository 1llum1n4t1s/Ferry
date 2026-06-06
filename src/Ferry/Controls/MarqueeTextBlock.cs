using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

namespace Ferry.Controls;

/// <summary>
/// テキストが横幅に収まらない時だけ左へ流すマーキー表示。収まる時は静止して左寄せ表示する。
/// Avalonia 標準にマーキーが無いため自作。テンプレート(ControlTheme)に依存せず、子 TextBlock を
/// コードで保持して DispatcherTimer で TranslateTransform.X を更新する単純方式
/// （「収まる時は流さない」「実測幅に応じた移動量」を確実に制御でき、Native AOT 安全）。
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

    private readonly TextBlock _text;
    private readonly TranslateTransform _xform = new();
    private DispatcherTimer? _timer;
    private double _offset;     // 現在の X（0 以下）
    private double _distance;   // 流す総距離（オーバーフロー分 + 余白）
    private double _pauseLeft;  // 端での静止残り秒

    private const double EdgePauseSeconds = 1.2; // 端で一旦止める時間
    private const double FrameMs = 33;           // ~30fps

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
        StopTimer(); // 行が履歴から消えた時にタイマーを残さない
    }

    private void Recompute(double viewW)
    {
        _offset = 0;
        _xform.X = 0;

        var textW = _text.DesiredSize.Width;
        if (textW <= viewW || viewW <= 0)
        {
            StopTimer(); // 収まる → 静止（普通の左寄せ表示）
            return;
        }

        _distance = textW - viewW + 8; // 末尾まで見せる + 余白
        _pauseLeft = EdgePauseSeconds;  // まず先頭で少し止めてから流す
        StartTimer();
    }

    private void StartTimer()
    {
        if (_timer is not null) return;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(FrameMs) };
        _timer.Tick += OnTick;
        _timer.Start();
    }

    private void StopTimer()
    {
        if (_timer is null) return;
        _timer.Stop();
        _timer.Tick -= OnTick;
        _timer = null;
    }

    private void OnTick(object? sender, EventArgs e)
    {
        if (_distance <= 0) return;

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
