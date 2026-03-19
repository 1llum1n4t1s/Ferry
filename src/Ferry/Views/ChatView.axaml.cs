using System;
using System.Collections.Specialized;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Ferry.ViewModels;

namespace Ferry.Views;

/// <summary>
/// チャットビュー。メッセージ一覧、入力エリア、添付ファイルチップを提供する。
/// </summary>
public partial class ChatView : UserControl
{
    private ScrollViewer? _scrollViewer;

    // FindControl キャッシュ（コンストラクタで一度だけ取得）
    private readonly TextBox? _messageInput;
    private readonly Button? _cancelReplyButton;
    private readonly Button? _searchButton;
    private readonly Button? _closeSearchButton;
    private readonly TextBox? _searchInput;
    private readonly Border? _searchBar;
    private readonly TextBlock? _searchResultCount;
    private readonly ListBox? _messageList;
    private readonly Button? _emojiButton;
    private readonly StackPanel? _emojiCategoryTabs;
    private readonly TextBox? _emojiSearchInput;
    private readonly WrapPanel? _emojiGrid;
    private readonly Border? _replyBar;
    private readonly TextBlock? _replyToLabel;
    private readonly TextBlock? _replyToText;

    /// <summary>検索デバウンス用タイマー（200ms）。</summary>
    private System.Threading.Timer? _searchDebounceTimer;

    public ChatView()
    {
        InitializeComponent();

        // FindControl キャッシュ初期化
        _messageInput = this.FindControl<TextBox>("MessageInput");
        _cancelReplyButton = this.FindControl<Button>("CancelReplyButton");
        _searchButton = this.FindControl<Button>("SearchButton");
        _closeSearchButton = this.FindControl<Button>("CloseSearchButton");
        _searchInput = this.FindControl<TextBox>("SearchInput");
        _searchBar = this.FindControl<Border>("SearchBar");
        _searchResultCount = this.FindControl<TextBlock>("SearchResultCount");
        _messageList = this.FindControl<ListBox>("MessageList");
        _emojiButton = this.FindControl<Button>("EmojiButton");
        _emojiCategoryTabs = this.FindControl<StackPanel>("EmojiCategoryTabs");
        _emojiSearchInput = this.FindControl<TextBox>("EmojiSearchInput");
        _emojiGrid = this.FindControl<WrapPanel>("EmojiGrid");
        _replyBar = this.FindControl<Border>("ReplyBar");
        _replyToLabel = this.FindControl<TextBlock>("ReplyToLabel");
        _replyToText = this.FindControl<TextBlock>("ReplyToText");

        // Enter キーで送信、Shift+Enter で改行
        if (_messageInput != null)
        {
            _messageInput.KeyDown += OnMessageInputKeyDown;
        }

        // リプライキャンセルボタン
        if (_cancelReplyButton != null)
        {
            _cancelReplyButton.Click += (_, _) => HideReplyBar();
        }

        // 絵文字グリッド生成
        InitEmojiPicker();

        // 検索ボタン・検索バー
        _searchButton?.AddHandler(Button.ClickEvent, OnSearchButtonClick);
        _closeSearchButton?.AddHandler(Button.ClickEvent, OnCloseSearchClick);

        if (_searchInput != null)
        {
            _searchInput.TextChanged += OnSearchInputChanged;
            _searchInput.KeyDown += OnSearchInputKeyDown;
        }
    }

    // === 絵文字ピッカー ===

