interface FenceEvidence {
  schemaVersion: number; worker: string; versionId: string; deploymentId: string;
  trafficPercent: number; snapshotSha256: string; noKvWriter: boolean; d1KeyAuthority: boolean;
  hmacRotated: boolean; observedAt: string;
  probe: { method: string; path: string; host: string; oldHmacStatus: number;
    oldHmacCode: string; newHmacStatus: number; newHmacCode?: string };
}

/** Operator の承認証跡を検査する。Cloudflare の稼働状態を CLI 自身が観測するものではない。 */
export function validateMigrationAuthority(options: {
  writersFrozen?: boolean; legacyWritersFenced?: boolean; fenceVersion?: string;
  fenceEvidence?: unknown; provenance?: string;
}, snapshotSha256: string): Record<string, unknown> {
  if (Boolean(options.writersFrozen) === Boolean(options.legacyWritersFenced)) {
    throw new Error('exactly one of writers-frozen / legacy-writers-fenced is required');
  }
  if (!options.provenance?.trim()) throw new Error('snapshot-provenance is required');
  if (options.writersFrozen) {
    if (options.fenceVersion || options.fenceEvidence !== undefined) throw new Error('physical freeze forbids fence evidence');
    return { mode: 'physical-frozen', provenance: options.provenance };
  }
  const uuid = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;
  const evidence = options.fenceEvidence as FenceEvidence | undefined;
  if (!options.fenceVersion || !uuid.test(options.fenceVersion) || !evidence
      || evidence.schemaVersion !== 1 || evidence.worker !== 'ferry-relay'
      || evidence.versionId !== options.fenceVersion || !uuid.test(evidence.deploymentId ?? '')
      || evidence.trafficPercent !== 100 || evidence.snapshotSha256 !== snapshotSha256.toLowerCase()
      || evidence.noKvWriter !== true || evidence.d1KeyAuthority !== true || evidence.hmacRotated !== true
      || !Number.isFinite(Date.parse(evidence.observedAt ?? ''))
      || evidence.probe?.method !== 'GET' || !/^\/pairs\/[0-9a-f]{32}_[0-9a-f]{32}$/.test(evidence.probe?.path ?? '')
      || evidence.probe?.oldHmacStatus !== 401 || evidence.probe?.oldHmacCode !== 'BAD_TOKEN'
      || !(evidence.probe?.newHmacStatus === 200
        || (evidence.probe?.newHmacStatus === 404 && evidence.probe?.newHmacCode === 'NOT_FOUND'))
      || !['watashiba.kagayoi.com', 'relay.ferry.nephilim.jp'].includes(evidence.probe?.host)) {
    throw new Error('complete logical fence version/deployment/100%/authority/HMAC probe evidence required');
  }
  const [a, b] = evidence.probe.path.slice('/pairs/'.length).split('_');
  if (a >= b) throw new Error('probe pair ID must use canonical order');
  // token / secret 等を誤って証跡へ混ぜても receipt へ転記しない。
  const allowed = ['schemaVersion', 'worker', 'versionId', 'deploymentId', 'trafficPercent',
    'snapshotSha256', 'noKvWriter', 'd1KeyAuthority', 'hmacRotated', 'observedAt', 'probe'];
  const allowedProbe = ['method', 'path', 'host', 'oldHmacStatus', 'oldHmacCode', 'newHmacStatus', 'newHmacCode'];
  if (Object.keys(evidence).some((key) => !allowed.includes(key))
      || Object.keys(evidence.probe).some((key) => !allowedProbe.includes(key))) {
    throw new Error('unknown evidence field; never include tokens or secrets');
  }
  return { mode: 'logical-fenced', provenance: options.provenance, evidence };
}
