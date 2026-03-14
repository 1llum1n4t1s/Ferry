/**
 * Ferry Bridge — スマートフォンブラウザで 2 台の PC をペアリングする。
 *
 * 処理フロー:
 * 1. URL の ?sid=&name= から接続元のセッション情報を取得
 * 2. Firebase Realtime Database で sessions/{sid} の存在を確認
 * 3. ページ内カメラ（html5-qrcode）でペアリング先の QR コードをスキャン
 * 4. pairings/ に両方のセッション ID を書き込み → 両 PC に通知
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

// DOM 要素
const statusText = document.getElementById("statusText");
const spinner = document.getElementById("spinner");
const sessionAInfo = document.getElementById("sessionAInfo");
const sessionAId = document.getElementById("sessionAId");
const sessionAName = document.getElementById("sessionAName");
const scanPanel = document.getElementById("scanPanel");
const qrReader = document.getElementById("qrReader");
const pairedPanel = document.getElementById("pairedPanel");
const pairedNames = document.getElementById("pairedNames");
const errorPanel = document.getElementById("errorPanel");
const errorText = document.getElementById("errorText");

let db = null;
let html5QrCode = null;

/**
 * URL パラメータを取得する。
 */
function getParams() {
    const params = new URLSearchParams(window.location.search);
    return {
        sid: params.get("sid"),
        name: params.get("name") ? decodeURIComponent(params.get("name")) : null,
    };
}

/**
 * エラーを表示する。
 */
function showError(message) {
    statusText.textContent = "エラー";
    spinner.classList.add("hidden");
    scanPanel.classList.add("hidden");
    errorPanel.classList.remove("hidden");
    errorText.textContent = message;
    // ステータスパネル内にもエラー詳細を表示（確実に見える位置）
    const detail = document.getElementById("errorDetail");
    if (detail) {
        detail.textContent = message;
        detail.classList.remove("hidden");
    }
    stopCamera();
}

/**
 * ペアリング成功を表示する。
 */
function showPaired(nameA, nameB) {
    statusText.textContent = "ペアリング完了！";
    spinner.classList.add("hidden");
    scanPanel.classList.add("hidden");
    pairedPanel.classList.remove("hidden");
    pairedNames.textContent = `「${nameA}」と「${nameB}」がペアリングされました`;
    stopCamera();
}

/**
 * カメラを停止する。
 */
function stopCamera() {
    if (html5QrCode) {
        html5QrCode.stop().catch(() => {});
        html5QrCode = null;
    }
}

/**
 * QR コードの URL からセッション情報を抽出する。
 */
function parseQrUrl(text) {
    try {
        const url = new URL(text);
        const params = new URLSearchParams(url.search);
        return {
            sid: params.get("sid"),
            name: params.get("name") ? decodeURIComponent(params.get("name")) : null,
        };
    } catch {
        return { sid: null, name: null };
    }
}

/**
 * ペアリング先の QR スキャン用カメラを起動する。
 */
async function startQrScanner(sidA, nameA) {
    scanPanel.classList.remove("hidden");
    statusText.textContent = "ペアリング先の QR コードをスキャンしてください";
    spinner.classList.add("hidden");

    html5QrCode = new Html5Qrcode("qrReader");

    try {
        await html5QrCode.start(
            { facingMode: "environment" },
            { fps: 10, qrbox: { width: 250, height: 250 } },
            async (decodedText) => {
                // QR コード読み取り成功
                const { sid: sidB, name: nameB } = parseQrUrl(decodedText);

                if (!sidB) {
                    // Ferry の QR コードではない
                    return;
                }

                if (sidB === sidA) {
                    // 同じ PC の QR コードをスキャンした
                    return;
                }

                // カメラ停止
                stopCamera();
                statusText.textContent = "ペアリング中…";
                spinner.classList.remove("hidden");
                scanPanel.classList.add("hidden");

                try {
                    // sessions/{sidB} の存在を確認
                    const snapB = await db.ref(`sessions/${sidB}`).once("value");
                    if (!snapB.exists()) {
                        showError("ペアリング先のセッションが見つかりません。PC でアプリが起動していることを確認してください。");
                        return;
                    }

                    // pairings/ にペアリング情報を書き込み
                    const pairingId = `${Date.now()}_${Math.random().toString(36).slice(2, 8)}`;
                    await db.ref(`pairings/${pairingId}`).set({
                        SidA: sidA,
                        SidB: sidB,
                        NameA: nameA || "PC-A",
                        NameB: nameB || snapB.val().DisplayName || "PC-B",
                        CreatedAt: Date.now(),
                    });

                    showPaired(nameA || "PC-A", nameB || snapB.val().DisplayName || "PC-B");
                } catch (err) {
                    showError(`ペアリングエラー: ${err.message}`);
                }
            },
            () => {
                // QR コード未検出（スキャン中）
            }
        );
    } catch (err) {
        showError(`カメラの起動に失敗しました: ${err.message}`);
    }
}

/**
 * メイン処理。
 */
async function main() {
    const { sid: sidA, name: nameA } = getParams();

    if (!sidA) {
        showError("セッション ID が見つかりません。QR コードを再スキャンしてください。");
        return;
    }

    // 接続元の情報を表示
    sessionAInfo.classList.remove("hidden");
    sessionAId.textContent = sidA;
    if (nameA) sessionAName.textContent = nameA;

    statusText.textContent = "Firebase に接続中…";

    try {
        // Firebase SDK 初期化（認証なし）
        firebase.initializeApp(FIREBASE_CONFIG);
        db = firebase.database();

        // sessions/{sidA} の存在を確認
        statusText.textContent = "セッション情報を確認中…";
        const snapA = await db.ref(`sessions/${sidA}`).once("value");
        if (!snapA.exists()) {
            showError("セッションが見つかりません。PC でアプリが起動していることを確認してください。");
            return;
        }

        // 接続元の登録確認完了 → ペアリング先のスキャンへ
        await startQrScanner(sidA, nameA || snapA.val().DisplayName || "PC-A");

    } catch (err) {
        console.error("Bridge エラー:", err);
        showError(`接続エラー: ${err.code || ""} ${err.message}`);
    }
}

// ページ読み込み時に実行
document.addEventListener("DOMContentLoaded", main);
