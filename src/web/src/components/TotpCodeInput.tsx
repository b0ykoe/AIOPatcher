import { TextInput, type TextInputProps } from "@mantine/core";
import { useTranslation } from "react-i18next";

/**
 * Ein einzelnes Feld für den 6-stelligen Authenticator-Code. Passwort-Manager (Bitwarden, 1Password, KeePassXC …)
 * füllen hier zuverlässig aus, anders als bei sechs Einzelfeldern. Leerzeichen und Bindestriche aus eingefügten oder
 * ausgefüllten Codes ("123 456") fallen weg.
 */
export function TotpCodeInput({ value, onChange, ...props }: { value: string; onChange: (code: string) => void } & Omit<TextInputProps, "value" | "onChange">) {
  const { t } = useTranslation();
  return (
    <TextInput
      label={t("common.totpCode")}
      name="totp"
      id="totp"
      type="text"
      inputMode="numeric"
      autoComplete="one-time-code"
      autoCorrect="off"
      spellCheck={false}
      placeholder="000000"
      aria-label={t("common.totpAria")}
      value={value}
      onChange={(e) => onChange(e.currentTarget.value.replace(/\D/g, "").slice(0, 6))}
      styles={{ input: { fontFamily: "var(--mantine-font-family-monospace)", letterSpacing: "0.3em", fontSize: "var(--mantine-font-size-lg)" } }}
      {...props}
    />
  );
}
