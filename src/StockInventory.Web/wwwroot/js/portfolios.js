// 庫存管理:只用 textContent 組畫面;除「均價預覽」外不做任何計算(§12.3)。
(function () {
  'use strict';
  var listEl = document.getElementById('list');
  var msgEl = document.getElementById('msg');
  var noticeEl = document.getElementById('notice');
  var csrf = null;
  var portfolios = [];
  var expanded = {};      // portfolioId -> true
  var holdingsCache = {}; // portfolioId -> 持股陣列

  function showError(t) { msgEl.textContent = t || ''; msgEl.hidden = !t; }
  function showNotice(t) { noticeEl.textContent = t || ''; noticeEl.hidden = !t; }
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
    wrap.append(shotEntry(p));
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

  // ===== FR-25 從截圖新增:辨識結果須經核對才寫入;只用 textContent;除均價預覽外不做計算(§12.3) =====
  var T = {
    entry: '從截圖新增',
    consent: '我了解此圖片將傳送至第三方 AI 辨識服務處理,辨識後不會保存於本站。我已遮蔽帳號與姓名等不需要的資訊。',
    start: '開始辨識', cancel: '取消', remove: '移除',
    busy: '辨識中,請稍候…',
    title: '請核對辨識結果',
    notice: '辨識結果可能有誤,請對照截圖逐列確認代號、股數與總成本。',
    costMissing: '圖片上找不到總成本,請自行填入',
    sharesMissing: '圖片上找不到股數,請自行填入',
    costZero: '總成本為 0,請確認',
    oddLot: '零股市值以整股報價估算',
    notFound: '找不到此標的,請搜尋指定或移除此列',
    inactive: '此標的已不在名單,無法新增',
    duplicate: '圖中有重複代號,請保留一列或自行合併數值',
    invalid: '股數或總成本不正確,請修正',
    nothing: '無法在圖片中找到持股資料,請確認是庫存截圖且內容清晰',
    badFile: '僅支援 JPG、PNG、WebP,且大小不超過 5 MB',
    rate: '操作過於頻繁,請稍後再試',
    upstream: '辨識服務暫時無法使用,請稍後再試或手動新增',
    timeout: '辨識逾時,請重試',
    skip: '略過', update: '更新為辨識值'
  };
  var TAG = { OK: '可新增', EXISTS: '已存在', NOT_FOUND: '找不到', INACTIVE: '已停用', INVALID: '資料不正確', DUPLICATE_IN_IMAGE: '代號重複' };
  var ALLOWED_TYPES = ['image/jpeg', 'image/png', 'image/webp'];
  var MAX_IMAGE_BYTES = 5 * 1024 * 1024;
  var shots = {}; // portfolioId -> 狀態

  function shotState(p) {
    if (!shots[p.id]) shots[p.id] = { open: false, consent: false, file: null, previewUrl: null, busy: false, rows: null, remaining: 0, max: 0, confirming: false };
    return shots[p.id];
  }
  function clearPreview(st) {
    if (st.previewUrl) { URL.revokeObjectURL(st.previewUrl); st.previewUrl = null; }
  }
  function resetShot(st) {
    clearPreview(st);
    st.consent = false; st.file = null; st.rows = null; st.busy = false; st.confirming = false;
  }
  function validShares(t) { return /^\d{1,10}$/.test(t) && Number(t) >= 1 && Number(t) <= 1000000000; }
  function validCost(t) { return /^\d{1,12}$/.test(t) && Number(t) <= 999999999999; }
  function existingOf(p, symbol) {
    var hs = holdingsCache[p.id] || [];
    for (var i = 0; i < hs.length; i++) if (hs[i].symbol === symbol) return hs[i];
    return null;
  }
  // 與伺服器相同的狀態優先序:NOT_FOUND > INACTIVE > DUPLICATE_IN_IMAGE > INVALID > EXISTS > OK(最終以 API-14 為準)
  function evalRow(p, st, r) {
    if (!r.symbol || !r.known) return r.inactive ? 'INACTIVE' : 'NOT_FOUND';
    var dup = st.rows.some(function (o) { return o !== r && !o.removed && o.known && o.symbol === r.symbol; });
    if (dup) return 'DUPLICATE_IN_IMAGE';
    if (!validShares(r.sharesText) || !validCost(r.costText)) return 'INVALID';
    return existingOf(p, r.symbol) ? 'EXISTS' : 'OK';
  }
  function rowWarnings(r) {
    var w = [];
    if (r.sharesText === '') w.push(T.sharesMissing);
    if (r.costText === '') w.push(T.costMissing);
    if (validCost(r.costText) && Number(r.costText) === 0) w.push(T.costZero);
    if (validShares(r.sharesText) && Number(r.sharesText) < 1000) w.push(T.oddLot);
    return w;
  }
  function toRow(it) {
    var known = it.status !== 'NOT_FOUND' && it.status !== 'INACTIVE';
    return {
      symbol: it.symbol, name: it.name, known: known, inactive: it.status === 'INACTIVE',
      sharesText: it.shares == null ? '' : String(it.shares),
      costText: it.totalCost == null ? '' : String(it.totalCost),
      include: it.status === 'OK', onExists: 'skip', removed: false, ui: null
    };
  }

  function shotEntry(p) {
    var st = shotState(p);
    var box = el('div'); box.className = 'shot';
    box.append(btn(T.entry, function () {
      showNotice('');
      st.open = !st.open;
      if (!st.open) resetShot(st);
      render();
    }));
    if (st.open) box.append(st.rows ? reviewView(p, st) : uploadView(p, st));
    return box;
  }

  function uploadView(p, st) {
    var panel = el('div'); panel.className = 'shot-panel';
    var c = el('div'); c.className = 'check';
    var cb = el('input', null, { type: 'checkbox', id: 'shot-consent-' + p.id });
    cb.checked = st.consent; cb.disabled = st.busy;
    var lb = el('label', T.consent, { 'for': 'shot-consent-' + p.id });
    c.append(cb, lb);
    var file = el('input', null, { type: 'file', accept: ALLOWED_TYPES.join(','), 'aria-label': T.entry });
    file.disabled = !st.consent || st.busy;
    var name = el('div', st.file ? st.file.name : '');
    var preview = el('img', null, { alt: T.entry });
    preview.className = 'shot-preview';
    if (st.previewUrl) preview.src = st.previewUrl; else preview.hidden = true;
    var status = el('div', st.busy ? T.busy : '', { role: 'status' });
    var go = btn(T.start, function () { recognize(p, st); });
    go.disabled = !st.consent || !st.file || st.busy;
    var cancel = btn(T.cancel, function () { showError(''); resetShot(st); st.open = false; render(); });
    cancel.disabled = st.busy;

    cb.addEventListener('change', function () { st.consent = cb.checked; render(); });
    file.addEventListener('change', function () {
      var f = file.files && file.files[0];
      showError('');
      if (!f) return;
      if (ALLOWED_TYPES.indexOf(f.type) < 0 || f.size > MAX_IMAGE_BYTES || f.size === 0) { file.value = ''; showError(T.badFile); return; }
      clearPreview(st);
      st.file = f;
      st.previewUrl = URL.createObjectURL(f);
      render();
    });
    panel.append(c, file, name, preview, status, go, cancel);
    return panel;
  }

  async function recognizeError(res) {
    var code = '';
    try { code = (await res.json()).code || ''; } catch (e) { /* 非 JSON */ }
    if (code === 'NOTHING_RECOGNIZED') return T.nothing;
    if (code === 'PAYLOAD_TOO_LARGE' || code === 'UNSUPPORTED_MEDIA') return T.badFile;
    if (code === 'RATE_LIMITED') return T.rate;
    if (code === 'UPSTREAM_TIMEOUT') return T.timeout;
    return T.upstream;
  }

  async function recognize(p, st) {
    if (st.busy || !st.consent || !st.file) return;
    showError(''); showNotice('');
    st.busy = true; render();
    try {
      var fd = new FormData();
      fd.append('consent', 'true'); // 先於圖片,伺服器可先驗證同意
      fd.append('image', st.file);
      var res;
      try {
        res = await fetch('/api/portfolios/' + p.id + '/holdings/recognize', {
          method: 'POST', credentials: 'same-origin', headers: { 'X-CSRF-TOKEN': await token() }, body: fd
        });
      } catch (e) { throw new Error(T.upstream); }
      if (!res.ok) throw new Error(await recognizeError(res));
      var data = await res.json();
      st.rows = data.items.map(toRow);
      st.remaining = data.remainingSlots;
      st.max = data.remainingSlots + (holdingsCache[p.id] || []).length;
      st.file = null; clearPreview(st); // 完成後釋放預覽
    } catch (e) {
      showError(e.message || T.upstream);
    } finally {
      st.busy = false;
      render();
    }
  }

  function reviewView(p, st) {
    var panel = el('div'); panel.className = 'shot-panel';
    panel.append(el('h3', T.title), el('p', T.notice));
    var head = el('div'); head.className = 'shot-row shot-head';
    ['', '代號', '股數', '總成本', '均價', '狀態', ''].forEach(function (t) { head.append(el('div', t)); });
    var list = el('div');
    var summary = el('div', null, { role: 'status' });
    var limitMsg = el('div'); limitMsg.className = 'err';
    var okBtn = btn('', function () { submit(p, st); });

    function updateRow(r) {
      if (r.removed || !r.ui) return;
      var s = evalRow(p, st, r);
      r.status = s;
      // 勾選區
      r.ui.pick.replaceChildren();
      if (s === 'OK') {
        var cb = el('input', null, { type: 'checkbox', 'aria-label': r.symbol + ' ' + T.entry });
        cb.checked = r.include;
        cb.addEventListener('change', function () { r.include = cb.checked; updateSummary(); });
        r.ui.pick.append(cb);
      } else if (s === 'EXISTS') {
        ['skip', 'update'].forEach(function (v) {
          var lab = el('label'); lab.className = 'radio';
          var rd = el('input', null, { type: 'radio', name: 'ex-' + r.symbol });
          rd.checked = r.onExists === v;
          rd.addEventListener('change', function () { r.onExists = v; updateSummary(); });
          lab.append(rd, document.createTextNode(v === 'skip' ? T.skip : T.update));
          r.ui.pick.append(lab);
        });
      }
      // 均價預覽:唯一前端運算(僅顯示)
      r.ui.avg.textContent = (validShares(r.sharesText) && validCost(r.costText))
        ? (Number(r.costText) / Number(r.sharesText)).toFixed(2) : '';
      // 狀態:文字標籤,不只靠顏色
      r.ui.status.replaceChildren();
      var tag = el('span', TAG[s]); tag.className = 'tag st-' + s.toLowerCase();
      r.ui.status.append(tag);
      var msg = null;
      if (s === 'NOT_FOUND') msg = T.notFound;
      else if (s === 'INACTIVE') msg = T.inactive;
      else if (s === 'DUPLICATE_IN_IMAGE') msg = T.duplicate;
      else if (s === 'INVALID') msg = T.invalid;
      else if (s === 'EXISTS') {
        var h = existingOf(p, r.symbol);
        msg = '此庫存已有 ' + r.symbol + ' ' + (h ? h.name : '') + '(股數 ' + fmt(h ? h.shares : 0) + '、總成本 ' + fmt(h ? h.totalCost : 0) + ')';
      }
      if (msg) r.ui.status.append(el('div', msg));
      rowWarnings(r).forEach(function (w) { var d = el('div', w); d.className = 'warn'; r.ui.status.append(d); });
    }

    function counts() {
      var add = 0, upd = 0, skip = 0;
      st.rows.forEach(function (r) {
        if (r.removed) return;
        var s = r.status;
        if (s === 'OK') { if (r.include) add++; else skip++; }
        else if (s === 'EXISTS') { if (r.onExists === 'update') upd++; else skip++; }
      });
      return { add: add, upd: upd, skip: skip };
    }

    function updateSummary() {
      var n = counts();
      summary.textContent = '將新增 ' + n.add + ' 檔、更新 ' + n.upd + ' 檔、略過 ' + n.skip + ' 檔;此庫存還可新增 ' + st.remaining + ' 檔';
      var over = n.add > st.remaining;
      limitMsg.textContent = over ? '此庫存最多 ' + st.max + ' 檔,目前只能再新增 ' + st.remaining + ' 檔' : '';
      okBtn.textContent = '確認新增 ' + (n.add + n.upd) + ' 檔';
      okBtn.disabled = st.confirming || over || (n.add + n.upd) === 0;
    }

    function updateAll() { st.rows.forEach(updateRow); updateSummary(); }

    function numberInput(r, key, label) {
      var inp = el('input', null, { type: 'text', inputmode: 'numeric', autocomplete: 'off', 'aria-label': label });
      inp.value = r[key];
      inp.addEventListener('input', function () { r[key] = inp.value.trim(); updateAll(); });
      return inp;
    }

    function searchBox(r) {
      var wrap = el('div');
      var q = el('input', null, { type: 'text', placeholder: '搜尋代碼或名稱', autocomplete: 'off', 'aria-label': '搜尋代碼或名稱' });
      var res = el('div');
      var timer = null;
      q.addEventListener('input', function () {
        clearTimeout(timer);
        timer = setTimeout(async function () {
          res.replaceChildren();
          var term = q.value.trim();
          if (!term) return;
          var r2 = await fetch('/api/instruments/search?q=' + encodeURIComponent(term), { credentials: 'same-origin' });
          if (!r2.ok) return;
          (await r2.json()).forEach(function (i) {
            res.append(btn(i.symbol + ' ' + i.name, function () {
              r.symbol = i.symbol; r.name = i.name; r.known = true; r.inactive = false; r.include = false;
              renderRows();
            }));
          });
        }, 250);
      });
      wrap.append(q, res);
      return wrap;
    }

    function renderRows() {
      list.replaceChildren();
      st.rows.forEach(function (r) {
        if (r.removed) return;
        var row = el('div'); row.className = 'shot-row';
        var pick = el('div'); pick.className = 'c-pick';
        var sym = el('div'); sym.className = 'c-sym';
        sym.append(el('strong', r.symbol ? r.symbol : '—'), el('div', r.name || ''));
        if (!r.known) sym.append(searchBox(r));
        var sh = el('div'); sh.className = 'c-shares';
        sh.append(el('span', '股數', { 'class': 'lbl' }), numberInput(r, 'sharesText', '股數'));
        var co = el('div'); co.className = 'c-cost';
        co.append(el('span', '總成本', { 'class': 'lbl' }), numberInput(r, 'costText', '總成本'));
        var avg = el('div'); avg.className = 'c-avg';
        var status = el('div'); status.className = 'c-status';
        var del = btn(T.remove, function () { r.removed = true; updateAll(); renderRows(); });
        row.append(pick, sym, sh, co, avg, status, del);
        r.ui = { pick: pick, avg: avg, status: status };
        list.append(row);
      });
      updateAll();
    }

    var cancel = btn(T.cancel, function () { showError(''); resetShot(st); st.open = false; render(); });
    panel.append(head, list, summary, limitMsg, okBtn, cancel);
    renderRows();
    return panel;
  }

  async function submit(p, st) {
    if (st.confirming) return;
    var items = [], skipped = 0;
    st.rows.forEach(function (r) {
      if (r.removed) return;
      if (r.status === 'OK' && r.include) items.push({ symbol: r.symbol, shares: Number(r.sharesText), totalCost: Number(r.costText), onExists: 'skip' });
      else if (r.status === 'EXISTS' && r.onExists === 'update') items.push({ symbol: r.symbol, shares: Number(r.sharesText), totalCost: Number(r.costText), onExists: 'update' });
      else if (r.status === 'OK' || r.status === 'EXISTS') skipped++;
    });
    if (items.length === 0) return;
    showError(''); showNotice('');
    st.confirming = true;
    try {
      var r = await call('POST', '/api/portfolios/' + p.id + '/holdings/batch', { items: items });
      showNotice('已新增 ' + r.created.length + ' 檔、更新 ' + r.updated.length + ' 檔、略過 ' + (skipped + r.skipped.length) + ' 檔');
      resetShot(st); st.open = false;
      await refresh();
    } catch (e) {
      showError(e.message);
      st.confirming = false;
      render();
    }
  }

  document.getElementById('create-form').addEventListener('submit', function (ev) {
    ev.preventDefault();
    var input = document.getElementById('new-name');
    act(async function () { await call('POST', '/api/portfolios', { name: input.value }); input.value = ''; await refresh(); });
  });

  loadAll();
})();
