/**
 * Ferry Bridge — 2 台の PC をペアリングするためのブラウザ橋渡しページ。
 *
 * このページはスマホで QR をスキャンして開く前提（カメラのある端末からのみ到達する）。
 * カメラ無し PC はコードの直接受け渡しでペアリングするためここには来ない。
 *   📷 カメラで QR スキャン — ボタンを押すと初めてカメラが起動する（自動起動しないことで
 *   「いきなりカメラ権限ダイアログが出る」不安を解消する）。
 *
 * 処理: ペアリング先 sid を読み取ったら performPairing() で
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
const scanPanel = document.getElementById("scanPanel");
const qrReader = document.getElementById("qrReader");
const backFromScan = document.getElementById("backFromScan");
const pairedPanel = document.getElementById("pairedPanel");
const pairedNames = document.getElementById("pairedNames");
const errorPanel = document.getElementById("errorPanel");
const errorText = document.getElementById("errorText");

let db = null;
let html5QrCode = null;
// ペアリング処理の重複実行を防ぐフラグ（カメラ連続読取の二重実行を防ぐ）
let pairingInProgress = false;
// Bridge 起動時に確定する PC-A の sid / name / pk / nonce (カメラ読取コールバックから参照)
let resolvedSidA = null;
let resolvedNameA = null;
let resolvedPkA = "";
let resolvedNonceA = "";  // #D-001a Phase B: QR に埋め込まれた PC-A の PairingNonce

// Workers /pair/token エンドポイント (Bridge 用 short-lived Custom Token 発行)
const PAIR_TOKEN_URL = "https://relay.ferry.nephilim.jp/pair/token";

/**
 * URL パラメータを取得する。
 */
function getParams() {
    const params = new URLSearchParams(window.location.search);
    return {
        sid: params.get("sid"),
        name: params.get("name") ? decodeURIComponent(params.get("name")) : null,
        // rere #D-001(b): 長期公開鍵(base64url)。Bridge は中身を解釈せず文字列のまま中継する。
        pk: params.get("pk") || "",
        // #D-001a Phase B: Workers /pair/token に渡す PairingNonce (PC 側 sessions/{sid}/PairingNonce と一致する)
        nonce: params.get("nonce") || "",
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
            pk: params.get("pk") || "",
            nonce: params.get("nonce") || "",  // #D-001a Phase B
        };
    } catch {
        return { sid: null, name: null, pk: "", nonce: "" };
    }
}

/**
 * sidA と sidB を Firebase pairings/ に書き込んでペアリングを成立させる。
 * カメラ経路 / URL 貼り付け経路の共通処理。
 */
async function performPairing(sidA, nameA, sidB, nameB, pkA, pkB) {
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

    try {
        // sessions/{sidB} の存在を確認
        const snapB = await db.ref(`sessions/${sidB}`).once("value");
        if (!snapB.exists()) {
            pairingInProgress = false;
            showError("ペアリング先のセッションが見つかりません。PC でアプリが起動していることを確認してください。");
            return;
        }

        // #D-001a Phase B: pairings/{deviceId}/{pid} per-device path に atomic multi-path update で書く。
        // 1 回の update で両 deviceId 配下に同時書込（片側成功・片側失敗が構造的に起こらない）。
        const pairingId = `${Date.now()}_${Math.random().toString(36).slice(2, 8)}`;
        const data = {
            SidA: sidA,
            SidB: sidB,
            NameA: nameA || "PC-A",
            NameB: nameB || snapB.val().DisplayName || "PC-B",
            CreatedAt: Date.now(),
            // rere #D-001(b): 両 PC の公開鍵を中継。受信側が session 削除レースに依らず PairSecret を導出できる。
            PkA: pkA || "",
            PkB: pkB || (snapB.val().PublicKey || ""),
        };
        const updates = {};
        updates[`pairings/${sidA}/${pairingId}`] = data;
        updates[`pairings/${sidB}/${pairingId}`] = data;
        await db.ref().update(updates);

        showPaired(nameA || "PC-A", nameB || snapB.val().DisplayName || "PC-B");
        // Codex P1 補足: ペアリング完了したら即 signOut。NONE persistence と併せて auth セッションを完全破棄。
        firebase.auth().signOut().catch(() => { /* 失敗は無視 (タブを閉じれば消える) */ });
    } catch (err) {
        pairingInProgress = false;
        showError(`ペアリングエラー: ${err.message}`);
    }
}

/**
 * #D-001a Phase B: Workers /pair/token で short-lived Custom Token を取得し
 * Firebase Auth に signInWithCustomToken でログインする。
 * 失敗時は例外を投げる（呼出側で showError）。
 */
