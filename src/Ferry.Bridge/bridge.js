/**
 * Ferry Bridge — スマートフォンブラウザでの QR ペアリングロジック。
 *
 * 処理フロー:
 * 1. URL の ?sid= パラメータからセッション ID (Side A) を取得
 * 2. Firebase Anonymous Auth でサインイン
 * 3. Firebase Realtime Database の pairing/{sessionId} にリクエストを書き込む
 * 4. デスクトップアプリ側が accepted を書き込むのを監視
 * 5. ペアリング成立を検知して完了表示
 */

const FIREBASE_CONFIG = {
    apiKey: "AIzaSyCOPRMYBv4keAHBjvFm4lgdfMoVva6rxTE",
    authDomain: "ferry-edf09.firebaseapp.com",
    databaseURL: "https://ferry-edf09-default-rtdb.firebaseio.com",
    projectId: "ferry-edf09",
    storageBucket: "ferry-edf09.firebasestorage.app",
    messagingSenderId: "453212071061",
    appId: "1:453212071061:web:a5daddfabaa5eff900279c",
    measurementId: "G-K29NXSWF83",
};

const statusText = document.getElementById("statusText");
const spinner = document.getElementById("spinner");
const sessionAInfo = document.getElementById("sessionAInfo");
const sessionAId = document.getElementById("sessionAId");
const waitingPanel = document.getElementById("waitingPanel");
const pairedPanel = document.getElementById("pairedPanel");
const errorPanel = document.getElementById("errorPanel");
const errorText = document.getElementById("errorText");

/**
 * URL パラメータからセッション ID を取得する。
 */
function getSessionIdFromUrl() {
    const params = new URLSearchParams(window.location.search);
    return params.get("sid");
}

/**
 * エラーを表示する。
 */
function showError(message) {
    statusText.textContent = "エラー";
    spinner.classList.add("hidden");
    waitingPanel.classList.add("hidden");
    errorPanel.classList.remove("hidden");
    errorText.textContent = message;
}

/**
 * ペアリング成功を表示する。
 */
function showPaired() {
    statusText.textContent = "ペアリング完了";
    spinner.classList.add("hidden");
    waitingPanel.classList.add("hidden");
    pairedPanel.classList.remove("hidden");
}

/**
 * メイン処理。
 */
async function main() {
    const sessionId = getSessionIdFromUrl();

    if (!sessionId) {
        showError("セッション ID が見つかりません。QR コードを再スキャンしてください。");
        return;
    }

    // セッション A の情報を表示
    sessionAInfo.classList.remove("hidden");
    sessionAId.textContent = sessionId;

    statusText.textContent = "Firebase に接続中…";

    try {
        // Firebase SDK 初期化
        firebase.initializeApp(FIREBASE_CONFIG);
        const db = firebase.database();
        const auth = firebase.auth();

        // Anonymous Auth でサインイン
        statusText.textContent = "認証中…";
        await auth.signInAnonymously();

        // pairing ノードにペアリングリクエストを書き込む
        statusText.textContent = "ペアリングリクエスト送信中…";
        const pairingRef = db.ref(`pairing/${sessionId}`);

        // デバイス情報を取得
        const deviceName = getDeviceName();

        await pairingRef.set({
            sidA: sessionId,
            deviceName: deviceName,
            timestamp: firebase.database.ServerValue.TIMESTAMP,
            status: "waiting",
        });

        // ペアリング待機中の表示
        statusText.textContent = "デスクトップアプリの応答を待機中…";
        waitingPanel.classList.remove("hidden");

        // pairing ノードの変更を監視
        pairingRef.on("value", (snapshot) => {
            const data = snapshot.val();
            if (!data) return;

            if (data.status === "accepted") {
                // デスクトップアプリがペアリングを承認した
                showPaired();
                // 監視を停止
                pairingRef.off("value");
            } else if (data.status === "rejected") {
                // デスクトップアプリがペアリングを拒否した
                showError("接続が拒否されました。");
                pairingRef.off("value");
            }
        });

        // タイムアウト処理（5 分）
        setTimeout(() => {
            pairingRef.off("value");
            pairingRef.remove().catch(() => {});
            if (!pairedPanel.classList.contains("hidden") === false) {
                showError("タイムアウト: デスクトップアプリからの応答がありませんでした。");
            }
        }, 5 * 60 * 1000);

    } catch (err) {
        showError(`接続エラー: ${err.message}`);
    }
}

/**
 * ブラウザの User Agent からデバイス名を簡易取得する。
 */
function getDeviceName() {
    const ua = navigator.userAgent;
    if (/iPhone/.test(ua)) return "iPhone";
    if (/iPad/.test(ua)) return "iPad";
    if (/Android/.test(ua)) {
        const match = ua.match(/;\s*([^;)]+)\s*Build\//);
        return match ? match[1].trim() : "Android";
    }
    return "モバイルブラウザ";
}

// ページ読み込み時に実行
document.addEventListener("DOMContentLoaded", main);
