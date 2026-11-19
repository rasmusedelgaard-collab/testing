// Progressive enhancement only: every action in the CRM is a link or a form that POSTs. Nothing here is required.
(function () {
  var html = document.documentElement;
  var sidebar = document.getElementById('main-nav');
  var column = document.querySelector('.column');

  // ---- Sidebar: collapse toggle (desktop and rail) ----
  function icon(name) {
    return '<span class="icon-ring" aria-hidden="true"><svg class="icon" focusable="false"><use href="/img/ui/icons.svg#i-' + name + '"/></svg></span>';
  }
  var foot = document.getElementById('sidebar-foot');
  if (sidebar && foot) {
    var toggle = document.createElement('button');
    toggle.type = 'button';
    toggle.className = 'nav-item collapse-toggle';
    toggle.setAttribute('aria-controls', 'main-nav');
    var refresh = function () {
      var collapsed = html.getAttribute('data-sidebar') === 'collapsed';
      toggle.setAttribute('aria-expanded', collapsed ? 'false' : 'true');
      toggle.innerHTML = icon(collapsed ? 'chevron-right' : 'chevron-left') + '<span class="nav-label collapse-label">' + (collapsed ? 'Expand sidebar' : 'Collapse sidebar') + '</span>';
    };
    toggle.addEventListener('click', function () {
      var next = html.getAttribute('data-sidebar') === 'collapsed' ? 'expanded' : 'collapsed';
      html.setAttribute('data-sidebar', next);
      try { window.localStorage.setItem('crm.sidebar', next); } catch (e) { /* ignore */ }
      refresh();
    });
    refresh();
    foot.appendChild(toggle);
    // Rail tooltips can be dismissed with Esc (WCAG 1.4.13) until the pointer or focus moves on.
    document.addEventListener('keydown', function (e) {
      if (e.key === 'Escape') sidebar.classList.add('tip-off');
    });
    ['mouseover', 'focusin'].forEach(function (ev) {
      sidebar.addEventListener(ev, function (e) {
        if (e.target.closest && e.target.closest('.nav-item') !== sidebar.__last) {
          sidebar.__last = e.target.closest('.nav-item');
          sidebar.classList.remove('tip-off');
        }
      });
    });
  }

  // ---- Drawer (<640px) ----
  var button = document.getElementById('menu-button');
  var closeButton = document.getElementById('close-menu');
  function drawerOpen() { return sidebar && sidebar.classList.contains('open'); }
  function setDrawer(open) {
    if (!sidebar || !button) return;
    sidebar.classList.toggle('open', open);
    button.setAttribute('aria-expanded', open ? 'true' : 'false');
    if (column) { if (open) column.setAttribute('inert', ''); else column.removeAttribute('inert'); }
    if (open && closeButton) closeButton.focus();
    if (!open) button.focus();
  }
  if (button && sidebar) {
    button.addEventListener('click', function () { setDrawer(!drawerOpen()); });
    if (closeButton) closeButton.addEventListener('click', function () { setDrawer(false); });
    document.addEventListener('keydown', function (e) {
      if (!drawerOpen()) return;
      if (e.key === 'Escape') { setDrawer(false); return; }
      if (e.key === 'Tab') {
        var f = sidebar.querySelectorAll('a[href], button:not([disabled])');
        var visible = Array.prototype.filter.call(f, function (n) { return n.offsetParent !== null; });
        if (!visible.length) return;
        var first = visible[0], last = visible[visible.length - 1];
        if (e.shiftKey && document.activeElement === first) { e.preventDefault(); last.focus(); }
        else if (!e.shiftKey && document.activeElement === last) { e.preventDefault(); first.focus(); }
      }
    });
    document.addEventListener('click', function (e) {
      if (drawerOpen() && !sidebar.contains(e.target) && e.target !== button && !button.contains(e.target)) {
        sidebar.classList.remove('open');
        button.setAttribute('aria-expanded', 'false');
        if (column) column.removeAttribute('inert');
      }
    });
    window.addEventListener('resize', function () {
      if (drawerOpen() && window.innerWidth >= 640) setDrawer(false);
    });
  }

  // ---- Menus (<details>): one open at a time; outside click and Esc close ----
  var menus = document.querySelectorAll('details.menu');
  menus.forEach(function (m) {
    m.addEventListener('toggle', function () {
      if (m.open) menus.forEach(function (o) { if (o !== m) o.open = false; });
    });
  });
  document.addEventListener('click', function (e) {
    menus.forEach(function (m) { if (m.open && !m.contains(e.target)) m.open = false; });
  });
  document.addEventListener('keydown', function (e) {
    if (e.key !== 'Escape') return;
    menus.forEach(function (m) {
      if (m.open) { m.open = false; var s = m.querySelector('summary'); if (s) s.focus(); }
    });
  });

  // ---- Command bar: Ctrl/Cmd+K, plus a listbox of commands under the search field ----
  var q = document.getElementById('q');
  var hint = document.getElementById('kbd-hint');
  if (hint && /Mac|iPhone|iPad/.test(navigator.platform || '')) hint.textContent = '⌘ K';
  document.addEventListener('keydown', function (e) {
    if ((e.ctrlKey || e.metaKey) && !e.altKey && !e.shiftKey && (e.key === 'k' || e.key === 'K')) {
      e.preventDefault();
      if (q && q.offsetParent !== null) { q.focus(); q.select(); }
      else window.location.href = '/search';
    }
  });
  if (q && q.form) {
    var commands = [
      ['New account', '/accounts/new'], ['New contact', '/contacts/new'], ['New opportunity', '/opportunities/new'], ['Log activity', '/activities/new'],
      ['Go to Dashboard', '/'], ['Go to Pipeline', '/pipeline'], ['Go to Accounts', '/accounts'], ['Go to Opportunities', '/opportunities'],
      ['Go to Contacts', '/contacts'], ['Go to Activities', '/activities'], ['Go to Analytics', '/analytics']
    ];
    if (document.querySelector('a.nav-item[href="/settings/general"]')) commands.push(['Go to Settings', '/settings/general']);
    var list = document.createElement('ul');
    list.id = 'q-list';
    list.className = 'combo-list';
    list.setAttribute('role', 'listbox');
    list.setAttribute('aria-label', 'Search and commands');
    list.hidden = true;
    q.form.appendChild(list);
    q.setAttribute('role', 'combobox');
    q.setAttribute('aria-expanded', 'false');
    q.setAttribute('aria-controls', 'q-list');
    q.setAttribute('aria-autocomplete', 'list');
    var items = [];
    var active = -1;
    var setActive = function (i) {
      active = i;
      items.forEach(function (li, n) { li.setAttribute('aria-selected', n === i ? 'true' : 'false'); });
      if (i >= 0) q.setAttribute('aria-activedescendant', items[i].id); else q.removeAttribute('aria-activedescendant');
    };
    var close = function () { list.hidden = true; q.setAttribute('aria-expanded', 'false'); setActive(-1); };
    var render = function () {
      var text = q.value.trim();
      list.innerHTML = '';
      items = [];
      if (!text) { close(); return; }
      var opts = [['Search all records for “' + text + '”', '/search?q=' + encodeURIComponent(text)]];
      var lower = text.toLowerCase();
      commands.filter(function (c) { return c[0].toLowerCase().indexOf(lower) !== -1; }).slice(0, 6).forEach(function (c) { opts.push(c); });
      opts.forEach(function (o, n) {
        var li = document.createElement('li');
        li.id = 'q-opt-' + n;
        li.setAttribute('role', 'option');
        li.setAttribute('aria-selected', 'false');
        li.textContent = o[0];
        li.dataset.href = o[1];
        li.addEventListener('mousedown', function (e) { e.preventDefault(); window.location.href = o[1]; });
        list.appendChild(li);
        items.push(li);
      });
      list.hidden = false;
      q.setAttribute('aria-expanded', 'true');
      setActive(-1);
    };
    q.addEventListener('input', render);
    q.addEventListener('blur', close);
    q.addEventListener('keydown', function (e) {
      if (e.key === 'Escape') { if (!list.hidden) { close(); e.stopPropagation(); } return; }
      if (list.hidden) return;
      if (e.key === 'ArrowDown') { e.preventDefault(); setActive((active + 1) % items.length); }
      else if (e.key === 'ArrowUp') { e.preventDefault(); setActive((active - 1 + items.length) % items.length); }
      else if (e.key === 'Enter' && active > 0) { e.preventDefault(); window.location.href = items[active].dataset.href; }
    });
  }

  // ---- Focus management after a page load (WCAG 2.4.3 / 3.3.1) ----
  // failed submit -> error summary or first invalid field; successful action -> the flash message; board move -> the moved card.
  var target = document.getElementById('error-summary');
  if (!target) target = document.querySelector('[data-autofocus]');
  if (!target) target = document.querySelector('main [aria-invalid="true"]');
  if (!target) target = document.querySelector('main .flash');
  if (target) {
    if (!target.hasAttribute('tabindex') && !/^(A|INPUT|SELECT|TEXTAREA|BUTTON)$/.test(target.tagName)) target.setAttribute('tabindex', '-1');
    target.focus();
  }

  // ---- Typing a date in a range filter switches the linked period select to "Custom" ----
  document.querySelectorAll('input[data-custom-for]').forEach(function (input) {
    input.addEventListener('change', function () {
      var select = document.getElementById(input.getAttribute('data-custom-for'));
      if (select) select.value = 'custom';
    });
  });

  // ---- Filters on small screens: collapsed behind "Filters" (open by default for no-JS and wide screens) ----
  var narrow = window.matchMedia && window.matchMedia('(max-width: 639px)');
  document.querySelectorAll('details.filters-toggle').forEach(function (d) {
    if (narrow && narrow.matches && !d.hasAttribute('data-keep-open')) d.open = false;
  });

  // ---- Pipeline board: drag a card to another column (the move form stays the keyboard path) ----
  var board = document.querySelector('.board');
  if (board) {
    var dragged = null;
    board.querySelectorAll('.deal-card').forEach(function (card) {
      card.setAttribute('draggable', 'true');
      card.addEventListener('dragstart', function (e) {
        dragged = card;
        card.classList.add('dragging');
        if (e.dataTransfer) { e.dataTransfer.effectAllowed = 'move'; e.dataTransfer.setData('text/plain', card.id || ''); }
      });
      card.addEventListener('dragend', function () {
        card.classList.remove('dragging');
        board.querySelectorAll('.drop').forEach(function (c) { c.classList.remove('drop'); });
      });
    });
    board.querySelectorAll('.board-col').forEach(function (col) {
      col.addEventListener('dragover', function (e) { if (dragged) { e.preventDefault(); col.classList.add('drop'); } });
      col.addEventListener('dragleave', function (e) { if (!col.contains(e.relatedTarget)) col.classList.remove('drop'); });
      col.addEventListener('drop', function (e) {
        e.preventDefault();
        col.classList.remove('drop');
        if (!dragged) return;
        var form = dragged.querySelector('form.move-form');
        var select = form && form.querySelector('select');
        var stage = col.getAttribute('data-stage');
        if (!select || !stage || select.value === stage) return;
        var has = Array.prototype.some.call(select.options, function (o) { return o.value === stage; });
        if (!has) return;
        select.value = stage;
        if (form.requestSubmit) form.requestSubmit(); else form.submit();
      });
    });
  }
})();
