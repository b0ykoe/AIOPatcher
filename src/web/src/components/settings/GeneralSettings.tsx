import { useEffect, useState } from "react";
import { Link } from "react-router";
import { Anchor, Badge, Button, Card, Group, Stack, Text } from "@mantine/core";
import { IconApi, IconWand } from "@tabler/icons-react";
import { useTranslation } from "react-i18next";
import { api, type ServerInfo } from "../../api";
import { MirrorStatusLine } from "../../pages/CveLookupPage";
import { openSetupWizard } from "../SetupWizard";

/** Einstellungen → Allgemein: Version, Zeitzone, CVE-Datenbank, Assistent, API. */
export function GeneralSettings() {
  const { t } = useTranslation();
  const [info, setInfo] = useState<ServerInfo | null>(null);

  useEffect(() => {
    void api.get<ServerInfo>("/api/server-info").then(setInfo).catch(() => {});
  }, []);

  return (
    <div className="card-grid card-grid-wide">
      <Card>
        <Text fw={700} mb="sm">{t("settings.generalVersion")}</Text>
        <Group gap="sm">
          <Text ff="monospace">v{info?.version ?? "…"}</Text>
          {info?.updateAvailable && info.latestVersion ? (
            <Badge color="violet" component="a" href={info.releaseUrl ?? undefined} target="_blank" style={{ cursor: "pointer" }}>
              {t("settings.updateAvailable", { version: info.latestVersion })}
            </Badge>
          ) : info?.latestVersion ? <Badge color="teal">{t("settings.upToDate")}</Badge> : null}
        </Group>
        <Text fw={700} mt="lg" mb={4}>{t("settings.generalTimeZone")}</Text>
        <Text ff="monospace">{info?.timeZone ?? "…"}</Text>
        <Text size="xs" c="dimmed">{t("settings.generalTimeZoneHint")}</Text>
      </Card>

      <Card>
        <Text fw={700} mb="sm">{t("settings.generalCve")}</Text>
        <MirrorStatusLine />
      </Card>

      <Card>
        <Stack gap="xs">
          <Text fw={700}>{t("settings.generalWizard")}</Text>
          <Text size="sm" c="dimmed">{t("settings.generalWizardText")}</Text>
          <Group><Button variant="light" leftSection={<IconWand size={16} />} onClick={openSetupWizard}>{t("settings.generalWizardStart")}</Button></Group>
        </Stack>
      </Card>

      <Card>
        <Stack gap="xs">
          <Text fw={700}>{t("settings.generalApi")}</Text>
          <Text size="sm" c="dimmed">{t("settings.generalApiText")}</Text>
          <Anchor component={Link} to="/developer" size="sm"><Group gap={4}><IconApi size={16} />{t("settings.generalApiOpen")}</Group></Anchor>
        </Stack>
      </Card>
    </div>
  );
}
