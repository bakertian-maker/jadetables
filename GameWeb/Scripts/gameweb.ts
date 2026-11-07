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
}

declare global {
    interface Window {
        gameweb: GameWebApi;
    }
}

const ID_KEY = "gw.id";
const NAME_KEY = "gw.name";
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
};

export {};
