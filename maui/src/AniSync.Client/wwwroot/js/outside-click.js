// Outside-press dismissal for the header dropdowns (notification bell, search typeahead).
// A panel registers its root element while open; any pointer-press that lands outside that
// root invokes OnOutsideClick on the component so it can close itself. Capture-phase
// pointerdown so the panel closes before the press turns into a click/focus/navigation on
// whatever was underneath — the same "mousedown outside" behaviour the web dropdowns had.
//
// register() returns an integer handle the component holds for unregister(), mirroring
// discover-scroll.js — simpler than round-tripping the ElementReference back each time.
const handlers = new Map();
let nextId = 1;

export function register(root, dotnet) {
    if (!root || !dotnet) return 0;
    const onDown = (e) => {
        if (!root.contains(e.target)) {
            dotnet.invokeMethodAsync('OnOutsideClick');
        }
    };
    document.addEventListener('pointerdown', onDown, true);
    const id = nextId++;
    handlers.set(id, onDown);
    return id;
}

export function unregister(id) {
    const onDown = handlers.get(id);
    if (onDown) {
        document.removeEventListener('pointerdown', onDown, true);
        handlers.delete(id);
    }
}
