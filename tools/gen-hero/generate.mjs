/**
 * Generates docs/assets/hero.gif — stylized animation of a cursor crossing
 * from a Windows desktop monitor to a Mac laptop.
 *
 * Run: node generate.mjs
 * Deps: npm install   (first time)
 */

import { createCanvas, Path2D } from '@napi-rs/canvas';
import GIFEncoder from 'gif-encoder-2';
import { writeFileSync, mkdirSync } from 'fs';
import { fileURLToPath } from 'url';
import { join, dirname } from 'path';

const __dir = dirname(fileURLToPath(import.meta.url));
const OUT = join(__dir, '../../docs/assets/hero.gif');
mkdirSync(dirname(OUT), { recursive: true });

const W = 960, H = 480, FPS = 20;

// ── timing (frames) ───────────────────────────────────────────────────────────
const F_HOLD_WIN = 16, F_MOVE_WIN = 20;
const F_EXIT = 5, F_TRANSIT = 7, F_ENTER = 5;
const F_MOVE_MAC = 18, F_HOLD_MAC = 12;
const F_MOVE_MAC2 = 18, F_EXIT2 = 5, F_TRANSIT2 = 7, F_ENTER2 = 5, F_MOVE_WIN2 = 18;
const N = F_HOLD_WIN + F_MOVE_WIN + F_EXIT + F_TRANSIT + F_ENTER
        + F_MOVE_MAC + F_HOLD_MAC
        + F_MOVE_MAC2 + F_EXIT2 + F_TRANSIT2 + F_ENTER2 + F_MOVE_WIN2;

// ── layout ────────────────────────────────────────────────────────────────────
const DESK_Y = 408;
const WM_X = 30,  WM_Y = 46,  WM_W = 425, WM_H = 308;
const WS_X = WM_X + 16, WS_Y = WM_Y + 14, WS_W = WM_W - 32, WS_H = WM_H - 59;
const ML_X = 540, ML_Y = 124, ML_W = 390, ML_H = 262;
const MS_X = ML_X + 14, MS_Y = ML_Y + 14, MS_W = ML_W - 28, MS_H = ML_H - 26;

const CUR_Y      = 230;
const WIN_IDLE_X = WS_X + WS_W * 0.68;
const WIN_EDGE_X = WS_X + WS_W - 2;
const MAC_EDGE_X = MS_X + 2;
const MAC_IDLE_X = MS_X + MS_W * 0.37;

const lerp  = (a, b, t) => a + (b - a) * t;
const ease  = t => t * t * (3 - 2 * t);
const clamp = (v, lo, hi) => Math.max(lo, Math.min(hi, v));

// ── Apple logo — Path2D, viewBox 0 0 24 24 (simpleicons.org) ─────────────────
// Body path starts at M12.152 6.896; leaf at M14.693 4.026
const APPLE_PATH = new Path2D(
  'M12.152 6.896c-.948 0-2.415-1.078-3.96-1.04-2.04.027-3.912 1.183-4.961 3.014' +
  '-2.12 3.67-.543 9.098 1.519 12.079 1.013 1.46 2.208 3.09 3.792 3.029 1.52-.06' +
  ' 2.088-.979 3.926-.979 1.838 0 2.352.979 3.96.948 1.637-.031 2.671-1.483' +
  ' 3.676-2.948 1.156-1.688 1.636-3.325 1.667-3.415-.034-.012-3.193-1.226' +
  '-3.223-4.862-.03-3.046 2.487-4.51 2.6-4.582-1.42-2.091-3.622-2.324-4.391-2.368' +
  '-1.98-.153-3.902 1.24-4.605 1.124z' +
  'M14.693 4.026c.757-.9 1.267-2.149 1.117-3.406-1.07.05-2.362.74-3.148 1.64' +
  '-.686.804-1.304 2.067-1.123 3.29 1.189.09 2.414-.623 3.154-1.524z'
);

// Draws the Apple logo centred on (x, y) at the given height in pixels
function drawAppleLogo(ctx, x, y, height, color = '#C8C8CE') {
  // path body spans roughly y: 0.6–22.0 in the 24-unit viewBox
  const scale = height / 21.4;
  ctx.save();
  ctx.translate(x, y);
  ctx.scale(scale, scale);
  ctx.fillStyle = color;
  ctx.fill(APPLE_PATH);
  ctx.restore();
}

