import { useEffect, useState } from "react";
import { Alert, Anchor, Code, Text } from "@mantine/core";
import { IconSparkles } from "@tabler/icons-react";
import { useTranslation } from "react-i18next";
import { api, type ServerInfo } from "../api";
import { useCan } from "../App";

/** Hinweis für Admins: neue AIOPatcher-Version verfügbar (es wird nie automatisch aktualisiert). */
export function UpdateBanner() {
  const { t } = useTranslation();
  const can = useCan();
  const [info, setInfo] = useState<ServerInfo | null>(null);

  useEffect(() => {
    if (can.admin) void api.get<ServerInfo>("/api/server-info").then(setInfo).catch(() => {});
  }, [can.admin]);

  if (!info?.updateAvailable) return null;

  return (
    <Alert color="blue" icon={<IconSparkles size={18} />} title={t("update.title", { version: info.latestVersion, current: info.version })}>
      <Text size="sm">{t("update.text")}</Text>
      <Code block mt="xs">docker compose pull && docker compose up -d</Code>
      {info.releaseUrl && (
        <Anchor href={info.releaseUrl} target="_blank" rel="noreferrer" size="sm" mt="xs" display="inline-block">
          {t("update.notes")}
        </Anchor>
      )}
    </Alert>
  );
}
