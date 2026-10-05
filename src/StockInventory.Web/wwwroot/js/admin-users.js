// 使用者管理:只用 textContent 組畫面(不使用 innerHTML),數字與判斷皆來自伺服器。
(function () {
  'use strict';
  var usersEl = document.getElementById('users');
  var msgEl = document.getElementById('msg');
  var tempEl = document.getElementById('temp');
  var csrf = null;

  function showError(text) { msgEl.textContent = text; msgEl.hidden = !text; }

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
    var problem = {};
    try { problem = await res.json(); } catch (e) { /* 非 JSON */ }
    var details = problem.errors ? Object.values(problem.errors).flat().join(';') : '';
    throw new Error(details || problem.title || '操作失敗');
  }

  function button(label, onClick) {
    var b = document.createElement('button');
    b.type = 'button'; b.textContent = label; b.addEventListener('click', onClick);
    return b;
  }

  async function act(fn) {
    showError('');
    try { await fn(); await load(); } catch (e) { showError(e.message); }
  }

  function render(list) {
    usersEl.replaceChildren();
    list.forEach(function (u) {
      var card = document.createElement('div');
      card.className = 'card';
      var title = document.createElement('strong');
      title.textContent = u.userName + (u.displayName ? '(' + u.displayName + ')' : '');
      var info = document.createElement('div');
      info.textContent = u.email + ' · ' + u.roles.join('、') + ' · ' +
        (u.isActive ? '啟用中' : '已停用') + ' · ' + (u.twoFactorEnabled ? '已設定雙重驗證' : '未設定雙重驗證');
      card.append(title, info);

      card.append(
        button(u.isActive ? '停用' : '啟用', function () {
          act(function () { return call('POST', '/api/admin/users/' + u.id + (u.isActive ? '/deactivate' : '/activate')); });
        }),
        button('重設密碼', function () {
          if (!confirm('重設「' + u.userName + '」的密碼?')) return;
          act(async function () {
            var r = await call('POST', '/api/admin/users/' + u.id + '/reset-password');
            document.getElementById('temp-pw').textContent = r.temporaryPassword;
            tempEl.hidden = false;
          });
        }),
        button('重設雙重驗證', function () {
          if (!confirm('重設「' + u.userName + '」的雙重驗證與還原碼?')) return;
          act(function () { return call('POST', '/api/admin/users/' + u.id + '/reset-2fa'); });
        })
      );
      usersEl.append(card);
    });
  }

  async function load() {
    var res = await fetch('/api/admin/users', { credentials: 'same-origin' });
    if (!res.ok) { showError('載入失敗,請重試'); return; }
    render(await res.json());
  }

  document.getElementById('create-form').addEventListener('submit', function (ev) {
    ev.preventDefault();
    act(async function () {
      await call('POST', '/api/admin/users', {
        userName: document.getElementById('c-user').value,
        email: document.getElementById('c-email').value,
        displayName: document.getElementById('c-name').value,
        initialPassword: document.getElementById('c-pw').value
      });
      ev.target.reset();
    });
  });

  load();
})();
