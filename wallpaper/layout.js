/* Coral Coast - widget boxes.

   Grid widgets live in standard boxes measured in grid cells (1 cell = 184 x 72 px, 12 px gaps), so a
   2x2 box is the same size whatever it shows. "Free" widgets (Now playing, photo frames) aren't tied to
   the grid: they move and resize freely, photo frames also rotate, and the one you grab comes to the front
   (for collages).

   Each box shows a module (System, Network, Weather...), listed in MODULES below. Adding a widget later
   means adding a module: its HTML block plus one line here.

   More of the same widget:
     photo frames - as many as you like, each with its own picture and caption
     display widgets (clock, weather, system...) - live copies that mirror the original
     the to-do list and Now playing stay single (typing and the live visualizer can't be duplicated)

   Edit mode (pencil, top-right of each screen):
     drag a box to move it; grid boxes snap to half cells and never overlap
     drag the corner handle to resize; drag the round knob on a photo to rotate it
     the menu button switches what a box shows, picks a photo, edits a caption or removes the box
     "Add widget" brings back a removed widget or adds another one
   If dragging doesn't work in Lively, click a box once to pick it up and click again to drop it.

   The layout is saved with the to-do state through Coral Launcher (localStorage is a backup). */

const CoralLayout = (() => {
  const STAGE_W = 5760, STAGE_H = 1080, SCREEN_W = 1920;
  const CELL_W = 184, CELL_H = 72, GAP = 12, PITCH_X = CELL_W + GAP, PITCH_Y = CELL_H + GAP;
  const STEP_X = PITCH_X / 2, STEP_Y = PITCH_Y / 2;     // grid positions snap to half cells
  const MARGIN = 32, BOTTOM = 56;                       // keep clear of the screen edge and the taskbar
  const MAX_CW = 9, MAX_CH = 11;
  const stage = document.getElementById('stage');

  // id, label, selector, index, fit, chrome, default size [cols, rows], default place [screen, col, row], options
  //   fit 'scale': the whole module scales to fit the box (gauges, clock, photos...)
  //   fit 'flow':  the module keeps its width and uses the box's height (lists: to-do, calendar, drives)
  //   chrome: the box draws the standard card background; false for free-floating pieces (clock, photos)
  //   options.free:   not on the grid; moves and resizes freely (keeping its shape) and may overlap things
  //   options.at:     default top-left for a free module (left out = where the design puts it)
  //   options.rotate: can be rotated (photo frames)
  //   options.copies: 'photo' = independent new frames, 'mirror' = live copies, left out = single
  const PHOTO = { free: true, rotate: true, copies: 'photo' };
  const MODULES = [
    ['system',   'System',       '#sys > .card', 0, 'scale', true,  [2, 1], [0, 0, 0],    { copies: 'mirror' }],
    ['rings',    'CPU and GPU',  '#sys > .card', 1, 'scale', true,  [2, 2], [0, 0, 1],    { copies: 'mirror' }],
    ['memory',   'Memory',       '#sys > .card', 2, 'scale', true,  [2, 1], [0, 0, 3],    { copies: 'mirror' }],
    ['load',     'Load graph',   '#sys > .card', 3, 'scale', true,  [2, 2], [0, 0, 4],    { copies: 'mirror' }],
    ['network',  'Network',      '#sys > .card', 4, 'scale', true,  [2, 2], [0, 0, 6],    { copies: 'mirror' }],
    ['temps',    'Temperatures', '#temps',       0, 'scale', true,  [2, 2], [0, 2, 0],    { copies: 'mirror' }],
    ['drives',   'Drive temps',  '#drives',      0, 'flow',  true,  [2, 2], [0, 2, 2],    { copies: 'mirror' }],
    ['clock',    'Clock',        '#clock',       0, 'scale', false, [3, 4], [1, 0.5, 0.5], { copies: 'mirror' }],
    ['photo1',   'Photo 1',      '#frames > figure', 0, 'scale', false, [2, 3], [1, 3.5, 0], PHOTO],
    ['photo2',   'Photo 2',      '#frames > figure', 1, 'scale', false, [2, 3], [1, 5.5, 0], PHOTO],
    ['photo3',   'Photo 3',      '#frames > figure', 2, 'scale', false, [2, 3], [1, 7.5, 0], PHOTO],
    ['calendar', 'Calendar',     '#cal',         0, 'flow',  true,  [2, 5], [1, 0, 5],    { copies: 'mirror' }],
    ['music',    'Now playing',  '#viz',         0, 'scale', false, [2, 4], [2, 7.5, 1],
       { free: true, round: true, at: [5400, 130], anchor: [5570, 300], anchorLabel: 'Center on the sun' }],
    ['weather',  'Weather',      '#life > .card', 0, 'scale', true, [2, 2], [2, 7.5, 5],  { copies: 'mirror' }],
    ['todo',     'To-do',        '#life > .card', 1, 'flow',  true, [2, 3], [2, 7.5, 7]],
    ['nas',      'Home NAS',     '#life > .card', 2, 'scale', true, [2, 1], [2, 7.5, 10], { copies: 'mirror' }]
  ];

  // M: every box on the wallpaper (originals and copies)
  //   { id, type, label, el, fit, chrome, free, opt, baseW, baseH, def, box, copyOf }
  // L: the saved layout, one entry per box
  //   grid: { x, y, cw, ch, hidden }   free: { x, y, fw, fh, rot, z, hidden }   copies add { type }, photos add { data }
  const M = {};
  let L = {};
  let ready = false, pending = null, editing = false;
  let drag = null, rs = null, rot = null;
  let pool, photoTemplate;
  let zTop = 10;

  const boxW = cw => cw * CELL_W + (cw - 1) * GAP;
  const boxH = ch => ch * CELL_H + (ch - 1) * GAP;
  const scale = () => stage.getBoundingClientRect().width / STAGE_W || 1;
  const isFree = id => !!(M[id] && M[id].free);
  const W_ = id => isFree(id) ? L[id].fw : boxW(L[id].cw);          // a box's width and height in px
  const H_ = id => isFree(id) ? L[id].fh : boxH(L[id].ch);
  const snapFree = v => Math.round(v / 2) * 2;
  function snapX(x) {
    const scr = Math.max(0, Math.min(2, Math.floor((x + STEP_X / 2) / SCREEN_W)));
    return scr * SCREEN_W + MARGIN + Math.round((x - scr * SCREEN_W - MARGIN) / STEP_X) * STEP_X;
  }
  const snapY = y => MARGIN + Math.round((y - MARGIN) / STEP_Y) * STEP_Y;
  function clamp(id, x, y) {
    return {
      x: Math.min(Math.max(0, x), STAGE_W - W_(id)),
      y: Math.min(Math.max(0, y), STAGE_H - (isFree(id) ? 0 : BOTTOM) - H_(id))
    };
  }
  const rect = (id, x = L[id].x, y = L[id].y) => ({ l: x, t: y, r: x + W_(id), b: y + H_(id) });
  const hits = (a, b) => a.l < b.r + 4 && b.l < a.r + 4 && a.t < b.b + 4 && b.t < a.b + 4;   // 12 px gaps are fine
  const shown = () => Object.keys(L).filter(id => M[id] && !L[id].hidden && !isFree(id));   // free boxes don't block
  function free(id, x, y, ignore) {
    if (isFree(id)) return true;
    const me = rect(id, x, y);
    return !shown().some(o => o !== id && o !== ignore && hits(me, rect(o)));
  }
  // nearest snapped spot (searching outward) where the box doesn't overlap another one
  function settle(id, x, y, ignore) {
    if (isFree(id)) return clamp(id, snapFree(x), snapFree(y));
    let p = clamp(id, snapX(x), snapY(y));
    if (free(id, p.x, p.y, ignore)) return p;
    for (let r = 1; r <= 40; r++) {
      for (const [dx, dy] of [[0, r], [0, -r], [r, 0], [-r, 0], [r, r], [-r, r], [r, -r], [-r, -r]]) {
        const q = clamp(id, p.x + dx * STEP_X, p.y + dy * STEP_Y);
        if (free(id, q.x, q.y, ignore)) return q;
      }
    }
    return p;
  }
  function stagePos(el) {
    let x = 0, y = 0, n = el;
    while (n && n !== stage) { x += n.offsetLeft; y += n.offsetTop; n = n.offsetParent; }
    return { x, y };
  }
  const stripIds = el => { el.removeAttribute('id'); el.querySelectorAll('[id]').forEach(n => n.removeAttribute('id')); return el; };

  // ---------- boxes ----------
  function makeBox(m) {
    const b = document.createElement('div');
    b.className = 'wbox' + (m.chrome ? ' chrome' : '') + (m.free ? ' free' : '') + (m.opt.round ? ' round' : '');
    b.dataset.w = m.id;
    const menu = document.createElement('button');
    menu.type = 'button'; menu.className = 'w-menu'; menu.title = 'Change, set up or remove this widget';
    menu.innerHTML = '<svg width="14" height="14" viewBox="0 0 24 24" fill="currentColor"><circle cx="5" cy="12" r="2.2"/><circle cx="12" cy="12" r="2.2"/><circle cx="19" cy="12" r="2.2"/></svg>';
    menu.addEventListener('mousedown', e => { e.stopPropagation(); });
    menu.addEventListener('click', e => { e.stopPropagation(); boxMenu(m.id, menu); });
    const grip = document.createElement('div');
    grip.className = 'w-resize'; grip.title = 'Drag to resize';
    grip.addEventListener('mousedown', e => resizeStart(e, m.id));
    b.appendChild(menu); b.appendChild(grip);
    if (m.opt.rotate) {
      const knob = document.createElement('div');
      knob.className = 'w-rot'; knob.title = 'Drag to rotate';
      knob.addEventListener('mousedown', e => rotStart(e, m.id));
      b.appendChild(knob);
    }
    b.addEventListener('mousedown', down);
    return b;
  }

  // fit the module inside its box
  function fit(id) {
    const m = M[id], l = L[id];
    if (!m || !l || l.hidden) return;
    const el = m.el, bw = W_(id), bh = H_(id);
    let s, left, top;
    el.style.width = m.baseW + 'px';
    if (m.fit === 'flow') {
      s = Math.min(bw / m.baseW, 2.2);
      el.style.height = (bh / s) + 'px'; el.style.maxHeight = 'none'; el.style.overflow = 'hidden';
      left = (bw - m.baseW * s) / 2; top = 0;
    } else {
      el.style.height = '';
      const h = el.offsetHeight || 1;
      s = Math.min(bw / m.baseW, bh / h);
      left = (bw - m.baseW * s) / 2; top = (bh - h * s) / 2;
    }
    el.style.left = left + 'px'; el.style.top = top + 'px';
    el.style.transformOrigin = '0 0';
    el.style.transform = 'scale(' + s.toFixed(4) + ')' + (m.baseT ? ' ' + m.baseT : '');
    if (id === 'todo' && typeof CoralTodo !== 'undefined') {           // fold to-dos that don't fit the box
      const add = el.querySelector('#todoAdd');
      if (add) CoralTodo.fit(add.offsetTop + add.offsetHeight + 12 - el.clientHeight);
    }
  }

  function render(id) {
    const m = M[id], l = L[id];
    if (!m || !l) return;
    if (l.hidden) { pool.appendChild(m.el); if (m.box.parentNode) m.box.remove(); return; }
    if (m.el.parentNode !== m.box) m.box.insertBefore(m.el, m.box.firstChild);
    if (!m.box.parentNode) stage.appendChild(m.box);
    m.box.style.left = l.x + 'px'; m.box.style.top = l.y + 'px';
    m.box.style.width = W_(id) + 'px'; m.box.style.height = H_(id) + 'px';
    m.box.style.transform = m.opt.rotate && l.rot ? 'rotate(' + l.rot + 'deg)' : '';
    m.box.style.zIndex = m.free ? String(l.z || 10) : '';
    m.box.dataset.wLabel = m.label + '  ' + (m.free ? Math.round(l.fw) + ' px' + (l.rot ? ' ' + Math.round(l.rot) + '°' : '') : l.cw + 'x' + l.ch);
    fit(id);
    if (m.opt.copies === 'photo' && typeof CoralPhotos !== 'undefined') CoralPhotos.apply(id, m.el, l.data || {});
  }
  const renderAll = () => Object.keys(M).forEach(render);

  // ---------- modules and copies ----------
  function addModule(id, type, label, el, base, def, copyOf) {
    const m = M[id] = { id, type, label, el, fit: base.fit, chrome: base.chrome, free: !!base.opt.free, opt: base.opt,
                        baseW: base.baseW, baseH: base.baseH, baseT: base.baseT, def, copyOf };
    el.classList.add('wmod');
    el.dataset.mod = id;
    m.box = makeBox(m);
    if (ro) ro.observe(el);
    return m;
  }
  let copyCount = 0;
  // a new box showing another `type`: an independent photo frame, or a live mirror of a display widget
  function createCopy(type, id) {
    const base = M[type];
    if (!base || !base.opt.copies) return null;
    id = id || type + '~' + (++copyCount);
    const n = parseInt(String(id).split('~')[1], 10); if (n > copyCount) copyCount = n;
    let el, label;
    if (base.opt.copies === 'photo') {
      el = photoTemplate.cloneNode(true);
      const frames = Object.values(M).filter(m => m.opt.copies === 'photo').length + 1;
      label = 'Photo ' + frames;
      const ph = el.querySelector('.ph'); if (ph) ph.textContent = label;
    } else {
      el = stripIds(base.el.cloneNode(true));
      el.classList.add('wmirror');
      label = base.label + ' (copy)';
    }
    el.style.transform = '';
    pool.appendChild(el);
    const def = base.free ? { x: base.def.x + 40, y: base.def.y + 40, fw: base.def.fw, fh: base.def.fh, rot: 0 }
                          : { x: base.def.x, y: base.def.y, cw: base.def.cw, ch: base.def.ch };
    addModule(id, type, label, el, base, def, type);
    return id;
  }
  // mirrors copy their original's content once a second
  setInterval(() => {
    for (const m of Object.values(M)) {
      if (!m.copyOf || m.opt.copies !== 'mirror' || L[m.id].hidden) continue;
      const src = M[m.copyOf].el;
      if (m.el.innerHTML !== src.innerHTML) { m.el.innerHTML = src.innerHTML; stripIds(m.el); m.el.setAttribute('data-mod', m.id); }
    }
  }, 1000);

  let ro = null;
  function init() {
    pool = document.createElement('div');
    pool.style.display = 'none';
    stage.appendChild(pool);
    if (typeof ResizeObserver !== 'undefined')
      ro = new ResizeObserver(entries => {
        for (const en of entries) { const id = en.target.dataset.mod; if (id && M[id] && M[id].fit === 'scale') requestAnimationFrame(() => fit(id)); }
      });
    const found = [];
    for (const [id, label, sel, i, fitMode, chrome, size, place, opt] of MODULES) {
      const el = document.querySelectorAll(sel)[i];
      if (!el) continue;
      const cs = getComputedStyle(el);
      found.push({ id, label, el, fitMode, chrome, size, place, opt: opt || {}, w: el.offsetWidth, h: el.offsetHeight,
                   padding: cs.padding, pos: stagePos(el) });
    }
    for (const f of found) {
      const el = f.el;
      if (f.opt.copies === 'photo' && !photoTemplate) {            // a clean frame to make new photo frames from
        photoTemplate = stripIds(el.cloneNode(true));
        photoTemplate.querySelector('img') && photoTemplate.querySelector('img').removeAttribute('src');
        photoTemplate.querySelectorAll('.photo').forEach(p => p.classList.remove('has'));
        photoTemplate.querySelectorAll('figcaption').forEach(c => c.textContent = '');
      }
      let baseT = el.style.transform || '', rot0 = 0;
      if (f.opt.rotate) {                                            // the box rotates instead of the frame
        const r = /rotate\((-?[\d.]+)deg\)/.exec(baseT); rot0 = r ? parseFloat(r[1]) : 0; baseT = ''; el.style.transform = '';
      }
      el.style.padding = f.padding;                                  // styles that came from the old parent (e.g. "#life .card")
      el.style.margin = '0';
      el.style.position = 'absolute';
      const at = f.opt.at || [f.pos.x, f.pos.y];
      const def = f.opt.free
        ? { x: at[0], y: at[1], fw: f.w, fh: f.h, rot: rot0 }
        : { x: f.place[0] * SCREEN_W + MARGIN + f.place[1] * PITCH_X, y: MARGIN + f.place[2] * PITCH_Y, cw: f.size[0], ch: f.size[1] };
      addModule(f.id, f.id, f.label, el, { fit: f.fitMode, chrome: f.chrome, opt: f.opt, baseW: f.w, baseH: f.h, baseT }, def, null);
      L[f.id] = Object.assign({}, def);
    }
    let saved = pending;
    if (!saved) { try { saved = JSON.parse(localStorage.getItem('cc.layout')); } catch (e) {} }
    ready = true;
    if (saved) apply(saved); else renderAll();
    buildControls();
  }

  // ---------- saved layout ----------
  function get() {
    if (!ready) return pending || undefined;   // not measured yet: hand back what was loaded, never an empty layout
    const w = {};
    for (const id of Object.keys(L)) {
      const l = L[id], m = M[id];
      if (!m) continue;
      w[id] = m.free ? { x: l.x, y: l.y, fw: l.fw, fh: l.fh } : { x: l.x, y: l.y, cw: l.cw, ch: l.ch };
      if (m.free && l.rot) w[id].rot = l.rot;
      if (m.free && l.z) w[id].z = l.z;
      if (m.copyOf) w[id].type = m.copyOf;
      if (l.data) w[id].data = l.data;
      if (l.hidden) w[id].hidden = true;
    }
    return { v: 2, w };
  }
  function apply(layout) {
    if (!ready) { pending = layout; return; }
    if (!layout || typeof layout !== 'object') return;
    // copies from before that are gone now
    for (const id of Object.keys(M)) if (M[id].copyOf && !(layout.v === 2 && layout.w && layout.w[id])) removeCopy(id, true);
    const fresh = {};
    if (layout.v === 2 && layout.w) {
      for (const id of Object.keys(layout.w)) { const p = layout.w[id]; if (!M[id] && p && p.type && M[p.type]) createCopy(p.type, id); }
      for (const id of Object.keys(M)) fresh[id] = Object.assign({}, M[id].def);
      for (const id of Object.keys(layout.w)) {
        const p = layout.w[id], f = fresh[id];
        if (!f || !p) continue;
        if (p.data && typeof p.data === 'object') f.data = p.data;
        f.hidden = !!p.hidden;
        if (isFree(id)) {                            // free boxes keep pixel sizes; an old grid entry falls back to the default spot
          if (isFinite(p.fw) && isFinite(p.fh) && isFinite(p.x) && isFinite(p.y)) { f.x = p.x; f.y = p.y; f.fw = p.fw; f.fh = p.fh; }
          if (isFinite(p.rot)) f.rot = p.rot;
          if (isFinite(p.z)) { f.z = p.z; zTop = Math.max(zTop, p.z); }
          continue;
        }
        if (isFinite(p.cw) && isFinite(p.ch)) { f.cw = Math.max(1, Math.min(MAX_CW, Math.round(p.cw))); f.ch = Math.max(1, Math.min(MAX_CH, Math.round(p.ch))); }
        if (isFinite(p.x) && isFinite(p.y)) { f.x = p.x; f.y = p.y; }
      }
      L = fresh;
      for (const id of Object.keys(L)) { const c = isFree(id) ? clamp(id, L[id].x, L[id].y) : clamp(id, snapX(L[id].x), snapY(L[id].y)); L[id].x = c.x; L[id].y = c.y; }
    } else {
      // older layouts saved free positions: keep where each widget was, on the new grid, then untangle overlaps
      for (const id of Object.keys(M)) fresh[id] = Object.assign({}, M[id].def);
      L = {};
      const order = Object.keys(fresh).sort((a, b) => {
        const pa = layout[a] && isFinite(layout[a].y) ? layout[a].y : fresh[a].y, pb = layout[b] && isFinite(layout[b].y) ? layout[b].y : fresh[b].y;
        return pa - pb;
      });
      for (const id of order) {
        const p = !fresh[id].fw && layout[id] && isFinite(layout[id].x) && isFinite(layout[id].y) ? layout[id] : fresh[id];
        L[id] = fresh[id];
        const q = settle(id, p.x, p.y);
        L[id].x = q.x; L[id].y = q.y;
      }
    }
    renderAll();
  }
  function save() {
    try { localStorage.setItem('cc.layout', JSON.stringify(get())); } catch (e) {}
    if (typeof pushState === 'function') { clearTimeout(save.t); save.t = setTimeout(pushState, 300); }
  }
  function reset() {
    for (const id of Object.keys(M)) if (M[id].copyOf) removeCopy(id, true);
    for (const id of Object.keys(M)) L[id] = Object.assign({}, M[id].def, L[id] && L[id].data ? { data: L[id].data } : {});
    renderAll(); save();
    tellLauncher('reset');
  }
  function removeCopy(id, quiet) {
    const m = M[id];
    if (!m || !m.copyOf) return;
    if (ro) ro.unobserve(m.el);
    m.box.remove(); m.el.remove();
    delete M[id]; delete L[id];
    if (!quiet) save();
  }
  function bringToFront(id) {
    if (!isFree(id)) return;
    L[id].z = ++zTop;
    if (zTop > 80) {                                       // keep the stacking numbers small (menus sit above 80)
      const order = Object.keys(L).filter(isFree).sort((a, b) => (L[a].z || 0) - (L[b].z || 0));
      order.forEach((k, i) => { L[k].z = 10 + i; render(k); });
      zTop = 10 + order.length;
    }
    M[id].box.style.zIndex = String(L[id].z);
  }

  // ---------- moving (drag, or click-to-carry) ----------
  function down(e) {
    if (!editing || e.button !== 0) return;
    e.preventDefault(); e.stopPropagation();
    closePop();
    const id = e.currentTarget.dataset.w;
    if (drag && drag.carry) { drop(); return; }          // second click drops a carried box
    bringToFront(id);
    drag = { id, sx: e.clientX, sy: e.clientY, ox: L[id].x, oy: L[id].y, moved: false, carry: false };
    M[id].box.classList.add('w-dragging');
  }
  function move(e) {
    if (!drag) return;
    const k = scale(), dx = (e.clientX - drag.sx) / k, dy = (e.clientY - drag.sy) / k;
    if (Math.abs(dx) + Math.abs(dy) > 4) drag.moved = true;
    if (!drag.moved && !drag.carry) return;
    const id = drag.id;
    const p = isFree(id) ? clamp(id, snapFree(drag.ox + dx), snapFree(drag.oy + dy)) : clamp(id, snapX(drag.ox + dx), snapY(drag.oy + dy));
    L[id].x = p.x; L[id].y = p.y;
    M[id].box.style.left = p.x + 'px'; M[id].box.style.top = p.y + 'px';
    M[id].box.classList.toggle('w-blocked', !free(id, p.x, p.y));
  }
  function up() {
    if (!drag || drag.carry) return;
    if (!drag.moved) { drag.carry = true; M[drag.id].box.classList.add('w-carry'); return; }   // a click: pick it up
    drop();
  }
  function drop() {
    const id = drag.id;
    const p = settle(id, L[id].x, L[id].y);
    L[id].x = p.x; L[id].y = p.y;
    M[id].box.classList.remove('w-dragging', 'w-carry', 'w-blocked');
    drag = null;
    render(id); save();
  }

  // ---------- resizing ----------
  function resizeStart(e, id) {
    if (!editing || e.button !== 0) return;
    e.preventDefault(); e.stopPropagation();
    if (drag) drop();
    closePop();
    bringToFront(id);
    rs = { id, sx: e.clientX, sy: e.clientY, cw0: L[id].cw, ch0: L[id].ch, fw0: L[id].fw, fh0: L[id].fh };
    M[id].box.classList.add('w-resizing');
  }
  function resizeMove(e) {
    if (!rs || rs.sx === undefined) return false;
    const k = scale(), id = rs.id, l = L[id];
    if (isFree(id)) {                                  // free: any size, same shape, centre stays put
      const d = Math.max((e.clientX - rs.sx) / k, (e.clientY - rs.sy) / k);
      const ratio = rs.fh0 / rs.fw0;
      const fw = snapFree(Math.max(120, Math.min(900, rs.fw0 + d))), fh = snapFree(fw * ratio);
      if (fw !== l.fw) {
        const cx = l.x + l.fw / 2, cy = l.y + l.fh / 2;
        l.fw = fw; l.fh = fh;
        const c = clamp(id, snapFree(cx - fw / 2), snapFree(cy - fh / 2)); l.x = c.x; l.y = c.y;
        render(id);
      }
      return true;
    }
    const tw = boxW(rs.cw0) + (e.clientX - rs.sx) / k, th = boxH(rs.ch0) + (e.clientY - rs.sy) / k;
    let cw = Math.max(1, Math.min(MAX_CW, Math.round((tw + GAP) / PITCH_X)));
    let ch = Math.max(1, Math.min(MAX_CH, Math.round((th + GAP) / PITCH_Y)));
    while (cw > 1 && l.x + boxW(cw) > STAGE_W) cw--;
    while (ch > 1 && l.y + boxH(ch) > STAGE_H - BOTTOM) ch--;
    if (cw !== l.cw || ch !== l.ch) {
      l.cw = cw; l.ch = ch; render(id);
      M[id].box.classList.toggle('w-blocked', !free(id, l.x, l.y));
    }
    return true;
  }
  function finishResize() {
    const id = rs.id; rs = null;
    M[id].box.classList.remove('w-resizing', 'w-blocked');
    if (isFree(id)) { save(); return; }
    // anything the bigger box now covers moves to the nearest free spot
    for (const o of shown()) if (o !== id && hits(rect(id), rect(o))) { const q = settle(o, L[o].x, L[o].y); L[o].x = q.x; L[o].y = q.y; render(o); }
    save();
  }

  // ---------- rotating (photo frames) ----------
  function rotStart(e, id) {
    if (!editing || e.button !== 0) return;
    e.preventDefault(); e.stopPropagation();
    if (drag) drop();
    closePop();
    bringToFront(id);
    const r = M[id].box.getBoundingClientRect();
    const cx = r.left + r.width / 2, cy = r.top + r.height / 2;
    rot = { id, cx, cy, a0: Math.atan2(e.clientY - cy, e.clientX - cx), r0: L[id].rot || 0 };
    M[id].box.classList.add('w-resizing');
  }
  function rotMove(e) {
    if (!rot) return false;
    const a = Math.atan2(e.clientY - rot.cy, e.clientX - rot.cx);
    let deg = rot.r0 + (a - rot.a0) * 180 / Math.PI;
    deg = ((deg + 540) % 360) - 180;                                  // keep it between -180 and 180
    for (const q of [-180, -90, 0, 90, 180]) if (Math.abs(deg - q) < 3) deg = q;   // snap to straight
    deg = Math.round(deg);
    if (deg !== L[rot.id].rot) { L[rot.id].rot = deg; render(rot.id); }
    return true;
  }

  window.addEventListener('mousemove', e => { if (resizeMove(e) || rotMove(e)) e.stopImmediatePropagation(); }, true);
  window.addEventListener('mouseup', e => {
    if (rs) { finishResize(); e.stopImmediatePropagation(); }
    else if (rot) { M[rot.id].box.classList.remove('w-resizing'); rot = null; save(); e.stopImmediatePropagation(); }
  }, true);
  window.addEventListener('mousemove', move);
  window.addEventListener('mouseup', up);
  window.addEventListener('mousedown', e => {
    if (drag && drag.carry && !e.target.closest('[data-w]')) drop();
    if (popEl && !e.target.closest('.w-pop')) closePop();
  });
  window.addEventListener('keydown', e => { if (e.key === 'Escape') { if (popEl) closePop(); else if (editing) setEditing(false); } });

  // ---------- pop-up menus (also used by the weather location and photo pickers) ----------
  let popEl = null;
  function closePop() { if (popEl) { popEl.remove(); popEl = null; } }
  // items: [{ label, sub, checked, disabled, danger, onClick }] or { head: 'text' }; or a ready-made element
  function popover(anchor, items, extraClass) {
    closePop();
    const p = document.createElement('div');
    p.className = 'w-pop' + (extraClass ? ' ' + extraClass : '');
    if (items instanceof Element) p.appendChild(items);
    else for (const it of items) {
      if (it.head) { const h = document.createElement('div'); h.className = 'w-pop-head'; h.textContent = it.head; p.appendChild(h); continue; }
      const b = document.createElement('button');
      b.type = 'button';
      b.className = 'w-pop-item' + (it.checked ? ' on' : '') + (it.danger ? ' danger' : '');
      b.disabled = !!it.disabled;
      const t = document.createElement('span'); t.textContent = it.label; b.appendChild(t);
      if (it.sub) { const s = document.createElement('small'); s.textContent = it.sub; b.appendChild(s); }
      b.addEventListener('mousedown', e => e.stopPropagation());
      b.addEventListener('click', e => { e.stopPropagation(); closePop(); if (it.onClick) it.onClick(); });
      p.appendChild(b);
    }
    p.addEventListener('mousedown', e => e.stopPropagation());
    stage.appendChild(p);
    // place it under the anchor, kept on the stage
    const sr = stage.getBoundingClientRect(), ar = anchor.getBoundingClientRect(), k = scale();
    let x = (ar.left - sr.left) / k, y = (ar.bottom - sr.top) / k + 6;
    x = Math.max(8, Math.min(x, STAGE_W - p.offsetWidth - 8));
    if (y + p.offsetHeight > STAGE_H - BOTTOM) y = Math.max(8, (ar.top - sr.top) / k - p.offsetHeight - 6);
    p.style.left = x + 'px'; p.style.top = y + 'px';
    popEl = p;
    return p;
  }

  function boxMenu(id, anchor) {
    const m = M[id], items = [];
    if (m.opt.copies === 'photo') {
      items.push({ head: m.label });
      items.push({ label: 'Choose picture...', onClick: () => CoralPhotos.pick(id, anchor) });
      items.push({ label: 'Edit caption...', onClick: () => CoralPhotos.caption(id) });
      if (L[id].data && L[id].data.caption) items.push({ label: 'Remove caption', onClick: () => CoralLayout.setData(id, { caption: '' }) });
      if (!m.copyOf && L[id].data && (L[id].data.src || L[id].data.caption != null))
        items.push({ label: 'Use the Customise picture and caption', onClick: () => { delete L[id].data; CoralPhotos.restore(id); render(id); save(); } });
    }
    if (!m.copyOf) {
      const same = Object.values(M).filter(o => !o.copyOf && !!o.free === !!m.free && o.opt.copies !== 'photo');
      if (m.opt.copies !== 'photo' && same.length > 1) {
        items.push({ head: 'Show in this box' });
        for (const o of same) {
          const here = o.id === id, elsewhere = !here && !L[o.id].hidden;
          items.push({ label: o.label, sub: here ? 'showing' : elsewhere ? 'on screen - swaps places' : '', checked: here,
                       onClick: here ? null : () => switchModule(id, o.id) });
        }
      }
    }
    if (isFree(id)) {
      const o = m.opt;
      items.push({ head: 'Free placement - ' + Math.round(L[id].fw) + ' px' + (L[id].rot ? ', ' + L[id].rot + '°' : '') });
      if (o.anchor) items.push({ label: o.anchorLabel || 'Center on its spot', onClick: () => {
        const c = clamp(id, snapFree(o.anchor[0] - L[id].fw / 2), snapFree(o.anchor[1] - L[id].fh / 2)); L[id].x = c.x; L[id].y = c.y; render(id); save(); } });
      if (o.rotate) items.push({ label: 'Straighten', onClick: () => { L[id].rot = 0; render(id); save(); } });
      items.push({ label: 'Reset size' + (o.rotate ? ' and angle' : ' and place'), onClick: () => {
        const d = m.def; L[id].fw = d.fw; L[id].fh = d.fh; L[id].rot = d.rot || 0; if (!o.rotate) { L[id].x = d.x; L[id].y = d.y; } render(id); save(); } });
    } else {
      items.push({ head: L[id].cw + ' x ' + L[id].ch + ' cells' });
      items.push({ label: 'Reset size', onClick: () => { L[id].cw = m.def.cw; L[id].ch = m.def.ch; render(id); rs = { id }; finishResize(); } });
    }
    if (m.copyOf) items.push({ label: 'Delete this copy', danger: true, onClick: () => removeCopy(id) });
    else items.push({ label: 'Remove from wallpaper', danger: true, onClick: () => { L[id].hidden = true; render(id); save(); } });
    popover(anchor, items);
  }
  // put module `to` where module `from` is; `from` swaps into `to`'s old place, or is put away if `to` was hidden
  function switchModule(from, to) {
    const a = L[from], b = L[to];
    const pa = { x: a.x, y: a.y, cw: a.cw, ch: a.ch }, pb = { x: b.x, y: b.y, cw: b.cw, ch: b.ch };
    const toWasHidden = b.hidden;
    Object.assign(b, pa, { hidden: false });
    if (toWasHidden) a.hidden = true; else Object.assign(a, pb);
    render(from); render(to); save();
  }
  function placeNew(id, screen) {
    const l = L[id];
    if (isFree(id)) {                                   // free: near the top-middle of the screen, a little offset each time
      const n = Object.keys(M).length % 6;
      const c = clamp(id, screen * SCREEN_W + SCREEN_W / 2 - l.fw / 2 + n * 30, 120 + n * 24); l.x = c.x; l.y = c.y;
      bringToFront(id);
    } else { const q = settle(id, screen * SCREEN_W + MARGIN, MARGIN); l.x = q.x; l.y = q.y; }
    render(id); save();
  }
  function addMenu(anchor, screen) {
    const items = [];
    const hidden = Object.values(M).filter(m => !m.copyOf && L[m.id].hidden);
    if (hidden.length) {
      items.push({ head: 'Bring back' });
      for (const m of hidden) items.push({ label: m.label, sub: m.free ? 'free' : m.def.cw + ' x ' + m.def.ch, onClick: () => {
        Object.assign(L[m.id], m.free ? { fw: m.def.fw, fh: m.def.fh } : { cw: m.def.cw, ch: m.def.ch }, { hidden: false });
        placeNew(m.id, screen);
      } });
    }
    items.push({ head: 'Add another' });
    items.push({ label: 'Photo frame', sub: 'own picture', onClick: () => {
      const id = createCopy('photo1'); L[id] = Object.assign({}, M[id].def); placeNew(id, screen);
      setTimeout(() => CoralPhotos.pick(id, M[id].box), 50);
    } });
    for (const m of Object.values(M)) {
      if (m.copyOf || m.opt.copies !== 'mirror') continue;
      items.push({ label: m.label, sub: 'live copy', onClick: () => {
        const id = createCopy(m.id); L[id] = Object.assign({}, M[id].def); placeNew(id, screen);
      } });
    }
    popover(anchor, items, 'w-pop-tall');
  }

  // ---------- edit mode controls ----------
  const SVC = 'http://127.0.0.1:47613';
  function tellLauncher(what) { fetch(SVC + '/edit', { method: 'POST', headers: { 'Content-Type': 'text/plain' }, body: what }).catch(() => {}); }

  function setEditing(on) {
    if (drag) drop();
    closePop();
    editing = on;
    document.body.classList.toggle('editing', on);
    tellLauncher(on ? 'on' : 'off');
  }

  function buildControls() {
    const grid = document.createElement('div');
    grid.id = 'w-grid';
    stage.insertBefore(grid, stage.children[1] || null);
    // one pencil per screen (top-right corner), so it is reachable whatever the monitor layout
    [0, 1, 2].forEach(scr => {
      const bar = document.createElement('div');
      bar.className = 'w-bar';
      bar.style.right = (STAGE_W - (scr * SCREEN_W + SCREEN_W - 16)) + 'px';
      bar.innerHTML =
        '<span class="w-hint">Drag to move &middot; corner to resize &middot; knob to rotate &middot; &#8943; for options</span>' +
        '<button type="button" class="w-btn w-add">+ Add widget</button>' +
        '<button type="button" class="w-btn w-reset">Reset layout</button>' +
        '<button type="button" class="w-btn w-done">Done</button>' +
        '<button type="button" class="w-pencil" title="Edit widgets"><svg width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M4 20h4L19 9l-4-4L4 16v4z"/><path d="M13.5 6.5l4 4"/></svg></button>';
      bar.querySelector('.w-pencil').addEventListener('click', () => setEditing(!editing));
      bar.querySelector('.w-done').addEventListener('click', () => setEditing(false));
      bar.querySelector('.w-reset').addEventListener('click', reset);
      const add = bar.querySelector('.w-add');
      add.addEventListener('mousedown', e => e.stopPropagation());
      add.addEventListener('click', e => { e.stopPropagation(); addMenu(add, scr); });
      stage.appendChild(bar);
    });
  }

  // wait for fonts and the first data so sizes are final before measuring
  function start() { setTimeout(init, 600); }
  if (document.fonts && document.fonts.ready) document.fonts.ready.then(start); else window.addEventListener('load', start);

  return {
    get, apply, reset, setEditing, popover, closePop,
    refit: id => { if (ready && M[id]) fit(id); },
    layout: () => L,
    data: id => (L[id] && L[id].data) || null,
    setData: (id, patch) => { if (!L[id]) return; L[id].data = Object.assign({}, L[id].data || {}, patch); render(id); save(); },
    reapply: id => { if (ready && M[id] && L[id] && L[id].data && typeof CoralPhotos !== 'undefined') CoralPhotos.apply(id, M[id].el, L[id].data); }
  };
})();
