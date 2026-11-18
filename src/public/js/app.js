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

// Focus management after a page load (WCAG 2.4.3 / 3.3.1): failed submit -> error summary or first invalid field;
// successful action -> the flash message (so it is announced and keyboard users start next to the result).
(function () {
  var target = document.getElementById('error-summary');
  if (!target) target = document.querySelector('[data-autofocus]');
  if (!target) target = document.querySelector('main [aria-invalid="true"]');
  if (!target) target = document.querySelector('main .flash');
  if (!target) return;
  if (!target.hasAttribute('tabindex')) target.setAttribute('tabindex', '-1');
  target.focus();
})();
