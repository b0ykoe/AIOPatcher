import { useEffect, useMemo, useState, type ReactNode } from "react";
import { Alert, Badge, Box, Button, Checkbox, Collapse, Group, Loader, Modal, ScrollArea, Stack, Table, Text, Tooltip, UnstyledButton } from "@mantine/core";
import { IconAlertTriangle, IconChevronDown, IconChevronRight } from "@tabler/icons-react";
import { useTranslation } from "react-i18next";
import { api, formatRelative, isActiveJob, type ComposeRead, type ContainerManageRequest, type DockerDiskUsage, type PruneScan } from "../api";
import { formatBytes } from "./ContainerManager";
import { errorText } from "./ui";

/** Docker meldet "2024-01-02 10:00:00 +0100 CET"; für "vor …" in ISO umwandeln, sonst null. */
export function dockerDate(value: string | null): string | null {
  const m = value?.match(/^(\d{4}-\d{2}-\d{2}) (\d{2}:\d{2}:\d{2}) ([+-]\d{2})(\d{2})/);
  return m ? `${m[1]}T${m[2]}${m[3]}:${m[4]}` : null;
}

/** Was aus dem Scan zusammen frei würde (Images laut docker image ls, inkl. geteilter Schichten: höchstens). */
export function selectedSize(scan: PruneScan, images: Set<string>, volumes: Set<string>, cache: boolean): number {
  return scan.images.filter((i) => images.has(i.id)).reduce((n, i) => n + i.size, 0)
    + scan.volumes.filter((v) => volumes.has(v.name)).reduce((n, v) => n + (v.size ?? 0), 0)
    + (cache ? scan.buildCacheSize : 0);
}

/** Aufklappbarer Abschnitt mit "alle auswählen"-Haken im Kopf. */
function Section({ title, detail, checked, indeterminate, onToggleAll, open, onOpen, danger, children }: {
  title: string; detail: string; checked: boolean; indeterminate: boolean; onToggleAll: () => void; open: boolean; onOpen: () => void; danger?: boolean;
  children: ReactNode;
}) {
  const Chevron = open ? IconChevronDown : IconChevronRight;
  return (
    <Box style={{ border: "1px solid var(--mantine-color-default-border)", borderRadius: "var(--mantine-radius-sm)" }}>
      <Group gap="xs" px="sm" py={8} wrap="nowrap">
        <Checkbox checked={checked} indeterminate={indeterminate} onChange={onToggleAll} color={danger ? "red" : undefined} aria-label={title} />
        <UnstyledButton onClick={onOpen} style={{ flex: 1, minWidth: 0 }}>
          <Group gap={6} wrap="nowrap" justify="space-between">
            <div style={{ minWidth: 0 }}>
              <Text size="sm" fw={600}>{title}</Text>
              <Text size="xs" c="dimmed">{detail}</Text>
            </div>
            <Chevron size={16} style={{ flexShrink: 0 }} />
          </Group>
        </UnstyledButton>
      </Group>
      <Collapse expanded={open}>
        <Box style={{ borderTop: "1px solid var(--mantine-color-default-border)" }}>{children}</Box>
      </Collapse>
    </Box>
  );
}

/**
 * "Platz freigeben" mit Auswahl: holt per Scan-Job vom Host, was gelöscht würde (ungenutzte Images, Volumes, Build-Cache),
 * zeigt jede Position mit Größe und Alter bzw. Compose-Projekt und löscht nur die ausgewählten. Der Agent prüft beim
 * Löschen noch einmal und überspringt, was inzwischen wieder genutzt wird. Volumes sind ab Werk nicht ausgewählt.
 */
