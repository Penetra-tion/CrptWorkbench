export function attach(zoneId, inputId) {
  const zone = document.getElementById(zoneId);
  const input = document.getElementById(inputId);
  if (!zone || !input) return;
  zone.addEventListener('dragover', event => { event.preventDefault(); zone.classList.add('dragging'); });
  zone.addEventListener('dragleave', () => zone.classList.remove('dragging'));
  zone.addEventListener('drop', event => {
    event.preventDefault();
    zone.classList.remove('dragging');
    if (input.disabled || event.dataTransfer.files.length !== 1) return;
    input.files = event.dataTransfer.files;
    input.dispatchEvent(new Event('change', { bubbles: true }));
  });
}

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