// ── helpers ───────────────────────────────────────────────────────────────────

function vgrad(ctx, x, y, w, h, c1, c2) {
  const g = ctx.createLinearGradient(x, y, x, y + h);
  g.addColorStop(0, `rgb(${c1})`);
  g.addColorStop(1, `rgb(${c2})`);
  ctx.fillStyle = g;
  ctx.fillRect(x, y, w, h);
}

function rrect(ctx, x, y, w, h, r) {
  ctx.beginPath();
  ctx.moveTo(x + r, y);
  ctx.lineTo(x + w - r, y);
  ctx.quadraticCurveTo(x + w, y,     x + w, y + r);
  ctx.lineTo(x + w, y + h - r);
  ctx.quadraticCurveTo(x + w, y + h, x + w - r, y + h);
  ctx.lineTo(x + r, y + h);
  ctx.quadraticCurveTo(x,     y + h, x, y + h - r);
  ctx.lineTo(x, y + r);
  ctx.quadraticCurveTo(x, y, x + r, y);
  ctx.closePath();
}

/**
 * VS Code-style editor window.
 * isWin → Windows chrome (tabs + ─□✕ buttons)
 * else  → macOS chrome (traffic lights)
 */
function drawAppWindow(ctx, wx, wy, ww, wh, isWin) {
  const tbh = isWin ? 32 : 30;

  // drop shadow
  ctx.fillStyle = 'rgba(0,0,0,0.38)';
  rrect(ctx, wx + 3, wy + 4, ww, wh, isWin ? 3 : 8);
  ctx.fill();

  // body
  ctx.fillStyle = isWin ? 'rgba(12,14,30,0.95)' : 'rgba(12,14,30,0.93)';
  rrect(ctx, wx, wy, ww, wh, isWin ? 3 : 8);
  ctx.fill();
  ctx.strokeStyle = 'rgba(48,52,88,0.48)';
  ctx.lineWidth = 1;
  rrect(ctx, wx, wy, ww, wh, isWin ? 3 : 8);
  ctx.stroke();

  // titlebar / tab bar
  ctx.fillStyle = 'rgba(18,20,44,0.98)';
  ctx.fillRect(wx + 1, wy + 1, ww - 2, tbh - 1);
  ctx.fillStyle = 'rgba(42,46,84,0.42)';
  ctx.fillRect(wx, wy + tbh, ww, 1);

  if (isWin) {
    // active tab
    ctx.fillStyle = 'rgba(12,14,30,0.95)';
    ctx.fillRect(wx + 6, wy + 6, 80, tbh - 6);
    ctx.fillStyle = 'rgba(135,148,210,0.58)';
    ctx.fillRect(wx + 12, wy + 14, 52, 6);
    // inactive tab
    ctx.fillStyle = 'rgba(105,115,165,0.36)';
    ctx.fillRect(wx + 94, wy + 14, 44, 6);
    // window buttons: ─  □  ✕
    ctx.fillStyle = 'rgba(115,120,162,0.55)';
    ctx.fillRect(wx + ww - 72, wy + 10, 18, 10);
    ctx.fillRect(wx + ww - 48, wy + 10, 18, 10);
    ctx.fillStyle = '#C0392B';
    ctx.fillRect(wx + ww - 24, wy + 10, 18, 10);
  } else {
    // traffic lights
    for (const [i, col] of ['#FF5F57', '#FEBC2E', '#28C840'].entries()) {
      ctx.fillStyle = col;
      ctx.beginPath();
      ctx.arc(wx + 14 + i * 20, wy + 15, 5.5, 0, Math.PI * 2);
      ctx.fill();
    }
    // centred title stub
    ctx.fillStyle = 'rgba(135,145,200,0.44)';
    ctx.fillRect(wx + ww / 2 - 34, wy + 11, 68, 8);
  }

  // activity bar (far left, VS Code style)
  const actW = 34;
  ctx.fillStyle = 'rgba(8,9,20,0.92)';
  ctx.fillRect(wx + 1, wy + tbh + 1, actW, wh - tbh - 2);
  for (let j = 0; j < 5; j++) {
    ctx.fillStyle = j === 0 ? 'rgba(210,218,255,0.72)' : 'rgba(95,105,155,0.38)';
    ctx.fillRect(wx + 10, wy + tbh + 10 + j * 22, 14, 14);
  }

  // sidebar (file tree)
  const sideW = 46;
  ctx.fillStyle = 'rgba(10,11,24,0.82)';
  ctx.fillRect(wx + actW + 1, wy + tbh + 1, sideW, wh - tbh - 2);
  // file tree: icon square + text stub, both anchored to left edge per indent
  const treeWidths = [20, 22, 16, 18, 14, 16, 12];
  for (let j = 0; j < 7; j++) {
    const rowY    = wy + tbh + 14 + j * 13;
    const iconX   = wx + actW + 4;
    const isActive = j === 2;
    const iconCol = isActive ? 'rgba(168,182,236,0.65)' : 'rgba(95,105,148,0.45)';
    const textCol = isActive ? 'rgba(168,182,236,0.58)' : 'rgba(95,105,148,0.35)';
    // icon (6×6)
    ctx.fillStyle = iconCol;
    ctx.fillRect(iconX, rowY, 6, 6);
    // text stub immediately right of icon
    ctx.fillStyle = textCol;
    ctx.fillRect(iconX + 8, rowY, treeWidths[j], 5);
  }

  // code area
  const cx = wx + actW + sideW + 2;
  const cw = ww - actW - sideW - 4;

  // gutter (line numbers)
  for (let j = 0; j < 9; j++) {
    ctx.fillStyle = 'rgba(68,72,108,0.55)';
    ctx.fillRect(cx + 2, wy + tbh + 5 + j * 13, 10, 5);
  }

  // syntax-colored code stubs
  const codeCols = [
    'rgba(128,160,240,0.60)', 'rgba(102,182,138,0.54)',
    'rgba(218,150,92,0.52)',  'rgba(128,160,240,0.48)',
    'rgba(102,182,138,0.44)', 'rgba(178,136,232,0.42)',
    'rgba(128,160,240,0.38)', 'rgba(218,150,92,0.36)',
    'rgba(102,182,138,0.32)',
  ];
  const lineW = [0.56, 0.34, 0.66, 0.28, 0.50, 0.44, 0.38, 0.52, 0.30];
  for (let j = 0; j < 9; j++) {
    ctx.fillStyle = codeCols[j];
    ctx.fillRect(cx + 16, wy + tbh + 5 + j * 13, (cw - 18) * lineW[j], 5);
  }
}

