// 庫存總覽:只做格式化與排版,數值全部來自伺服器(§12.5)。只用 textContent,不使用 innerHTML。
(function () {
  'use strict';
  var MINUS = '−';
  var chipsEl = document.getElementById('chips');
  var summaryEl = document.getElementById('summary');
  var contentEl = document.getElementById('content');
  var bannerEl = document.getElementById('banner');
  var errorEl = document.getElementById('error');
  var exportEl = document.getElementById('export');

  var portfolios = [];
  var settings = null;
  var selected = null;     // null = 全部,否則為整數陣列
  var view = null;
  var names = {};          // portfolioId -> 名稱
  var openRows = {};       // symbol -> 展開
  var sortKey = null, sortDir = -1;
  var csrf = null;

  function el(tag, text, cls) {
    var e = document.createElement(tag);
    if (text != null) e.textContent = text;
    if (cls) e.className = cls;
    return e;
  }

  // ---------- 格式化 ----------
  function group(n) { return Math.abs(n).toString().replace(/\B(?=(\d{3})+(?!\d))/g, ','); }
  function int(n, signed) {
    if (n == null) return '—';
    if (n === 0) return '0';
    var s = group(n);
    if (n < 0) return MINUS + s;
    return signed ? '+' + s : s;
  }
  function dec(n, signed) {
    if (n == null) return '—';
    var f = Math.abs(n).toFixed(2).replace(/\B(?=(\d{3})+(?!\d))/g, ',');
    if (n === 0) return f;
    if (n < 0) return MINUS + f;
    return signed ? '+' + f : f;
  }
  function pct(n, signed) { return n == null ? '—' : dec(n, signed) + '%'; }
  function cls(n) { return n == null || n === 0 ? 'pnl-flat' : n > 0 ? 'pnl-up' : 'pnl-down'; }
  function span(text, n) { return el('span', text, cls(n)); }
  function hhmmss(iso) {
    return new Intl.DateTimeFormat('zh-TW', { timeZone: 'Asia/Taipei', hour12: false, hour: '2-digit', minute: '2-digit', second: '2-digit' })
      .format(new Date(iso));
  }
  function statusTag(r) {
    var t = r.quoteStatus === 'reference' ? '參考價' : r.quoteStatus === 'prevclose' ? '昨收' : r.quoteStatus === 'delayed' ? '延遲' : null;
    return t ? el('span', t, 'tag') : null;
  }

  // ---------- 資料 ----------
  async function json(url) {
    var res = await fetch(url, { credentials: 'same-origin' });
    if (!res.ok) throw new Error(String(res.status));
    return res.json();
  }
  async function token() {
    if (!csrf) csrf = (await json('/api/csrf')).token;
    return csrf;
  }
  function query() { return selected ? 'portfolioIds=' + selected.join(',') : ''; }

  async function loadView() {
    try {
      view = await json('/api/holdings' + (query() ? '?' + query() : ''));
      errorEl.hidden = true;
      render();
    } catch (e) {
      errorEl.hidden = false; // 保留舊資料
    }
  }

  async function saveSelection() {
    if (!settings) return;
    settings.selectedPortfolioIds = selected;
    try {
      await fetch('/api/settings', {
        method: 'PUT', credentials: 'same-origin',
        headers: { 'Content-Type': 'application/json', 'X-CSRF-TOKEN': await token() },
        body: JSON.stringify(settings)
      });
    } catch (e) { /* 儲存失敗不影響檢視 */ }
  }

  function updateUrl() {
    var u = new URL(location.href);
    if (selected) u.searchParams.set('p', selected.join(',')); else u.searchParams.delete('p');
    history.replaceState(null, '', u);
    exportEl.href = '/api/export/holdings.csv' + (query() ? '?' + query() : '');
  }

  // ---------- 庫存選擇列(§12.3) ----------
  function renderChips() {
    chipsEl.replaceChildren();
    function chip(label, pressed, fn) {
      var b = el('button', label, 'chip');
      b.type = 'button'; b.setAttribute('aria-pressed', pressed ? 'true' : 'false');
      b.addEventListener('click', fn);
      chipsEl.append(b);
    }
    chip('全部', selected === null, function () { choose(null); });
    portfolios.forEach(function (p) {
      chip(p.name, selected !== null && selected.indexOf(p.id) >= 0, function () {
        var cur = selected ? selected.slice() : [];
        var i = cur.indexOf(p.id);
        if (i >= 0) cur.splice(i, 1); else cur.push(p.id);
        choose(cur.length ? cur : null); // 全部取消時自動回到「全部」
      });
    });
  }
  function choose(ids) {
    selected = ids;
    renderChips(); updateUrl(); saveSelection(); loadView();
  }

  // ---------- 畫面 ----------
  function render() {
    renderBanner(); renderSummary(); renderList();
  }

  function renderBanner() {
    var m = view.market;
    bannerEl.className = 'banner';
    if (m.stale) {
      bannerEl.textContent = '報價延遲,最後更新 ' + (m.lastFetchedAtUtc ? hhmmss(m.lastFetchedAtUtc) : '—');
      bannerEl.classList.add('warn'); bannerEl.hidden = false;
    } else if (m.state === 'closed') {
      bannerEl.textContent = '已收盤'; bannerEl.classList.add('info'); bannerEl.hidden = false;
    } else if (m.state === 'holiday') {
      bannerEl.textContent = '休市'; bannerEl.classList.add('info'); bannerEl.hidden = false;
    } else bannerEl.hidden = true;
  }

  function item(label, valueNode) {
    var d = el('div', null, 'item');
    d.append(el('div', label, 'label'), valueNode);
    valueNode.classList.add('value');
    return d;
  }

  function renderSummary() {
    var s = view.summary;
    summaryEl.replaceChildren();
    var pnl = el('div');
    pnl.append(span(int(s.unrealizedPnl, true), s.unrealizedPnl), document.createTextNode(' ('), span(pct(s.returnRatePct, true), s.returnRatePct), document.createTextNode(')'));
    summaryEl.append(
      item('市值', el('div', int(s.marketValue))),
      item('總成本', el('div', int(s.totalCost))),
      item('未實現損益', pnl),
      item('今日損益', (function () { var d = el('div'); d.append(span(int(s.todayPnl, true), s.todayPnl)); return d; })())
    );
    if (s.missingQuoteCount > 0) summaryEl.append(el('div', '另有 ' + s.missingQuoteCount + ' 檔缺價未計入', 'note'));
  }

  function sortedRows() {
    var rows = view.rows.slice();
    if (!sortKey) return rows; // 預設為伺服器順序(市值由大到小)
    return rows.sort(function (a, b) {
      var x = a[sortKey], y = b[sortKey];
      if (x == null && y == null) return 0;
      if (x == null) return 1;
      if (y == null) return -1;
      if (typeof x === 'string') return sortDir * x.localeCompare(y);
      return sortDir * (x - y);
    });
  }

  function renderList() {
    contentEl.replaceChildren();
    if (portfolios.length === 0) {
      var a = el('a', '建立第一個庫存'); a.href = '/Portfolios';
      var p = el('p'); p.append(a); contentEl.append(p); return;
    }
    if (view.rows.length === 0) {
      var b = el('a', '新增持股'); b.href = '/Portfolios';
      var q = el('p'); q.append(b); contentEl.append(q); return;
    }
    var rows = sortedRows();
    var hasFee = rows.some(function (r) { return r.estFee != null; });
    contentEl.append(table(rows, hasFee), cards(rows));
  }

  function sourcesList(r) {
    var ul = el('ul');
    r.sources.forEach(function (s) {
      ul.append(el('li', (names[s.portfolioId] || '庫存 ' + s.portfolioId) + ':' + int(s.shares) + ' 股,總成本 ' + int(s.totalCost)));
    });
    return ul;
  }

  function toggle(sym) { openRows[sym] = !openRows[sym]; renderList(); }

  function table(rows, hasFee) {
    var t = el('table', null, 'tbl');
    var heads = [['標的', 'symbol'], ['現價'], ['漲跌'], ['漲跌幅'], ['均價'], ['股數'], ['總成本'], ['市值', 'marketValue'],
      ['未實現損益', 'unrealizedPnl'], ['報酬率', 'returnRatePct'], ['今日損益', 'todayPnl']];
    if (hasFee) heads.push(['預估手續費'], ['預估證交稅']);
    heads.push(['']);
    var tr = el('tr');
    heads.forEach(function (h) {
      var th = el('th', h[0]);
      if (h[1]) {
        th.className = 'sortable';
        th.addEventListener('click', function () {
          if (sortKey === h[1]) sortDir = -sortDir; else { sortKey = h[1]; sortDir = h[1] === 'symbol' ? 1 : -1; }
          renderList();
        });
      }
      tr.append(th);
    });
    t.append(el('thead')); t.firstChild.append(tr);
    var body = el('tbody');
    rows.forEach(function (r) {
      var row = el('tr');
      var name = el('td', r.symbol + ' ' + r.name);
      var tag = statusTag(r); if (tag) name.append(tag);
      row.append(name,
        el('td', r.lastPrice == null ? '—' : dec(r.lastPrice)),
        cell(int2(r.change), r.change), cell(pct(r.changePct, true), r.changePct),
        el('td', dec(r.avgCost)), el('td', int(r.shares)), el('td', int(r.totalCost)),
        el('td', int(r.marketValue)),
        cell(int(r.unrealizedPnl, true), r.unrealizedPnl), cell(pct(r.returnRatePct, true), r.returnRatePct),
        cell(int(r.todayPnl, true), r.todayPnl));
      if (hasFee) row.append(el('td', int(r.estFee)), el('td', int(r.estTax)));
      var last = el('td');
      if (r.sources.length) {
        var b = el('button', openRows[r.symbol] ? '▾' : '▸', 'linkbtn'); b.type = 'button';
        b.setAttribute('aria-label', '展開各庫存明細'); b.addEventListener('click', function () { toggle(r.symbol); });
        last.append(b);
      }
      row.append(last);
      body.append(row);
      if (r.sources.length && openRows[r.symbol]) {
        var sub = el('tr', null, 'sub'); var td = el('td'); td.colSpan = heads.length;
        td.append(sourcesList(r)); sub.append(td); body.append(sub);
      }
    });
    t.append(body);
    return t;
  }
  function int2(n) { return n == null ? '—' : dec(n, true); }
  function cell(text, n) { var td = el('td'); td.append(span(text, n)); return td; }

  function cards(rows) {
    var wrap = el('div', null, 'cards');
    rows.forEach(function (r) {
      var c = el('div', null, 'card');
      var top = el('div', null, 'top');
      var title = el('strong', r.symbol + ' ' + r.name);
      var tag = statusTag(r); if (tag) title.append(tag);
      var price = el('div'); price.append(document.createTextNode(r.lastPrice == null ? '—' : dec(r.lastPrice) + ' '), span(pct(r.changePct, true), r.changePct));
      top.append(title, price);

      var grid = el('div', null, 'grid');
      function g(label, node) { var d = el('div'); d.append(document.createTextNode(label + ' '), node); grid.append(d); }
      g('股數', el('span', int(r.shares)));
      g('均價', el('span', dec(r.avgCost)));
      var u = el('span'); u.append(span(int(r.unrealizedPnl, true), r.unrealizedPnl), document.createTextNode(' ('), span(pct(r.returnRatePct, true), r.returnRatePct), document.createTextNode(')'));
      g('未實現', u);
      g('今日', span(int(r.todayPnl, true), r.todayPnl));
      c.append(top, grid);

      if (openRows[r.symbol]) {
        var more = el('div', null, 'more');
        more.append(el('div', '總成本 ' + int(r.totalCost)), el('div', '市值 ' + int(r.marketValue)));
        if (r.estFee != null) more.append(el('div', '預估手續費 ' + int(r.estFee) + ',預估證交稅 ' + int(r.estTax)));
        if (r.sources.length) more.append(sourcesList(r));
        c.append(more);
      }
      c.addEventListener('click', function () { toggle(r.symbol); });
      wrap.append(c);
    });
    return wrap;
  }

  // ---------- 啟動 ----------
  function parseUrlSelection(ids) {
    var raw = new URL(location.href).searchParams.get('p');
    if (!raw) return undefined;
    var valid = raw.split(',').map(Number).filter(function (n) { return ids.indexOf(n) >= 0; });
    return valid.length ? valid : undefined;
  }

  async function init() {
    document.getElementById('retry').addEventListener('click', loadView);
    document.addEventListener('visibilitychange', function () { if (!document.hidden) loadView(); });
    try {
      var all = await Promise.all([json('/api/portfolios'), json('/api/settings')]);
      portfolios = all[0]; settings = all[1];
    } catch (e) { errorEl.hidden = false; return; }
    portfolios.forEach(function (p) { names[p.id] = p.name; });
    document.body.setAttribute('data-scheme', String(settings.colorScheme));

    var ids = portfolios.map(function (p) { return p.id; });
    var fromUrl = parseUrlSelection(ids);          // 網址優先於已儲存的選擇
    var saved = (settings.selectedPortfolioIds || []).filter(function (n) { return ids.indexOf(n) >= 0; });
    selected = fromUrl || (saved.length ? saved : null);
    renderChips(); updateUrl();
    await loadView();
    // P4 以 SignalR 取代;在此之前頁面可見時每 5 秒更新一次(§9 的輪詢備援)
    setInterval(function () { if (!document.hidden) loadView(); }, 5000);
  }

  init();
})();
