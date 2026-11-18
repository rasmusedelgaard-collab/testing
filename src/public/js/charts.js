// Renders the server-provided chart data with the vendored Chart.js. Without JS (or if Chart.js fails to load)
// the data tables stay visible, so nothing depends on this script.
(function () {
  if (!window.Chart) return;
  var palette = ['#1D4ED8', '#B45309', '#047857', '#7E22CE', '#BE185D', '#0E7490'];
  var reduced = window.matchMedia && window.matchMedia('(prefers-reduced-motion: reduce)').matches;

  function formatter(cfg) {
    if (cfg.valueKind === 'money') {
      return function (v) { return new Intl.NumberFormat(cfg.locale, { style: 'currency', currency: cfg.currency, notation: 'compact', minimumFractionDigits: 0, maximumFractionDigits: 1 }).format(v); };
    }
    if (cfg.valueKind === 'percent') {
      return function (v) { return new Intl.NumberFormat(cfg.locale, { maximumFractionDigits: 1 }).format(v) + ' %'; };
    }
    return function (v) { return new Intl.NumberFormat(cfg.locale, { maximumFractionDigits: 0 }).format(v); };
  }

  document.querySelectorAll('canvas[data-chart]').forEach(function (canvas) {
    var holder = document.getElementById(canvas.getAttribute('data-chart') + '-data');
    if (!holder) return;
    var cfg;
    try { cfg = JSON.parse(holder.textContent); } catch (e) { return; }
    var horizontal = cfg.kind === 'hbar';
    var fmt = formatter(cfg);
    var valueAxis = { beginAtZero: true, ticks: { callback: function (v) { return fmt(v); } } };
    var scales = horizontal ? { x: valueAxis, y: { ticks: { autoSkip: false } } } : { y: valueAxis, x: { ticks: { autoSkip: true } } };
    new window.Chart(canvas, {
      type: cfg.kind === 'line' ? 'line' : 'bar',
      data: {
        labels: cfg.labels,
        datasets: [{ label: cfg.seriesLabel, data: cfg.data, backgroundColor: palette[0], borderColor: palette[0], borderWidth: cfg.kind === 'line' ? 2 : 0, pointRadius: cfg.kind === 'line' ? 4 : 0, tension: 0 }],
      },
      options: {
        indexAxis: horizontal ? 'y' : 'x',
        responsive: true,
        maintainAspectRatio: false,
        animation: reduced ? false : undefined,
        scales: scales,
        plugins: {
          legend: { display: false },
          tooltip: { callbacks: { label: function (c) { return fmt(horizontal ? c.parsed.x : c.parsed.y); } } },
        },
      },
    });
    var fig = canvas.closest('figure');
    var details = fig && fig.querySelector('details.chart-table');
    if (details) details.open = false; // charts are primary with JS on; the table stays one click away
  });
})();
