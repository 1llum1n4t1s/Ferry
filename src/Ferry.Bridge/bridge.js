/**
 * Ferry Bridge — 2 台の PC をペアリングするためのブラウザ橋渡しページ。
 *
 * 提供する 2 つのモード（ユーザーが明示的に選択する）:
 *   A) 📷 カメラで QR スキャン  — スマートフォン向け。ボタンを押すと初めてカメラが起動する
 *   B) 📋 URL を貼り付け        — カメラ無し PC ブラウザ向け
 *
 * 自動でカメラを起動しないため、「いきなりカメラ権限ダイアログが出る」不安を解消する。
 *
 * 共通処理: ペアリング先 sid を取得したら performPairing() で
 *   Firebase の pairings/{pairingId} に SidA / SidB / NameA / NameB を書き込み、
 *   両 PC のクライアントが pairings リスナーで成立を検知する。
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
const statusPanel = document.getElementById("statusPanel");
const statusText = document.getElementById("statusText");
const spinner = document.getElementById("spinner");
const sessionAInfo = document.getElementById("sessionAInfo");
const sessionAId = document.getElementById("sessionAId");
const sessionAName = document.getElementById("sessionAName");
const modePanel = document.getElementById("modePanel");
const modeCameraBtn = document.getElementById("modeCameraBtn");
const modePasteBtn = document.getElementById("modePasteBtn");
const scanPanel = document.getElementById("scanPanel");
const qrReader = document.getElementById("qrReader");
const backFromScan = document.getElementById("backFromScan");
const pastePanel = document.getElementById("pastePanel");
const pasteInput = document.getElementById("pasteInput");
const pasteStatus = document.getElementById("pasteStatus");
const backFromPaste = document.getElementById("backFromPaste");
const pairedPanel = document.getElementById("pairedPanel");
const pairedNames = document.getElementById("pairedNames");
const errorPanel = document.getElementById("errorPanel");
const errorText = document.getElementById("errorText");

let db = null;
let html5QrCode = null;
// ペアリング処理の重複実行を防ぐフラグ（カメラ/貼り付け両経路で参照）
let pairingInProgress = false;
// Bridge 起動時に確定する PC-A の sid / name (モード関数から参照)
let resolvedSidA = null;
let resolvedNameA = null;

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
    modePanel.classList.add("hidden");
    scanPanel.classList.add("hidden");
    pastePanel.classList.add("hidden");
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
    modePanel.classList.add("hidden");
    scanPanel.classList.add("hidden");
    pastePanel.classList.add("hidden");
    statusPanel.classList.add("hidden");
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
    statusPanel.classList.remove("hidden");
    statusText.textContent = "ペアリング中…";
    spinner.classList.remove("hidden");
    modePanel.classList.add("hidden");
    scanPanel.classList.add("hidden");
    pastePanel.classList.add("hidden");

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
 * モード選択画面に戻る (カメラ起動を停止 + パネル非表示)。
 */
function showModeSelection() {
    stopCamera();
    statusPanel.classList.add("hidden");
    scanPanel.classList.add("hidden");
    pastePanel.classList.add("hidden");
    modePanel.classList.remove("hidden");
    // 戻る際に貼り付け欄のステータスはクリア
    if (pasteInput) pasteInput.value = "";
    if (pasteStatus) {
        pasteStatus.textContent = "";
        pasteStatus.className = "paste-status";
    }
}

/**
 * モード A: カメラで QR スキャン。
 * ボタンクリック時に **初めて** カメラ起動を試みる (Bridge 起動時に自動起動はしない)。
 */
async function startCameraMode() {
    modePanel.classList.add("hidden");
    statusPanel.classList.add("hidden");
    scanPanel.classList.remove("hidden");

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

                if (sidB === resolvedSidA) {
                    // 同じ PC の QR コードをスキャンした
                    return;
                }

                await performPairing(resolvedSidA, resolvedNameA, sidB, nameB);
            },
            () => {
                // QR コード未検出（スキャン中）
            }
        );
    } catch (err) {
        // カメラ起動失敗 → モード選択画面に戻して URL ペーストモードへの誘導メッセージ
        console.warn("カメラ起動失敗:", err);
        const friendlyMessage =
            err && err.name === "NotAllowedError"
                ? "カメラの使用が許可されませんでした。「📋 URL を貼り付けてペアリング」モードをお試しください。"
                : "カメラが使えない環境のようです。「📋 URL を貼り付けてペアリング」モードをお試しください。";
        scanPanel.classList.add("hidden");
        modePanel.classList.remove("hidden");
        statusPanel.classList.remove("hidden");
        statusText.textContent = friendlyMessage;
        spinner.classList.add("hidden");
    }
}

/**
 * モード B: URL ペーストモード。
 */
function startPasteMode() {
    modePanel.classList.add("hidden");
    statusPanel.classList.add("hidden");
    pastePanel.classList.remove("hidden");
    if (pasteInput) pasteInput.focus();
}

/**
 * URL ペースト経路のリスナー設定。
 * 入力欄に Ferry のペアリングリンクが入った瞬間に自動でペアリング処理を起動する。
 */
function setupPasteListener() {
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

        if (sidB === resolvedSidA) {
            pasteStatus.textContent = "同じ PC の URL です。もう片方の PC のリンクを貼り付けてください";
            pasteStatus.className = "paste-status err";
            return;
        }

        pasteStatus.textContent = "✓ URL を認識、ペアリング処理中…";
        pasteStatus.className = "paste-status ok";
        await performPairing(resolvedSidA, resolvedNameA, sidB, nameB);
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
 * メイン処理。
 */
async function main() {
    const { sid: sidA, name: nameA } = getParams();

    if (!sidA) {
        showError("セッション ID が見つかりません。QR コードを再スキャンしてください。");
        return;
    }

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

        // 接続元の情報を表示 + グローバルに保持
        resolvedSidA = sidA;
        resolvedNameA = nameA || snapA.val().DisplayName || "PC-A";
        sessionAInfo.classList.remove("hidden");
        sessionAId.textContent = sidA;
        sessionAName.textContent = resolvedNameA;

        // モード選択画面を表示。カメラは選択後にのみ起動する
        statusPanel.classList.add("hidden");
        modePanel.classList.remove("hidden");

        // モード選択ボタンのハンドラ
        modeCameraBtn.addEventListener("click", startCameraMode);
        modePasteBtn.addEventListener("click", startPasteMode);
        backFromScan.addEventListener("click", showModeSelection);
        backFromPaste.addEventListener("click", showModeSelection);

        // URL ペースト経路のリスナーをセットアップ（モード B 選択時に有効化される）
        setupPasteListener();
    } catch (err) {
        console.error("Bridge エラー:", err);
        showError(`接続エラー: ${err.code || ""} ${err.message}`);
    }
}

// ページ読み込み時に実行
document.addEventListener("DOMContentLoaded", main);
