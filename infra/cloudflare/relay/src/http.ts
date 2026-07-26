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

/** JSON オブジェクトとしてボディを読む。壊れた JSON・非オブジェクトは 400 INVALID_JSON を返す。 */
export async function readJsonBody(req: Request): Promise<{ value: unknown } | { error: Response }> {
  try {
    const v = await req.json();
    if (v === null || typeof v !== 'object') {
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
