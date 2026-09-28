export function freezePreview(element) {
  element.style.height = `${element.getBoundingClientRect().height}px`;
  element.style.overflowY = 'hidden';
}

export function resetPreview(element) {
  element.style.height = '';
  element.style.overflowY = '';
}

export async function animatePreview(element, expanded, rowLimit) {
  if (!element?.isConnected) return;
  const table = element.querySelector('table');
  const lastVisibleRow = table.tBodies[0].rows[rowLimit - 1];
  const start = element.getBoundingClientRect().height;
  const end = expanded
    ? table.getBoundingClientRect().height
    : lastVisibleRow.getBoundingClientRect().bottom - table.getBoundingClientRect().top;
  element.style.height = `${end}px`;
  element.style.overflowY = 'hidden';
  if (!matchMedia('(prefers-reduced-motion: reduce)').matches) {
    const animation = element.animate(
      [{ height: `${start}px` }, { height: `${end}px` }],
      { duration: 220, easing: 'cubic-bezier(0.4, 0, 0.2, 1)' });
    try { await animation.finished; } catch { /* The page may close during animation. */ }
  }
  // Keep the collapsed height until Blazor removes the clipped rows.
  if (expanded) resetPreview(element);
}


const textListeners = new Map();
export function attachText(id, receiver) {
  detachText(id);
  const element = document.getElementById(id);
  if (!element) return;
  const listener = () => receiver.invokeMethodAsync('TextChanged').catch(() => {});
  element.addEventListener('input', listener);
  textListeners.set(id, { element, listener });
}
export function detachText(id) {
  const attached = textListeners.get(id);
  if (attached) attached.element.removeEventListener('input', attached.listener);
  textListeners.delete(id);
}
export function readText(id) {
  return new Blob([document.getElementById(id).value], { type: 'text/plain;charset=utf-8' });
}
export function setText(id, text) {
  const element = document.getElementById(id);
  if (element) element.value = text;
}
