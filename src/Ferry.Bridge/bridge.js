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
const pasteInput = document.getElementById("pasteInput");
const pasteStatus = document.getElementById("pasteStatus");

let db = null;
let html5QrCode = null;
// ペアリング処理の重複実行を防ぐフラグ（カメラ/貼り付け両経路で参照）
let pairingInProgress = false;

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
 * sidA と sidB を Firebase pairings/ に書き込んでペアリングを成立させる。
 * カメラ経路 / URL 貼り付け経路の共通処理。
 */
async function performPairing(sidA, nameA, sidB, nameB) {
    // 重複実行防止（同時にカメラ読取 + 貼り付け確定が起きた場合の保険）
    if (pairingInProgress) return;
    pairingInProgress = true;

    // カメラ停止 + 進行表示
    stopCamera();
    statusText.textContent = "ペアリング中…";
    spinner.classList.remove("hidden");
    scanPanel.classList.add("hidden");

    try {
        // sessions/{sidB} の存在を確認
        const snapB = await db.ref(`sessions/${sidB}`).once("value");
        if (!snapB.exists()) {
            pairingInProgress = false;
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
        pairingInProgress = false;
        showError(`ペアリングエラー: ${err.message}`);
    }
}

/**
 * URL 貼り付け経路 — PC ブラウザでカメラが使えないケース向け。
 * 入力欄に Ferry のペアリングリンクが入った瞬間に自動でペアリング処理を起動する。
 */
function setupPasteListener(sidA, nameA) {
    if (!pasteInput) return;

    const tryPair = async () => {
        const text = (pasteInput.value || "").trim();
        if (!text) {
            pasteStatus.textContent = "";
            pasteStatus.className = "paste-status";
            return;
        }

        const { sid: sidB, name: nameB } = parseQrUrl(text);

        if (!sidB) {
            pasteStatus.textContent = "Ferry のペアリングリンクではないみたい…URL を確認してね";
            pasteStatus.className = "paste-status err";
            return;
        }

        if (sidB === sidA) {
            pasteStatus.textContent = "同じ PC の URL です。もう片方の PC のリンクを貼り付けてください";
            pasteStatus.className = "paste-status err";
            return;
        }

        pasteStatus.textContent = "✓ URL を認識、ペアリング処理中…";
        pasteStatus.className = "paste-status ok";
        await performPairing(sidA, nameA, sidB, nameB);
    };

    // ペースト直後 / 入力変更時 / Enter 押下 で都度ペアリング試行
    pasteInput.addEventListener("paste", () => {
        // paste イベントは値の反映前に発火するので次フレームで読み取る
        setTimeout(tryPair, 0);
    });
    pasteInput.addEventListener("input", tryPair);
    pasteInput.addEventListener("keydown", (e) => {
        if (e.key === "Enter") {
            e.preventDefault();
            tryPair();
        }
    });
}

/**
 * ペアリング先の QR スキャン用カメラを起動する。
 * カメラ起動に失敗した場合（PC ブラウザ等）も貼り付け経路は使えるよう、scanPanel 自体は表示する。
 */
async function startQrScanner(sidA, nameA) {
    scanPanel.classList.remove("hidden");
    statusText.textContent = "ペアリング先の QR コードをスキャン、または URL を貼り付けてください";
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

                await performPairing(sidA, nameA, sidB, nameB);
            },
            () => {
                // QR コード未検出（スキャン中）
            }
        );
    } catch (err) {
        // カメラ起動失敗 (PC ブラウザ / カメラ非搭載デバイス) でも URL 貼り付け経路は維持
        console.warn("カメラ起動失敗:", err);
        const qrReader = document.getElementById("qrReader");
        if (qrReader) {
            qrReader.innerHTML =
                '<p style="color:#888; font-size:0.9rem; padding:24px; text-align:center; background:#0a1729; border-radius:8px;">' +
                '📷 カメラが使えない環境のため、下の URL 貼り付け欄を使ってペアリングしてください' +
                '</p>';
        }
        statusText.textContent = "URL を貼り付けてペアリングしてください";
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

        // 接続元の登録確認完了 → ペアリング先の経路 2 通りを並行起動:
        //   1. カメラで QR スキャン (スマホ向け)
        //   2. URL ペースト入力 (PC ブラウザ向け、両方とも有効)
        const resolvedNameA = nameA || snapA.val().DisplayName || "PC-A";
        setupPasteListener(sidA, resolvedNameA);
        await startQrScanner(sidA, resolvedNameA);

    } catch (err) {
        console.error("Bridge エラー:", err);
        showError(`接続エラー: ${err.code || ""} ${err.message}`);
    }
}

// ページ読み込み時に実行
document.addEventListener("DOMContentLoaded", main);
