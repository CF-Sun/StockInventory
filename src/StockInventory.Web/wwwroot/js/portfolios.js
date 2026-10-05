// 庫存管理:只用 textContent 組畫面;除「均價預覽」外不做任何計算(§12.3)。
(function () {
  'use strict';
  var listEl = document.getElementById('list');
  var msgEl = document.getElementById('msg');
  var csrf = null;
  var portfolios = [];
  var expanded = {};      // portfolioId -> true
  var holdingsCache = {}; // portfolioId -> 持股陣列

  function showError(t) { msgEl.textContent = t || ''; msgEl.hidden = !t; }
  function el(tag, text, attrs) {
    var e = document.createElement(tag);
    if (text != null) e.textContent = text;
    if (attrs) Object.keys(attrs).forEach(function (k) { e.setAttribute(k, attrs[k]); });
    return e;
  }
  function btn(label, fn, cls) {
    var b = el('button', label, { type: 'button' });
    if (cls) b.className = cls;
    b.addEventListener('click', fn);
    return b;
  }
  function fmt(n) { return Number(n).toLocaleString('en-US'); }

  async function token() {
    if (!csrf) csrf = (await (await fetch('/api/csrf', { credentials: 'same-origin' })).json()).token;
    return csrf;
  }
  async function call(method, url, body) {
    var res = await fetch(url, {
      method: method, credentials: 'same-origin',
      headers: { 'Content-Type': 'application/json', 'X-CSRF-TOKEN': await token() },
      body: body ? JSON.stringify(body) : undefined
    });
    if (res.ok) return res.status === 204 ? null : res.json();
    var p = {};
    try { p = await res.json(); } catch (e) { /* 非 JSON */ }
    var details = p.errors ? Object.values(p.errors).flat().join(';') : '';
    throw new Error(details || p.title || '操作失敗');
  }
  async function act(fn) {
    showError('');
    try { await fn(); } catch (e) { showError(e.message); }
  }

  async function loadAll() {
    var res = await fetch('/api/portfolios', { credentials: 'same-origin' });
    if (!res.ok) { showError('載入失敗,請重試'); return; }
    portfolios = await res.json();
    await Promise.all(portfolios.filter(function (p) { return expanded[p.id]; }).map(loadHoldings));
    render();
  }
  async function loadHoldings(p) {
    var res = await fetch('/api/portfolios/' + p.id + '/holdings', { credentials: 'same-origin' });
    holdingsCache[p.id] = res.ok ? await res.json() : [];
  }
  async function refresh() { await loadAll(); }

  function render() {
    listEl.replaceChildren();
    if (portfolios.length === 0) {
      listEl.append(el('p', '建立第一個庫存'));
      return;
    }
    portfolios.forEach(function (p, i) { listEl.append(card(p, i)); });
  }

  function card(p, i) {
    var c = el('div'); c.className = 'card';
    c.append(el('strong', p.name), el('span', '(' + p.holdingCount + ' 檔)'));
    var row = el('div'); row.className = 'row';
    row.append(
      btn(expanded[p.id] ? '收合' : '持股', function () {
        act(async function () {
          expanded[p.id] = !expanded[p.id];
          if (expanded[p.id]) await loadHoldings(p);
          render();
        });
      }),
      btn('改名', function () {
        var name = prompt('新的庫存名稱', p.name);
        if (name == null) return;
        act(async function () { await call('PUT', '/api/portfolios/' + p.id, { name: name }); await refresh(); });
      }),
      btn('上移', function () { move(i, -1); }),
      btn('下移', function () { move(i, 1); }),
      btn('刪除', function () {
        if (!confirm('刪除「' + p.name + '」將一併刪除其中所有持股,且無法復原。確定刪除?')) return;
        act(async function () { await call('DELETE', '/api/portfolios/' + p.id); delete expanded[p.id]; await refresh(); });
      })
    );
    c.append(row);
    if (expanded[p.id]) c.append(holdingsView(p));
    return c;
  }

  function move(i, d) {
    var j = i + d;
    if (j < 0 || j >= portfolios.length) return;
    var ids = portfolios.map(function (p) { return p.id; });
    var t = ids[i]; ids[i] = ids[j]; ids[j] = t;
    act(async function () { await call('PUT', '/api/portfolios/order', { ids: ids }); await refresh(); });
  }

  function holdingsView(p) {
    var wrap = el('div');
    var hs = holdingsCache[p.id] || [];
    if (hs.length === 0) wrap.append(el('p', '新增持股'));
    hs.forEach(function (h) {
      var r = el('div'); r.className = 'holding';
      r.append(el('div', h.symbol + ' ' + h.name),
        el('div', '股數 ' + fmt(h.shares) + ' · 總成本 ' + fmt(h.totalCost)));
      r.append(
        btn('編輯', function () { editHolding(p, h); }),
        btn('刪除', function () {
          if (!confirm('刪除 ' + h.symbol + ' ' + h.name + '?刪除後無法復原。')) return;
          act(async function () { await call('DELETE', '/api/holdings/' + h.holdingId); await refresh(); });
        })
      );
      wrap.append(r);
    });
    wrap.append(addForm(p));
    return wrap;
  }

  function editHolding(p, h) {
    var cost = prompt(h.symbol + ' 總成本(整數,≥ 0)', h.totalCost);
    if (cost == null) return;
    var shares = prompt(h.symbol + ' 股數(整數,≥ 1)', h.shares);
    if (shares == null) return;
    act(async function () {
      await call('PUT', '/api/holdings/' + h.holdingId, { totalCost: Number(cost), shares: Number(shares) });
      await refresh();
    });
  }

  function addForm(p) {
    var f = el('form'); f.className = 'add';
    var q = el('input', null, { type: 'text', placeholder: '搜尋代碼或名稱', autocomplete: 'off' });
    var results = el('div');
    var picked = null, pickedEl = el('div');
    var cost = el('input', null, { type: 'text', inputmode: 'numeric', placeholder: '總成本' });
    var shares = el('input', null, { type: 'text', inputmode: 'numeric', placeholder: '股數' });
    var preview = el('div');
    var timer = null;

    function updatePreview() {
      var c = Number(cost.value), s = Number(shares.value);
      // 唯一允許前端計算之處:僅為預覽,最終以伺服器為準
      preview.textContent = (Number.isInteger(c) && Number.isInteger(s) && s >= 1 && c >= 0 && cost.value !== '')
        ? '均價 = ' + (c / s).toFixed(2) : '';
    }
    cost.addEventListener('input', updatePreview);
    shares.addEventListener('input', updatePreview);

    q.addEventListener('input', function () {
      clearTimeout(timer);
      timer = setTimeout(async function () {
        results.replaceChildren();
        var term = q.value.trim();
        if (!term) return;
        var res = await fetch('/api/instruments/search?q=' + encodeURIComponent(term), { credentials: 'same-origin' });
        if (!res.ok) return;
        (await res.json()).forEach(function (i) {
          results.append(btn(i.symbol + ' ' + i.name, function () {
            picked = i; pickedEl.textContent = '已選:' + i.symbol + ' ' + i.name; results.replaceChildren();
          }));
        });
      }, 250);
    });

    f.addEventListener('submit', function (ev) {
      ev.preventDefault();
      if (!picked) { showError('請先搜尋並選擇標的'); return; }
      act(async function () {
        await call('POST', '/api/portfolios/' + p.id + '/holdings',
          { symbol: picked.symbol, totalCost: Number(cost.value), shares: Number(shares.value) });
        await refresh();
      });
    });
    f.append(q, results, pickedEl, cost, shares, preview, el('button', '新增持股', { type: 'submit' }));
    return f;
  }

  document.getElementById('create-form').addEventListener('submit', function (ev) {
    ev.preventDefault();
    var input = document.getElementById('new-name');
    act(async function () { await call('POST', '/api/portfolios', { name: input.value }); input.value = ''; await refresh(); });
  });

  loadAll();
})();
