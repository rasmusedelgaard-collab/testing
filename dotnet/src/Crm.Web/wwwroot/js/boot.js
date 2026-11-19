// Runs in <head> before first paint: marks JS as available and applies the saved sidebar state, so there is no layout jump.
document.documentElement.classList.replace('no-js', 'js');
try {
  var saved = window.localStorage.getItem('crm.sidebar');
  if (saved === 'collapsed' || (saved !== 'expanded' && window.innerWidth < 1280)) {
    document.documentElement.setAttribute('data-sidebar', 'collapsed');
  }
} catch (e) { /* storage blocked: the sidebar simply stays expanded */ }
