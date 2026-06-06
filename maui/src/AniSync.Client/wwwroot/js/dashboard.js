// Dashboard body-class toggle. The Blazor Home page sets `dash-page` on <body> on its
// first interactive render and clears it on dispose — the equivalent of the original
// Index.cshtml's `ViewData["BodyClass"] = "dash-page"`. It drives `body.dash-page main`,
// which makes <main> the containing block for the absolutely-positioned "Customize"
// float (so the button lands on the media-type pills row instead of anchoring to the
// viewport) and tightens the top padding so the content tucks under the sticky pills.
export function setDashPage(on) {
    document.body.classList.toggle('dash-page', !!on);
}
