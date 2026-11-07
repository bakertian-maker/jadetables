// Reconnection handling for Blazor Server's live connection.
//
// Phones drop the connection when they sleep or switch apps. Blazor fires
// "components-reconnect-state-changed" on #components-reconnect-modal; we show a small
// banner while it retries, retry again when the page becomes visible, and reload the page
// when the server no longer has our session. A reload puts the player straight back in
// their seat, because their identity is kept in this browser.

interface ReconnectDetail {
    state: "show" | "hide" | "retrying" | "failed" | "rejected" | "paused" | "resume-failed" | string;
}

interface BlazorApi {
    reconnect?: () => Promise<boolean>;
    resumeCircuit?: () => Promise<boolean>;
}

declare const Blazor: BlazorApi | undefined;

const modal = document.getElementById("components-reconnect-modal") as HTMLDialogElement | null;
const retryButton = document.getElementById("components-reconnect-button");

function setState(state: "retrying" | "failed" | "reloading"): void {
    if (!modal) return;
    modal.dataset.state = state;
    if (!modal.open) modal.show();
}

function hide(): void {
    if (modal?.open) modal.close();
    document.removeEventListener("visibilitychange", retryWhenVisible);
}

function reload(): void {
    setState("reloading");
    location.reload();
}

async function retry(): Promise<void> {
    document.removeEventListener("visibilitychange", retryWhenVisible);
    setState("retrying");
    try {
        const reconnected = (await Blazor?.reconnect?.()) ?? false;
        if (reconnected) return; // Blazor sends "hide" when it is back
        // The server forgot this session (e.g. the phone slept too long): try to resume, else reload.
        const resumed = (await Blazor?.resumeCircuit?.()) ?? false;
        if (resumed) hide();
        else reload();
    } catch {
        setState("failed");
        document.addEventListener("visibilitychange", retryWhenVisible);
    }
}

function retryWhenVisible(): void {
    if (document.visibilityState === "visible") void retry();
}

modal?.addEventListener("components-reconnect-state-changed", (event: Event) => {
    const { state } = (event as CustomEvent<ReconnectDetail>).detail;
    switch (state) {
        case "show":
        case "retrying":
            setState("retrying");
            break;
        case "hide":
            hide();
            break;
        case "failed":
        case "resume-failed":
            // Retries ran out (often because the phone was asleep): try again as soon as the page is visible.
            setState("failed");
            document.addEventListener("visibilitychange", retryWhenVisible);
            break;
        case "rejected":
            reload();
            break;
    }
});

retryButton?.addEventListener("click", () => void retry());

export {};
