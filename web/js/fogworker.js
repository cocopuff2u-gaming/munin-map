// Turns the server's explored mask (data/fog.png) into the two veils layers/fog.js keeps, off the
// main thread: decoding a 2048x2048 PNG, walking its 4 million pixels and blurring it held the page
// up for a few hundred ms, at load and each time the mask changed while people explored.
// Replies { sharp, soft, explored } (RGBA bytes, transferred), or { error } when this browser can't
// draw in a worker (the page then does it itself).

self.onmessage = async (e) => {
  const { png, size, blur } = e.data;
  try {
    const img = await createImageBitmap(new Blob([png], { type: 'image/png' }));
    const src = new OffscreenCanvas(size, size), s = src.getContext('2d', { willReadFrequently: true });
    s.drawImage(img, 0, 0, size, size);
    img.close();
    const id = s.getImageData(0, 0, size, size), d = id.data;
    let explored = 0;
    for (let i = 0; i < d.length; i += 4) {
      const ex = d[i] > 127;
      if (ex) explored++;
      d[i] = 0; d[i + 1] = 0; d[i + 2] = 0; d[i + 3] = ex ? 0 : 255;
    }
    s.putImageData(id, 0, 0);
    const out = new OffscreenCanvas(size, size), o = out.getContext('2d', { willReadFrequently: true });
    o.filter = `blur(${blur}px)`;
    o.drawImage(src, 0, 0);
    const soft = o.getImageData(0, 0, size, size).data;
    self.postMessage({ sharp: d.buffer, soft: soft.buffer, explored }, [d.buffer, soft.buffer]);
  } catch (err) {
    self.postMessage({ error: String(err) });
  }
};
