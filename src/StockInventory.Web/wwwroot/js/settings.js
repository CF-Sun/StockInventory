(function () {
  'use strict';
  var msg = document.getElementById('msg');
  var saved = document.getElementById('saved');
  var current = null; // 保留 selectedPortfolioIds:PUT 為整份覆寫,原值需原樣送回
  var csrf = null;

  function err(t) { msg.textContent = t || ''; msg.hidden = !t; }
  async function token() {
    if (!csrf) csrf = (await (await fetch('/api/csrf', { credentials: 'same-origin' })).json()).token;
    return csrf;
  }

  async function load() {
    var res = await fetch('/api/settings', { credentials: 'same-origin' });
    if (!res.ok) { err('載入失敗,請重試'); return; }
    current = await res.json();
    document.getElementById('c' + current.colorScheme).checked = true;
    document.getElementById('deduct').checked = current.deductFees;
    document.getElementById('fee-rate').value = String(current.feeRate);
    document.getElementById('fee-discount').value = String(current.feeDiscount);
  }

  document.getElementById('form').addEventListener('submit', async function (ev) {
    ev.preventDefault();
    err(''); saved.hidden = true;
    var body = {
      colorScheme: Number(document.querySelector('input[name=color]:checked').value),
      deductFees: document.getElementById('deduct').checked,
      feeRate: Number(document.getElementById('fee-rate').value),
      feeDiscount: Number(document.getElementById('fee-discount').value),
      selectedPortfolioIds: current ? current.selectedPortfolioIds : null
    };
    var res = await fetch('/api/settings', {
      method: 'PUT', credentials: 'same-origin',
      headers: { 'Content-Type': 'application/json', 'X-CSRF-TOKEN': await token() },
      body: JSON.stringify(body)
    });
    if (res.ok) { current = await res.json(); saved.hidden = false; return; }
    var p = {};
    try { p = await res.json(); } catch (e) { /* 非 JSON */ }
    err((p.errors ? Object.values(p.errors).flat().join(';') : '') || p.title || '儲存失敗');
  });

  load();
})();