// ── scene drawers ─────────────────────────────────────────────────────────────

function drawBackground(ctx) {
  vgrad(ctx, 0, 0, W, H, '7,7,18', '4,5,12');
  ctx.fillStyle = '#0E0E14';
  ctx.fillRect(0, DESK_Y, W, H - DESK_Y);
  ctx.fillStyle = '#20202E';
  ctx.fillRect(0, DESK_Y, W, 1);
}

function drawWindowsMonitor(ctx) {
  const [bx, by, bw, bh] = [WM_X, WM_Y, WM_W, WM_H];
  const [sx, sy, sw, sh] = [WS_X, WS_Y, WS_W, WS_H];
  const cx = bx + bw / 2;

  // stand neck + base
  ctx.fillStyle = '#252528';
  ctx.fillRect(cx - 17, by + bh, 34, 37);
  ctx.fillStyle = '#1E1E22';
  rrect(ctx, cx - 52, by + bh + 35, 104, 14, 4);
  ctx.fill();

  // bezel
  ctx.fillStyle = '#181818';
  rrect(ctx, bx, by, bw, bh, 7);
  ctx.fill();

  // chin
  ctx.fillStyle = '#101012';
  ctx.fillRect(bx + 2, by + bh - 34, bw - 4, 32);
  // power LED
  ctx.fillStyle = '#1880EE';
  ctx.beginPath();
  ctx.arc(cx, by + bh - 12, 2.5, 0, Math.PI * 2);
  ctx.fill();

  // wallpaper — Windows 11 dark purple-blue
  vgrad(ctx, sx, sy, sw, sh, '14,20,78', '40,10,90');
  {
    const rg = ctx.createRadialGradient(sx + sw * 0.22, sy + sh * 0.2, 0,
                                        sx + sw * 0.22, sy + sh * 0.2, sw * 0.55);
    rg.addColorStop(0, 'rgba(52,72,172,0.26)');
    rg.addColorStop(1, 'rgba(0,0,0,0)');
    ctx.fillStyle = rg;
    ctx.fillRect(sx, sy, sw, sh);
  }

  // VS Code window
  drawAppWindow(ctx, sx + 10, sy + 8, sw - 22, 178, true);

  // ── taskbar ────────────────────────────────────────────────────────────────
  const tbh = 42;
  ctx.fillStyle = '#080910';
  ctx.fillRect(sx, sy + sh - tbh, sw, tbh);
  ctx.fillStyle = 'rgba(255,255,255,0.03)';
  ctx.fillRect(sx, sy + sh - tbh, sw, 1);

  // centered group: Windows 11 logo + 4 app icons
  const iconSz = 26, iconGap = 8;
  const logoW = 22, logoGap = 14;
  const nApps = 4;
  const groupW = logoW + logoGap + nApps * iconSz + (nApps - 1) * iconGap;
  const gx = Math.round(sx + (sw - groupW) / 2);
  const gy = Math.round(sy + sh - tbh + (tbh - iconSz) / 2);

  // Windows 11 logo: 4 colored panes (original Microsoft colors)
  const pSz = 9, pGap = 2;
  const paneColors = ['#D94515', '#7FBA00', '#00A4EF', '#FFB900'];
  for (let i = 0; i < 4; i++) {
    const row = Math.floor(i / 2), col = i % 2;
    ctx.fillStyle = paneColors[i];
    ctx.fillRect(gx + col * (pSz + pGap), gy + 2 + row * (pSz + pGap), pSz, pSz);
  }

  // pinned app icons
  const appCols = ['#0078D4', '#E84D3C', '#107C10', '#FFB900'];
  const firstAppX = gx + logoW + logoGap;
  for (let i = 0; i < nApps; i++) {
    ctx.fillStyle = appCols[i];
    rrect(ctx, firstAppX + i * (iconSz + iconGap), gy, iconSz, iconSz, 5);
    ctx.fill();
  }

  // active dot under first app
  ctx.fillStyle = 'rgba(100,175,255,0.88)';
  ctx.fillRect(firstAppX + iconSz / 2 - 3, sy + sh - 3, 6, 3);

  // clock
  ctx.font = '10px sans-serif';
  ctx.textAlign = 'right';
  ctx.textBaseline = 'middle';
  ctx.fillStyle = 'rgba(195,198,215,0.70)';
  ctx.fillText('12:34', sx + sw - 10, sy + sh - tbh / 2 - 5);
  ctx.fillText('5/28',  sx + sw - 10, sy + sh - tbh / 2 + 7);
  ctx.textAlign = 'left';
  ctx.textBaseline = 'alphabetic';
}

