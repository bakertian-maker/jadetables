// Browser helpers for GameWeb, called from Blazor through JS interop.
// Guest identity (a random player id and a nickname) is kept in this browser only.

interface Identity {
    id: string;
    name: string;
}

interface GameWebApi {
    identity(guestPrefix?: string): Identity;
    setName(name: string): void;
    copy(text: string): Promise<boolean>;
    origin(): string;
    select(elementId: string): void;
    attention(needed: boolean, label: string): void;
    soundEnabled(): boolean;
    setSound(on: boolean): void;
    getPref(key: string): string | null;
    setPref(key: string, value: string): void;
}

declare global {
    interface Window {
        gameweb: GameWebApi;
    }
}

const ID_KEY = "gw.id";
const NAME_KEY = "gw.name";
const SOUND_KEY = "gw.sound";
let memoryId: string | null = null;

function newId(): string {
    try {
        if (typeof crypto !== "undefined" && "randomUUID" in crypto) return crypto.randomUUID();
    } catch {
        // randomUUID needs a secure context; fall through.
    }
    let s = "";
    for (let i = 0; i < 32; i++) s += Math.floor(Math.random() * 16).toString(16);
    return s;
}

function read(key: string): string | null {
    try {
        return localStorage.getItem(key);
    } catch {
        return null;
    }
}

function write(key: string, value: string): void {
    try {
        localStorage.setItem(key, value);
    } catch {
        // Storage blocked (private mode): the identity lasts for this page only.
    }
}

// ---- "Your turn" alert -------------------------------------------------------------
// When it becomes the player's turn while the tab is in the background, the tab title
// flashes and a soft chime plays (unless sound is off). Both stop when the tab is shown.

let attentionNeeded = false;
let attentionLabel = "";
let baseTitle: string | null = null;
let flashTimer: number | undefined;
let audio: AudioContext | null = null;

// Browsers only allow sound after the user has interacted with the page, so create the
// audio context on the first tap or key press.
function unlockAudio(): void {
    try {
        audio ??= new AudioContext();
        void audio.resume();
    } catch {
        audio = null;
    }
}
document.addEventListener("pointerdown", unlockAudio, { once: true });
document.addEventListener("keydown", unlockAudio, { once: true });

function chime(): void {
    if (!audio || read(SOUND_KEY) === "off") return;
    const now = audio.currentTime;
    [659.25, 987.77].forEach((freq, i) => {
        const osc = audio!.createOscillator();
        const gain = audio!.createGain();
        osc.type = "sine";
        osc.frequency.value = freq;
        const start = now + i * 0.16;
        gain.gain.setValueAtTime(0.0001, start);
        gain.gain.exponentialRampToValueAtTime(0.18, start + 0.02);
        gain.gain.exponentialRampToValueAtTime(0.0001, start + 0.45);
        osc.connect(gain).connect(audio!.destination);
        osc.start(start);
        osc.stop(start + 0.5);
    });
}

function startFlash(): void {
    if (flashTimer !== undefined) return;
    baseTitle = document.title;
    let marked = true;
    document.title = `● ${attentionLabel}`;
    flashTimer = window.setInterval(() => {
        marked = !marked;
        document.title = marked ? `● ${attentionLabel}` : baseTitle ?? "";
    }, 1000);
}

function stopFlash(): void {
    if (flashTimer === undefined) return;
    window.clearInterval(flashTimer);
    flashTimer = undefined;
    if (baseTitle !== null) document.title = baseTitle;
    baseTitle = null;
}

document.addEventListener("visibilitychange", () => {
    if (!document.hidden) stopFlash();
});

window.gameweb = {
    identity(guestPrefix: string = "Guest"): Identity {
        let id = read(ID_KEY);
        if (!id) {
            id = memoryId ?? newId();
            memoryId = id;
            write(ID_KEY, id);
        }
        let name = read(NAME_KEY);
        if (!name) {
            name = `${guestPrefix} ${Math.floor(1000 + Math.random() * 9000)}`;
            write(NAME_KEY, name);
        }
        return { id, name };
    },

    setName(name: string): void {
        write(NAME_KEY, name.trim().slice(0, 20));
    },

    async copy(text: string): Promise<boolean> {
        if (!navigator.clipboard) return false;
        try {
            await navigator.clipboard.writeText(text);
            return true;
        } catch {
            return false;
        }
    },

    origin(): string {
        return location.origin;
    },

    select(elementId: string): void {
        const el = document.getElementById(elementId);
        if (el instanceof HTMLInputElement) el.select();
    },

    attention(needed: boolean, label: string): void {
        const wasNeeded = attentionNeeded;
        attentionNeeded = needed;
        attentionLabel = label;
        if (!needed) {
            stopFlash();
            return;
        }
        if (!wasNeeded && document.hidden) {
            startFlash();
            chime();
        }
    },

    soundEnabled(): boolean {
        return read(SOUND_KEY) !== "off";
    },

    /** Small per-browser preferences, such as letter pieces in Xiangqi. */
    getPref(key: string): string | null {
        return read("gw.pref." + key);
    },

    setPref(key: string, value: string): void {
        write("gw.pref." + key, value);
    },

    setSound(on: boolean): void {
        write(SOUND_KEY, on ? "on" : "off");
        if (on) {
            unlockAudio();
            chime(); // let the player hear what it sounds like
        }
    },
};

// ---- Accounts ----------------------------------------------------------------------
// Sign-in and register forms carry this browser's player id (input[data-player-id]), so the games
// already played here are added to the account. Logging out (or deleting the account) gives the
// browser a new id, so whoever uses it next plays separately.

function fillPlayerIdFields(): void {
    const fields = document.querySelectorAll<HTMLInputElement>("input[data-player-id]");
    if (fields.length === 0) return;
    const id = window.gameweb.identity().id;
    fields.forEach((f) => { f.value = id; });
}

document.addEventListener("submit", (e) => {
    const form = e.target as HTMLElement | null;
    if (form?.hasAttribute("data-logout")) {
        try {
            localStorage.removeItem(ID_KEY);
        } catch {
            // Storage blocked: nothing kept to clear.
        }
        memoryId = null;
    }
}, true);

if (document.readyState === "loading") document.addEventListener("DOMContentLoaded", fillPlayerIdFields);
else fillPlayerIdFields();
const blazor = (window as unknown as { Blazor?: { addEventListener(name: string, cb: () => void): void } }).Blazor;
blazor?.addEventListener("enhancedload", fillPlayerIdFields);

export {};
