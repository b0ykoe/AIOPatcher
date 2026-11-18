import { useEffect, useState } from "react";
import { Link } from "react-router";
import { Anchor, Badge, Button, Card, Group, Stack, Text } from "@mantine/core";
import { IconApi, IconWand } from "@tabler/icons-react";
import { useTranslation } from "react-i18next";
import { api, type ServerInfo } from "../../api";
import { MirrorStatusLine } from "../../pages/CveLookupPage";
import { openSetupWizard } from "../SetupWizard";
import { ScanSettingsCard } from "../ScanIntervals";
import { CardTitle } from "../ui";

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
        <CardTitle>{t("settings.generalVersion")}</CardTitle>
        <Group gap="xs">
          <Text ff="monospace">v{info?.version ?? "…"}</Text>
          {info?.updateAvailable && info.latestVersion ? (
            <Badge size="sm" color="blue" component="a" href={info.releaseUrl ?? undefined} target="_blank" style={{ cursor: "pointer" }}>
              {t("settings.updateAvailable", { version: info.latestVersion })}
            </Badge>
          ) : info?.latestVersion ? <Badge size="sm" color="teal">{t("settings.upToDate")}</Badge> : null}
        </Group>
        <Text fw={700} mt="md" mb={4}>{t("settings.generalTimeZone")}</Text>
        <Text ff="monospace">{info?.timeZone ?? "…"}</Text>
        <Text size="xs" c="dimmed">{t("settings.generalTimeZoneHint")}</Text>
      </Card>

      <ScanSettingsCard />

      <Card>
        <CardTitle>{t("settings.generalCve")}</CardTitle>
        <MirrorStatusLine />
      </Card>

      <Card>
        <CardTitle>{t("settings.generalWizard")}</CardTitle>
        <Stack gap="xs" align="flex-start">
          <Text size="sm" c="dimmed">{t("settings.generalWizardText")}</Text>
          <Button size="compact-xs" variant="light" leftSection={<IconWand size={14} />} onClick={openSetupWizard}>{t("settings.generalWizardStart")}</Button>
        </Stack>
      </Card>

      <Card>
        <CardTitle>{t("settings.generalApi")}</CardTitle>
        <Stack gap="xs">
          <Text size="sm" c="dimmed">{t("settings.generalApiText")}</Text>
          <Anchor component={Link} to="/developer" size="sm"><Group gap={4}><IconApi size={16} />{t("settings.generalApiOpen")}</Group></Anchor>
        </Stack>
      </Card>
    </div>
  );
}
