// Options-page glue. Reads/writes apiBase + uid into chrome.storage.local;
// background.js picks them up at message-dispatch time.

const $apiBase = document.getElementById('apiBase');
const $uid = document.getElementById('uid');
const $save = document.getElementById('save');
const $status = document.getElementById('status');

(async () => {
  const stored = await chrome.storage.local.get(['apiBase', 'uid']);
  $apiBase.value = stored.apiBase || 'https://anisync.fly.dev';
  $uid.value = stored.uid || '';
})();

$save.addEventListener('click', async () => {
  const apiBase = $apiBase.value.trim().replace(/\/+$/, '');
  const uid = $uid.value.trim();
  if (!uid) {
    $status.textContent = 'UID is required.';
    $status.className = 'err';
    return;
  }
  await chrome.storage.local.set({ apiBase, uid });
  $status.textContent = 'Saved.';
  $status.className = 'ok';
});
