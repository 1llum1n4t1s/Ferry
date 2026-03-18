// === チャット UI ===
const Chat = {
    messages: [],
    selectedPeerId: null,
    peerName: '',
    _escapeEl: null, // escapeHtml 用のキャッシュ要素
    _lastRenderedDate: null, // 日付区切り線用の前回日付

    init() {
        const input = document.getElementById('chat-input');
        const sendBtn = document.getElementById('btn-send');
        const attachBtn = document.getElementById('btn-attach');

        // Enter=送信、Shift+Enter=改行
        input.addEventListener('keydown', (e) => {
            if (e.key === 'Enter' && !e.shiftKey) {
                e.preventDefault();
                this.sendMessage();
            }
        });

        // textarea の高さを内容に合わせて自動調整
        input.addEventListener('input', () => this.autoGrow(input));

        sendBtn.addEventListener('click', () => this.sendMessage());
        attachBtn.addEventListener('click', () => Bridge.send('attachFile'));

        // クリップボードからの画像貼り付け
        input.addEventListener('paste', (e) => {
            const items = e.clipboardData?.items;
            if (!items) return;
            for (const item of items) {
                if (item.type.startsWith('image/')) {
                    e.preventDefault();
                    const blob = item.getAsFile();
                    if (!blob) return;
                    const reader = new FileReader();
                    reader.onload = () => {
                        Bridge.send('pasteImage', JSON.stringify({
                            data: reader.result,
                            name: 'clipboard-image.png',
                        }));
                    };
                    reader.readAsDataURL(blob);
                    return;
                }
            }
        });

        // コンテキストメニュー外クリックで閉じる
        document.addEventListener('click', () => this.closeContextMenu());
        // 右クリックメニュー（メッセージバブル上）
        document.getElementById('chat-messages').addEventListener('contextmenu', (e) => {
            const msgEl = e.target.closest('.message');
            if (!msgEl) return;
            const bubble = e.target.closest('.bubble');
            if (!bubble) return;
            e.preventDefault();
            this.showContextMenu(e.clientX, e.clientY, msgEl);
        });

        // 検索ボタン
        document.getElementById('btn-search').addEventListener('click', () => this.toggleSearch());
        document.getElementById('search-input').addEventListener('keydown', (e) => {
            if (e.key === 'Enter') {
                const query = e.target.value.trim();
                if (query) Bridge.send('searchMessages', query);
            }
            if (e.key === 'Escape') this.closeSearch();
        });

        // 絵文字ボタン
        document.getElementById('btn-emoji').addEventListener('click', () => EmojiPicker.toggle(document.getElementById('btn-emoji')));
    },

    // textarea の高さを自動調整（最大3行）
    autoGrow(el) {
        el.style.height = 'auto';
        el.style.height = Math.min(el.scrollHeight, 72) + 'px';
    },

    sendMessage() {
        const input = document.getElementById('chat-input');
        const text = input.value.trim();
        if (!text) return;
        input.value = '';
        input.style.height = 'auto';
        // リプライモードの場合
        if (this._replyToId) {
            Bridge.send('sendReply', JSON.stringify({ text, replyToId: this._replyToId, replyToText: this._replyToText || '' }));
            this.cancelReply();
        } else {
            Bridge.send('sendMessage', text);
        }
    },

    // ピア選択時のヘッダー更新
    setPeer(data) {
        this.selectedPeerId = data.peerId;
        this.peerName = data.displayName;
        document.getElementById('chat-peer-name').textContent = data.displayName;
        document.getElementById('chat-peer-status').textContent =
            data.isOnline ? '🟢 Online' : '🔴 Offline';
    },

    // チャット履歴の読み込み
    loadHistory(messages) {
        this.messages = messages || [];
        this.renderMessages();
        if (this._scrollToMessageId) {
            const msgId = this._scrollToMessageId;
            this._scrollToMessageId = null;
            requestAnimationFrame(() => this.scrollToMessage(msgId));
        }
    },

    // 新着メッセージ追加
    addMessage(msg) {
        this.messages.push(msg);
        const container = document.getElementById('chat-messages');
        // 日付区切り線の挿入チェック
        const prevMsg = this.messages.length > 1 ? this.messages[this.messages.length - 2] : null;
        const sep = this.createDateSeparatorIfNeeded(msg, prevMsg);
        if (sep) container.appendChild(sep);
        container.appendChild(this.createMessageElement(msg));
        this.scrollToBottom();
    },

    // プログレスバーを直接 DOM 更新（再描画なし）
    updateProgress(msgId, progress) {
        const el = document.querySelector(`[data-msg-id="${msgId}"] .file-progress-bar`);
        if (el) el.style.width = `${progress * 100}%`;
    },

    // メッセージ状態を直接更新（要素の再生成を回避）
    updateState(msgId, state) {
        const msgEl = document.querySelector(`[data-msg-id="${msgId}"]`);
        if (!msgEl) return;
        // 状態テキスト更新
        const stateEl = msgEl.querySelector('.message-state');
        if (stateEl) stateEl.textContent = this.stateText(state);
        // Failed 状態の場合、再送ボタンを表示
        const existingRetry = msgEl.querySelector('.retry-btn');
        if (state === 'Failed') {
            if (!existingRetry) {
                const btn = document.createElement('button');
                btn.className = 'retry-btn';
                btn.textContent = '再送';
                btn.onclick = () => Bridge.send('retryMessage', msgId);
                msgEl.appendChild(btn);
            }
        } else {
            // Failed でなくなったら再送ボタンを削除
            if (existingRetry) existingRetry.remove();
        }
    },

    // メッセージ一覧を描画（DocumentFragment でバッチ追加、リフロー1回）
    renderMessages() {
        const container = document.getElementById('chat-messages');
        container.innerHTML = '';
        this._lastRenderedDate = null;
        const frag = document.createDocumentFragment();
        let prevMsg = null;
        for (const msg of this.messages) {
            const sep = this.createDateSeparatorIfNeeded(msg, prevMsg);
            if (sep) frag.appendChild(sep);
            frag.appendChild(this.createMessageElement(msg));
            prevMsg = msg;
        }
        container.appendChild(frag);
        this.scrollToBottom();
    },

    // 日付区切り線を生成（必要な場合のみ）
    createDateSeparatorIfNeeded(msg, prevMsg) {
        const msgDate = this.extractDate(msg.sentAt);
        if (!msgDate) return null;
        const prevDate = prevMsg ? this.extractDate(prevMsg.sentAt) : null;
        if (prevDate && prevDate === msgDate) return null;
        // 日付が変わった → 区切り線を生成
        const div = document.createElement('div');
        div.className = 'date-separator';
        div.textContent = this.formatDateLabel(msgDate);
        return div;
    },

    // sentAt 文字列から日付部分を抽出（"YYYY/MM/DD" or "MM/DD" 等に対応）
    extractDate(sentAt) {
        if (!sentAt) return null;
        // "2026/03/19 14:30" や "14:30" 等のフォーマットに対応
        // 日付部分を含む場合はその日付を返す
        const match = sentAt.match(/(\d{4})[\/\-](\d{1,2})[\/\-](\d{1,2})/);
        if (match) return `${match[1]}-${match[2].padStart(2,'0')}-${match[3].padStart(2,'0')}`;
        // 日付なし（時刻のみ）→ 今日とみなす
        const now = new Date();
        return `${now.getFullYear()}-${String(now.getMonth()+1).padStart(2,'0')}-${String(now.getDate()).padStart(2,'0')}`;
    },

    // 日付ラベルのフォーマット（「今日」「昨日」「3月18日」）
    formatDateLabel(dateStr) {
        const today = new Date();
        const todayStr = `${today.getFullYear()}-${String(today.getMonth()+1).padStart(2,'0')}-${String(today.getDate()).padStart(2,'0')}`;
        const yesterday = new Date(today);
        yesterday.setDate(yesterday.getDate() - 1);
        const yesterdayStr = `${yesterday.getFullYear()}-${String(yesterday.getMonth()+1).padStart(2,'0')}-${String(yesterday.getDate()).padStart(2,'0')}`;

        if (dateStr === todayStr) return '今日';
        if (dateStr === yesterdayStr) return '昨日';
        // "YYYY-MM-DD" → "M月D日"
        const parts = dateStr.split('-');
        return `${parseInt(parts[1])}月${parseInt(parts[2])}日`;
    },

    // テキストを Markdown + URL リンク化して HTML に変換
    // 処理順序: escapeHtml → code block → inline code → bold → italic → URL linkify
    renderText(text) {
        let html = this.escapeHtml(text);

        // コードブロック: ```...```（改行を含む）
        html = html.replace(/```([\s\S]*?)```/g, (_, code) => {
            return `<pre><code>${code}</code></pre>`;
        });

        // インラインコード: `...`
        html = html.replace(/`([^`\n]+)`/g, (_, code) => {
            return `<code>${code}</code>`;
        });

        // 太字: **...**（code 内部を除外するため、<code> タグを含まない部分のみ処理）
        html = html.replace(/\*\*([^*]+)\*\*/g, '<strong>$1</strong>');

        // 斜体: *...*（太字の * と衝突しないよう、strong 処理後に実行）
        html = html.replace(/(?<!\*)\*([^*]+)\*(?!\*)/g, '<em>$1</em>');

        // URL リンク化: pre/code 内の URL はリンク化しない
        html = this.linkifyOutsideCode(html);

        // 改行を <br> に変換（pre 内部は除外）
        html = this.nlToBrOutsidePre(html);

        return html;
    },

    // <pre>/<code> 外の URL をリンクに変換
    linkifyOutsideCode(html) {
        // <pre>...</pre> と <code>...</code> をプレースホルダーに退避
        const codeBlocks = [];
        html = html.replace(/<(pre|code)[\s\S]*?<\/\1>/g, (match) => {
            codeBlocks.push(match);
            return `\x00CODE${codeBlocks.length - 1}\x00`;
        });
        // URL をリンク化
        html = html.replace(/(https?:\/\/[^\s<>"']+)/g,
            '<a href="$1" target="_blank" rel="noopener">$1</a>');
        // プレースホルダーを復元
        html = html.replace(/\x00CODE(\d+)\x00/g, (_, i) => codeBlocks[parseInt(i)]);
        return html;
    },

    // <pre> 外の改行を <br> に変換
    nlToBrOutsidePre(html) {
        const parts = html.split(/(<pre[\s\S]*?<\/pre>)/);
        return parts.map((part, i) => {
            // 奇数インデックス = <pre> ブロック → そのまま
            if (i % 2 === 1) return part;
            return part.replace(/\n/g, '<br>');
        }).join('');
    },

    // メッセージ要素を生成
    createMessageElement(msg) {
        const div = document.createElement('div');
        const side = msg.isFromMe ? 'mine' : 'peer';
        const type = msg.type || 'text';

        div.className = `message ${side}`;
        if (type === 'system') div.className = 'message system';
        div.dataset.msgId = msg.id;

        if (type === 'text') {
            const renderedText = this.renderText(msg.text);
            const retryHtml = msg.state === 'Failed' ?
                `<button class="retry-btn" onclick="Chat.retryMessage('${msg.id}')">再送</button>` : '';
            div.innerHTML = `
                <div class="bubble">${renderedText}</div>
                <div class="message-time">${msg.sentAt || ''}</div>
                <div class="message-state">${this.stateText(msg.state)}</div>
                ${retryHtml}
            `;
        } else if (type === 'file') {
            const progress = msg.fileProgress || 0;
            const showActions = msg.state === 'WaitingApproval' && !msg.isFromMe;
            const retryHtml = msg.state === 'Failed' ?
                `<button class="retry-btn" onclick="Chat.retryMessage('${msg.id}')">再送</button>` : '';
            const isImage = this.isImageFile(msg.fileName);
            const thumbnailHtml = isImage && msg.thumbnailData
                ? `<img class="message-image" src="${msg.thumbnailData}" onclick="Bridge.send('openFile','${this.escapeHtml(msg.filePath || '')}')" alt="${this.escapeHtml(msg.fileName || '')}">`
                : '';
            const openFolderHtml = msg.state === 'Completed' && !msg.isFromMe && msg.filePath
                ? `<button class="open-folder-btn" onclick="Bridge.send('openFolder','${this.escapeHtml(msg.filePath)}')">📂 ${I18n.t('Transfer.OpenFolder')}</button>`
                : '';
            const cancelHtml = msg.state === 'Transferring' && msg.transferId
                ? `<button class="cancel-transfer-btn" onclick="Bridge.send('cancelTransfer','${msg.transferId}')">✕</button>`
                : '';

            div.innerHTML = `
                <div class="file-bubble">
                    ${cancelHtml}
                    ${thumbnailHtml}
                    <div class="file-name">
                        <span class="file-icon">📎</span>
                        <span>${this.escapeHtml(msg.fileName || '')}</span>
                    </div>
                    <div class="file-size">${msg.fileSize || ''}</div>
                    ${msg.state === 'Transferring' ? `
                        <div class="file-progress">
                            <div class="file-progress-bar" style="width:${progress * 100}%"></div>
                        </div>` : ''}
                    ${showActions ? `
                        <div class="file-actions">
                            <button class="approve-btn" onclick="Chat.approve('${msg.transferId}')">${I18n.t('Transfer.Approve')}</button>
                            <button class="reject-btn" onclick="Chat.reject('${msg.transferId}')">${I18n.t('Transfer.Reject')}</button>
                        </div>` : ''}
                    ${openFolderHtml}
                    <div class="message-state">${this.stateText(msg.state)}</div>
                </div>
                <div class="message-time">${msg.sentAt || ''}</div>
                ${retryHtml}
            `;
        } else if (type === 'system') {
            div.innerHTML = `<div class="bubble">${this.escapeHtml(msg.text)}</div>`;
        }

        return div;
    },

    stateText(state) {
        const map = {
            'Sending': I18n.t('State.Sending'),
            'Sent': I18n.t('State.Sent'),
            'Delivered': '✓✓',
            'Failed': '❌ ' + I18n.t('State.Error'),
            'WaitingApproval': I18n.t('State.WaitingApproval'),
            'Transferring': I18n.t('State.Receiving'),
            'Completed': '✅ ' + I18n.t('State.Completed'),
        };
        return map[state] || state || '';
    },

    approve(transferId) {
        Bridge.send('approveFile', transferId);
    },

    reject(transferId) {
        Bridge.send('rejectFile', transferId);
    },

    retryMessage(msgId) {
        Bridge.send('retryMessage', msgId);
    },

    // === 検索機能 ===
    toggleSearch() {
        const bar = document.getElementById('search-bar');
        const results = document.getElementById('search-results');
        bar.classList.toggle('hidden');
        if (!bar.classList.contains('hidden')) {
            document.getElementById('search-input').focus();
        } else {
            results.classList.add('hidden');
            results.innerHTML = '';
            document.getElementById('search-input').value = '';
        }
    },
    closeSearch() {
        document.getElementById('search-bar').classList.add('hidden');
        const results = document.getElementById('search-results');
        results.classList.add('hidden');
        results.innerHTML = '';
        document.getElementById('search-input').value = '';
    },
    showSearchResults(results) {
        const container = document.getElementById('search-results');
        container.classList.remove('hidden');
        if (!results || results.length === 0) {
            container.innerHTML = '<div class="search-result-item"><div class="search-result-text">検索結果がありません</div></div>';
            return;
        }
        container.innerHTML = results.map(r => `
            <div class="search-result-item" onclick="Chat.goToSearchResult('${this.escapeHtml(r.peerId)}', '${this.escapeHtml(r.messageId)}')">
                <div class="search-result-peer">${this.escapeHtml(r.peerName)}</div>
                <div class="search-result-text">
                    ${this.escapeHtml(r.text.length > 80 ? r.text.substring(0, 80) + '...' : r.text)}
                    <span class="search-result-time">${this.escapeHtml(r.sentAt)}</span>
                </div>
            </div>
        `).join('');
    },
    goToSearchResult(peerId, messageId) {
        this.closeSearch();
        App.selectPeer(peerId);
        this._scrollToMessageId = messageId;
    },
    scrollToMessage(messageId) {
        const el = document.querySelector(`[data-msg-id="${messageId}"]`);
        if (el) {
            el.scrollIntoView({ behavior: 'smooth', block: 'center' });
            el.classList.add('highlight');
            setTimeout(() => el.classList.remove('highlight'), 2000);
        }
    },

    scrollToBottom() {
        const container = document.getElementById('chat-messages');
        requestAnimationFrame(() => {
            container.scrollTop = container.scrollHeight;
        });
    },

    escapeHtml(text) {
        // DOM 要素を再利用してGC圧を削減
        if (!this._escapeEl) this._escapeEl = document.createElement('span');
        this._escapeEl.textContent = text || '';
        return this._escapeEl.innerHTML;
    },

    // === コンテキストメニュー ===
    showContextMenu(x, y, msgEl) {
        this.closeContextMenu();
        const msgId = msgEl.dataset.msgId;
        const isMine = msgEl.classList.contains('mine');
        const menu = document.createElement('div');
        menu.className = 'context-menu';
        menu.id = 'chat-context-menu';

        // メニュー項目定義
        const items = [
            { label: 'コピー', action: () => Bridge.send('copyMessage', msgId) },
            { label: 'リプライ', action: () => Bridge.send('replyMessage', msgId) },
            { label: 'リアクション', action: () => Bridge.send('reactMessage', msgId) },
        ];

        // 自分のメッセージのみ: 編集・削除
        if (isMine) {
            items.push({ separator: true });
            items.push({
                label: '編集',
                action: () => {
                    const msg = this.messages.find(m => m.id === msgId);
                    if (msg) Bridge.send('editMessage', JSON.stringify({ id: msgId, text: msg.text }));
                }
            });
            items.push({
                label: '削除',
                danger: true,
                action: () => Bridge.send('deleteMessage', msgId)
            });
        }

        for (const item of items) {
            if (item.separator) {
                const sep = document.createElement('div');
                sep.className = 'context-menu-separator';
                menu.appendChild(sep);
                continue;
            }
            const el = document.createElement('div');
            el.className = 'context-menu-item' + (item.danger ? ' danger' : '');
            el.textContent = item.label;
            el.addEventListener('click', (e) => {
                e.stopPropagation();
                item.action();
                this.closeContextMenu();
            });
            menu.appendChild(el);
        }

        // 画面内に収まるよう位置調整
        document.body.appendChild(menu);
        const rect = menu.getBoundingClientRect();
        if (x + rect.width > window.innerWidth) x = window.innerWidth - rect.width - 8;
        if (y + rect.height > window.innerHeight) y = window.innerHeight - rect.height - 8;
        menu.style.left = x + 'px';
        menu.style.top = y + 'px';
    },

    closeContextMenu() {
        const menu = document.getElementById('chat-context-menu');
        if (menu) menu.remove();
    },

    // 画像ファイルかどうかを判定
    isImageFile(fileName) {
        if (!fileName) return false;
        const ext = fileName.split('.').pop()?.toLowerCase();
        return ['jpg', 'jpeg', 'png', 'gif', 'webp', 'bmp'].includes(ext);
    },

    // 添付ファイル表示
    showAttachments(fileNames) {
        const container = document.getElementById('chat-attachments');
        container.classList.remove('hidden');
        container.innerHTML = fileNames.map(name =>
            `<div class="attachment-chip">📎 ${this.escapeHtml(name)} <span class="remove" onclick="this.parentElement.remove()">✕</span></div>`
        ).join('');
    },

    // === メッセージ操作 ===

    markDeleted(msgId) {
        const el = document.querySelector(`[data-msg-id="${msgId}"]`);
        if (el) { el.classList.add('deleted'); const b = el.querySelector('.bubble'); if (b) b.innerHTML = '<em>このメッセージは削除されました</em>'; }
    },

    markEdited(msgId, newText) {
        const el = document.querySelector(`[data-msg-id="${msgId}"]`);
        if (el) { const b = el.querySelector('.bubble'); if (b) b.innerHTML = this.renderText(newText) + '<span class="edited-label">(編集済み)</span>'; }
    },

    showEditDialog(dataStr) {
        try {
            const d = typeof dataStr === 'string' ? JSON.parse(dataStr) : dataStr;
            const newText = prompt('メッセージを編集:', d.text || '');
            if (newText !== null && newText.trim()) {
                Bridge.send('submitEdit', JSON.stringify({ id: d.id, newText: newText.trim() }));
            }
        } catch(e) { /* ignore */ }
    },

    // リプライバー
    _replyToId: null,
    _replyToText: null,

    showReplyBar(data) {
        this._replyToId = data.id;
        this._replyToText = data.text;
        const bar = document.getElementById('reply-bar');
        if (bar) { bar.classList.remove('hidden'); document.getElementById('reply-text').textContent = data.text || ''; }
        document.getElementById('chat-input').focus();
    },

    cancelReply() {
        this._replyToId = null;
        this._replyToText = null;
        const bar = document.getElementById('reply-bar');
        if (bar) bar.classList.add('hidden');
    },

    // リアクションピッカー（6種固定）
    showReactionPicker(msgId) {
        this.closeReactionPicker();
        const el = document.querySelector(`[data-msg-id="${msgId}"]`);
        if (!el) return;
        const rect = el.getBoundingClientRect();
        const picker = document.createElement('div');
        picker.className = 'reaction-picker';
        picker.id = 'reaction-picker';
        const emojis = ['👍','❤️','😂','😮','😢','😡'];
        picker.innerHTML = emojis.map(e => `<button onclick="Chat.sendReaction('${msgId}','${e}')">${e}</button>`).join('');
        picker.style.left = rect.left + 'px';
        picker.style.top = (rect.top - 40) + 'px';
        document.body.appendChild(picker);
        setTimeout(() => document.addEventListener('click', Chat._closeReactionOnClick = () => Chat.closeReactionPicker(), { once: true }), 0);
    },

    closeReactionPicker() {
        const p = document.getElementById('reaction-picker');
        if (p) p.remove();
    },

    sendReaction(msgId, emoji) {
        this.closeReactionPicker();
        Bridge.send('sendReaction', JSON.stringify({ msgId, emoji }));
    },

    addReaction(msgId, emoji, senderName) {
        const el = document.querySelector(`[data-msg-id="${msgId}"]`);
        if (!el) return;
        let container = el.querySelector('.reactions');
        if (!container) { container = document.createElement('div'); container.className = 'reactions'; el.appendChild(container); }
        // 既存バッジに追加
        const existing = container.querySelector(`[data-emoji="${emoji}"]`);
        if (existing) { const count = existing.querySelector('.count'); if (count) count.textContent = parseInt(count.textContent) + 1; }
        else { container.innerHTML += `<span class="reaction-badge" data-emoji="${emoji}">${emoji}<span class="count">1</span></span>`; }
    },

    // 添付ファイルをクリア
    clearAttachments() {
        const container = document.getElementById('chat-attachments');
        container.innerHTML = '';
        container.classList.add('hidden');
    },
};

// C# からのイベント登録
Bridge.on('peerSelected', (data) => Chat.setPeer(data));
Bridge.on('loadHistory', (messages) => Chat.loadHistory(messages));
Bridge.on('newMessage', (msg) => Chat.addMessage(msg));
Bridge.on('filesAttached', (names) => Chat.showAttachments(names));
Bridge.on('clearAttachments', () => Chat.clearAttachments());
Bridge.on('updateProgress', (d) => Chat.updateProgress(d.id, d.progress));
Bridge.on('updateState', (d) => Chat.updateState(d.id, d.state));
Bridge.on('searchResults', (results) => Chat.showSearchResults(results));
Bridge.on('messageDeleted', (msgId) => Chat.markDeleted(msgId));
Bridge.on('messageEdited', (d) => Chat.markEdited(d.id, d.newText));
Bridge.on('showEditDialog', (d) => Chat.showEditDialog(d));
Bridge.on('showReplyBar', (d) => Chat.showReplyBar(d));
Bridge.on('showReactionPicker', (msgId) => Chat.showReactionPicker(msgId));
Bridge.on('reactionReceived', (d) => Chat.addReaction(d.id, d.emoji, d.senderName));
