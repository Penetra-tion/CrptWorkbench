const attachments = new Map();
let nextToken = 0;

export function attach(zoneId, fileId, buttonId, receiver) {
  detach(zoneId);
  const zone = document.getElementById(zoneId);
  const input = document.getElementById(fileId);
  const button = document.getElementById(buttonId);
  if (!zone || !input || !button) return 0;
  const token = ++nextToken;
  let depth = 0;
  const enter = event => {
    if (!event.dataTransfer?.types.includes('Files')) return;
    event.preventDefault();
    if (input.disabled) return;
    depth++;
    zone.classList.add('dragging');
  };
  const over = event => {
    if (event.dataTransfer?.types.includes('Files')) event.preventDefault();
  };
  const leave = event => {
    event.preventDefault();
    if (--depth <= 0) { depth = 0; zone.classList.remove('dragging'); }
  };
  const drop = event => {
    event.preventDefault();
    depth = 0;
    zone.classList.remove('dragging');
    if (input.disabled) return;
    if (event.dataTransfer.files.length !== 1) {
      receiver.invokeMethodAsync('MultipleFiles').catch(() => {});
      return;
    }
    input.files = event.dataTransfer.files;
    input.dispatchEvent(new Event('change', { bubbles: true }));
  };
  const open = () => {
    if (input.disabled || button.disabled) return;
    input.value = '';
    input.click();
  };
  button.addEventListener('click', open);
  zone.addEventListener('dragenter', enter);
  zone.addEventListener('dragover', over);
  zone.addEventListener('dragleave', leave);
  zone.addEventListener('drop', drop);
  attachments.set(zoneId, { zone, button, enter, over, leave, drop, open, token });
  return token;
}

export function detach(zoneId, token) {
  const item = attachments.get(zoneId);
  if (!item || (token !== undefined && item.token !== token)) return;
  item.button.removeEventListener('click', item.open);
  for (const [name, handler] of [['dragenter', item.enter], ['dragover', item.over], ['dragleave', item.leave], ['drop', item.drop]]) {
    item.zone.removeEventListener(name, handler);
  }
  attachments.delete(zoneId);
}
