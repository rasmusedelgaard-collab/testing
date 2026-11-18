// Small-screen navigation toggle. Without JS the nav is simply shown stacked above the content.
(function () {
  var button = document.getElementById('menu-button');
  var nav = document.getElementById('main-nav');
  if (!button || !nav) return;
  function setOpen(open) {
    button.setAttribute('aria-expanded', open ? 'true' : 'false');
    nav.classList.toggle('open', open);
  }
  button.addEventListener('click', function () {
    setOpen(button.getAttribute('aria-expanded') !== 'true');
  });
  document.addEventListener('keydown', function (e) {
    if (e.key === 'Escape' && button.getAttribute('aria-expanded') === 'true') {
      setOpen(false);
      button.focus();
    }
  });
})();
