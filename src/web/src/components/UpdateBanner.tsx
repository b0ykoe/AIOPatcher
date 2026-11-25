import { useEffect, useState } from "react";
import { Alert, Anchor, Button, Code, Group, Menu, Text, type ButtonProps } from "@mantine/core";
import { IconChevronDown, IconRefresh, IconSparkles } from "@tabler/icons-react";
import { useTranslation } from "react-i18next";
import { Link, useNavigate } from "react-router";
import { api, type ServerInfo } from "../api";
import { useCan } from "../App";
import { t } from "../i18n";
import { confirmAction, notifyError, notifySuccess } from "./ui";

export interface CheckResult {
  checkedNow: boolean;
  checkedAt: string | null;
  error: string | null;
  latestVersion: string | null;
  updateAvailable: boolean;
}

/**
 * Sofort bei GitHub nachsehen (Einstellungen → Allgemein, Seitenleiste). Die Meldung nennt, welche Version GitHub als
 * neueste meldet; danach den frischen Server-Stand zurückgeben.
 */
export async function checkForUpdate(): Promise<{ result: CheckResult; info: ServerInfo } | null> {
  try {
    const result = await api.post<CheckResult>("/api/server/check-update");
    const info = await api.get<ServerInfo>("/api/server-info");
    if (result.error) notifyError(new Error(result.error), t("settings.checkFailed"));
    else if (result.updateAvailable && result.latestVersion) notifySuccess(t("settings.updateAvailable", { version: result.latestVersion }));
    else if (result.latestVersion) notifySuccess(t("settings.upToDate"), t("settings.latestRelease", { version: result.latestVersion }));
    else notifyError(new Error(t("settings.noRelease")), t("settings.checkFailed"));
    return { result, info };
  } catch (e) {
    notifyError(e);
    return null;
  }
}

/**
 * "Jetzt aktualisieren": startet aiopatcher-update über den Agent auf dem Server-Host (mit Bestätigung). Mehrere
 * Server-Hosts → Auswahlmenü. Nichts, wenn kein Host den Server aktualisieren kann.
 */
export function ServerUpdateButton({ info, size = "xs", fullWidth }: { info: ServerInfo; size?: ButtonProps["size"]; fullWidth?: boolean }) {
  const { t } = useTranslation();
  const navigate = useNavigate();
  const hosts = info.updateHosts ?? [];
  if (!info.updateAvailable || hosts.length === 0) return null;

  const start = (host: { id: string; name: string }) =>
    confirmAction({
      title: t("update.confirmTitle", { version: info.latestVersion }),
      message: t("update.confirmText", { host: host.name }),
      confirmLabel: t("update.now"),
      onConfirm: async () => {
        try {
          const { jobId } = await api.post<{ jobId: string }>("/api/server/update", { hostId: host.id });
          navigate(`/jobs/${jobId}`);
        } catch (e) {
          notifyError(e);
        }
      },
    });

  const props = { size, fullWidth, variant: "gradient", className: "gradient-button", leftSection: <IconRefresh size={14} /> } as const;
  if (hosts.length === 1) {
    return <Button {...props} onClick={() => start(hosts[0])}>{t("update.now")}</Button>;
  }
  return (
    <Menu position="bottom-start">
      <Menu.Target>
        <Button {...props} rightSection={<IconChevronDown size={14} />}>{t("update.now")}</Button>
      </Menu.Target>
      <Menu.Dropdown>
        {hosts.map((h) => <Menu.Item key={h.id} onClick={() => start(h)}>{t("update.via", { host: h.name })}</Menu.Item>)}
      </Menu.Dropdown>
    </Menu>
  );
}

/** Läuft das Update gerade: Hinweis mit Link auf den Job. */
export function ServerUpdateRunning({ jobId }: { jobId: string }) {
  const { t } = useTranslation();
  return (
    <Text size="sm">
      {t("update.running")} <Anchor component={Link} to={`/jobs/${jobId}`} size="sm">{t("update.openJob")}</Anchor>
    </Text>
  );
}

/** Kein Server-Host mit aiopatcher-update: Befehl zum Aktualisieren von Hand. */
export function ServerUpdateManual() {
  const { t } = useTranslation();
  return (
    <>
      <Text size="sm">{t("update.text")}</Text>
      <Code block mt="xs">sudo aiopatcher-update</Code>
      <Text size="xs" c="dimmed" mt={4}>{t("update.textCompose")}</Text>
    </>
  );
}

/**
 * Hinweis für Admins: neue AIOPatcher-Version verfügbar. Ist der Server-Host als Agent eingetragen (Setup mit
 * aiopatcher-update), geht das Update per Knopf; sonst steht der Befehl da.
 */
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
      {info.runningUpdateJob ? (
        <ServerUpdateRunning jobId={info.runningUpdateJob} />
      ) : (info.updateHosts ?? []).length > 0 ? (
        <Group gap="xs" mt={4}>
          <ServerUpdateButton info={info} />
          <Text size="xs" c="dimmed">{t("update.buttonHint")}</Text>
        </Group>
      ) : (
        <ServerUpdateManual />
      )}
      {info.releaseUrl && (
        <Anchor href={info.releaseUrl} target="_blank" rel="noreferrer" size="sm" mt="xs" display="inline-block">
          {t("update.notes")}
        </Anchor>
      )}
    </Alert>
  );
}
