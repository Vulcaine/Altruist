import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { describe, it } from 'node:test';
import {
  clientFrame,
  decodeEnvelope,
  decodeMsgPack,
  decodeMsgPackAt,
  encodeEnvelope,
  encodeMsgPack,
  extractMessage,
  f32,
  MAX_EVENT_BYTES,
  MsgPackError,
  MsgPackExt,
  packetFrame,
  PacketDispatcher,
  parseClientFrame,
  peekMessageCode,
  serverPacket,
  skipMsgPackValue,
  SnapshotSchema,
  ticksToUnixMs,
  unixMsToTicks,
  type DropReason,
} from '../src/index.ts';

const hex = (b: Uint8Array) => Buffer.from(b).toString('hex');
const bytes = (h: string) => new Uint8Array(Buffer.from(h, 'hex'));

interface Vectors {
  format: string;
  values: { name: string; type: string; value: unknown; hex: string }[];
  envelopes: { name: string; code: number; header: { timestamp: string; receiver: string | null; sender: string }; packet: { messageCode: number; name: string; score: number }; hex: string }[];
  clientFrames: { name: string; event: string; packet: { messageCode: number; name: string; score: number }; hex: string }[];
}

/** Shared with Tests/Altruist/Client/WireVectorTests.cs (MessagePack-CSharp writes the same bytes). */
const vectors = JSON.parse(readFileSync(new URL('../../../../Tests/Resources/WireVectors/wire-vectors.json', import.meta.url), 'utf8')) as Vectors;

describe('golden wire vectors (shared with C#)', () => {
  it('has the expected format', () => assert.equal(vectors.format, 'altruist.wire-vectors/1'));

  for (const v of vectors.values) {
    it(`value ${v.name}: encodes to the C# bytes and decodes back`, () => {
      const input = v.type === 'float' ? f32(v.value as number) : v.value;
      assert.equal(hex(encodeMsgPack(input)), v.hex);
      assert.deepEqual(decodeMsgPack(bytes(v.hex)), v.value);
    });
  }

  for (const e of vectors.envelopes) {
    it(`envelope ${e.name}`, () => {
      const frame = bytes(e.hex);
      assert.equal(peekMessageCode(frame), e.code);
      assert.deepEqual(decodeMsgPack(extractMessage(frame)), [e.packet.messageCode, e.packet.name, e.packet.score]);
      const env = decodeEnvelope(frame, { int64: 'bigint' });
      assert.equal(env.code, e.code);
      assert.equal(String(env.header?.timestamp), e.header.timestamp);
      assert.equal(env.header?.receiver, e.header.receiver);
      assert.equal(env.header?.sender, e.header.sender);
      const again = encodeEnvelope(e.code, [e.packet.messageCode, e.packet.name, e.packet.score], {
        timestamp: BigInt(e.header.timestamp),
        receiver: e.header.receiver,
        sender: e.header.sender,
      });
      assert.equal(hex(again), e.hex);
    });
  }

  for (const f of vectors.clientFrames) {
    it(`client frame ${f.name}`, () => {
      assert.equal(hex(clientFrame(f.event, [f.packet.messageCode, f.packet.name, f.packet.score])), f.hex);
      assert.equal(hex(packetFrame(f.event, f.packet.messageCode, f.packet.name, f.packet.score)), f.hex);
      const parsed = parseClientFrame(bytes(f.hex));
      assert.equal(parsed?.event, f.event);
      assert.deepEqual(decodeMsgPack(parsed!.payload), [f.packet.messageCode, f.packet.name, f.packet.score]);
    });
  }
});

