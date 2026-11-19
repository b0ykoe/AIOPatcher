import { useEffect, useState } from "react";
import { Alert, Anchor, Button, Code, Group, Menu, Text } from "@mantine/core";
import { IconChevronDown, IconRefresh, IconSparkles } from "@tabler/icons-react";
import { useTranslation } from "react-i18next";
import { Link, useNavigate } from "react-router";
import { api, type ServerInfo } from "../api";
import { useCan } from "../App";
import { confirmAction, notifyError } from "./ui";

/**
 * Hinweis für Admins: neue AIOPatcher-Version verfügbar. Ist der Server-Host als Agent eingetragen (Setup mit
 * aiopatcher-update), geht das Update per Knopf; sonst steht der Befehl da.
 */
export function UpdateBanner() {
  const { t } = useTranslation();
  const can = useCan();
  const navigate = useNavigate();
  const [info, setInfo] = useState<ServerInfo | null>(null);

  useEffect(() => {
    if (can.admin) void api.get<ServerInfo>("/api/server-info").then(setInfo).catch(() => {});
  }, [can.admin]);

  if (!info?.updateAvailable) return null;
  const hosts = info.updateHosts ?? [];

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

  return (
    <Alert color="blue" icon={<IconSparkles size={18} />} title={t("update.title", { version: info.latestVersion, current: info.version })}>
      {info.runningUpdateJob ? (
        <Text size="sm">
          {t("update.running")} <Anchor component={Link} to={`/jobs/${info.runningUpdateJob}`} size="sm">{t("update.openJob")}</Anchor>
        </Text>
      ) : hosts.length > 0 ? (
        <Group gap="xs" mt={4}>
          {hosts.length === 1 ? (
            <Button size="xs" variant="gradient" className="gradient-button" leftSection={<IconRefresh size={14} />} onClick={() => start(hosts[0])}>
              {t("update.now")}
            </Button>
          ) : (
            <Menu position="bottom-start">
              <Menu.Target>
                <Button size="xs" variant="gradient" className="gradient-button" leftSection={<IconRefresh size={14} />} rightSection={<IconChevronDown size={14} />}>
                  {t("update.now")}
                </Button>
              </Menu.Target>
              <Menu.Dropdown>
                {hosts.map((h) => <Menu.Item key={h.id} onClick={() => start(h)}>{t("update.via", { host: h.name })}</Menu.Item>)}
              </Menu.Dropdown>
            </Menu>
          )}
          <Text size="xs" c="dimmed">{t("update.buttonHint")}</Text>
        </Group>
      ) : (
        <>
          <Text size="sm">{t("update.text")}</Text>
          <Code block mt="xs">sudo aiopatcher-update</Code>
          <Text size="xs" c="dimmed" mt={4}>{t("update.textCompose")}</Text>
        </>
      )}
      {info.releaseUrl && (
        <Anchor href={info.releaseUrl} target="_blank" rel="noreferrer" size="sm" mt="xs" display="inline-block">
          {t("update.notes")}
        </Anchor>
      )}
    </Alert>
  );
}
