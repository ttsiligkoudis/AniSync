// Reports the app's effective light/dark theme to .NET so a native host (MAUI Android) can re-tint the OS
// status/navigation bars to match. The theme lives in CSS: chrome.js sets data-theme on <html> for a manual
// pick, otherwise prefers-color-scheme decides. We observe both and push the resolved value on every change.
// Harmless on the web head, where the platform service is a no-op.
let _observer = null;
let _mql = null;
let _notify = null;

function effectiveDark() {
    const t = document.documentElement.getAttribute('data-theme');
    if (t === 'dark') return true;
    if (t === 'light') return false;
    return !!(window.matchMedia && window.matchMedia('(prefers-color-scheme: dark)').matches);
}

export function register(dotnet) {
    if (!dotnet) return;
    _notify = () => dotnet.invokeMethodAsync('OnThemeChanged', effectiveDark());

    // Manual toggle flips <html data-theme>.
    _observer = new MutationObserver(_notify);
    _observer.observe(document.documentElement, { attributes: true, attributeFilter: ['data-theme'] });

    // System theme change — used when there's no manual override.
    if (window.matchMedia) {
        _mql = window.matchMedia('(prefers-color-scheme: dark)');
        _mql.addEventListener('change', _notify);
    }

    _notify(); // push the initial value
}

export function unregister() {
    if (_observer) { _observer.disconnect(); _observer = null; }
    if (_mql && _notify) { _mql.removeEventListener('change', _notify); _mql = null; }
    _notify = null;
}