function drawMacLaptop(ctx) {
  const [lx, ly, lw, lh] = [ML_X, ML_Y, ML_W, ML_H];
  const [sx, sy, sw, sh] = [MS_X, MS_Y, MS_W, MS_H];

  // keyboard base
  ctx.fillStyle = '#BCBCC0';
  rrect(ctx, lx - 10, ly + lh, lw + 20, 22, 4);
  ctx.fill();
  ctx.fillStyle = 'rgba(126,126,134,0.75)';
  ctx.fillRect(lx - 10, ly + lh, lw + 20, 2);

  // lid
  ctx.fillStyle = '#B5B5B9';
  rrect(ctx, lx, ly, lw, lh, 10);
  ctx.fill();

  // screen bezel
  ctx.fillStyle = '#0B0B0F';
  rrect(ctx, sx - 5, sy - 5, sw + 10, sh + 10, 8);
  ctx.fill();

  // wallpaper — macOS dark teal
  vgrad(ctx, sx, sy, sw, sh, '10,26,56', '16,50,82');

  // VS Code window
  drawAppWindow(ctx, sx + 10, sy + 28, sw - 22, 162, false);

  // notch
  ctx.fillStyle = '#0B0B0F';
  ctx.fillRect(sx + sw / 2 - 32, sy, 64, 20);

  // menubar background
  ctx.fillStyle = '#0E0E13';
  ctx.fillRect(sx, sy, sw, 24);

  // Apple logo
  drawAppleLogo(ctx, sx + 6, sy + 5, 13, '#C6C6CC');

  // menu items as actual text
  ctx.font = '11px sans-serif';
  ctx.textBaseline = 'middle';
  ctx.fillStyle = 'rgba(215,216,222,0.88)';
  let mx = sx + 29;
  for (const label of ['File', 'Edit', 'View', 'Window', 'Help']) {
    ctx.fillText(label, mx, sy + 12);
    mx += ctx.measureText(label).width + 12;
  }

  // status area right: WiFi + clock
  ctx.textAlign = 'right';
  ctx.fillStyle = 'rgba(200,202,210,0.72)';
  ctx.fillText('12:34', sx + sw - 8, sy + 12);
  ctx.textAlign = 'left';
  ctx.textBaseline = 'alphabetic';

  // dock
  const nIcons = 6, iconSz = 28, iconGap = 5;
  const dkInner = nIcons * iconSz + (nIcons - 1) * iconGap;
  const dkPad = 8, dkVPad = 5;
  const dkW = dkInner + dkPad * 2;
  const dkH = iconSz + dkVPad * 2 + 2;
  const dkX = Math.round(sx + sw / 2 - dkW / 2);
  const dkY = sy + sh - dkH;

  ctx.fillStyle = 'rgba(18,20,34,0.90)';
  rrect(ctx, dkX, dkY, dkW, dkH, 10);
  ctx.fill();
  ctx.strokeStyle = 'rgba(46,50,68,0.80)';
  ctx.lineWidth = 1;
  rrect(ctx, dkX, dkY, dkW, dkH, 10);
  ctx.stroke();

  const dockCols = ['#007AD6', '#30C355', '#FF9500', '#5AC8FA', '#FF3B30', '#AA4EE0'];
  for (let i = 0; i < nIcons; i++) {
    ctx.fillStyle = dockCols[i];
    rrect(ctx, dkX + dkPad + i * (iconSz + iconGap), dkY + dkVPad, iconSz, iconSz, 6);
    ctx.fill();
  }
}