describe('MessagePack codec', () => {
  it('round-trips nested values', () => {
    const value = { a: [1, -1, 1.25, 'x', null, true, false], b: { c: new Uint8Array([1, 2, 3]) }, big: 2 ** 40, neg: -(2 ** 40) };
    assert.deepEqual(decodeMsgPack(encodeMsgPack(value)), value);
  });

  it('uses the smallest headers for long strings, arrays, maps and bin', () => {
    assert.equal(encodeMsgPack('x'.repeat(70000))[0], 0xdb);
    assert.equal(encodeMsgPack(new Array(70000).fill(0))[0], 0xdd);
    const map: Record<string, number> = {};
    for (let i = 0; i < 20; i++) map[`k${i}`] = i;
    assert.equal(encodeMsgPack(map)[0], 0xde);
    assert.deepEqual(decodeMsgPack(encodeMsgPack(map)), map);
    assert.equal(encodeMsgPack(new Uint8Array(300))[0], 0xc5);
    assert.equal(encodeMsgPack(new Uint8Array(70000))[0], 0xc6);
    assert.equal((decodeMsgPack(encodeMsgPack(new Uint8Array(70000))) as Uint8Array).length, 70000);
  });

  it('writes float32 for f32 and the float32 option, float64 otherwise', () => {
    assert.equal(hex(encodeMsgPack(0.5)), 'cb3fe0000000000000');
    assert.equal(hex(encodeMsgPack(0.5, { float32: true })), 'ca3f000000');
    assert.equal(hex(encodeMsgPack(f32(2))), 'ca40000000');
    assert.ok(Math.abs((decodeMsgPack(encodeMsgPack(f32(0.1))) as number) - 0.1) < 1e-7);
  });

  it('handles int64 modes and bigint input', () => {
    const big = 2n ** 60n + 1n;
    const enc = encodeMsgPack(big);
    assert.equal(enc[0], 0xcf);
    assert.equal(decodeMsgPack(enc, { int64: 'bigint' }), big);
    assert.equal(typeof decodeMsgPack(enc), 'number');
    assert.equal(decodeMsgPack(enc, { int64: 'auto' }), big);
    assert.equal(decodeMsgPack(encodeMsgPack(2 ** 40), { int64: 'auto' }), 2 ** 40);
    assert.equal(decodeMsgPack(encodeMsgPack(-(2n ** 62n)), { int64: 'bigint' }), -(2n ** 62n));
    assert.throws(() => encodeMsgPack(2n ** 64n), MsgPackError);
  });

  it('maps: objects, Map, ignoreUndefined, __proto__ is a plain key', () => {
    assert.deepEqual(decodeMsgPack(encodeMsgPack({ a: undefined }, { ignoreUndefined: true })), {});
    assert.deepEqual(decodeMsgPack(encodeMsgPack({ a: undefined })), { a: null });
    const m = decodeMsgPack(encodeMsgPack(new Map<unknown, unknown>([[1, 'one'], ['two', 2]])), { mapAsMap: true }) as Map<unknown, unknown>;
    assert.equal(m.get(1), 'one');
    assert.equal(m.get('two'), 2);
    const evil = decodeMsgPack(bytes('81a95f5f70726f746f5f5f01')) as Record<string, unknown>;
    assert.equal(Object.getPrototypeOf(evil), Object.prototype);
    assert.equal(evil['__proto__'], 1);
  });

  it('ext values round-trip', () => {
    const ext = decodeMsgPack(encodeMsgPack(new MsgPackExt(-1, new Uint8Array([1, 2, 3, 4])))) as MsgPackExt;
    assert.ok(ext instanceof MsgPackExt);
    assert.equal(ext.type, -1);
    assert.deepEqual([...ext.data], [1, 2, 3, 4]);
    assert.equal((decodeMsgPack(encodeMsgPack(new MsgPackExt(5, new Uint8Array(3)))) as MsgPackExt).data.length, 3);
  });

  it('rejects truncated, trailing, invalid and unsupported input', () => {
    assert.throws(() => decodeMsgPack(bytes('93cd10')), MsgPackError);
    assert.throws(() => decodeMsgPack(bytes('0101')), MsgPackError);
    assert.throws(() => decodeMsgPack(bytes('c1')), MsgPackError);
    assert.throws(() => decodeMsgPack(bytes('dd7fffffff')), MsgPackError); // absurd length, no allocation
    assert.throws(() => encodeMsgPack(() => 1), MsgPackError);
    const deep: unknown[] = [];
    let cur = deep;
    for (let i = 0; i < 200; i++) cur.push((cur = []));
    assert.throws(() => encodeMsgPack(deep), MsgPackError);
  });

  it('decodeMsgPackAt and skipMsgPackValue walk concatenated values', () => {
    const buf = new Uint8Array([...encodeMsgPack([1, 'a', { b: 2 }]), ...encodeMsgPack('next')]);
    const first = decodeMsgPackAt(buf, 0);
    assert.deepEqual(first.value, [1, 'a', { b: 2 }]);
    assert.equal(skipMsgPackValue(buf, 0), first.end);
    assert.equal(decodeMsgPackAt(buf, first.end).value, 'next');
    assert.equal(skipMsgPackValue(bytes('92'), 0), -1);
    assert.equal(skipMsgPackValue(encodeMsgPack(new MsgPackExt(1, new Uint8Array(20))), 0), 23);
  });
});

