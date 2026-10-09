// 當日即時線圖(FR-26,SPEC §12.6):自繪 SVG,不引入圖表函式庫。
// 規則:
//  - 不使用 inline style(CSP):顏色用既有 .pnl-up / .pnl-down / .pnl-flat 與 SVG 表現屬性,其餘樣式在 site.css 的 class。
//  - 不以 HTML 字串建立節點:一律以 DOM API 建立,文字用 textContent。
//  - 前端唯一允許的運算是「座標映射」(時間、價格線性對應到 SVG 座標,§0.5)。
//    最高、最低、趨勢方向、缺口、降採樣、Y 軸範圍全部使用伺服器(API-15)給的值。
(function () {
  'use strict';

  var SVG_NS = 'http://www.w3.org/2000/svg';
  var BATCH = 50;                 // API-15 單次最多 50 個代號
  var POLL_MS = 60 * 1000;        // 增量輪詢間隔
  var REFETCH_MIN_MS = 15 * 1000; // 價格超出軸範圍時,重取的最短間隔
  var MINUS = '−';

  var cfg = { rerender: function () {}, refreshList: null };
  var state = {};      // symbol -> 走勢狀態(見 fresh())
  var symbols = [];    // 目前清單的代號
  var open = {};       // symbol -> 是否展開大圖
  var nodes = {};      // key -> { el, sig }:持久節點,畫面重繪(每 5 秒 ViewUpdated)時沿用,避免互動狀態被洗掉
  var lastRefetch = {};
  var disabled = false;
  var paused = false;
  var inflight = false;
  var timer = null;

  function fresh() {
    return { status: 'loading', name: '', tradeDate: null, isToday: true, axis: null, prevClose: null,
      high: null, low: null, yMin: null, yMax: null, trend: null, points: [], tail: null, failed: false, version: 0 };
  }

  // ---------- DOM 小工具 ----------
  function el(tag, text, cls) {
    var e = document.createElement(tag);
    if (text != null) e.textContent = text;
    if (cls) e.className = cls;
    return e;
  }
  function svgEl(tag, attrs, cls) {
    var e = document.createElementNS(SVG_NS, tag);
    if (attrs) Object.keys(attrs).forEach(function (k) { e.setAttribute(k, String(attrs[k])); });
    if (cls) e.setAttribute('class', cls);
    return e;
  }
  function svgText(text, attrs, cls) {
    var t = svgEl('text', attrs, cls);
    t.textContent = text;
    return t;
  }
  function num(n) { return n.toFixed(1); }

  // ---------- 格式化(只格式化,不運算;規則同 overview.js 的現價欄) ----------
  function price(n) {
    if (n == null) return '—';
    var f = Math.abs(n).toFixed(2).replace(/\B(?=(\d{3})+(?!\d))/g, ',');
    return n < 0 ? MINUS + f : f;
  }
  var timeFmt = new Intl.DateTimeFormat('zh-TW', { timeZone: 'Asia/Taipei', hourCycle: 'h23', hour: '2-digit', minute: '2-digit' });
  function hhmm(ms) { return timeFmt.format(new Date(ms)); }
  function mmdd(tradeDate) { return tradeDate.slice(5).replace('-', '/'); } // yyyy-MM-dd -> MM/dd
  function title(st) { return st.isToday || !st.tradeDate ? '當日走勢' : mmdd(st.tradeDate) + ' 走勢'; }
  function trendClass(st) { return st.trend === 'up' ? 'pnl-up' : st.trend === 'down' ? 'pnl-down' : 'pnl-flat'; }

  // ---------- 座標映射(Q10 唯一允許的前端運算) ----------
  function mapper(st, box) {
    var open0 = Date.parse(st.axis.openUtc);
    var span = Date.parse(st.axis.closeUtc) - open0;
    var range = st.yMax - st.yMin;
    return {
      x: function (ms) { return box.x + (ms - open0) / span * box.w; },
      y: function (p) { return range > 0 ? box.y + (st.yMax - p) / range * box.h : box.y + box.h / 2; },
      open: open0,
      close: open0 + span
    };
  }

  // 暫時線尾(ViewUpdated 的最新價):只在今日、有軸、時間在軸範圍內且晚於最後一個既有點時畫
  function tailPoint(st) {
    if (!st.tail || !st.isToday || !st.axis || !st.points.length) return null;
    var ms = Date.parse(st.tail.t);
    var last = Date.parse(st.points[st.points.length - 1].t);
    if (!(ms > last) || ms > Date.parse(st.axis.closeUtc) || ms < Date.parse(st.axis.openUtc)) return null;
    return { ms: ms, p: st.tail.p };
  }

  // 折線:gap = true 的點開新線段(M);只有一個點的線段以極短的線加圓端點畫成一個點
  function pathData(st, m, withTail) {
    var segs = [];
    st.points.forEach(function (pt, i) {
      var xy = [m.x(Date.parse(pt.t)), m.y(pt.p)];
      if (i === 0 || pt.gap) segs.push([xy]); else segs[segs.length - 1].push(xy);
    });
    var tail = withTail ? tailPoint(st) : null;
    if (tail && segs.length) segs[segs.length - 1].push([m.x(tail.ms), m.y(tail.p)]);
    return segs.map(function (s) {
      var d = 'M' + num(s[0][0]) + ' ' + num(s[0][1]);
      if (s.length === 1) return d + 'h0.1';
      for (var i = 1; i < s.length; i++) d += 'L' + num(s[i][0]) + ' ' + num(s[i][1]);
      return d;
    }).join('');
  }

  function lastShown(st) {
    var t = tailPoint(st);
    if (t) return { ms: t.ms, p: t.p };
    var last = st.points[st.points.length - 1];
    return { ms: Date.parse(last.t), p: last.p };
  }

  // ---------- 迷你圖 ----------
  var MINI = { w: 96, h: 32 };
  var MINI_BOX = { x: 2, y: 3, w: 92, h: 26 };

  function miniSvg(sym, st) {
    var svg = svgEl('svg', { viewBox: '0 0 ' + MINI.w + ' ' + MINI.h, width: MINI.w, height: MINI.h, role: 'img',
      'aria-label': sym + ' 當日走勢' }, 'spark-svg');
    var m = mapper(st, MINI_BOX);
    svg.append(svgEl('path', { d: pathData(st, m, true), fill: 'none', stroke: 'currentColor', 'stroke-width': 1.5,
      'stroke-linejoin': 'round', 'stroke-linecap': 'round' }, 'spark-line ' + trendClass(st)));
    return svg;
  }

  function drawMini(entry, sym) {
    var st = state[sym];
    var box = entry.el;
    box.replaceChildren();
    if (!st || st.status === 'loading') { box.append(el('span', '…', 'spark-note')); return; }
    if (st.status === 'error') {
      var rb = el('button', '走勢載入失敗', 'linkbtn spark-err');
      rb.type = 'button'; rb.title = '重試';
      rb.addEventListener('click', function (e) { e.stopPropagation(); retry(); });
      box.append(rb);
      return;
    }
    var b = el('button', null, 'spark-btn');
    b.type = 'button';
    b.setAttribute('aria-label', (open[sym] ? '收合走勢圖 ' : '展開走勢圖 ') + sym);
    b.setAttribute('aria-expanded', open[sym] ? 'true' : 'false');
    b.addEventListener('click', function (e) { e.stopPropagation(); toggle(sym); });
    if (st.status === 'noData') {
      var dash = el('span', '—', 'spark-note'); dash.title = '尚無成交';
      b.append(dash);
    } else {
      b.append(miniSvg(sym, st));
      if (!st.isToday && st.tradeDate) b.append(el('span', mmdd(st.tradeDate), 'spark-date')); // 休市日:迷你圖旁以小字標日期
    }
    box.append(b);
  }

  // ---------- 大圖 ----------
  var BIG = { w: 640, h: 240 };
  var BIG_BOX = { x: 8, y: 14, w: 624, h: 192 };

  function bigSvg(sym, st) {
    var m = mapper(st, BIG_BOX);
    var svg = svgEl('svg', { viewBox: '0 0 ' + BIG.w + ' ' + BIG.h, role: 'img', 'aria-label': sym + ' 當日走勢',
      preserveAspectRatio: 'xMidYMid meet' }, 'chart-svg');
    var right = BIG_BOX.x + BIG_BOX.w, bottom = BIG_BOX.y + BIG_BOX.h;
    svg.append(svgEl('rect', { x: BIG_BOX.x, y: BIG_BOX.y, width: BIG_BOX.w, height: BIG_BOX.h, fill: 'none' }, 'chart-frame'));

    // X 軸刻度:09:00、11:00、13:30(開盤、開盤後 2 小時、收盤)
    [[m.open, 'start'], [m.open + 2 * 3600 * 1000, 'middle'], [m.close, 'end']].forEach(function (t) {
      var x = m.x(t[0]);
      svg.append(svgEl('line', { x1: num(x), y1: BIG_BOX.y, x2: num(x), y2: bottom, stroke: 'currentColor' }, 'chart-grid'));
      svg.append(svgText(hhmm(t[0]), { x: num(x), y: bottom + 18, 'text-anchor': t[1] }, 'chart-label'));
    });

    // 最高、最低(伺服器值)與昨收參考線
    function hline(p, label, cls, anchorLeft) {
      var y = m.y(p);
      svg.append(svgEl('line', { x1: BIG_BOX.x, y1: num(y), x2: right, y2: num(y), stroke: 'currentColor' }, cls));
      svg.append(svgText(label + ' ' + price(p), { x: anchorLeft ? BIG_BOX.x + 4 : right - 4, y: num(y - 3),
        'text-anchor': anchorLeft ? 'start' : 'end' }, 'chart-label'));
    }
    if (st.high != null) hline(st.high, '最高', 'chart-hl', false);
    if (st.low != null) hline(st.low, '最低', 'chart-hl', false);
    if (st.prevClose != null) hline(st.prevClose, '昨收', 'chart-ref', true);

    svg.append(svgEl('path', { d: pathData(st, m, true), fill: 'none', stroke: 'currentColor', 'stroke-width': 1.8,
      'stroke-linejoin': 'round', 'stroke-linecap': 'round' }, 'chart-line ' + trendClass(st)));

    // 右端標最後價
    var last = lastShown(st);
    var lx = m.x(last.ms), ly = m.y(last.p);
    svg.append(svgEl('circle', { cx: num(lx), cy: num(ly), r: 3, fill: 'currentColor' }, 'chart-dot ' + trendClass(st)));
    svg.append(svgText(price(last.p), { x: num(Math.min(lx + 6, right)), y: num(ly - 6), 'text-anchor': lx + 60 > right ? 'end' : 'start' },
      'chart-last ' + trendClass(st)));

    // 互動:滑鼠移動或觸控時,顯示最接近的「既有點」的時間(Taipei HH:mm)與價格(不內插、不計算漲跌)
    var guide = svgEl('line', { x1: 0, y1: BIG_BOX.y, x2: 0, y2: bottom, stroke: 'currentColor', visibility: 'hidden' }, 'chart-guide');
    var mark = svgEl('circle', { cx: 0, cy: 0, r: 3.5, visibility: 'hidden', fill: 'currentColor' }, 'chart-mark');
    var tip = svgText('', { x: 0, y: BIG_BOX.y + 12, visibility: 'hidden' }, 'chart-tip');
    var hit = svgEl('rect', { x: BIG_BOX.x, y: BIG_BOX.y, width: BIG_BOX.w, height: BIG_BOX.h, fill: 'transparent' }, 'chart-hit');
    function hide() { [guide, mark, tip].forEach(function (n) { n.setAttribute('visibility', 'hidden'); }); }
    function show(ev) {
      var r = svg.getBoundingClientRect();
      if (!r.width || !st.points.length) return;
      var px = (ev.clientX - r.left) / r.width * BIG.w;
      var best = null, bestD = Infinity;
      st.points.forEach(function (pt) {
        var x = m.x(Date.parse(pt.t)), d = Math.abs(x - px);
        if (d < bestD) { bestD = d; best = { x: x, pt: pt }; }
      });
      if (!best) return;
      var y = m.y(best.pt.p);
      guide.setAttribute('x1', num(best.x)); guide.setAttribute('x2', num(best.x));
      mark.setAttribute('cx', num(best.x)); mark.setAttribute('cy', num(y));
      tip.textContent = hhmm(Date.parse(best.pt.t)) + '  ' + price(best.pt.p);
      tip.setAttribute('x', num(best.x > BIG.w / 2 ? best.x - 6 : best.x + 6));
      tip.setAttribute('text-anchor', best.x > BIG.w / 2 ? 'end' : 'start');
      [guide, mark, tip].forEach(function (n) { n.setAttribute('visibility', 'visible'); });
    }
    hit.addEventListener('pointermove', show);
    hit.addEventListener('pointerdown', show);
    hit.addEventListener('pointerleave', hide);
    hit.addEventListener('pointercancel', hide);
    svg.append(guide, mark, tip, hit);
    return svg;
  }

  function drawBig(entry, sym) {
    var st = state[sym];
    var box = entry.el;
    box.replaceChildren();
    var head = el('div', null, 'chart-head');
    head.append(el('strong', st ? title(st) : '當日走勢'));
    if (st && st.name) head.append(el('span', sym + ' ' + st.name, 'chart-sub'));
    var close = el('button', '收合走勢圖', 'linkbtn chart-close');
    close.type = 'button';
    close.addEventListener('click', function (e) { e.stopPropagation(); toggle(sym); });
    head.append(close);
    box.append(head);

    if (!st || st.status === 'loading') { box.append(el('p', '載入中…', 'chart-msg')); return; }
    if (st.status === 'error') {
      var p = el('p', '走勢載入失敗', 'chart-msg err');
      var rb = el('button', '重試', 'linkbtn'); rb.type = 'button';
      rb.addEventListener('click', function (e) { e.stopPropagation(); retry(); });
      p.append(' ', rb);
      box.append(p);
      return;
    }
    if (st.status === 'noData') { box.append(el('p', '尚無成交', 'chart-msg')); return; }
    if (st.failed) { // 已有資料,但最近一次更新失敗:保留圖形並提示
      var f = el('p', '走勢載入失敗', 'chart-msg err');
      var fb = el('button', '重試', 'linkbtn'); fb.type = 'button';
      fb.addEventListener('click', function (e) { e.stopPropagation(); retry(); });
      f.append(' ', fb);
      box.append(f);
    }
    box.append(bigSvg(sym, st));
  }

  // ---------- 持久節點 ----------
  function nodeFor(sym, kind, variant, draw) {
    var key = sym + '|' + kind + '|' + variant;
    var entry = nodes[key];
    if (!entry) {
      entry = nodes[key] = { el: el('div', null, kind === 'mini' ? 'spark' : 'chart-big'), sig: null };
      if (kind === 'big') entry.el.addEventListener('click', function (e) { e.stopPropagation(); });
    }
    var st = state[sym];
    var sig = (st ? st.version : -1) + ':' + (open[sym] ? 1 : 0);
    if (entry.sig !== sig) { entry.sig = sig; draw(entry, sym); }
    return entry.el;
  }

  function dropNodes(sym, kind) {
    Object.keys(nodes).forEach(function (k) {
      if (k.indexOf(sym + '|' + (kind || '')) === 0) delete nodes[k];
    });
  }

  function toggle(sym) {
    open[sym] = !open[sym];
    if (!open[sym]) dropNodes(sym, 'big');
    cfg.rerender();
  }

  // ---------- API-15 ----------
  async function request(syms, tradeDate, since) {
    var url = '/api/intraday?symbols=' + encodeURIComponent(syms.join(','));
    if (tradeDate && since) url += '&tradeDate=' + encodeURIComponent(tradeDate) + '&sinceUtc=' + encodeURIComponent(since);
    var res = await fetch(url, { credentials: 'same-origin' });
    if (res.ok) return res.json();
    var code = null;
    try { code = (await res.json()).code; } catch (e) { /* 非 JSON 的錯誤回應 */ }
    var err = new Error(String(res.status));
    err.status = res.status; err.code = code;
    throw err;
  }

  function chunk(list) {
    var out = [];
    for (var i = 0; i < list.length; i += BATCH) out.push(list.slice(i, i + BATCH));
    return out;
  }

  function inList(sym) { return symbols.indexOf(sym) >= 0; }

  // 把伺服器回應套進狀態。增量只接在既有點後面(以時間去重);其餘一律整份取代
  function apply(s) {
    var st = state[s.symbol];
    if (!st) return false;
    var needFull = false;
    if (s.incremental) {
      if (st.status === 'ok' && st.tradeDate === s.tradeDate && st.points.length) {
        var lastMs = Date.parse(st.points[st.points.length - 1].t);
        s.points.forEach(function (p) { if (Date.parse(p.t) > lastMs) st.points.push(p); });
      } else {
        needFull = true; // 沒有可接續的既有點:改取完整資料
      }
    } else {
      st.points = s.points.slice();
    }
    if (!needFull) {
      st.status = s.status; st.name = s.name; st.tradeDate = s.tradeDate; st.isToday = s.isToday; st.axis = s.axis;
      st.prevClose = s.prevClose; st.high = s.high; st.low = s.low; st.yMin = s.yMin; st.yMax = s.yMax; st.trend = s.trend;
      st.failed = false;
      st.version++;
    }
    return needFull;
  }

  function markError(syms) {
    syms.forEach(function (sym) {
      var st = state[sym];
      if (!st) return;
      if (st.status === 'ok' && st.points.length) st.failed = true; // 已有資料:保留圖形,只標記更新失敗
      else st.status = 'error';
      st.version++;
    });
  }

  function setDisabled() {
    disabled = true;
    stopTimer();
    cfg.rerender(); // 隱藏整個走勢欄與按鈕(503 FEATURE_DISABLED)
  }

  // 完整查詢(整頁載入、清單新增標的、重試)。404(清單剛刪除標的的競態)靜默重取清單後重試一次
  async function loadFull(syms, retried) {
    syms = syms.filter(inList);
    var batches = chunk(syms);
    for (var i = 0; i < batches.length; i++) {
      var batch = batches[i];
      try {
        var body = await request(batch);
        body.series.forEach(function (s) { s.incremental = false; apply(s); });
      } catch (e) {
        if (e.status === 503 && e.code === 'FEATURE_DISABLED') { setDisabled(); return; }
        if (e.status === 404 && !retried && cfg.refreshList) {
          try { await cfg.refreshList(); } catch (e2) { /* 重取失敗就當作一般錯誤 */ }
          await loadFull(batch, true);
          continue;
        }
        markError(batch);
      }
    }
    cfg.rerender();
  }

  // 每 60 秒增量輪詢。有資料的代號帶 tradeDate 與 sinceUtc(同一批取最早的最後一點,重複的點以時間去重);其餘代號取完整資料
  async function poll() {
    if (disabled || paused || document.hidden || inflight || !symbols.length) return;
    inflight = true;
    try {
      var byDate = {}, full = [];
      symbols.forEach(function (sym) {
        var st = state[sym];
        if (!st || st.status === 'loading') return;
        if (st.status === 'ok' && st.points.length && st.tradeDate) (byDate[st.tradeDate] = byDate[st.tradeDate] || []).push(sym);
        else full.push(sym);
      });
      var needFull = full.slice();
      var dates = Object.keys(byDate);
      for (var d = 0; d < dates.length; d++) {
        var batches = chunk(byDate[dates[d]]);
        for (var i = 0; i < batches.length; i++) {
          var since = null;
          batches[i].forEach(function (sym) {
            var t = state[sym].points[state[sym].points.length - 1].t;
            if (since === null || Date.parse(t) < Date.parse(since)) since = t;
          });
          try {
            var body = await request(batches[i], dates[d], since);
            body.series.forEach(function (s) { if (apply(s)) needFull.push(s.symbol); });
          } catch (e) {
            if (e.status === 503 && e.code === 'FEATURE_DISABLED') { setDisabled(); return; }
            markError(batches[i]); // 已有圖形者保留並標記失敗;下個週期自動重試
          }
        }
      }
      if (needFull.length) await loadFull(needFull);
      else cfg.rerender();
    } finally {
      inflight = false;
    }
  }

  function retry() {
    var failed = symbols.filter(function (sym) { var st = state[sym]; return st && (st.status === 'error' || st.failed); });
    failed.forEach(function (sym) { state[sym].status = state[sym].points.length ? state[sym].status : 'loading'; state[sym].failed = false; state[sym].version++; });
    cfg.rerender();
    loadFull(failed);
  }

  function ensureTimer() { if (!timer && !disabled) timer = setInterval(poll, POLL_MS); }
  function stopTimer() { if (timer) { clearInterval(timer); timer = null; } }

  // ---------- 與主畫面整合 ----------
  // 每次取得新的 View(API-06 或 ViewUpdated)呼叫:同步清單代號、延伸線尾
  function update(view) {
    var list = [], seen = {};
    view.rows.forEach(function (r) { if (!seen[r.symbol]) { seen[r.symbol] = true; list.push(r.symbol); } });
    symbols = list;
    Object.keys(state).forEach(function (sym) {
      if (!seen[sym]) { delete state[sym]; delete open[sym]; delete lastRefetch[sym]; dropNodes(sym); }
    });
    var added = list.filter(function (sym) { return !state[sym]; });
    added.forEach(function (sym) { state[sym] = fresh(); });
    if (added.length && !disabled) loadFull(added); // 清單新增標的:對新標的做完整查詢
    extendTail(view);
    if (list.length) ensureTimer();
  }

  // ViewUpdated:market.state = open 且該列 priceSource = trade、quoteStatus = live 時,把 (asOf, lastPrice) 當暫時線尾點(不寫入、不送出)
  function extendTail(view) {
    var live = view.market && view.market.state === 'open';
    var minute = Math.floor(Date.parse(view.asOf) / 60000);
    view.rows.forEach(function (r) {
      var st = state[r.symbol];
      if (!st) return;
      if (live && r.priceSource === 'trade' && r.quoteStatus === 'live' && r.lastPrice != null) {
        var t = st.tail;
        if (!t || t.p !== r.lastPrice || t.minute !== minute) { st.tail = { t: view.asOf, p: r.lastPrice, minute: minute }; st.version++; }
        if (st.status === 'ok' && st.yMin != null && (r.lastPrice > st.yMax || r.lastPrice < st.yMin)) refetchOutOfRange(r.symbol);
      } else if (st.tail) {
        st.tail = null; st.version++;
      }
    });
  }

  // 價格超出目前軸範圍:立即(最多每 15 秒一次)重打 API-15 取得新的軸範圍
  function refetchOutOfRange(sym) {
    var now = Date.now();
    if (disabled || now - (lastRefetch[sym] || 0) < REFETCH_MIN_MS) return;
    lastRefetch[sym] = now;
    loadFull([sym]);
  }

  window.Intraday = {
    /** opts.rerender:狀態改變時重畫清單;opts.refreshList:重取清單(回傳 Promise) */
    init: function (opts) { cfg = { rerender: opts.rerender || cfg.rerender, refreshList: opts.refreshList || null }; },
    update: update,
    enabled: function () { return !disabled; },
    isOpen: function (sym) { return !!open[sym]; },
    toggle: toggle,
    mini: function (sym, variant) { return nodeFor(sym, 'mini', variant, drawMini); },
    big: function (sym, variant) { return nodeFor(sym, 'big', variant, drawBig); },
    pause: function () { paused = true; },
    resume: function () { paused = false; poll(); }, // 回到前景立即補一次
    retry: retry
  };
})();
