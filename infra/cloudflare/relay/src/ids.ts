/**
 * ID 形式と pairId 導出の共通部品。
 *
 * 他モジュールを import しない（循環と vi.mock の影響を避けるため）。
 * 検査順とエラーメッセージは呼び出し側ごとに異なるため、形式検査の組み立ては各ルートに残す。
 */

/** 32 桁小文字 hex（deviceId / sid / nonce）。 */
export const HEX32_RE = /^[a-f0-9]{32}$/;

/** C# 側 `Util.PairId.Generate` と一致する pairId の形式。 */
export const PAIR_ID_RE = /^[a-f0-9]{32}_[a-f0-9]{32}$/;

/** C# ConnectionService.GeneratePairId と同じ規約: deviceId を Ordinal 昇順に並べて "_" 連結。
 *  hex 小文字なら JS 文字列比較 (UTF-16 code unit) は .NET Ordinal と一致する。 */
export function derivePairId(a: string, b: string): string {
  return a < b ? `${a}_${b}` : `${b}_${a}`;
}

/** 形式が正しく、かつ deviceId が昇順（canonical）の pairId か。 */
export function isCanonicalPairId(pairId: string): boolean {
  if (!PAIR_ID_RE.test(pairId)) return false;
  const [a, b] = pairId.split('_');
  return a < b;
}

/** 汎用 salted SHA-256 (pairId/deviceId 共通の DO 名ハッシュ)。 */
export async function hashPairId(pairId: string, salt: string): Promise<string> {
  const data = new TextEncoder().encode(pairId + '|' + salt);
  const buf = await crypto.subtle.digest('SHA-256', data);
  return Array.from(new Uint8Array(buf))
    .map((b) => b.toString(16).padStart(2, '0'))
    .join('');
}
