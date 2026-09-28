const attached = new WeakMap();

export function attach(root) {
  if (!root || attached.has(root)) return;
  const state = { frame: 0 };
  const onToggle = (event) => {
    const details = event.target;
    if (!(details instanceof HTMLDetailsElement) || !root.contains(details) ||
        !details.matches('.code-details, .code-detail-disclosure')) return;
    cancelAnimationFrame(state.frame);
    if (!details.open) return;

    const reducedMotion = window.matchMedia('(prefers-reduced-motion: reduce)').matches;
    const start = window.scrollY;
    let startedAt;
    const followBottom = (now) => {
      startedAt ??= now;
      const progress = reducedMotion ? 1 : Math.min((now - startedAt) / 380, 1);
      const eased = 1 - Math.pow(1 - progress, 3);
      const bottom = Math.max(0, document.documentElement.scrollHeight - window.innerHeight);
      window.scrollTo({ top: start + (bottom - start) * eased, behavior: 'instant' });
      if (progress < 1 && details.open && details.isConnected)
        state.frame = requestAnimationFrame(followBottom);
    };
    state.frame = requestAnimationFrame(followBottom);
  };
  root.addEventListener('toggle', onToggle, true);
  attached.set(root, { onToggle, state });
}

export function detach(root) {
  const entry = attached.get(root);
  if (entry) {
    cancelAnimationFrame(entry.state.frame);
    root.removeEventListener('toggle', entry.onToggle, true);
  }
  attached.delete(root);
}
