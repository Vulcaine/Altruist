import { useState } from 'preact/hooks';
import { Button } from './basics';
import { Modal } from './overlays';

/** Parses JSON text: the value, or the parser's message. */
export function parseJson(text: string): { ok: true; value: unknown } | { ok: false; error: string } {
  try {
    return { ok: true, value: JSON.parse(text) as unknown };
  } catch (error) {
    return { ok: false, error: (error as Error).message };
  }
}

/** Edits a JSON value in a dialog; Save is enabled only for valid JSON. */
export function JsonEditor({ title, value, onSave, onClose }: { title: string; value: unknown; onSave: (value: unknown) => Promise<void>; onClose: () => void }) {
  const [text, setText] = useState(() => JSON.stringify(value, null, 2));
  const [saving, setSaving] = useState(false);
  const parsed = parseJson(text);
  const save = async () => {
    if (!parsed.ok) return;
    setSaving(true);
    try {
      await onSave(parsed.value);
      onClose();
    } finally {
      setSaving(false);
    }
  };
  return (
    <Modal
      wide
      title={title}
      onClose={onClose}
      footer={
        <>
          {!parsed.ok && <span class="grow error-text">{parsed.error}</span>}
          <Button onClick={onClose}>Cancel</Button>
          <Button variant="primary" icon="save" disabled={!parsed.ok || saving} onClick={save}>
            Save
          </Button>
        </>
      }
    >
      <textarea class="json-edit" rows={22} value={text} spellcheck={false} onInput={(e) => setText(e.currentTarget.value)} />
    </Modal>
  );
}