    private static readonly (string Label, string[] Emojis)[] EmojiCategories =
    [
        ("😀", ["😀","😃","😄","😁","😆","😅","🤣","😂","🙂","🙃","😉","😊","😇","🥰","😍","🤩","😘","😗","😚","😙","🥲","😋","😛","😜","🤪","😝","🤑","🤗","🤭","🫢","🤫","🤔","🫡","🤐","🤨","😐","😑","😶","🫥","😏","😒","🙄","😬","🤥","😌","😔","😪","🤤","😴","😷","🤒","🤕","🤢","🤮","🥵","🥶","🥴","😵","🤯","🤠","🥳","🥸","😎","🤓","🧐","😕","🫤","😟","🙁","😮","😯","😲","😳","🥺","🥹","😦","😧","😨","😰","😥","😢","😭","😱","😖","😣","😞","😓","😩","😫","🥱","😤","😡","😠","🤬","😈","👿","💀","☠️","💩","🤡","👹","👺","👻","👽","👾","🤖"]),
        ("👋", ["👋","🤚","🖐️","✋","🖖","🫷","🫸","🫳","🫴","👌","🤌","🤏","✌️","🤞","🫰","🤟","🤘","🤙","👈","👉","👆","🖕","👇","☝️","🫵","👍","👎","✊","👊","🤛","🤜","👏","🙌","🫶","👐","🤲","🤝","🙏"]),
        ("❤️", ["❤️","🧡","💛","💚","💙","💜","🖤","🤍","🤎","💔","❤️‍🔥","❤️‍🩹","❣️","💕","💞","💓","💗","💖","💘","💝"]),
        ("🐶", ["🐶","🐱","🐭","🐹","🐰","🦊","🐻","🐼","🐻‍❄️","🐨","🐯","🦁","🐮","🐷","🐸","🐵","🐔","🐧","🐦","🦅","🦆","🦉","🦇","🐺","🐗","🐴","🦄","🐝","🐛","🦋","🐌","🐞","🐜","🪲","🐢","🐍","🦎","🦖","🦕","🐙","🦑","🦀","🐡","🐠","🐟","🐬","🐳","🐋","🦈","🐊"]),
        ("🍔", ["🍏","🍎","🍐","🍊","🍋","🍌","🍉","🍇","🍓","🫐","🍈","🍒","🍑","🥭","🍍","🥥","🥝","🍅","🍆","🥑","🥦","🥬","🥒","🌶️","🫑","🌽","🥕","🫒","🧄","🧅","🥔","🍠","🫘","🥐","🍞","🥖","🥨","🧀","🥚","🍳","🧈","🥞","🧇","🥓","🥩","🍗","🍖","🦴","🌭","🍔","🍟","🍕","🫓","🥪","🥙","🧆","🌮","🌯","🫔","🥗","🥘","🫕","🍝","🍜","🍲","🍛","🍣","🍱","🥟","🦪","🍤","🍙","🍚","🍘","🍥","🥠","🥮","🍢","🍡","🍧","🍨","🍦","🥧","🧁","🍰","🎂","🍮","🍭","🍬","🍫","🍿","🍩","🍪","🌰","🥜","🍯"]),
        ("⚽", ["⚽","🏀","🏈","⚾","🥎","🎾","🏐","🏉","🥏","🎱","🪀","🏓","🏸","🏒","🏑","🥍","🏏","🪃","🥅","⛳","🪁","🏹","🎣","🤿","🥊","🥋","🎽","🛹","🛼","🛷","⛸️","🥌","🎿","⛷️","🏂"]),
        ("🏠", ["🏠","🏢","🏥","🏦","🏫","🏪","🏨","💒","⛪","🕌","🛕","🕍","⛩️","🕋","⛲","⛺","🏕️","🗼","🗽","🗻","🌋","🏔️","⛰️","🏜️","🏖️","🏝️","🌅","🌄","🌠","🎇","🎆","🌇","🌆","🏙️","🌃","🌌","🌉","🌁"]),
    ];

    private string _currentCategory = "😀";

    private void InitEmojiPicker()
    {
        if (_emojiCategoryTabs == null) return;

        foreach (var (label, _) in EmojiCategories)
        {
            var btn = new Button
            {
                Content = label,
                FontSize = 16,
                Padding = new Avalonia.Thickness(6, 4),
                MinWidth = 32,
                Background = Avalonia.Media.Brushes.Transparent,
                BorderThickness = new Avalonia.Thickness(0),
                Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand),
                Tag = label,
            };
            btn.Click += OnCategoryClick;
            _emojiCategoryTabs.Children.Add(btn);
        }

        RenderEmojiGrid("😀");

