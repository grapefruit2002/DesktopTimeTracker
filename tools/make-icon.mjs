// Generates Assets/app.ico (multi-size, 32-bit BGRA) without any external dependency.
import { writeFileSync, mkdirSync } from 'node:fs';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const here = dirname(fileURLToPath(import.meta.url));
const out = resolve(here, '../src/DesktopTimeTracker/Assets/app.ico');
const SIZES = [16, 24, 32, 48, 64, 128, 256];
const SS = 3; // supersampling factor

const lerp = (a, b, t) => a + (b - a) * t;
const clamp01 = (v) => (v < 0 ? 0 : v > 1 ? 1 : v);

function insideRoundedRect(x, y, w, h, r) {
  const cx = Math.min(Math.max(x, r), w - r);
  const cy = Math.min(Math.max(y, r), h - r);
  const dx = x - cx, dy = y - cy;
  return dx * dx + dy * dy <= r * r;
}

// distance to a capsule (segment with round caps), used for clock hands
function capsuleDist(px, py, ax, ay, bx, by, r) {
  const vx = bx - ax, vy = by - ay;
  const wx = px - ax, wy = py - ay;
  const len2 = vx * vx + vy * vy || 1;
  const t = clamp01((wx * vx + wy * vy) / len2);
  const dx = px - (ax + vx * t), dy = py - (ay + vy * t);
  return Math.sqrt(dx * dx + dy * dy) - r;
}

function render(size) {
  const n = size * SS;
  const acc = new Float64Array(n * n * 4);

  const bgR = size * 0.225;
  const cx = size / 2, cy = size / 2;
  const ringR = size * 0.30;
  const ringW = size * 0.052;
  const handW = size * 0.048;
  // hour hand points to ~10 o'clock, minute hand to ~2 o'clock
  const hLen = ringR * 0.52, mLen = ringR * 0.78;
  const hAng = (-90 - 55) * Math.PI / 180;
  const mAng = (-90 + 62) * Math.PI / 180;
  const hx = cx + Math.cos(hAng) * hLen, hy = cy + Math.sin(hAng) * hLen;
  const mx = cx + Math.cos(mAng) * mLen, my = cy + Math.sin(mAng) * mLen;

  for (let y = 0; y < n; y++) {
    for (let x = 0; x < n; x++) {
      const sx = (x + 0.5) / SS, sy = (y + 0.5) / SS;
      const i = (y * n + x) * 4;

      if (!insideRoundedRect(sx, sy, size, size, bgR)) continue;

      const t = sy / size;
      let r = lerp(0x54, 0x25, t), g = lerp(0x92, 0x55, t), b = lerp(0xFF, 0xD4, t);

      // subtle top-left highlight
      const hl = clamp01(1 - Math.hypot(sx - size * 0.3, sy - size * 0.22) / (size * 0.85));
      r = lerp(r, 255, hl * 0.16); g = lerp(g, 255, hl * 0.16); b = lerp(b, 255, hl * 0.16);

      const d = Math.hypot(sx - cx, sy - cy);
      const ring = Math.abs(d - ringR) - ringW / 2;
      const hour = capsuleDist(sx, sy, cx, cy, hx, hy, handW / 2);
      const minute = capsuleDist(sx, sy, cx, cy, mx, my, handW / 2);
      const hub = d - size * 0.045;
      const ink = Math.min(ring, hour, minute, hub);

      if (ink <= 0) { r = 255; g = 255; b = 255; }

      acc[i] = r; acc[i + 1] = g; acc[i + 2] = b; acc[i + 3] = 255;
    }
  }

  // downsample
  const px = Buffer.alloc(size * size * 4);
  for (let y = 0; y < size; y++) {
    for (let x = 0; x < size; x++) {
      let r = 0, g = 0, b = 0, a = 0;
      for (let dy = 0; dy < SS; dy++) {
        for (let dx = 0; dx < SS; dx++) {
          const i = ((y * SS + dy) * n + (x * SS + dx)) * 4;
          r += acc[i]; g += acc[i + 1]; b += acc[i + 2]; a += acc[i + 3];
        }
      }
      const c = SS * SS, o = (y * size + x) * 4;
      const alpha = a / c;
      // acc stores premultiplied-by-coverage color; un-premultiply for straight alpha
      px[o] = alpha > 0 ? Math.round(clamp01(b / c / alpha) * 255) : 0;
      px[o + 1] = alpha > 0 ? Math.round(clamp01(g / c / alpha) * 255) : 0;
      px[o + 2] = alpha > 0 ? Math.round(clamp01(r / c / alpha) * 255) : 0;
      px[o + 3] = Math.round(alpha);
    }
  }
  return px; // RGBA, top-down
}

function bmpEntry(size, rgba) {
  const rowBytes = size * 4;
  const maskRow = Math.ceil(size / 32) * 4;
  const xor = Buffer.alloc(rowBytes * size);
  for (let y = 0; y < size; y++) {
    const srcY = size - 1 - y; // BMP is bottom-up
    for (let x = 0; x < size; x++) {
      const s = (srcY * size + x) * 4, d = y * rowBytes + x * 4;
      xor[d] = rgba[s + 2]; xor[d + 1] = rgba[s + 1]; xor[d + 2] = rgba[s]; xor[d + 3] = rgba[s + 3];
    }
  }
  const and = Buffer.alloc(maskRow * size); // all zero: alpha channel drives transparency
  const header = Buffer.alloc(40);
  header.writeUInt32LE(40, 0);
  header.writeInt32LE(size, 4);
  header.writeInt32LE(size * 2, 8); // XOR + AND height
  header.writeUInt16LE(1, 12);
  header.writeUInt16LE(32, 14);
  header.writeUInt32LE(0, 16);
  header.writeUInt32LE(xor.length + and.length, 20);
  return Buffer.concat([header, xor, and]);
}

const images = SIZES.map((s) => ({ size: s, data: bmpEntry(s, render(s)) }));
const header = Buffer.alloc(6);
header.writeUInt16LE(0, 0); header.writeUInt16LE(1, 2); header.writeUInt16LE(images.length, 4);

const entries = Buffer.alloc(16 * images.length);
let offset = 6 + entries.length;
images.forEach((img, i) => {
  const e = i * 16;
  entries[e] = img.size >= 256 ? 0 : img.size;
  entries[e + 1] = img.size >= 256 ? 0 : img.size;
  entries[e + 2] = 0; entries[e + 3] = 0;
  entries.writeUInt16LE(1, e + 4);
  entries.writeUInt16LE(32, e + 6);
  entries.writeUInt32LE(img.data.length, e + 8);
  entries.writeUInt32LE(offset, e + 12);
  offset += img.data.length;
});

mkdirSync(dirname(out), { recursive: true });
writeFileSync(out, Buffer.concat([header, entries, ...images.map((i) => i.data)]));
console.log(`wrote ${out} (${offset} bytes, sizes: ${SIZES.join(', ')})`);