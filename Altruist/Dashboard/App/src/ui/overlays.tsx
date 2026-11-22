import type { ComponentChildren } from 'preact';
import { createContext } from 'preact';
import { useCallback, useContext, useEffect, useState } from 'preact/hooks';
import { Button, Icon } from './basics';

export function Modal({ title, onClose, children, footer, wide }: { title: ComponentChildren; onClose: () => void; children: ComponentChildren; footer?: ComponentChildren; wide?: boolean }) {
  useEffect(() => {
    const onKey = (e: KeyboardEvent) => e.key === 'Escape' && onClose();
    window.addEventListener('keydown', onKey);
    return () => window.removeEventListener('keydown', onKey);
  }, [onClose]);
  return (
    <div class="modal-backdrop" onClick={(e) => e.target === e.currentTarget && onClose()}>
      <div class={`modal${wide ? ' modal-wide' : ''}`} role="dialog" aria-modal="true">
        <header class="modal-head">
          <h2>{title}</h2>
          <Button small icon="close" aria-label="Close" onClick={onClose} />
        </header>
        <div class="modal-body">{children}</div>
        {footer && <footer class="modal-foot">{footer}</footer>}
      </div>
    </div>
  );
}

interface Toast {
  readonly id: number;
  readonly tone: 'ok' | 'danger';
  readonly text: string;
}

type Notify = (tone: Toast['tone'], text: string) => void;
const ToastContext = createContext<Notify>(() => undefined);

/** `notify('ok' | 'danger', text)`: a short message in the corner. */
export function useNotify(): Notify {
  return useContext(ToastContext);
}

export function ToastHost({ children }: { children: ComponentChildren }) {
  const [toasts, setToasts] = useState<readonly Toast[]>([]);
  const notify = useCallback<Notify>((tone, text) => {
    const id = Date.now() + Math.random();
    setToasts((t) => [...t, { id, tone, text }]);
    setTimeout(() => setToasts((t) => t.filter((x) => x.id !== id)), tone === 'danger' ? 8000 : 3500);
  }, []);
  return (
    <ToastContext.Provider value={notify}>
      {children}
      <div class="toasts" aria-live="polite">
        {toasts.map((t) => (
          <div key={t.id} class={`toast toast-${t.tone}`}>
            <Icon name={t.tone === 'ok' ? 'pass' : 'error'} />
            {t.text}
          </div>
        ))}
      </div>
    </ToastContext.Provider>
  );
}

interface Ask {
  readonly title: string;
  readonly body: ComponentChildren;
  readonly action: string;
}

/** Asks before a destructive action: `confirm({...})` resolves true when the user agrees. Render `dialog` somewhere. */
export function useConfirm(): { dialog: ComponentChildren; confirm: (ask: Ask) => Promise<boolean> } {
  const [ask, setAsk] = useState<(Ask & { resolve: (ok: boolean) => void }) | null>(null);
  const confirm = useCallback((q: Ask) => new Promise<boolean>((resolve) => setAsk({ ...q, resolve })), []);
  const done = (ok: boolean) => {
    ask?.resolve(ok);
    setAsk(null);
  };
  const dialog = ask && (
    <Modal
      title={ask.title}
      onClose={() => done(false)}
      footer={
        <>
          <Button onClick={() => done(false)}>Cancel</Button>
          <Button variant="danger" onClick={() => done(true)}>
            {ask.action}
          </Button>
        </>
      }
    >
      {ask.body}
    </Modal>
  );
  return { dialog, confirm };
}
