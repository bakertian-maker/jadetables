// Analytics consent. Google Tag Manager is loaded only after the visitor accepts; until then
// nothing is sent to Google. The choice is kept in this browser and can be changed from the
// "Cookie settings" link in the footer.
//
// The page opts in by rendering <meta name="gtm-id" content="GTM-..."> (production only) and
// the #consent-banner element.

export {};

const KEY = "jt.consent";
type Choice = "granted" | "denied";

declare global {
    interface Window {
        dataLayer: unknown[];
        gtag?: (...args: unknown[]) => void;
    }
}

function readChoice(): Choice | null {
    try {
        const v = localStorage.getItem(KEY);
        return v === "granted" || v === "denied" ? v : null;
    } catch {
        return null;
    }
}

function saveChoice(choice: Choice): void {
    try {
        localStorage.setItem(KEY, choice);
    } catch {
        // Storage blocked: the choice lasts for this page only.
    }
}

function gtmId(): string | null {
    return document.querySelector<HTMLMetaElement>('meta[name="gtm-id"]')?.content || null;
}

function banner(): HTMLElement | null {
    return document.getElementById("consent-banner");
}

let loaded = false;

function loadTagManager(id: string): void {
    if (loaded) return;
    loaded = true;
    window.dataLayer = window.dataLayer || [];
    // gtag must push the arguments object itself, as Google's snippet does.
    window.gtag = function gtag() {
        // eslint-disable-next-line prefer-rest-params
        window.dataLayer.push(arguments);
    };
    // Analytics only: advertising stays off.
    window.gtag("consent", "default", {
        analytics_storage: "granted",
        ad_storage: "denied",
        ad_user_data: "denied",
        ad_personalization: "denied",
    });
    window.gtag("js", new Date());
    const script = document.createElement("script");
    script.async = true;
    script.src = "https://www.googletagmanager.com/gtm.js?id=" + encodeURIComponent(id);
    document.head.appendChild(script);
}

/** Removes Google Analytics cookies (_ga, _ga_*, _gid) for this host and its parent domain. */
function clearAnalyticsCookies(): void {
    const host = location.hostname;
    const parts = host.split(".");
    const domains = ["", host, "." + host];
    if (parts.length > 2) domains.push("." + parts.slice(-2).join("."));
    for (const cookie of document.cookie.split(";")) {
        const name = cookie.split("=")[0].trim();
        if (!/^(_ga|_gid|_gat)/.test(name)) continue;
        for (const domain of domains) {
            document.cookie = `${name}=; expires=Thu, 01 Jan 1970 00:00:00 GMT; path=/` + (domain ? `; domain=${domain}` : "");
        }
    }
}

function choose(choice: Choice): void {
    const before = readChoice();
    saveChoice(choice);
    const b = banner();
    if (b) b.hidden = true;
    const id = gtmId();
    if (!id) return;
    if (choice === "granted") loadTagManager(id);
    else if (before === "granted" || loaded) {
        // Tag Manager can't be unloaded: clear its cookies and start the page again without it.
        clearAnalyticsCookies();
        location.reload();
    }
}

function start(): void {
    const id = gtmId();
    if (!id) return;
    const choice = readChoice();
    if (choice === "granted") loadTagManager(id);
    else if (choice === null) {
        const b = banner();
        if (b) b.hidden = false;
    }
}

document.addEventListener("click", (e) => {
    const target = (e.target as Element | null)?.closest<HTMLElement>("[data-consent], [data-consent-open]");
    if (!target) return;
    if (target.hasAttribute("data-consent-open")) {
        e.preventDefault();
        const b = banner();
        if (b) {
            b.hidden = false;
            b.querySelector<HTMLElement>("button")?.focus();
        }
        return;
    }
    const value = target.getAttribute("data-consent");
    if (value === "granted" || value === "denied") choose(value);
});

if (document.readyState === "loading") document.addEventListener("DOMContentLoaded", start);
else start();

// Blazor swaps page content without a full reload and resets the banner to its server-rendered
// (hidden) state, so show it again after each such page change until the visitor has chosen.
const blazor = (window as unknown as { Blazor?: { addEventListener(name: string, cb: () => void): void } }).Blazor;
blazor?.addEventListener("enhancedload", start);