function drawLabels(ctx) {
  ctx.font = 'bold 13px sans-serif';
  ctx.textAlign = 'center';
  ctx.fillStyle = 'rgba(148, 152, 168, 0.65)';
  ctx.fillText('Windows', WM_X + WM_W / 2, DESK_Y + 42);
  ctx.fillText('macOS',   ML_X + ML_W / 2, DESK_Y + 42);
}

/**
 * Edge glow using crisp concentric solid rects — GIF-palette-friendly.
 * Wide gradient fills produce harsh banding in 256-color GIF; solid rects don't.
 * packetT: 0 = Win side, 1 = Mac side, -1 = no packet.
 */
function drawEdgeGlow(ctx, glowWin, glowMac, packetT = -1) {
  if (glowWin > 0) {
    const ex = WS_X + WS_W;
    ctx.fillStyle = `rgba(20,200,215,${(glowWin * 0.18).toFixed(2)})`;
    ctx.fillRect(ex - 6, WS_Y, 12, WS_H);
    ctx.fillStyle = `rgba(20,200,215,${(glowWin * 0.52).toFixed(2)})`;
    ctx.fillRect(ex - 2, WS_Y, 4, WS_H);
    ctx.fillStyle = `rgba(165,250,255,${(glowWin * 0.90).toFixed(2)})`;
    ctx.fillRect(ex - 1, WS_Y, 2, WS_H);
  }
  if (glowMac > 0) {
    const mx = MS_X;
    ctx.fillStyle = `rgba(20,200,215,${(glowMac * 0.18).toFixed(2)})`;
    ctx.fillRect(mx - 6, MS_Y, 12, MS_H);
    ctx.fillStyle = `rgba(20,200,215,${(glowMac * 0.52).toFixed(2)})`;
    ctx.fillRect(mx - 2, MS_Y, 4, MS_H);
    ctx.fillStyle = `rgba(165,250,255,${(glowMac * 0.90).toFixed(2)})`;
    ctx.fillRect(mx - 1, MS_Y, 2, MS_H);
  }

  const strength = Math.max(glowWin, glowMac);
  if (strength > 0) {
    const x1 = WS_X + WS_W + 4;
    const x2 = MS_X - 4;
    const lineY = CUR_Y + 9;

    ctx.fillStyle = `rgba(20,200,215,${(strength * 0.26).toFixed(2)})`;
    ctx.fillRect(x1, lineY - 2, x2 - x1, 5);
    ctx.fillStyle = `rgba(165,250,255,${(strength * 0.52).toFixed(2)})`;
    ctx.fillRect(x1, lineY, x2 - x1, 1);

    if (packetT >= 0) {
      const px = x1 + (x2 - x1) * packetT;
      const pr = 6;
      const pg = ctx.createRadialGradient(px, lineY, 0, px, lineY, pr * 2.5);
      pg.addColorStop(0,   'rgba(235,255,255,0.95)');
      pg.addColorStop(0.3, 'rgba(60,222,242,0.80)');
      pg.addColorStop(1,   'rgba(20,200,215,0)');
      ctx.fillStyle = pg;
      ctx.fillRect(px - pr * 2.5, lineY - pr * 2.5, pr * 5, pr * 5);
    }
  }
}

