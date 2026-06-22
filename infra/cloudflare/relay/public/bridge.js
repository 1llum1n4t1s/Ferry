/**
 * Ferry Bridge (CF 単独完結版) — 2 台の PC をペアリングするためのブラウザ橋渡しページ。
 *
 * docs/design/cf-only-migration.md §2.3 / §3.3 / §5 Step 4。
 * 旧 Firebase 版 (src/Ferry.Bridge/bridge.js) の CF 置換。relay Worker の Static Assets で配信され、
 * Firebase SDK / Custom Token / signInWithCustomToken を一切使わない。
 *
 * このページはスマホで QR をスキャンして開く前提（カメラのある端末からのみ到達する）。
 *   📷 カメラで QR スキャン — ボタンを押すと初めてカメラが起動する（自動起動しないことで
 *   「いきなりカメラ権限ダイアログが出る」不安を解消する）。
 *
 * 処理: ペアリング先 sid を読み取ったら performPairing() で **同一オリジンの**
 *   POST /pair/create に { sidA, nonceA, nameA, pkA, sidB, nonceB, nameB, pkB } を 1 回投げる。
 *   Worker が D1 pairing_nonces で両 sid の nonce 一致を server 検証 → 両 PC の DeviceDO inbox(WS)へ
 *   ペア成立を push する（Bridge には token を一切発行しない・1 リクエスト完結）。
 *   認可の源は「2 つの QR を物理スキャンして得た両 nonce の所有」。
 */

// 同一オリジン (relay Worker) の pairing エンドポイント。Static Assets で配信されるため相対 URL で
// 必ず同一オリジン = CORS preflight が発生しない。
const PAIR_CREATE_URL = "/pair/create";

// sid / nonce は C# 側 DeviceId / PairingNonce 由来で 32 桁の小文字 hex (Guid "N")。
// server も同じ正規表現で検証する (BAD_SID / BAD_NONCE)。早期に分かりやすいエラーを出すため client でも検査する。
const HEX32 = /^[a-f0-9]{32}$/;

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

let html5QrCode = null;
// ペアリング処理の重複実行を防ぐフラグ（カメラ連続読取の二重実行を防ぐ）
let pairingInProgress = false;
// Bridge 起動時に確定する PC-A の sid / name / pk / nonce (カメラ読取コールバックから参照)
let resolvedSidA = null;
let resolvedNameA = null;
let resolvedPkA = "";
let resolvedNonceA = "";

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
        // 接続元 PC の PairingNonce (D1 pairing_nonces と一致する 32 hex)。
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
            nonce: params.get("nonce") || "",
        };
    } catch {
        return { sid: null, name: null, pk: "", nonce: "" };
    }
}

/**
 * 同一オリジンの POST /pair/create を叩いてペアリングを成立させる。
 * Worker が両 sid の nonce を D1 で server 検証 → 両 PC の DeviceDO inbox へ push する。
 * カメラ経路の共通処理（CF 版では URL 貼り付け経路は廃止）。
 */
async function performPairing(sidA, nameA, sidB, nameB, pkA, pkB, nonceA, nonceB) {
    // 重複実行防止（同時にカメラ読取が二重発火した場合の保険）
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
        const resp = await fetch(PAIR_CREATE_URL, {
            method: "POST",
            headers: { "Content-Type": "application/json" },
            body: JSON.stringify({
                sidA, nonceA, nameA: nameA || "PC-A", pkA: pkA || "",
                sidB, nonceB, nameB: nameB || "PC-B", pkB: pkB || "",
            }),
        });

        if (!resp.ok) {
            pairingInProgress = false;
            showError(await friendlyPairError(resp));
            return;
        }

        showPaired(nameA || "PC-A", nameB || "PC-B");
    } catch (err) {
        pairingInProgress = false;
        showError(`ペアリングエラー: ${err.message}`);
    }
}

/**
 * /pair/create のエラーレスポンスを利用者向けの日本語メッセージに翻訳する。
 * server のエラーコード (pairing-routes.ts) に対応。
 */
async function friendlyPairError(resp) {
    let code = "";
    try {
        const body = await resp.json();
        code = body && body.error ? String(body.error) : "";
    } catch {
        /* body 無し/非 JSON */
    }
    switch (code) {
        case "SESSION_NOT_FOUND":
            return "ペアリング先の PC が見つかりません。PC でアプリを起動し、ペアリング画面（QR 表示中）であることを確認してください。";
        case "INVALID_NONCE_MATCH":
            return "QR コードが古い可能性があります。両方の PC でペアリングをやり直して、表示し直した QR をスキャンしてください。";
        case "EXPIRED_SESSION":
            return "ペアリングの有効期限（1 時間）が切れています。PC 側でペアリングをやり直してください。";
        case "SAME_SID":
            return "同じ PC の QR コードを 2 回スキャンしています。もう片方の PC の QR を読み取ってください。";
        case "BAD_SID":
        case "BAD_NONCE":
            return "QR コードの形式が正しくありません。Ferry の最新版で表示された QR をスキャンしてください。";
        default:
            return `ペアリングに失敗しました (HTTP ${resp.status} ${code})。`;
    }
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
                const { sid: sidB, name: nameB, pk: pkB, nonce: nonceB } = parseQrUrl(decodedText);

                if (!sidB) {
                    // Ferry の QR コードではない
                    return;
                }
                if (sidB === resolvedSidA) {
                    // 同じ PC の QR コードをスキャンした（読み続ける）
                    return;
                }
                if (!HEX32.test(sidB)) {
                    showError("スキャンした QR コードの形式が正しくありません（Ferry の最新版で表示された QR をスキャンしてください）。");
                    return;
                }
                if (!nonceB || !HEX32.test(nonceB)) {
                    showError("スキャンした QR コードに有効な nonce が含まれていません（相手 PC を最新版に更新してください）。");
                    return;
                }
                await performPairing(resolvedSidA, resolvedNameA, sidB, nameB, resolvedPkA, pkB, resolvedNonceA, nonceB);
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
function main() {
    const { sid: sidA, name: nameA, pk: pkA, nonce: nonceA } = getParams();

    if (!sidA) {
        showError("セッション ID が見つかりません。QR コードを再スキャンしてください。");
        return;
    }
    if (!HEX32.test(sidA)) {
        showError("QR コードの形式が正しくありません（Ferry の最新版で表示された QR をスキャンしてください）。");
        return;
    }
    if (!nonceA || !HEX32.test(nonceA)) {
        showError("ペアリング nonce が見つかりません（PC を最新版に更新してください）。");
        return;
    }

    // CF 版は Firebase 接続/認証が無いので、QR パラメータ検証後すぐ接続元情報を表示できる。
    resolvedSidA = sidA;
    resolvedNameA = nameA || "PC-A";
    resolvedPkA = pkA || "";
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
}

// ページ読み込み時に実行
document.addEventListener("DOMContentLoaded", main);
