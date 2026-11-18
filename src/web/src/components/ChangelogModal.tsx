import { useEffect, useState } from "react";
import { Alert, Badge, Group, Loader, Modal, ScrollArea, Stack, Text } from "@mantine/core";
import { IconAlertTriangle, IconInfoCircle, IconSparkles } from "@tabler/icons-react";
import { useTranslation } from "react-i18next";
import { api, formatRelative } from "../api";

interface ChangelogSection {
  /** null, wenn der Text kein Debian-Changelog war */
  version: string | null;
  date: string | null;
  text: string;
}

interface Changelog {
  state: "ready" | "pending" | "unavailable" | "unsupported";
  name: string;
  currentVersion: string | null;
  availableVersion: string | null;
  sections: ChangelogSection[] | null;
  error: string | null;
  reason: "windows" | "agent" | "notFound" | null;
  fetchedAt: string | null;
  /** KI-Zusammenfassung (falls eingerichtet) */
  summary: string | null;
  summaryError: string | null;
  summaryModel: string | null;
}

/** Wie lange auf den Agent gewartet wird (er fragt höchstens jede Minute nach Aufträgen). */
const maxWaitMs = 3 * 60_000;

/**
 * Patchnotes eines Updates: was die neue Version gegenüber der installierten ändert (apt-get changelog).
 * Mit hostId: anstehendes Update dieses Hosts (holt fehlende Patchnotes beim Agent). Mit version: aus der Historie.
 */
export function ChangelogModal({ hostId, name, version, from, onClose }: {
  hostId?: string; name: string | null; version?: string; from?: string; onClose: () => void;
}) {
  const { t } = useTranslation();
  const [data, setData] = useState<Changelog | null>(null);
  const [failed, setFailed] = useState<string | null>(null);
  const [gaveUp, setGaveUp] = useState(false);

  useEffect(() => {
    if (!name) return;
    setData(null);
    setFailed(null);
    setGaveUp(false);
    let stop = false;
    const started = Date.now();
    const load = async () => {
      try {
        const result = await api.get<Changelog>(version
          ? `/api/changelogs?name=${encodeURIComponent(name)}&version=${encodeURIComponent(version)}${from ? `&from=${encodeURIComponent(from)}` : ""}`
          : `/api/hosts/${hostId}/updates/changelog?name=${encodeURIComponent(name)}`);
        if (stop) return;
        setData(result);
        if (result.state === "pending") {
          if (Date.now() - started > maxWaitMs) setGaveUp(true);
          else setTimeout(() => void load(), 3000);
        }
      } catch (e) {
        if (!stop) setFailed(e instanceof Error ? e.message : String(e));
      }
    };
    void load();
    return () => { stop = true; };
  }, [hostId, name, version, from]);

  return (
    <Modal opened={name !== null} onClose={onClose} size="xl" title={<Text fw={700}>{t("changelog.title", { name: name ?? "" })}</Text>}>
      <Stack gap="md">
        {data && (
          <Group gap="xs">
            <Text size="sm" c="dimmed">{t("changelog.versions")}</Text>
            <Badge size="sm" variant="light" color="gray" ff="monospace">{data.currentVersion ?? t("hostDetail.new")}</Badge>
            <Text size="sm" c="dimmed">→</Text>
            <Badge size="sm" variant="light" color="blue" ff="monospace">{data.availableVersion}</Badge>
          </Group>
        )}

        {failed && <Alert color="red" icon={<IconAlertTriangle size={18} />}>{failed}</Alert>}

        {!failed && (!data || data.state === "pending") && !gaveUp && (
          <Group gap="sm"><Loader size="sm" /><Text size="sm" c="dimmed">{t("changelog.pending")}</Text></Group>
        )}
        {gaveUp && <Alert color="yellow" icon={<IconAlertTriangle size={18} />}>{t("changelog.gaveUp")}</Alert>}

        {data?.state === "unavailable" && (
          <Alert color="gray" icon={<IconInfoCircle size={18} />} title={t("changelog.unavailable")}>
            <Text size="sm" ff="monospace">{data.error}</Text>
          </Alert>
        )}
        {data?.state === "unsupported" && (
          <Alert color="gray" icon={<IconInfoCircle size={18} />}>{t(`changelog.reason_${data.reason ?? "notFound"}`)}</Alert>
        )}

        {data?.summary && (
          <Alert color="grape" variant="light" icon={<IconSparkles size={18} />} title={t("summary.packageTitle")}>
            <Text size="sm" style={{ whiteSpace: "pre-wrap" }}>{data.summary}</Text>
            {data.summaryModel && <Text size="xs" c="dimmed" mt={4}>{data.summaryModel}</Text>}
          </Alert>
        )}
        {data?.summaryError && !data.summary && (
          <Alert color="gray" icon={<IconAlertTriangle size={18} />}>{t("summary.failed", { error: data.summaryError })}</Alert>
        )}
        {data?.state === "ready" && (
          data.sections && data.sections.length > 0 ? (
            <ScrollArea.Autosize mah="65vh" offsetScrollbars>
              <Stack gap="md">
                {data.sections.map((s, i) => (
                  <Stack key={`${s.version}-${i}`} gap={4}>
                    {s.version && (
                      <Group gap="xs">
                        <Badge size="sm" variant="light" color="violet" ff="monospace">{s.version}</Badge>
                        {s.date && <Text size="xs" c="dimmed">{s.date}</Text>}
                      </Group>
                    )}
                    <Text component="pre" size="xs" ff="monospace" m={0} style={{ whiteSpace: "pre-wrap", wordBreak: "break-word" }}>{s.text}</Text>
                  </Stack>
                ))}
              </Stack>
            </ScrollArea.Autosize>
          ) : <Text size="sm" c="dimmed">{t("changelog.empty")}</Text>
        )}
        {data?.fetchedAt && <Text size="xs" c="dimmed">{t("changelog.fetched", { when: formatRelative(data.fetchedAt) })}</Text>}
      </Stack>
    </Modal>
  );
}
