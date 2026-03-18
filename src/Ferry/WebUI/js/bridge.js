// === C# ↔ JS 通信ブリッジ ===
const Bridge = {
    // C# にメッセージ送信
    send(action, data) {
        try {
            const msg = JSON.stringify({ action, data });
            if (window.chrome && window.chrome.webview) {
                window.chrome.webview.postMessage(msg);
            } else if (window.external && window.external.sendMessage) {
                window.external.sendMessage(msg);
            } else {
                console.warn('Bridge: C# 通信チャネルが見つかりません', action, data);
            }
        } catch (e) {
            console.error('Bridge.send エラー:', e);
        }
    },

    // C# からの受信ハンドラ登録
    handlers: {},
    on(action, handler) {
        this.handlers[action] = handler;
    },

    // C# からのメッセージディスパッチ
    dispatch(action, data) {
        const handler = this.handlers[action];
        if (handler) {
            try { handler(data); }
            catch (e) { console.error(`Bridge handler error [${action}]:`, e); }
        } else {
            console.warn('Bridge: 未知のアクション', action);
        }
    }
};

// C# → JS: PostWebMessageAsString で受信
if (window.chrome && window.chrome.webview) {
    window.chrome.webview.addEventListener('message', (e) => {
        try {
            const { action, data } = JSON.parse(e.data);
            Bridge.dispatch(action, data);
        } catch (err) {
            console.error('Bridge message parse error:', err);
        }
    });
}

// C# → JS: ExecuteScript 経由のフォールバック
window.receiveBridgeMessage = function(json) {
    try {
        const { action, data } = JSON.parse(json);
        Bridge.dispatch(action, data);
    } catch (e) {
        console.error('receiveBridgeMessage error:', e);
    }
};
