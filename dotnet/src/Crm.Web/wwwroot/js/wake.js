// Polls /readyz every 5 s while the database resumes; reloads GET pages once it is up. Gives up after 90 s.
(function () {
  var root = document.getElementById('wake');
  if (!root) return;
  var isPost = root.getAttribute('data-post') === 'true';
  var status = document.getElementById('wake-status');
  var giveUp = document.getElementById('wake-giveup');
  var retry = document.getElementById('wake-retry');
  var started = Date.now();
  var announced = 0;
  var back = document.getElementById('wake-back');
  if (retry) retry.setAttribute('href', window.location.href);
  // With JS the button returns to the form (the browser usually restores the fields); without JS it stays a link to the dashboard.
  if (back) {
    back.textContent = 'Go back';
    back.addEventListener('click', function (e) { e.preventDefault(); history.back(); });
  }
  function tick() {
    var seconds = Math.floor((Date.now() - started) / 1000);
    var step = Math.floor(seconds / 20) * 20;
    if (step > announced) {
      announced = step;
      status.textContent = 'Still waking up. About ' + step + ' seconds so far.';
    }
    if (seconds >= 90) {
      status.hidden = true;
      giveUp.hidden = false;
      giveUp.focus();
      if (retry) retry.textContent = 'Try again';
      return;
    }
    fetch('/readyz', { cache: 'no-store' }).then(function (r) {
      if (r.ok && !isPost) window.location.reload();
      else setTimeout(tick, 5000);
    }).catch(function () { setTimeout(tick, 5000); });
  }
  setTimeout(tick, 5000);
})();