export function PruneDialog({ hostId, disk, onClose, onSubmit, busy }: {
  hostId: string; disk: DockerDiskUsage[]; onClose: () => void; onSubmit: (request: ContainerManageRequest) => Promise<void>; busy: boolean;
}) {
  const { t } = useTranslation();
  const [scan, setScan] = useState<PruneScan | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [images, setImages] = useState<Set<string>>(new Set());
  const [volumes, setVolumes] = useState<Set<string>>(new Set());
  const [cache, setCache] = useState(false);
  const [open, setOpen] = useState<"images" | "volumes" | null>("images");

  // Scan-Job starten und warten, bis der Agent geantwortet hat (bis zum nächsten Abruf)
  useEffect(() => {
    let stop = false;
    let timer: ReturnType<typeof setTimeout> | undefined;
    (async () => {
      try {
        const job = await api.post<{ id: string }>(`/api/hosts/${hostId}/containers/manage`, { op: "scan" });
        const poll = async () => {
          if (stop) return;
          try {
            const read = await api.get<ComposeRead>(`/api/hosts/${hostId}/containers/result/${job.id}`);
            if (read.scan) {
              setScan(read.scan);
              setImages(new Set(read.scan.images.map((i) => i.id))); // Images: neu ziehbar, ab Werk ausgewählt
              setCache(read.scan.buildCacheSize > 0);
            } else if (isActiveJob(read.status)) {
              timer = setTimeout(() => void poll(), 2000);
            } else {
              setError(read.summary ?? t("manager.scanFailed"));
            }
          } catch (e) {
            setError(errorText(e));
          }
        };
        await poll();
      } catch (e) {
        setError(errorText(e));
      }
    })();
    return () => {
      stop = true;
      if (timer) clearTimeout(timer);
    };
  }, [hostId, t]);

  const total = useMemo(() => (scan ? selectedSize(scan, images, volumes, cache) : 0), [scan, images, volumes, cache]);
  const nothing = images.size === 0 && volumes.size === 0 && !cache;

  const toggle = (set: Set<string>, setter: (s: Set<string>) => void, key: string) => {
    const next = new Set(set);
    if (next.has(key)) next.delete(key);
    else next.add(key);
    setter(next);
  };
  const reclaim = (type: string) => disk.find((d) => d.type.toLowerCase().startsWith(type))?.reclaimable ?? null;

  const submit = () => void onSubmit({ op: "clean", images: [...images], volumeNames: [...volumes], buildCache: cache });

  return (
    <Modal opened onClose={onClose} size="xl" title={<Text fw={800}>{t("manager.pruneTitle")}</Text>}>
      <Stack gap="sm">
        <Text size="sm" c="dimmed">{t("manager.pruneIntro")}</Text>
        {error && <Alert color="red">{error}</Alert>}
        {!scan && !error && (
          <Stack gap={6}>
            <Group gap="xs"><Loader size="sm" /><Text size="sm" c="dimmed">{t("manager.scanning")}</Text></Group>
            {disk.length > 0 && (
              <Text size="xs" c="dimmed">
                {t("manager.scanEstimate", {
                  images: formatBytes(reclaim("images") ?? 0), cache: formatBytes(reclaim("build") ?? 0), volumes: formatBytes(reclaim("local volumes") ?? 0),
                })}
              </Text>
            )}
          </Stack>
        )}

        {scan && (
          <>
            <Section title={t("manager.sectionImages", { count: scan.images.length })}
              detail={t("manager.sectionSelected", { count: images.size, size: formatBytes(scan.images.filter((i) => images.has(i.id)).reduce((n, i) => n + i.size, 0)) })}
              checked={scan.images.length > 0 && images.size === scan.images.length} indeterminate={images.size > 0 && images.size < scan.images.length}
              onToggleAll={() => setImages(images.size === scan.images.length ? new Set() : new Set(scan.images.map((i) => i.id)))}
              open={open === "images"} onOpen={() => setOpen(open === "images" ? null : "images")}>
              {scan.images.length === 0 ? <Text size="sm" c="dimmed" p="sm">{t("manager.noneImages")}</Text> : (
                <ScrollArea.Autosize mah={280}>
                  <Table>
                    <Table.Tbody>
                      {scan.images.map((i) => (
                        <Table.Tr key={i.id}>
                          <Table.Td w={36}><Checkbox checked={images.has(i.id)} onChange={() => toggle(images, setImages, i.id)} aria-label={i.references[0] ?? i.id} /></Table.Td>
                          <Table.Td>
                            {i.references.length > 0
                              ? i.references.map((r) => <Text key={r} size="xs" ff="monospace" style={{ overflowWrap: "anywhere" }}>{r}</Text>)
                              : <Text size="xs" ff="monospace" c="dimmed">{t("manager.untagged", { id: i.id.replace("sha256:", "").slice(0, 12) })}</Text>}
                          </Table.Td>
                          <Table.Td ta="right" style={{ fontVariantNumeric: "tabular-nums", whiteSpace: "nowrap" }}><Text size="xs">{formatBytes(i.size)}</Text></Table.Td>
                          <Table.Td style={{ whiteSpace: "nowrap" }}>
                            <Tooltip label={i.created ?? ""} disabled={!i.created}>
                              <Text size="xs" c="dimmed">{dockerDate(i.created) ? t("manager.created", { when: formatRelative(dockerDate(i.created)) }) : "–"}</Text>
                            </Tooltip>
                          </Table.Td>
                        </Table.Tr>
                      ))}
                    </Table.Tbody>
                  </Table>
                </ScrollArea.Autosize>
              )}
            </Section>

            <Box style={{ border: "1px solid var(--mantine-color-default-border)", borderRadius: "var(--mantine-radius-sm)" }} px="sm" py={8}>
              <Checkbox checked={cache} onChange={(e) => setCache(e.currentTarget.checked)} disabled={scan.buildCacheSize === 0 && scan.buildCacheCount === 0}
                label={<Text size="sm" fw={600}>{t("manager.sectionCache")}</Text>}
                description={t("manager.cacheDetail", { count: scan.buildCacheCount, size: formatBytes(scan.buildCacheSize) })} />
            </Box>

            <Section danger title={t("manager.sectionVolumes", { count: scan.volumes.length })}
              detail={volumes.size === 0 ? t("manager.volumesNoneSelected") : t("manager.sectionSelected", {
                count: volumes.size, size: scan.volumeSizesKnown ? formatBytes(scan.volumes.filter((v) => volumes.has(v.name)).reduce((n, v) => n + (v.size ?? 0), 0)) : "?",
              })}
              checked={scan.volumes.length > 0 && volumes.size === scan.volumes.length} indeterminate={volumes.size > 0 && volumes.size < scan.volumes.length}
              onToggleAll={() => setVolumes(volumes.size === scan.volumes.length ? new Set() : new Set(scan.volumes.map((v) => v.name)))}
              open={open === "volumes"} onOpen={() => setOpen(open === "volumes" ? null : "volumes")}>
              <Alert color="red" radius={0} p="xs" icon={<IconAlertTriangle size={16} />}><Text size="xs">{t("manager.volumesListWarning")}</Text></Alert>
              {scan.volumes.length === 0 ? <Text size="sm" c="dimmed" p="sm">{t("manager.noneVolumes")}</Text> : (
                <ScrollArea.Autosize mah={240}>
                  <Table>
                    <Table.Tbody>
                      {scan.volumes.map((v) => (
                        <Table.Tr key={v.name}>
                          <Table.Td w={36}><Checkbox color="red" checked={volumes.has(v.name)} onChange={() => toggle(volumes, setVolumes, v.name)} aria-label={v.name} /></Table.Td>
                          <Table.Td><Text size="xs" ff="monospace" style={{ overflowWrap: "anywhere" }}>{v.name}</Text></Table.Td>
                          <Table.Td>
                            {v.project
                              ? <Tooltip label={t("manager.volumeProjectHint", { project: v.project })} multiline w={260}><Badge size="sm" color="orange">{t("manager.volumeProject", { project: v.project })}</Badge></Tooltip>
                              : <Text size="xs" c="dimmed">{t("manager.volumeNoProject")}</Text>}
                          </Table.Td>
                          <Table.Td ta="right" style={{ fontVariantNumeric: "tabular-nums", whiteSpace: "nowrap" }}><Text size="xs">{v.size == null ? "–" : formatBytes(v.size)}</Text></Table.Td>
                        </Table.Tr>
                      ))}
                    </Table.Tbody>
                  </Table>
                </ScrollArea.Autosize>
              )}
            </Section>
          </>
        )}

        <Group justify="space-between" gap="xs">
          <Text size="xs" c="dimmed">{scan ? t("manager.selectedTotal", { size: formatBytes(total) }) : ""}</Text>
          <Group gap="xs">
            <Button variant="default" onClick={onClose}>{t("common.cancel")}</Button>
            <Button color="red" loading={busy} disabled={!scan || nothing} onClick={submit}>{t("manager.deleteSelected")}</Button>
          </Group>
        </Group>
      </Stack>
    </Modal>
  );
}
