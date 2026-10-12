// Fetches a region of building or ruin pieces (JSON, up to ~2 MB) and packs each chunk for the GPU
// off the main thread: parsing a big region on the page stalled a zoom-out for tens of ms. The
// page gets each chunk as bytes in the layout of layers/shapes.js (RECT_STRIDE, packRects).
const STRIDE = 28;

self.onmessage = async (e) => {
  const { id, url, colors, fallback } = e.data;   // colors[mat] = [r, g, b]
  try {
    const r = await fetch(url, { cache: 'default' });
    if (!r.ok) throw new Error(`${url}: ${r.status}`);
    const d = await r.json();
    const out = [];
    for (const c of d.chunks || []) {
      const pieces = c.pieces || [], n = pieces.length;
      const bytes = new ArrayBuffer(n * STRIDE), f = new Float32Array(bytes), u = new Uint8Array(bytes);
      for (let i = 0; i < n; i++) {
        const [x, z, , yaw, sx, sz, h, mat, , site] = pieces[i];
        const o = i * 7;
        f[o] = x; f[o + 1] = z; f[o + 2] = sx; f[o + 3] = sz; f[o + 4] = yaw; f[o + 5] = h;
        const [cr, cg, cb] = colors[mat] || fallback;
        u[o * 4 + 24] = cr; u[o * 4 + 25] = cg; u[o * 4 + 26] = cb; u[o * 4 + 27] = site ?? 255;   // a world structure's kind of place (shapes.js RUIN_SITES), 255 none
      }
      out.push({ cx: c.cx, cz: c.cz, rev: c.rev, bytes, count: n });
    }
    self.postMessage({ id, chunks: out }, out.map((c) => c.bytes));
  } catch (err) {
    self.postMessage({ id, error: String(err) });
  }
};
