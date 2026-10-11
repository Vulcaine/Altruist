/** Splits NDJSON text chunks into parsed values. Pure: feed chunks, get the complete lines' values and the rest. */
export function splitNdjson(buffer: string, chunk: string): { values: unknown[]; rest: string } {
  const lines = (buffer + chunk).split(/\r?\n/);
  const rest = lines.pop() ?? '';
  return { values: lines.map((l) => l.trim()).filter(Boolean).map((l) => JSON.parse(l) as unknown), rest };
}
