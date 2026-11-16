// Übersetzungen: Deutsch und Englisch. Fehlende Schlüssel fallen auf Englisch zurück.
import i18n from "i18next";
import { initReactI18next } from "react-i18next";
import dayjs from "dayjs";
import "dayjs/locale/de";
import de from "./de";
import en from "./en";

export type Language = "de" | "en";

const storageKey = "aiopatcher.language";

function stored(): Language | null {
  try {
    const value = localStorage.getItem(storageKey);
    return value === "de" || value === "en" ? value : null;
  } catch {
    return null;
  }
}

function browserLanguage(): Language {
  return navigator.language?.toLowerCase().startsWith("de") ? "de" : "en";
}

function apply(lang: Language) {
  dayjs.locale(lang);
  document.documentElement.lang = lang;
}

const initial = stored() ?? browserLanguage();

void i18n.use(initReactI18next).init({
  resources: { de: { translation: de }, en: { translation: en } },
  lng: initial,
  fallbackLng: "en",
  interpolation: { escapeValue: false }, // React escaped selbst
});
apply(initial);

/** Sprache umschalten. null = automatisch (Browser). */
export function setLanguage(lang: Language | null) {
  try {
    if (lang) localStorage.setItem(storageKey, lang);
    else localStorage.removeItem(storageKey);
  } catch {
    // Speicher gesperrt: Sprache gilt dann nur für diese Sitzung
  }
  const effective = lang ?? browserLanguage();
  apply(effective);
  void i18n.changeLanguage(effective);
}

export const currentLanguage = (): Language => (i18n.language === "de" ? "de" : "en");

/** Locale für Datums- und Zahlenformatierung. */
export const locale = () => (currentLanguage() === "de" ? "de-DE" : "en-GB");

export const t = i18n.t.bind(i18n);

export default i18n;
