import { useEffect, useState } from "react";
import { Link } from "react-router";
import { Anchor, Badge, Button, Card, Group, Stack, Text } from "@mantine/core";
import { IconApi, IconRefresh, IconWand } from "@tabler/icons-react";
import { useTranslation } from "react-i18next";
import { api, formatRelative, type ServerInfo } from "../../api";
import { useCan } from "../../App";
import { MirrorStatusLine } from "../../pages/CveLookupPage";
import { openSetupWizard } from "../SetupWizard";
import { ScanSettingsCard, TimingsCard } from "../ScanIntervals";
import { CardTitle } from "../ui";
import { checkForUpdate, ServerUpdateButton, ServerUpdateManual, ServerUpdateRunning } from "../UpdateBanner";

/** Einstellungen → Allgemein: Version, Zeitzone, CVE-Datenbank, Assistent, API. */
export function GeneralSettings() {
  const { t } = useTranslation();
  const [info, setInfo] = useState<ServerInfo | null>(null);
  const can = useCan();
  const [checking, setChecking] = useState(false);
  const [checked, setChecked] = useState<{ at: string; latest: string | null } | null>(null);

  const checkNow = async () => {
    setChecking(true);
    const r = await checkForUpdate();
    setChecking(false);
    if (!r) return;
    if (r.result.checkedAt) setChecked({ at: r.result.checkedAt, latest: r.result.latestVersion });
    setInfo(r.info);
  };

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
          {can.admin && (
            <Button size="compact-xs" variant="light" leftSection={<IconRefresh size={12} />} loading={checking} onClick={() => void checkNow()}>{t("settings.checkNow")}</Button>
          )}
        </Group>
        {checked && (
          <Text size="xs" c="dimmed" mt={4}>
            {t("settings.lastChecked", { when: formatRelative(checked.at) })}
            {checked.latest ? ` · ${t("settings.latestRelease", { version: checked.latest })}` : ""}
          </Text>
        )}
        {can.admin && info?.updateAvailable && (
          <Stack gap={4} mt="sm" align="flex-start">
            {info.runningUpdateJob ? <ServerUpdateRunning jobId={info.runningUpdateJob} />
              : (info.updateHosts ?? []).length > 0 ? (
                <>
                  <ServerUpdateButton info={info} size="compact-sm" />
                  <Text size="xs" c="dimmed">{t("update.buttonHint")}</Text>
                </>
              ) : <ServerUpdateManual />}
          </Stack>
        )}
        <Text fw={700} mt="md" mb={4}>{t("settings.generalTimeZone")}</Text>
        <Text ff="monospace">{info?.timeZone ?? "…"}</Text>
        <Text size="xs" c="dimmed">{t("settings.generalTimeZoneHint")}</Text>
      </Card>

      <ScanSettingsCard className="card-tall" />

      <Card>
        <CardTitle>{t("settings.generalCve")}</CardTitle>
        <MirrorStatusLine />
      </Card>

      <Card>
        <CardTitle>{t("settings.generalWizard")}</CardTitle>
        <Stack gap="xs" align="flex-start">
          <Text size="sm" c="dimmed">{t("settings.generalWizardText")}</Text>
          <Group gap="xs">
            <Button size="compact-xs" variant="light" leftSection={<IconWand size={14} />} onClick={openSetupWizard}>{t("settings.generalWizardStart")}</Button>
            <Button size="compact-xs" variant="subtle" component={Link} to="/getting-started">{t("gettingStarted.title")}</Button>
          </Group>
        </Stack>
      </Card>

      <Card>
        <CardTitle>{t("settings.generalApi")}</CardTitle>
        <Stack gap="xs">
          <Text size="sm" c="dimmed">{t("settings.generalApiText")}</Text>
          <Anchor component={Link} to="/developer" size="sm"><Group gap={4}><IconApi size={16} />{t("settings.generalApiOpen")}</Group></Anchor>
        </Stack>
      </Card>

      <TimingsCard className="card-full" />
    </div>
  );
}
