/**
 * CF 単独完結移行 Step 2: derivePairId の単体テスト。
 *
 * C# ConnectionService.GeneratePairId (string.Compare Ordinal 昇順 + "_" 連結) と一致することを固定する。
 * 不一致だと A 側と B 側で別の pairId を導出し、signaling DO が別インスタンスに分裂して接続不能になる。
 */
import { describe, it, expect } from 'vitest';
import { derivePairId } from '../src/pairing-routes';

const A = 'a'.repeat(32);
const B = 'b'.repeat(32);
const PAIR_ID_RE = /^[a-f0-9]{32}_[a-f0-9]{32}$/;

describe('derivePairId', () => {
  it('Ordinal 昇順で連結する (引数順に依存しない)', () => {
    expect(derivePairId(A, B)).toBe(`${A}_${B}`);
    expect(derivePairId(B, A)).toBe(`${A}_${B}`); // 入替えても同じ
  });

  it('結果は pairId 正規表現にマッチする', () => {
    expect(derivePairId(A, B)).toMatch(PAIR_ID_RE);
    expect(derivePairId(B, A)).toMatch(PAIR_ID_RE);
  });

  it('hex 小文字は JS 文字列比較が .NET Ordinal と一致する (代表値)', () => {
    const x = '0'.repeat(32);
    const y = 'f'.repeat(32);
    // '0'(0x30) < 'f'(0x66) なので x が先
    expect(derivePairId(y, x)).toBe(`${x}_${y}`);
  });
});
