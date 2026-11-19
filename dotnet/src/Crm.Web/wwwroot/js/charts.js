// Renders the server-provided chart data with the vendored Chart.js. Colours, font and grid come from the CSS variables of the
// design system (ui-redesign 6.2), so Light and Dark follow the page. Without JS (or if Chart.js fails to load) the data tables
// stay visible, so nothing depends on this script.
(function () {
  if (!window.Chart) return;
  var root = document.documentElement;
  var reduced = window.matchMedia && window.matchMedia('(prefers-reduced-motion: reduce)').matches;
  var charts = [];

  function token(name) {
    return window.getComputedStyle(root).getPropertyValue(name).trim();
  }

  function formatter(cfg) {
    if (cfg.valueKind === 'money') {
      return function (v) { return new Intl.NumberFormat(cfg.locale, { style: 'currency', currency: cfg.currency, notation: 'compact', minimumFractionDigits: 0, maximumFractionDigits: 1 }).format(v); };
    }
    if (cfg.valueKind === 'percent') {
      // The locale decides the spacing: en-GB "34%", da "34 %". Values are 0-100, Intl expects a fraction.
      return function (v) { return new Intl.NumberFormat(cfg.locale, { style: 'percent', maximumFractionDigits: 1 }).format(v / 100); };
    }
    return function (v) { return new Intl.NumberFormat(cfg.locale, { maximumFractionDigits: 0 }).format(v); };
  }

  function build(canvas, cfg) {
    var series = token('--chart-1');
    var ink = token('--chart-axis');
    var grid = token('--chart-grid');
    var surface = token('--surface');
    var outline = token('--chart-outline');
    var panel = token('--surface-panel');
    var border = token('--border-input');
    var horizontal = cfg.kind === 'hbar';
    var isLine = cfg.kind === 'line';
    var fmt = formatter(cfg);
    var narrow = window.matchMedia && window.matchMedia('(max-width: 639px)').matches;
    var labels = cfg.labels.map(function (l) { return narrow && /^\S+ \d{4}$/.test(l) ? l.split(' ')[0] : l; });
    var valueAxis = { beginAtZero: true, grid: { color: grid, lineWidth: 1, drawBorder: false }, border: { display: false }, ticks: { color: ink, callback: function (v) { return fmt(v); } } };
    if (cfg.valueKind === 'count') valueAxis.ticks.precision = 0; // whole numbers only: no 0.1 steps that round to repeated labels
    if (cfg.valueKind === 'percent') valueAxis.max = 100;
    var categoryAxis = { grid: { display: false }, border: { color: grid }, ticks: { color: ink, autoSkip: !horizontal, maxRotation: 0 } };
    var scales = horizontal ? { x: valueAxis, y: categoryAxis } : { y: valueAxis, x: categoryAxis };
    var dataset = isLine
      ? { label: cfg.seriesLabel, data: cfg.data, borderColor: series, backgroundColor: series, borderWidth: 2, pointRadius: 4, pointBackgroundColor: surface, pointBorderColor: series, pointBorderWidth: 2, tension: 0, fill: false }
      : { label: cfg.seriesLabel, data: cfg.data, backgroundColor: series, borderColor: outline, borderWidth: outline === 'transparent' || outline === '' ? 0 : 1, borderRadius: 0, maxBarThickness: 40, categoryPercentage: 0.7 };
    return new window.Chart(canvas, {
      type: isLine ? 'line' : 'bar',
      data: { labels: labels, datasets: [dataset] },
      options: {
        indexAxis: horizontal ? 'y' : 'x',
        responsive: true,
        maintainAspectRatio: false,
        animation: reduced ? false : { duration: 200 },
        scales: scales,
        plugins: {
          legend: { display: false },
          tooltip: {
            backgroundColor: panel, titleColor: ink, bodyColor: ink, borderColor: border, borderWidth: 1, cornerRadius: 0, displayColors: false, caretSize: 0,
            callbacks: { label: function (c) { return fmt(horizontal ? c.parsed.x : c.parsed.y); } }
          },
        },
      },
    });
  }

  function render() {
    charts.forEach(function (c) { c.destroy(); });
    charts = [];
    window.Chart.defaults.font.family = window.getComputedStyle(document.body).fontFamily;
    window.Chart.defaults.font.size = 14;
    window.Chart.defaults.color = token('--chart-axis');
    document.querySelectorAll('canvas[data-chart]').forEach(function (canvas) {
      var holder = document.getElementById(canvas.getAttribute('data-chart') + '-data');
      if (!holder) return;
      var cfg;
      try { cfg = JSON.parse(holder.textContent); } catch (e) { return; }
      charts.push(build(canvas, cfg));
      var fig = canvas.closest('figure');
      var details = fig && fig.querySelector('details.chart-table');
      if (details && charts.length) details.open = false; // charts are primary with JS on; the table stays one click away
    });
  }

  render();
  // The OS theme can change while the page is open; the manual toggle reloads the page anyway.
  if (window.matchMedia) {
    var mq = window.matchMedia('(prefers-color-scheme: dark)');
    if (mq.addEventListener) mq.addEventListener('change', render);
  }
})();