describe('envelopes', () => {
  it('peekMessageCode mirrors C#: array16/32 headers, uint widths, 0 for garbage', () => {
    assert.equal(peekMessageCode(bytes('dc0003cd1092c0c0')), 4242);
    assert.equal(peekMessageCode(bytes('dd00000003ce00012345')), 0x12345);
    assert.equal(peekMessageCode(bytes('9301c0c0')), 1);
    assert.equal(peekMessageCode(bytes('ffee')), 0);
    assert.equal(peekMessageCode(new Uint8Array(0)), 0);
    assert.equal(peekMessageCode(bytes('93ff')), 0); // negative codes are not message codes
    assert.equal(peekMessageCode(bytes('93cd10')), 0); // truncated
    assert.equal(peekMessageCode(new Uint8Array([0x93, 0xcc, 200]).buffer), 200);
  });

  it('extractMessage returns empty for non-envelope arrays and malformed frames, and reads array16/32 envelopes like C#', () => {
    assert.deepEqual([...extractMessage(bytes('dc000301c0cd1092'))], [0xcd, 0x10, 0x92]);
    assert.deepEqual([...extractMessage(bytes('dd0000000301c0c3'))], [0xc3]);
    assert.equal(extractMessage(bytes('dc000201c0')).length, 0);
    assert.equal(extractMessage(bytes('9301c7ff')).length, 0); // truncated ext8 header
    assert.equal(extractMessage(bytes('9100')).length, 0);
    assert.equal(extractMessage(bytes('9301')).length, 0);
    assert.equal(extractMessage(bytes('9301c0')).length, 0);
    assert.deepEqual(decodeMsgPack(extractMessage(encodeEnvelope(7, { any: 'shape' }))), { any: 'shape' });
  });

  it('decodeEnvelope rejects non-envelopes; default header is the server broadcast', () => {
    assert.throws(() => decodeEnvelope(encodeMsgPack({ a: 1 })), TypeError);
    const env = decodeEnvelope(encodeEnvelope(9, [9]));
    assert.deepEqual(env.header, { timestamp: 0, receiver: null, sender: 'server' });
  });

  it('converts .NET ticks', () => {
    assert.equal(ticksToUnixMs(unixMsToTicks(1_700_000_000_123)), 1_700_000_000_123);
    assert.equal(ticksToUnixMs(621355968000000000n), 0);
    assert.ok(Math.abs(ticksToUnixMs(Number(unixMsToTicks(1000))) - 1000) < 1);
  });

  it('client frames enforce the 1..127 byte event name', () => {
    assert.throws(() => clientFrame('', [1]), RangeError);
    assert.throws(() => clientFrame('x'.repeat(MAX_EVENT_BYTES + 1), [1]), RangeError);
    assert.equal(clientFrame('x'.repeat(MAX_EVENT_BYTES), [1])[0], 127);
    const raw = encodeMsgPack([5]);
    assert.deepEqual([...parseClientFrame(clientFrame('e', raw))!.payload], [...raw]);
    assert.equal(parseClientFrame(bytes('00aa01')), null);
    assert.equal(parseClientFrame(bytes('80aa01')), null);
    assert.equal(parseClientFrame(bytes('05aa01')), null);
  });
});