// ── cursor ────────────────────────────────────────────────────────────────────

function drawCursor(ctx, cx, cy, alpha = 1) {
  if (alpha <= 0) return;
  const s = 1.3;
  const raw   = [[0,0],[0,18],[5,14],[8.5,22],[11,21],[7.5,13],[13,13]];
  const inner = [[1,1],[1,16],[5.4,12.5],[8.6,19.8],[10.3,19.3],[7.2,11.8],[11.7,11.8]];
  const pts = raw.map(([x, y]) => [cx + x * s, cy + y * s]);

  ctx.globalAlpha = alpha;

  ctx.fillStyle = 'rgba(0,0,0,0.45)';
  ctx.beginPath();
  ctx.moveTo(pts[0][0] + 1.5, pts[0][1] + 1.5);
  for (const [x, y] of pts.slice(1)) ctx.lineTo(x + 1.5, y + 1.5);
  ctx.closePath();
  ctx.fill();

  ctx.fillStyle = '#000';
  ctx.beginPath();
  ctx.moveTo(pts[0][0], pts[0][1]);
  for (const [x, y] of pts.slice(1)) ctx.lineTo(x, y);
  ctx.closePath();
  ctx.fill();

  ctx.fillStyle = '#FFF';
  ctx.beginPath();
  ctx.moveTo(cx + inner[0][0] * s, cy + inner[0][1] * s);
  for (const [x, y] of inner.slice(1)) ctx.lineTo(cx + x * s, cy + y * s);
  ctx.closePath();
  ctx.fill();

  ctx.globalAlpha = 1;
}

// ── animation state ───────────────────────────────────────────────────────────

