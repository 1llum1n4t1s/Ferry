// === チャット UI ===
const Chat = {
    messages: [],
    selectedPeerId: null,
    peerName: '',
    _escapeEl: null, // escapeHtml 用のキャッシュ要素

    init() {
        const input = document.getElementById('chat-input');
        const sendBtn = document.getElementById('btn-send');
        const attachBtn = document.getElementById('btn-attach');

        // Enter キーで送信
        input.addEventListener('keydown', (e) => {
            if (e.key === 'Enter' && !e.shiftKey) {
                e.preventDefault();
                this.sendMessage();
            }
        });

        sendBtn.addEventListener('click', () => this.sendMessage());
        attachBtn.addEventListener('click', () => Bridge.send('attachFile'));
    },

    sendMessage() {
        const input = document.getElementById('chat-input');
        const text = input.value.trim();
        if (!text) return;
        input.value = '';
        Bridge.send('sendMessage', text);
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
    },

    // 新着メッセージ追加
    addMessage(msg) {
        this.messages.push(msg);
        this.appendMessageElement(msg);
        this.scrollToBottom();
    },

    // プログレスバーを直接 DOM 更新（再描画なし）
    updateProgress(msgId, progress) {
        const el = document.querySelector(`[data-msg-id="${msgId}"] .file-progress-bar`);
        if (el) el.style.width = `${progress * 100}%`;
    },

    // メッセージ状態を直接更新（要素の再生成を回避）
    updateState(msgId, state) {
        const el = document.querySelector(`[data-msg-id="${msgId}"] .message-state`);
        if (el) el.textContent = this.stateText(state);
    },

    // メッセージ一覧を描画（DocumentFragment でバッチ追加、リフロー1回）
    renderMessages() {
        const container = document.getElementById('chat-messages');
        container.innerHTML = '';
        const frag = document.createDocumentFragment();
        for (const msg of this.messages) {
            frag.appendChild(this.createMessageElement(msg));
        }
        container.appendChild(frag);
        this.scrollToBottom();
    },

    // 単一メッセージ要素を追加
    appendMessageElement(msg) {
        const container = document.getElementById('chat-messages');
        container.appendChild(this.createMessageElement(msg));
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
            div.innerHTML = `
                <div class="bubble">${this.escapeHtml(msg.text)}</div>
                <div class="message-time">${msg.sentAt || ''}</div>
            `;
        } else if (type === 'file') {
            const progress = msg.fileProgress || 0;
            const showActions = msg.state === 'WaitingApproval' && !msg.isFromMe;

            div.innerHTML = `
                <div class="file-bubble">
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
                    <div class="message-state">${this.stateText(msg.state)}</div>
                </div>
                <div class="message-time">${msg.sentAt || ''}</div>
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

    // 添付ファイル表示
    showAttachments(fileNames) {
        const container = document.getElementById('chat-attachments');
        container.classList.remove('hidden');
        container.innerHTML = fileNames.map(name =>
            `<div class="attachment-chip">📎 ${this.escapeHtml(name)} <span class="remove" onclick="this.parentElement.remove()">✕</span></div>`
        ).join('');
    }
};

// C# からのイベント登録
Bridge.on('peerSelected', (data) => Chat.setPeer(data));
Bridge.on('loadHistory', (messages) => Chat.loadHistory(messages));
Bridge.on('newMessage', (msg) => Chat.addMessage(msg));
Bridge.on('filesAttached', (names) => Chat.showAttachments(names));
Bridge.on('updateProgress', (d) => Chat.updateProgress(d.id, d.progress));
Bridge.on('updateState', (d) => Chat.updateState(d.id, d.state));
