import { useEffect, useState } from "react";
import QRCode from "qrcode";
import { Code, Image, PinInput, Stack, Text } from "@mantine/core";
import { useTranslation } from "react-i18next";
import type { SetupStartResponse } from "../api";

/** QR-Code + manuelles Secret + Code-Eingabe; gemeinsam für Ersteinrichtung und Einladung. */
export function TotpEnroll({ totp, code, onCodeChange }: { totp: SetupStartResponse; code: string; onCodeChange: (code: string) => void }) {
  const { t } = useTranslation();
  const [qr, setQr] = useState<string | null>(null);

  useEffect(() => {
    void QRCode.toDataURL(totp.otpAuthUri, { margin: 1, width: 220 }).then(setQr);
  }, [totp]);

  return (
    <Stack gap="md" align="center">
      <Text size="sm" c="dimmed" ta="center" maw={440}>
        {t("common.totpScan")}
      </Text>
      {qr && <Image src={qr} alt={t("common.totpQrAlt")} w={220} h={220} className="qr" />}
      <Text size="xs" c="dimmed">
        {t("common.totpManual")} <Code>{totp.totpSecret}</Code>
      </Text>
      <PinInput length={6} type="number" oneTimeCode size="lg" value={code} onChange={onCodeChange} autoFocus aria-label={t("common.totpAria")} />
    </Stack>
  );
}
