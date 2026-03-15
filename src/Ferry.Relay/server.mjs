// Ferry WebSocket リレーサーバー
// 同じ pairId を持つ2つのクライアント間でバイナリメッセージを中継する。
//
// 使い方:
//   node server.mjs                    # デフォルト: ポート 8765
//   PORT=9000 node server.mjs          # カスタムポート
//
// クライアント接続:
//   ws://host:port?pairId=xxx&role=offer   (Offer 側)
//   ws://host:port?pairId=xxx&role=answer  (Answer 側)
//
// プロトコル:
//   1. 両クライアントが接続後、"ready" テキストメッセージを送信
//   2. 以降、一方から受信したバイナリメッセージを他方にそのまま転送

import { WebSocketServer } from "ws";

const PORT = parseInt(process.env.PORT || "8765", 10);

// ルーム管理: pairId → { offer: WebSocket, answer: WebSocket }
const rooms = new Map();

// 古いルームの自動クリーンアップ間隔（ミリ秒）
const ROOM_TIMEOUT_MS = 5 * 60 * 1000; // 5分

const wss = new WebSocketServer({ port: PORT });

console.log(`Ferry リレーサーバー起動: ws://0.0.0.0:${PORT}`);

wss.on("connection", (ws, req) => {
  const url = new URL(req.url, `http://localhost:${PORT}`);
  const pairId = url.searchParams.get("pairId");
  const role = url.searchParams.get("role");

  if (!pairId || !role || (role !== "offer" && role !== "answer")) {
    console.log(`不正な接続パラメータ: pairId=${pairId}, role=${role}`);
    ws.close(4000, "不正なパラメータ: pairId と role (offer/answer) が必要");
    return;
  }

  console.log(`接続: pairId=${pairId}, role=${role}`);

  // ルームを取得または作成
  if (!rooms.has(pairId)) {
    rooms.set(pairId, { offer: null, answer: null, createdAt: Date.now() });
  }
  const room = rooms.get(pairId);

  // 同じ役割が既に接続している場合は古い方を閉じる
  if (room[role]) {
    console.log(`既存の ${role} を切断して置き換え: pairId=${pairId}`);
    try {
      room[role].close(4001, "新しい接続で置き換え");
    } catch {}
  }

  room[role] = ws;

  // 両方揃ったら "ready" を送信
  if (room.offer && room.answer) {
    console.log(`ルーム準備完了: pairId=${pairId}`);
    try {
      room.offer.send("ready");
      room.answer.send("ready");
    } catch (err) {
      console.log(`ready 送信エラー: ${err.message}`);
    }
  }

  // メッセージ中継
  ws.on("message", (data, isBinary) => {
    const peer = role === "offer" ? room.answer : room.offer;
    if (peer && peer.readyState === 1) {
      // WebSocket.OPEN === 1
      try {
        peer.send(data, { binary: isBinary });
      } catch (err) {
        console.log(`中継エラー: pairId=${pairId}, ${err.message}`);
      }
    }
  });

  // 切断処理
  ws.on("close", () => {
    console.log(`切断: pairId=${pairId}, role=${role}`);
    if (room[role] === ws) {
      room[role] = null;
    }

    // ルームが空になったら削除
    if (!room.offer && !room.answer) {
      rooms.delete(pairId);
      console.log(`ルーム削除: pairId=${pairId}`);
    }
  });

  ws.on("error", (err) => {
    console.log(`WebSocket エラー: pairId=${pairId}, role=${role}, ${err.message}`);
  });
});

// 古いルームの定期クリーンアップ
setInterval(() => {
  const now = Date.now();
  for (const [pairId, room] of rooms) {
    if (now - room.createdAt > ROOM_TIMEOUT_MS) {
      console.log(`タイムアウトルーム削除: pairId=${pairId}`);
      try {
        room.offer?.close(4002, "タイムアウト");
      } catch {}
      try {
        room.answer?.close(4002, "タイムアウト");
      } catch {}
      rooms.delete(pairId);
    }
  }
}, 60_000); // 1分ごとにチェック