        // 検索イベント
        if (_emojiSearchInput != null)
        {
            _emojiSearchInput.TextChanged += (_, _) =>
            {
                var query = _emojiSearchInput.Text?.Trim();
                if (string.IsNullOrEmpty(query))
                    RenderEmojiGrid(_currentCategory);
                else
                    SearchEmoji(query);
            };
        }
    }

    private void OnCategoryClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string label })
        {
            _currentCategory = label;
            RenderEmojiGrid(label);
            if (_emojiSearchInput != null) _emojiSearchInput.Text = string.Empty;
        }
    }

    /// <summary>絵文字グリッド内のボタンのイベントハンドラを解除する。</summary>
    private void DetachEmojiGridHandlers()
    {
        if (_emojiGrid == null) return;
        foreach (var child in _emojiGrid.Children)
        {
            if (child is Button btn)
                btn.Click -= OnEmojiClick;
        }
    }

    private void RenderEmojiGrid(string categoryLabel)
    {
        if (_emojiGrid == null) return;
        DetachEmojiGridHandlers();
        _emojiGrid.Children.Clear();

        var category = System.Array.Find(EmojiCategories, c => c.Label == categoryLabel);
        if (category.Emojis == null) return;

        foreach (var emoji in category.Emojis)
        {
            var btn = new Button
            {
                Content = emoji,
                FontSize = 22,
                Padding = new Avalonia.Thickness(2),
                MinWidth = 34,
                MinHeight = 34,
                Background = Avalonia.Media.Brushes.Transparent,
                BorderThickness = new Avalonia.Thickness(0),
                Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand),
            };
            btn.Click += OnEmojiClick;
            _emojiGrid.Children.Add(btn);
        }
    }

    private void SearchEmoji(string query)
    {
        if (_emojiGrid == null) return;
        DetachEmojiGridHandlers();
        _emojiGrid.Children.Clear();

        foreach (var (_, emojis) in EmojiCategories)
        {
            foreach (var emoji in emojis)
            {
                if (emoji.Contains(query, StringComparison.OrdinalIgnoreCase))
                {
                    var btn = new Button
                    {
                        Content = emoji, FontSize = 22,
                        Padding = new Avalonia.Thickness(2), MinWidth = 34, MinHeight = 34,
                        Background = Avalonia.Media.Brushes.Transparent,
                        BorderThickness = new Avalonia.Thickness(0),
                        Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand),
                    };
                    btn.Click += OnEmojiClick;
                    _emojiGrid.Children.Add(btn);
                }
            }
        }
    }

    private void OnEmojiClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Content: string emoji } && DataContext is ChatViewModel vm)
        {
            vm.MessageText = (vm.MessageText ?? string.Empty) + emoji;
            _emojiButton?.Flyout?.Hide();
            _messageInput?.Focus();
        }
    }

    // === メッセージ検索 ===

    private System.Collections.Generic.List<Models.ChatMessage>? _searchResults;
    private int _searchIndex = -1;

    private void OnSearchButtonClick(object? sender, RoutedEventArgs e)
    {
        if (_searchBar == null) return;

        _searchBar.IsVisible = !_searchBar.IsVisible;
        if (_searchBar.IsVisible)
        {
            _searchInput?.Focus();
        }
        else
        {
            ClearSearch();
        }
    }

    private void OnCloseSearchClick(object? sender, RoutedEventArgs e)
    {
        if (_searchBar != null) _searchBar.IsVisible = false;
        ClearSearch();
    }

    private void OnSearchInputChanged(object? sender, EventArgs e)
    {
        // 200ms デバウンス: キー入力ごとにタイマーをリセット
        _searchDebounceTimer?.Dispose();
        _searchDebounceTimer = new System.Threading.Timer(
            _ => Avalonia.Threading.Dispatcher.UIThread.Post(ExecuteSearch),
            null,
            200,
            System.Threading.Timeout.Infinite);
    }

    /// <summary>デバウンス後に実行される検索処理。</summary>
    private void ExecuteSearch()
    {
        var query = _searchInput?.Text?.Trim();
        if (string.IsNullOrEmpty(query) || DataContext is not ChatViewModel vm)
        {
            ClearSearch();
            return;
        }

        // メッセージを検索
        _searchResults = [];
        foreach (var msg in vm.Messages)
        {
            if (msg.Type == Models.ChatMessageType.Text
                && msg.Text != null
                && msg.Text.Contains(query, StringComparison.OrdinalIgnoreCase))
            {
                _searchResults.Add(msg);
            }
        }

        if (_searchResultCount != null)
            _searchResultCount.Text = _searchResults.Count > 0
                ? $"{_searchResults.Count} 件"
                : "0 件";

        // 最初の結果にスクロール
        if (_searchResults.Count > 0)
        {
            _searchIndex = 0;
            ScrollToMessage(_searchResults[0]);
        }
    }

    private void OnSearchInputKeyDown(object? sender, KeyEventArgs e)
    {
        if (_searchResults == null || _searchResults.Count == 0) return;

        if (e.Key == Key.Enter)
        {
            // 次の結果へ (Shift+Enter で前へ)
            if (e.KeyModifiers.HasFlag(KeyModifiers.Shift))
                _searchIndex = (_searchIndex - 1 + _searchResults.Count) % _searchResults.Count;
            else
                _searchIndex = (_searchIndex + 1) % _searchResults.Count;

            ScrollToMessage(_searchResults[_searchIndex]);

            if (_searchResultCount != null)
                _searchResultCount.Text = $"{_searchIndex + 1}/{_searchResults.Count} 件";

            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            OnCloseSearchClick(sender, e);
            e.Handled = true;
        }
    }

    private void ScrollToMessage(Models.ChatMessage msg)
    {
        if (_messageList == null) return;
        _messageList.SelectedItem = msg;
        _messageList.ScrollIntoView(msg);
    }

    private void ClearSearch()
    {
        _searchResults = null;
        _searchIndex = -1;
        if (_searchInput != null) _searchInput.Text = string.Empty;
        if (_searchResultCount != null) _searchResultCount.Text = string.Empty;
        if (_messageList != null) _messageList.SelectedItem = null;
    }

    protected override void OnLoaded(RoutedEventArgs e)
    {
        base.OnLoaded(e);

        // ScrollViewer を取得
        if (_messageList != null)
        {
            // ListBox 内の ScrollViewer を探す
            _scrollViewer = _messageList.Scroll as ScrollViewer;
        }

        // Messages の CollectionChanged で自動スクロール
        // 重複登録を防止するため、登録前に解除する
        if (DataContext is ChatViewModel vm)
        {
            vm.Messages.CollectionChanged -= OnMessagesChanged;
            vm.Messages.CollectionChanged += OnMessagesChanged;
        }
    }

    protected override void OnUnloaded(RoutedEventArgs e)
    {
        base.OnUnloaded(e);

        if (DataContext is ChatViewModel vm)
        {
            vm.Messages.CollectionChanged -= OnMessagesChanged;
        }

        // 検索デバウンスタイマーを破棄
        _searchDebounceTimer?.Dispose();
        _searchDebounceTimer = null;
    }

    /// <summary>リプライバーを表示する。</summary>
    public void ShowReplyBar(string senderName, string text)
    {
        if (_replyBar != null) _replyBar.IsVisible = true;
        if (_replyToLabel != null) _replyToLabel.Text = senderName;
        if (_replyToText != null) _replyToText.Text = text;
    }

    /// <summary>リプライバーを非表示にする。</summary>
    public void HideReplyBar()
    {
        if (_replyBar != null) _replyBar.IsVisible = false;
    }

    private void OnMessagesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        // 新しいメッセージが追加されたら末尾にスクロール
        if (e.Action == NotifyCollectionChangedAction.Add)
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                _scrollViewer?.ScrollToEnd();
            }, Avalonia.Threading.DispatcherPriority.Background);
        }
    }

    private void OnMessageInputKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && !e.KeyModifiers.HasFlag(KeyModifiers.Shift))
        {
            e.Handled = true;
            if (DataContext is ChatViewModel vm)
            {
                vm.SendMessageCommand.Execute(null);
            }
        }
    }
}
