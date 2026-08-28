import { describe, expect, it } from 'vitest';
import { MAX_JSON_BODY_BYTES, readJsonBody, readJsonObject } from '../src/http';

function objectBodyOfBytes(length: number): string {
  const prefix = '{"value":"';
  const suffix = '"}';
  return `${prefix}${'x'.repeat(length - prefix.length - suffix.length)}${suffix}`;
}

async function readError(result: Awaited<ReturnType<typeof readJsonBody>>): Promise<{ status: number; error: string }> {
  if (!('error' in result)) throw new Error('expected an error response');
  return { status: result.error.status, error: ((await result.error.json()) as { error: string }).error };
}

describe('readJsonBody の本文上限', () => {
  it('Content-Length が上限を 1 byte 超えたら本文を読まずに 413 BODY_TOO_LARGE', async () => {
    const req = new Request('https://relay.test', {
      method: 'POST',
      headers: {
        'content-type': 'application/json',
        'content-length': String(MAX_JSON_BODY_BYTES + 1),
      },
      body: '{}',
    });

    await expect(readError(await readJsonBody(req))).resolves.toEqual({
      status: 413,
      error: 'BODY_TOO_LARGE',
    });
  });

  it('Content-Length が無くても実本文が上限を 1 byte 超えたら 413 BODY_TOO_LARGE', async () => {
    const body = objectBodyOfBytes(MAX_JSON_BODY_BYTES + 1);
    expect(new TextEncoder().encode(body).byteLength).toBe(MAX_JSON_BODY_BYTES + 1);
    const req = new Request('https://relay.test', {
      method: 'POST',
      headers: { 'content-type': 'application/json' },
      body,
    });

    await expect(readError(await readJsonBody(req))).resolves.toEqual({
      status: 413,
      error: 'BODY_TOO_LARGE',
    });
  });

  it('本文がちょうど上限なら JSON オブジェクトとして読める', async () => {
    const body = objectBodyOfBytes(MAX_JSON_BODY_BYTES);
    const req = new Request('https://relay.test', {
      method: 'POST',
      headers: {
        'content-type': 'application/json',
        'content-length': String(MAX_JSON_BODY_BYTES),
      },
      body,
    });

    const result = await readJsonObject(req);
    expect('error' in result).toBe(false);
    if (!('error' in result)) expect(result.value.value).toHaveLength(MAX_JSON_BODY_BYTES - 12);
  });

  it('壊れた JSON は従来どおり 400 INVALID_JSON', async () => {
    const req = new Request('https://relay.test', {
      method: 'POST',
      headers: { 'content-type': 'application/json' },
      body: '{',
    });

    await expect(readError(await readJsonBody(req))).resolves.toEqual({
      status: 400,
      error: 'INVALID_JSON',
    });
  });
});