function getState(frame) {
  let f = frame;

  if (f < F_HOLD_WIN)
    return { x: WIN_IDLE_X, y: CUR_Y, glowWin: 0, glowMac: 0, cursorAlpha: 1, packetT: -1 };
  f -= F_HOLD_WIN;

  if (f < F_MOVE_WIN) {
    const t = ease(f / F_MOVE_WIN);
    return { x: lerp(WIN_IDLE_X, WIN_EDGE_X, t), y: CUR_Y, glowWin: 0, glowMac: 0, cursorAlpha: 1, packetT: -1 };
  }
  f -= F_MOVE_WIN;

  // Win edge glows, cursor fades to invisible
  if (f < F_EXIT) {
    const t = f / (F_EXIT - 1);
    return { x: WIN_EDGE_X, y: CUR_Y, glowWin: 1, glowMac: 0, cursorAlpha: clamp(1 - ease(t) * 1.6, 0, 1), packetT: -1 };
  }
  f -= F_EXIT;

  // both edges glow, packet crosses gap, cursor invisible
  if (f < F_TRANSIT) {
    const t = f / F_TRANSIT;
    return { x: lerp(WIN_EDGE_X, MAC_EDGE_X, ease(t)), y: CUR_Y, glowWin: ease(1 - t), glowMac: ease(t), cursorAlpha: 0, packetT: ease(t) };
  }
  f -= F_TRANSIT;

  // Mac edge glows, cursor fades in
  if (f < F_ENTER) {
    const t = f / (F_ENTER - 1);
    return { x: MAC_EDGE_X, y: CUR_Y, glowWin: 0, glowMac: 1, cursorAlpha: ease(t), packetT: -1 };
  }
  f -= F_ENTER;

  if (f < F_MOVE_MAC) {
    const t = ease(f / F_MOVE_MAC);
    return { x: lerp(MAC_EDGE_X, MAC_IDLE_X, t), y: CUR_Y, glowWin: 0, glowMac: Math.max(0, 1 - f / 5), cursorAlpha: 1, packetT: -1 };
  }
  f -= F_MOVE_MAC;

  if (f < F_HOLD_MAC)
    return { x: MAC_IDLE_X, y: CUR_Y, glowWin: 0, glowMac: 0, cursorAlpha: 1, packetT: -1 };
  f -= F_HOLD_MAC;

  // ── return trip (Mac → Windows) ───────────────────────────────────────────

  if (f < F_MOVE_MAC2) {
    const t = ease(f / F_MOVE_MAC2);
    return { x: lerp(MAC_IDLE_X, MAC_EDGE_X, t), y: CUR_Y, glowWin: 0, glowMac: 0, cursorAlpha: 1, packetT: -1 };
  }
  f -= F_MOVE_MAC2;

  if (f < F_EXIT2) {
    const t = f / (F_EXIT2 - 1);
    return { x: MAC_EDGE_X, y: CUR_Y, glowWin: 0, glowMac: 1, cursorAlpha: clamp(1 - ease(t) * 1.6, 0, 1), packetT: -1 };
  }
  f -= F_EXIT2;

  if (f < F_TRANSIT2) {
    const t = f / F_TRANSIT2;
    return { x: lerp(MAC_EDGE_X, WIN_EDGE_X, ease(t)), y: CUR_Y, glowWin: ease(t), glowMac: ease(1 - t), cursorAlpha: 0, packetT: 1 - ease(t) };
  }
  f -= F_TRANSIT2;

  if (f < F_ENTER2) {
    const t = f / (F_ENTER2 - 1);
    return { x: WIN_EDGE_X, y: CUR_Y, glowWin: 1, glowMac: 0, cursorAlpha: ease(t), packetT: -1 };
  }
  f -= F_ENTER2;

  // return to Windows idle — glow fades as cursor moves away
  const t = ease(f / F_MOVE_WIN2);
  return { x: lerp(WIN_EDGE_X, WIN_IDLE_X, t), y: CUR_Y, glowWin: Math.max(0, 1 - f / 5), glowMac: 0, cursorAlpha: 1, packetT: -1 };
}

// ── main ──────────────────────────────────────────────────────────────────────

const canvas = createCanvas(W, H);
const ctx = canvas.getContext('2d');

const encoder = new GIFEncoder(W, H, 'octree', true);
encoder.setDelay(Math.round(1000 / FPS));
encoder.setRepeat(0);
encoder.start();

process.stdout.write(`Generating ${N} frames (${(N / FPS).toFixed(1)}s @ ${FPS}fps)...\n`);

for (let i = 0; i < N; i++) {
  ctx.clearRect(0, 0, W, H);

  drawBackground(ctx);
  drawWindowsMonitor(ctx);
  drawMacLaptop(ctx);
  drawLabels(ctx);

  const s = getState(i);
  drawEdgeGlow(ctx, s.glowWin, s.glowMac, s.packetT);

  // motion trail during fast movement
  for (let lag = 2; lag >= 1; lag--) {
    if (i >= lag) {
      const prev = getState(i - lag);
      if (Math.abs(prev.x - s.x) > 3 && prev.cursorAlpha > 0 && s.cursorAlpha > 0)
        drawCursor(ctx, prev.x, prev.y, lag === 2 ? 0.12 : 0.22);
    }
  }

  drawCursor(ctx, s.x, s.y, s.cursorAlpha);

  encoder.addFrame(ctx);
  process.stdout.write(`\r  frame ${i + 1}/${N}`);
}

encoder.finish();
writeFileSync(OUT, encoder.out.getData());
process.stdout.write(`\nSaved → ${OUT}\n`);
