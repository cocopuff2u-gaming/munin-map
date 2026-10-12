// Server connection: JSON over websocket for live state, plain fetch for data.
//
// Frames from the server (see MapDataServer.Broadcast):
//   hello {version, worldRev, config}   players {data}   events {data, initial?}
//   tiles {keys[], status}   world {rev, stats}   ping {id,name,x,z}
//   pin {...}   rmpin {id}   reload
// The bus fans each frame out to listeners by its `t`.

const listeners = new Map();
let ws = null, backoff = 1000, closedByUs = false;
export const state = { connected: false, config: null, worldRev: 0 };

export function on(type, fn) {
  if (!listeners.has(type)) listeners.set(type, new Set());
  listeners.get(type).add(fn);
  return () => listeners.get(type).delete(fn);
}

export function emit(type, payload) {
  const set = listeners.get(type);
  if (set) for (const fn of set) { try { fn(payload); } catch (e) { console.error('listener', type, e); } }
}

export function connect() {
  const proto = location.protocol === 'https:' ? 'wss:' : 'ws:';
  const url = `${proto}//${location.host}${location.pathname.replace(/[^/]*$/, '')}ws`;
  try { ws = new WebSocket(url); } catch (e) { scheduleReconnect(); return; }
  // 'reconnected': open again after a gap (not the first time: the page has just fetched everything)
  ws.onopen = () => { state.connected = true; backoff = 1000; emit('connection', true); if (state.wasOpen) emit('reconnected'); state.wasOpen = true; };
  ws.onclose = () => { state.connected = false; emit('connection', false); if (!closedByUs) scheduleReconnect(); };
  ws.onerror = () => { try { ws.close(); } catch {} };
  ws.onmessage = (m) => {
    let f;
    try { f = JSON.parse(m.data); } catch { return; }
    if (!f || !f.t) return;
    if (f.t === 'hello') { state.config = f.config; state.worldRev = f.worldRev; }
    if (f.t === 'world') state.worldRev = f.rev;
    if (f.t === 'reload') { location.reload(); return; }
    emit(f.t, f);
  };
}

function scheduleReconnect() {
  setTimeout(connect, backoff);
  backoff = Math.min(backoff * 1.7, 15000);
}

// The startup data, requested by index.html's head before any script has loaded (window.__early):
// the first ask for one of those paths takes that response instead of fetching again.
export function fetchEarly(path, opts) {
  const e = window.__early && window.__early[path];
  if (e) { delete window.__early[path]; return e.then((r) => r || fetch(path, opts)); }
  return fetch(path, opts);
}

export async function getJSON(path, opts) {
  const r = await fetchEarly(path, Object.assign({ cache: 'no-cache' }, opts));
  if (!r.ok) throw new Error(`${path}: ${r.status}`);
  return r.json();
}

export async function getBuffer(path, opts) {
  const r = await fetchEarly(path, Object.assign({ cache: 'no-cache' }, opts));
  if (!r.ok) throw new Error(`${path}: ${r.status}`);
  return r.arrayBuffer();
}

export function loadImage(src) {
  return new Promise((resolve, reject) => {
    const img = new Image();
    img.crossOrigin = 'anonymous';
    img.onload = () => resolve(img);
    img.onerror = () => reject(new Error('image ' + src));
    img.src = src;
  });
}
