// Native head only. Android's BlazorWebView silently drops <a target="_blank"> (the platform WebView has
// no multi-window support), so external links — Sign up / Get API key / addon manifest pages — appeared
// dead. Intercept a click on any such link and turn it into a main-frame navigation; the BlazorWebView's
// UrlLoading handler (MainPage) then routes the external host to the system browser and cancels the
// in-app navigation, so the SPA stays put. Scoped to target="_blank" external links so normal in-app
// navigation (relative routes) is untouched.
(function () {
    document.addEventListener('click', function (e) {
        var a = e.target && e.target.closest ? e.target.closest('a[href]') : null;
        if (!a || a.target !== '_blank') return;
        var href = a.getAttribute('href');
        if (!href || !/^https?:\/\//i.test(href)) return;   // absolute external links only
        e.preventDefault();
        window.location.href = href;   // main-frame nav → UrlLoading → system browser
    }, true);
}());
