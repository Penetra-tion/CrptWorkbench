export { attachText, detachText, readText, setText } from './package-drop.js';

export function downloadText(name, text) {
    const url = URL.createObjectURL(new Blob([text], { type: 'text/tab-separated-values;charset=utf-8' }));
    const link = document.createElement('a');
    link.href = url;
    link.download = name;
    link.click();
    setTimeout(() => URL.revokeObjectURL(url), 1000);
}
