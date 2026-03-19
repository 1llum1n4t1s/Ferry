// === Ferry SPA アプリ ===
const App = {
    currentView: 'empty',
    selectedPeerId: null,
    peers: [],

    init() {
        Chat.init();

        // サイドバーボタン
        document.getElementById('btn-add-member').addEventListener('click', () =>
            Bridge.send('addMember'));
        document.getElementById('btn-settings').addEventListener('click', () =>
            Bridge.send('toggleSettings'));

        // ピア検索フィルタ
        document.getElementById('peer-search').addEventListener('input', (e) => {
            const query = e.target.value.toLowerCase();
            if (!query) {
                this.renderPeers(this.peers);
            } else {
                const filtered = this.peers.filter(p =>
                    p.displayName.toLowerCase().includes(query));
                this.renderPeers(filtered, false);
            }
        });

        // C# からのピアリスト更新
        Bridge.on('loadPeers', (peers) => this.renderPeers(peers));
        Bridge.on('showView', (view) => this.showView(view));
        Bridge.on('peerStatus', (data) => this.updatePeerStatus(data));

        // ファイルドラッグ＆ドロップ（capture フェーズで WebView2 のデフォルト動作を抑止）
        window.addEventListener('dragover', (e) => { e.preventDefault(); e.dataTransfer.dropEffect = 'copy'; }, true);
        window.addEventListener('dragenter', (e) => { e.preventDefault(); }, true);
        window.addEventListener('drop', (e) => {
            e.preventDefault();
            if (!e.dataTransfer?.files?.length) return;
            for (const file of e.dataTransfer.files) {
                const id = crypto.randomUUID();
                Bridge.send('dropFileStart', JSON.stringify({ id, name: file.name }));
                const reader = new FileReader();
                reader.onload = () => {
                    const base64 = reader.result.split(',')[1];
                    // 64KB チャンクに分割して送信
                    const chunkSize = 65536;
                    for (let i = 0; i < base64.length; i += chunkSize) {
                        Bridge.send('dropFileChunk', JSON.stringify({ id, data: base64.slice(i, i + chunkSize) }));
                    }
                    Bridge.send('dropFileEnd', JSON.stringify({ id }));
                };
                reader.readAsDataURL(file);
            }
        });

        // C# に UI 準備完了を通知
        Bridge.send('ready');
    },

    // ビュー切り替え
    showView(view) {
        this.currentView = view;
        document.querySelectorAll('.view').forEach(el => {
            el.classList.remove('active');
            el.classList.add('hidden');
        });
        const target = document.getElementById(`${view}-view`);
        if (target) {
            target.classList.remove('hidden');
            target.classList.add('active');
        }
    },

    // ピアリスト描画（updateCache=true でキャッシュも更新）
    renderPeers(peers, updateCache = true) {
        if (updateCache) this.peers = peers;
        const container = document.getElementById('peer-list');
        container.innerHTML = peers.map(p => `
            <div class="peer-item ${p.peerId === this.selectedPeerId ? 'selected' : ''}"
                 onclick="App.selectPeer('${p.peerId}')"
                 data-peer-id="${p.peerId}">
                <div class="peer-dot ${p.isOnline ? 'online' : 'offline'}"></div>
                <div class="peer-info">
                    <div class="peer-name">${Chat.escapeHtml(p.displayName)}</div>
                    <div class="peer-preview">${Chat.escapeHtml(p.lastMessagePreview || '')}</div>
                </div>
                <div class="peer-badges">
                    ${p.hasIncomingFile ? '<span class="badge-file">📦</span>' : ''}
                    ${p.unreadCount > 0 ? `<span class="badge-unread">${p.unreadCount}</span>` : ''}
                </div>
            </div>
        `).join('');
    },

    // ピア選択
    selectPeer(peerId) {
        this.selectedPeerId = peerId;
        // サイドバーの選択状態を更新
        document.querySelectorAll('.peer-item').forEach(el => {
            el.classList.toggle('selected', el.dataset.peerId === peerId);
        });
        Bridge.send('selectPeer', peerId);
    },

    // ピアステータス更新
    updatePeerStatus(data) {
        const item = document.querySelector(`.peer-item[data-peer-id="${data.peerId}"]`);
        if (item) {
            const dot = item.querySelector('.peer-dot');
            if (dot) {
                dot.className = `peer-dot ${data.isOnline ? 'online' : 'offline'}`;
            }
        }
    }
};

// DOM 読み込み完了時に初期化
if (document.readyState === 'loading') {
    document.addEventListener('DOMContentLoaded', () => App.init());
} else {
    App.init();
}