async function ensureAuth(sessionId, pairingNonce) {
    if (!pairingNonce) {
        throw new Error("QR に PairingNonce が含まれていません（古い PC バージョンの可能性）");
    }
    // Codex P1 指摘: signInWithCustomToken のデフォルトは LOCAL persistence (refresh token を localStorage に保存)。
    // ペアリング後もスマホ側に sidA として書ける長期 auth セッションが残るのは攻撃面（nonce/session 期限後でも
    // 再度 pairings/{sidA}/... に書ける）。NONE に倒してタブを閉じれば auth が消えるようにする。
    await firebase.auth().setPersistence(firebase.auth.Auth.Persistence.NONE);
    const resp = await fetch(PAIR_TOKEN_URL, {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ sessionId, pairingNonce }),
    });
    if (!resp.ok) {
        const body = await resp.text().catch(() => "");
        throw new Error(`Bridge 認証エラー: HTTP ${resp.status} ${body}`);
    }
    const { customToken } = await resp.json();
    if (!customToken) throw new Error("Bridge 認証エラー: customToken が返ってきませんでした");
    await firebase.auth().signInWithCustomToken(customToken);
}

/**
 * モード選択画面（カメラ起動の確認画面）に戻る (カメラ停止 + パネル非表示)。
 */
function showModeSelection() {
    stopCamera();
    statusPanel.classList.add("hidden");
    scanPanel.classList.add("hidden");
    modePanel.classList.remove("hidden");
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
                const { sid: sidB, name: nameB, pk: pkB } = parseQrUrl(decodedText);

                if (!sidB) {
                    // Ferry の QR コードではない
                    return;
                }

                if (sidB === resolvedSidA) {
                    // 同じ PC の QR コードをスキャンした
                    return;
                }

                await performPairing(resolvedSidA, resolvedNameA, sidB, nameB, resolvedPkA, pkB);
            },
            () => {
                // QR コード未検出（スキャン中）
            }
        );
    } catch (err) {
        // カメラ起動失敗 → モード選択画面に戻して案内メッセージ
        console.warn("カメラ起動失敗:", err);
        const friendlyMessage =
            err && err.name === "NotAllowedError"
                ? "カメラの使用が許可されませんでした。設定でカメラを許可するか、カメラ付きの端末でお試しください。"
                : "カメラが使えない環境のようです。カメラ付きの端末でお試しください。";
        scanPanel.classList.add("hidden");
        modePanel.classList.remove("hidden");
        statusPanel.classList.remove("hidden");
        statusText.textContent = friendlyMessage;
        spinner.classList.add("hidden");
    }
}

/**
 * メイン処理。
 */
async function main() {
    const { sid: sidA, name: nameA, pk: pkA, nonce: nonceA } = getParams();

    if (!sidA) {
        showError("セッション ID が見つかりません。QR コードを再スキャンしてください。");
        return;
    }
    if (!nonceA) {
        showError("ペアリング nonce が見つかりません（PC を v1.0.62 以上に更新してください）。");
        return;
    }

    statusText.textContent = "Firebase に接続中…";

    try {
        // Firebase SDK 初期化
        firebase.initializeApp(FIREBASE_CONFIG);
        db = firebase.database();

        // #D-001a Phase B: anonymous 撤去 → Workers /pair/token で short-lived Custom Token を取得して Auth ログイン。
        // 旧 signInAnonymously() は不採用（Ghost peer 強制注入を完全排除するため）。
        statusText.textContent = "認証中…";
        await ensureAuth(sidA, nonceA);

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
        resolvedPkA = pkA || "";
        // resolvedNonceA は現状追加の用途は無いが、将来 PC 側で nonce rotation を入れた時の再認証や
        // デバッグ表示用に保持しておく (CodeRabbit nitpick への対応: 削除せず意図を明記)。
        resolvedNonceA = nonceA;
        sessionAInfo.classList.remove("hidden");
        sessionAId.textContent = sidA;
        sessionAName.textContent = resolvedNameA;

        // モード選択画面を表示。カメラは選択後にのみ起動する
        statusPanel.classList.add("hidden");
        modePanel.classList.remove("hidden");

        // カメラ起動ボタン / スキャンから戻るボタンのハンドラ
        modeCameraBtn.addEventListener("click", startCameraMode);
        backFromScan.addEventListener("click", showModeSelection);
    } catch (err) {
        console.error("Bridge エラー:", err);
        showError(`接続エラー: ${err.code || ""} ${err.message}`);
    }
}

// ページ読み込み時に実行
document.addEventListener("DOMContentLoaded", main);
