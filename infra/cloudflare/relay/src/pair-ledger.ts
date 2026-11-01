import type { Env } from './index';
import { jsonError } from './http';

type PairLedgerMode = 'transition' | 'required';

/** 未設定だけを旧クライアント互換とし、明示値は厳密に検証する。 */
export function pairLedgerMode(env: Env): PairLedgerMode | null {
  const configured: unknown = env.PAIR_LEDGER_MODE;
  if (configured === undefined) return 'transition';
  if (configured === 'transition' || configured === 'required') return configured;
  return null;
}

export function d1Unavailable(): Response {
  return jsonError(503, 'D1_UNAVAILABLE', 'pair ledger unavailable');
}

export function ledgerMisconfigured(): Response {
  return jsonError(503, 'PAIR_LEDGER_MISCONFIGURED', 'PAIR_LEDGER_MODE must be transition or required');
}
