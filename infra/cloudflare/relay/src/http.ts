/**
 * HTTP レスポンス / リクエストボディの共通ヘルパー。
 *
 * jsonOk / jsonError は auth・signaling・pairing・device の 4 ルートに同じ実装がコピーされていたので
 * ここへ集約する（エラー形状 `{ error, message }` はクライアント (`TryReadErrorCodeAsync`) が
 * コード分岐に使う契約なので変えない）。
 *
 * readJsonBody は「壊れた JSON / 空ボディ → 400 INVALID_JSON」を全ルートで揃えるためのもの。
 * 素の `await req.json()` は SyntaxError が Worker まで抜けて CF 既定の 500（error コード無し）になり、
 * クライアントからは入力起因かサーバー障害か区別できなくなる。
 */

export function jsonOk(body: object): Response {
  return new Response(JSON.stringify(body), {
    status: 200,
    headers: { 'content-type': 'application/json' },
  });
}

/**
 * rere レビュー #C-14: 認証・認可の拒否理由をサーバー側にも残す。
 *
 * 旧実装は BAD_SIGNATURE / DEVICE_PUBKEY_MISMATCH / CLOCK_SKEW / EXPIRED_SESSION などを
 * すべて無言で返しており、エラーコードはレスポンスボディにしか存在しなかった。その結果
 * 「一部ユーザーだけペアリングできない」という報告に対し、Workers Analytics では 401 の
 * スパイクしか見えず、クライアント署名バグなのか鍵束縛衝突なのか端末時刻ズレなのかを
 * サーバー側から区別できなかった（ユーザーの手元ログを回収するまで切り分けが始まらない）。
 *
 * ここを一箇所直せば auth / pairing / signaling / device の全ルートが同時に可観測になる。
 * 出力するのはステータスとエラーコードだけで、deviceId・pairId・表示名などの PII は含めない
 * （message は各呼び出し元で固定文字列を渡す契約なので、可変値が混入しない）。
 * 4xx のうち入力形式エラー (400) はノイズになるので、認可判断が絡む 401 以上に絞る。
 */
export function jsonError(status: number, code: string, message: string, extra?: object): Response {
  if (status >= 401) {
    console.error('reject', JSON.stringify({ status, code, message }));
  }
  const body = { error: code, message, ...(extra ?? {}) };
  return new Response(JSON.stringify(body), {
    status,
    headers: { 'content-type': 'application/json' },
  });
}

/** JSON リクエスト本文の上限。Content-Length が無い chunked body も同じ上限で読む。 */
export const MAX_JSON_BODY_BYTES = 64 * 1024;

/** Content-Length と実ストリームの両方を検査し、上限内の本文だけを文字列化する。 */
export async function readTextBody(
  req: Request,
  maxBytes = MAX_JSON_BODY_BYTES,
): Promise<{ text: string } | { error: Response }> {
  const contentLength = req.headers.get('content-length');
  if (contentLength !== null) {
    const declaredLength = Number(contentLength);
    if (Number.isFinite(declaredLength) && declaredLength > maxBytes) {
      return { error: jsonError(413, 'BODY_TOO_LARGE', 'request body exceeds the size limit') };
    }
  }

  const bytes = await readBodyBytes(req, maxBytes);
  if (bytes === null) {
    return { error: jsonError(413, 'BODY_TOO_LARGE', 'request body exceeds the size limit') };
  }
  return { text: new TextDecoder().decode(bytes) };
}

/**
 * JSON オブジェクトとしてボディを読む。壊れた JSON・非オブジェクトは 400 INVALID_JSON、
 * 上限超過は 413 BODY_TOO_LARGE を返す。
 *
 * req.json()/req.text() は本文を全量読み込んでから返るため、Content-Length を偽装した
 * chunked body を防げない。先に宣言長を確認し、ストリームも上限+1 byte を読んだ時点で
 * 打ち切ることで、ヘッダーと実本文の両方を制限する。
 */
export async function readJsonBody(req: Request): Promise<{ value: unknown } | { error: Response }> {
  try {
    const body = await readTextBody(req);
    if ('error' in body) return body;
    const v: unknown = JSON.parse(body.text);
    if (v === null || typeof v !== 'object' || Array.isArray(v)) {
      return { error: jsonError(400, 'INVALID_JSON', 'JSON body must be an object') };
    }
    return { value: v };
  } catch {
    return { error: jsonError(400, 'INVALID_JSON', 'JSON parse failed') };
  }
}

/** readJsonBody の薄いラッパ。成功時は `Record<string, unknown>` として扱う。 */
export async function readJsonObject(
  req: Request,
): Promise<{ value: Record<string, unknown> } | { error: Response }> {
  const r = await readJsonBody(req);
  if ('error' in r) return r;
  return { value: r.value as Record<string, unknown> };
}

/** 本文を上限内だけ保持して読む。null は本文が上限を超えたことを表す。 */
async function readBodyBytes(req: Request, maxBytes: number): Promise<Uint8Array | null> {
  if (!req.body) return new Uint8Array(0);

  const reader = req.body.getReader();
  const chunks: Uint8Array[] = [];
  let total = 0;
  try {
    for (;;) {
      const { done, value } = await reader.read();
      if (done) break;
      if (!value) continue;

      if (value.byteLength > maxBytes - total) {
        // 接続元がまだ送信中でも、残りの本文を保持せずに読み取りを終了する。
        try {
          await reader.cancel();
        } catch {
          // 切断済みのストリームでも本文超過という判定は変わらない。
        }
        return null;
      }
      chunks.push(value);
      total += value.byteLength;
    }
  } finally {
    reader.releaseLock();
  }

  const bytes = new Uint8Array(total);
  let offset = 0;
  for (const chunk of chunks) {
    bytes.set(chunk, offset);
    offset += chunk.byteLength;
  }
  return bytes;
}