describe('PacketDispatcher', () => {
  const Welcome = serverPacket(2001, (m) => ({ player: Number(m[1]), room: String(m[2]) }));

  it('decodes once per decoder and calls every handler in order', () => {
    let decodes = 0;
    const Counted = serverPacket(2001, (m) => (decodes++, { player: Number(m[1]) }));
    const d = new PacketDispatcher();
    const seen: string[] = [];
    d.on(Counted, (p) => seen.push(`a${p.player}`));
    d.on(Counted, (p) => seen.push(`b${p.player}`));
    d.on(Welcome, (p, ctx) => seen.push(`w${p.room}${ctx.code}`));
    assert.equal(d.handlerCount(2001), 3);
    assert.ok(d.dispatch(encodeEnvelope(2001, [2001, 7, 'r1'])));
    assert.deepEqual(seen, ['a7', 'b7', 'wr12001']);
    assert.equal(decodes, 1);
  });

  it('reports drops and isolates handler errors', () => {
    const drops: [DropReason, number][] = [];
    const errors: number[] = [];
    const d = new PacketDispatcher({ onDrop: (r, c) => drops.push([r, c]), onError: (_e, c) => errors.push(c) });
    const bad = serverPacket(3, () => {
      throw new Error('nope');
    });
    d.on(bad, () => assert.fail('not called'));
    let ran = false;
    d.on(Welcome, () => {
      throw new Error('handler');
    });
    d.on(Welcome, () => (ran = true));
    assert.equal(d.dispatch(new Uint8Array(0)), false);
    assert.equal(d.dispatch(bytes('ffee')), false);
    assert.equal(d.dispatch(encodeEnvelope(99, [99])), false);
    assert.equal(d.dispatch(bytes('93cd07d1c0')), false); // code 2001, truncated
    assert.equal(d.dispatch(encodeEnvelope(3, [3])), false);
    assert.equal(d.dispatch(encodeEnvelope(2001, 'not an array')), false);
    assert.ok(d.dispatch(encodeEnvelope(2001, [2001, 1, 'x'])));
    assert.ok(ran);
    assert.deepEqual(errors, [2001]);
    assert.deepEqual(
      drops.map(([r]) => r),
      ['empty', 'unrecognised', 'unhandled', 'malformed', 'decode-failed', 'decode-failed'],
    );
  });

  it('raw handlers, onAny, once and unregister', () => {
    const d = new PacketDispatcher();
    const raw: unknown[] = [];
    const any: number[] = [];
    const off = d.on(5, (m) => raw.push(m));
    d.onAny((code) => any.push(code));
    let onceCount = 0;
    d.once(serverPacket(6, (m) => m[0]), () => onceCount++);
    d.dispatch(encodeEnvelope(5, { free: 'form' }));
    d.dispatch(encodeEnvelope(6, [6]));
    d.dispatch(encodeEnvelope(6, [6]));
    off();
    d.dispatch(encodeEnvelope(5, {}));
    assert.deepEqual(raw, [{ free: 'form' }]);
    assert.deepEqual(any, [5, 6, 6, 5]);
    assert.equal(onceCount, 1);
    assert.equal(d.handlerCount(5), 0);
  });

  it('accepts snapshot schema packet decoders directly', () => {
    const schema = new SnapshotSchema({
      format: 'altruist.snapshot-schema/1',
      rows: { p: { length: 2, fields: [{ name: 'id', index: 0, kind: 'int' }, { name: 'x', index: 1, kind: 'number' }] } },
      packets: { snap: { code: 2002, fields: [{ name: 'code', index: 0, kind: 'value' }, { name: 'players', index: 1, kind: 'rows', row: 'p' }] } },
    });
    const d = new PacketDispatcher();
    let got: unknown = null;
    d.on(2002, (p) => (got = p), schema.packet<{ players: { id: number; x: number }[] }>('snap'));
    d.dispatch(encodeEnvelope(2002, [2002, [[1, 2.5]]]));
    assert.deepEqual(got, { code: 2002, players: [{ id: 1, x: 2.5 }] });
  });

  it('serverPacket rejects invalid codes', () => {
    assert.throws(() => serverPacket(0, () => 1), RangeError);
    assert.throws(() => serverPacket(1.5, () => 1), RangeError);
  });
});
