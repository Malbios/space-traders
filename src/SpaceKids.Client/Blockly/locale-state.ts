/**
 * Milestone 12 (bilingual support): the single shared module-level "what language are
 * we rendering blocks in right now" flag — read live by every block's own `init()`
 * (`blocks.ts`, `blocks-catalog.ts`) and by `toolbox.ts`'s category names, so switching
 * locale never requires re-registering block types, only tearing down and recreating
 * whichever workspace is currently open (`blockly-host.ts`'s `setLocale`).
 */
export type Locale = "de" | "en";

let currentLocale: Locale = "de";

export function getCurrentLocale(): Locale {
    return currentLocale;
}

export function setCurrentLocale(locale: Locale): void {
    currentLocale = locale;
}
