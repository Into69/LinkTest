// Loaded in <head> so the saved theme applies before the page draws (no flash of the wrong theme).
// 'system' (default) follows the browser; 'light' / 'dark' force one via <html data-theme>.
(function () {
  let t = null;
  try { t = localStorage.getItem('linktest.theme'); } catch { }
  if (t === 'light' || t === 'dark') document.documentElement.dataset.theme = t;
})();
