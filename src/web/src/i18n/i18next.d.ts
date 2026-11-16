import "i18next";
import type de from "./de";

// Schlüssel in t("…") werden vom Compiler gegen de.ts geprüft.
declare module "i18next" {
  interface CustomTypeOptions {
    resources: { translation: typeof de };
  }
}
