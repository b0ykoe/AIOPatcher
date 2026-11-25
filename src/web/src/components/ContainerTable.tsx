import { useEffect, useState, type ReactNode } from "react";
import { Link, useNavigate } from "react-router";
import { Alert, Anchor, Badge, Box, Button, Card, Code, Group, Loader, Modal, Select, Stack, Table, Text, Tooltip, UnstyledButton } from "@mantine/core";
import { IconAlertTriangle, IconArrowsSort, IconBox, IconDownload, IconHammer, IconInfoCircle, IconRefresh, IconScript, IconStack2 } from "@tabler/icons-react";
import { useTranslation } from "react-i18next";
import { api, formatRelative, type ContainerItem, type ContainerRecipe, type ContainerVulnList } from "../api";
import { useCan } from "../App";
import { FeatureOffHint, useFeatures } from "../features";
import { t as tGlobal } from "../i18n";
import { CardTitle, confirmAction, errorText, notifyError, notifySuccess } from "./ui";

type SortKey = "name" | "image" | "state" | "registry" | "cves";

/** Registry-Spalte: neues Image zuerst, dann Probleme, dann der Rest. */
const registryRank = (c: ContainerItem) => (c.pinned || (c.localImage && !c.baseImage) ? 4 : c.checkError ? 1 : c.updateAvailable ? 0 : c.checkedAt ? 3 : 2);

/** Status: Auffälliges zuerst (abgestürzt, ungesund, startet neu), dann laufende, zuletzt sauber beendete. */
const stateRank = (c: ContainerItem) => {
  const state = (c.state ?? "").toLowerCase();
  if ((state === "exited" || state === "dead") && c.exitCode !== 0) return 0;
  if (state === "running" && c.health === "unhealthy") return 1;
  if (state === "restarting") return 2;
  if (state === "running") return 3;
  if (state === "paused") return 4;
  return 5;
};

/** CVEs: aktiv ausgenutzte zuerst, dann nach Anzahl. */
const cveRank = (c: ContainerItem) => (c.vulns?.state === "ok" ? -(c.vulns.kev * 1000 + c.vulns.count) : 1);

const compare: Record<SortKey, (a: ContainerItem, b: ContainerItem) => number> = {
  name: (a, b) => a.name.localeCompare(b.name),
  image: (a, b) => a.image.localeCompare(b.image),
  state: (a, b) => stateRank(a) - stateRank(b),
  registry: (a, b) => registryRank(a) - registryRank(b),
  cves: (a, b) => cveRank(a) - cveRank(b),
};

const stateLabel = {
  running: "containers.state_running",
  exited: "containers.state_exited",
  dead: "containers.state_dead",
  restarting: "containers.state_restarting",
  paused: "containers.state_paused",
  created: "containers.state_created",
  removing: "containers.state_removing",
} as const;

/** Status mit Farbe: läuft (grün), ungesund (orange), abgestürzt (rot), sauber beendet (grau); darunter seit wann und Exit-Code. */
function StateCell({ c }: { c: ContainerItem }) {
  const { t } = useTranslation();
  const state = (c.state ?? "").toLowerCase();
  const stopped = state === "exited" || state === "dead";
  const color = state === "running" ? (c.health === "unhealthy" ? "orange" : "teal")
    : stopped ? (c.exitCode !== 0 ? "red" : "gray")
    : state === "restarting" ? "yellow" : "gray";
  const label = state === "running" && c.health === "unhealthy" ? t("containers.unhealthy")
    : state in stateLabel ? t(stateLabel[state as keyof typeof stateLabel]) : (c.state ?? "–");
  const since = state === "running" ? c.startedAt : stopped ? c.finishedAt : null;
  const detail = [since ? t("containers.since", { when: formatRelative(since) }) : null, stopped && c.exitCode != null ? t("containers.exitCode", { code: c.exitCode }) : null]
    .filter(Boolean).join(" · ");
  return (
    <Stack gap={2} align="flex-start">
      <Tooltip label={t("containers.restartPolicy", { policy: c.restartPolicy })} disabled={!c.restartPolicy}>
        <Badge size="sm" variant="light" color={color}>{label}</Badge>
      </Tooltip>
      {detail && <Text size="xs" c="dimmed" style={{ whiteSpace: "nowrap" }}>{detail}</Text>}
    </Stack>
  );
}

function RegistryCell({ c }: { c: ContainerItem }) {
  const { t } = useTranslation();
  if (c.localImage && !c.baseImage) {
    return (
      <Tooltip label={t("containers.localBuildHint")} multiline w={300}>
        <Text size="xs" c="dimmed" style={{ cursor: "help" }}>{t("containers.localBuild")}</Text>
      </Tooltip>
    );
  }
  if (c.pinned) return <Text size="xs" c="dimmed">{t("security.pinned")}</Text>;
  if (c.checkError) {
    return (
      <Tooltip label={c.checkError} multiline w={300}>
        <Text size="xs" c="dimmed">{t("security.checkFailed")}</Text>
      </Tooltip>
    );
  }
  const base = c.baseImage ? t("containers.baseHint", { image: c.baseImage }) : null;
  if (c.updateAvailable) {
    return (
      <Tooltip label={base} disabled={!base} multiline w={300}>
        <Badge size="sm" color="blue">{c.baseImage ? t("containers.baseNew") : t("security.newImage")}</Badge>
      </Tooltip>
    );
  }
  if (c.checkedAt) {
    return (
      <Tooltip label={[base, t("security.checked", { when: formatRelative(c.checkedAt) })].filter(Boolean).join(" ")} multiline w={300}>
        <Badge size="sm" color="teal">{t("security.upToDate")}</Badge>
      </Tooltip>
    );
  }
  return <Text size="xs" c="dimmed">…</Text>;
}

/** CVE-Spalte: Anzahl relevanter Lücken im Image (KEV rot), sonst warum es (noch) keine Angabe gibt. */
function VulnCell({ c, onOpen }: { c: ContainerItem; onOpen: () => void }) {
  const { t } = useTranslation();
  const v = c.vulns;
  if (!v || v.state === "off") return null;
  if (v.state === "ok") {
    return v.count === 0 ? (
      <Tooltip label={t("containers.cvesNoneHint", { count: v.packages, os: v.os ?? "", when: formatRelative(v.scannedAt) })} multiline w={300}>
        <Badge size="sm" variant="light" color="teal">{t("containers.cvesNone")}</Badge>
      </Tooltip>
    ) : (
      <UnstyledButton onClick={onOpen} aria-label={t("containers.vulnTitle", { name: c.name })}>
        <Badge size="sm" color={v.kev > 0 ? "red" : "orange"} style={{ cursor: "pointer" }}>
          {t("security.cveCount", { count: v.count })}{v.kev > 0 ? " · KEV" : ""}
        </Badge>
      </UnstyledButton>
    );
  }
  const [label, hint] = v.state === "pending" ? ["…", t("containers.cvesPending")]
    : v.state === "agent" ? [t("containers.cvesAgent"), t("containers.cvesAgentHint")]
    : [t("containers.cvesUnsupported"), v.error ?? ""];
  return (
    <Tooltip label={hint} multiline w={300}>
      <Text size="xs" c="dimmed" style={{ cursor: "help" }}>{label}</Text>
    </Tooltip>
  );
}

/** Relevante Lücken im Image eines Containers. */
function ContainerVulnModal({ hostId, name, onClose }: { hostId: string; name: string | null; onClose: () => void }) {
  const { t, i18n } = useTranslation();
  const [data, setData] = useState<ContainerVulnList | null>(null);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    if (!name) return;
    setData(null);
    setError(null);
    api.get<ContainerVulnList>(`/api/hosts/${hostId}/containers/${encodeURIComponent(name)}/vulnerabilities`).then(setData).catch((e) => setError(errorText(e)));
  }, [hostId, name]);

  const percent = (n: number | null) => (n == null ? "–" : `${(n * 100).toLocaleString(i18n.language, { maximumFractionDigits: 1 })} %`);

  return (
    <Modal opened={name !== null} onClose={onClose} size="xl" title={<Text fw={700}>{t("containers.vulnTitle", { name: name ?? "" })}</Text>}>
      {error && <Alert color="red" icon={<IconAlertTriangle size={18} />}>{error}</Alert>}
      {!error && !data && <Loader size="sm" />}
      {data && (
        <Stack gap="sm">
          <Text size="sm" c="dimmed">{t("containers.vulnIntro", { image: data.image, os: data.os ?? "–", count: data.packages })}</Text>
          {data.items.length === 0 ? <Text size="sm">{t("containers.vulnEmpty")}</Text> : (
            <Table.ScrollContainer minWidth={560}>
              <Table>
                <Table.Thead>
                  <Table.Tr>
                    <Table.Th>{t("containers.colCve")}</Table.Th>
                    <Table.Th>{t("containers.colPackage")}</Table.Th>
                    <Table.Th>{t("containers.colVersion")}</Table.Th>
                    <Table.Th ta="right">EPSS</Table.Th>
                  </Table.Tr>
                </Table.Thead>
                <Table.Tbody>
                  {data.items.map((v) => (
                    <Table.Tr key={`${v.cve}-${v.package}`}>
                      <Table.Td>
                        <Group gap={6} wrap="nowrap">
                          <Anchor component={Link} to={`/cves/${v.cve}`} size="sm" ff="monospace">{v.cve}</Anchor>
                          {v.isKev && (
                            <Tooltip label={v.kevName ?? "KEV"} disabled={!v.kevName}>
                              <Badge size="xs" color="red">KEV</Badge>
                            </Tooltip>
                          )}
                        </Group>
                      </Table.Td>
                      <Table.Td><Text size="sm">{v.package}</Text></Table.Td>
                      <Table.Td><Text size="xs" ff="monospace" c="dimmed">{v.installedVersion ?? "–"}</Text></Table.Td>
                      <Table.Td ta="right" style={{ fontVariantNumeric: "tabular-nums" }}>{percent(v.epss)}</Table.Td>
                    </Table.Tr>
                  ))}
                </Table.Tbody>
              </Table>
            </Table.ScrollContainer>
          )}
          <Text size="xs" c="dimmed">{t("containers.vulnFix")}</Text>
          {data.scannedAt && <Text size="xs" c="dimmed">{t("containers.scanned", { when: formatRelative(data.scannedAt) })}</Text>}
        </Stack>
      )}
    </Modal>
  );
}

const ALL = "__all";
const STACK = "__stack:";
/** Container-Namen beginnen unter dem Stack-Namen (Kartenrand + Icon + Abstand). */
const STACK_INDENT = 40;

const projectOf = (c: ContainerItem) => c.compose?.split("/")[0] ?? null;
const serviceOf = (c: ContainerItem) => c.compose?.split("/").slice(1).join("/") || null;
/** Läuft vorher (wird nach dem Update wieder gestartet); gestoppte bekommen das neue Image, bleiben aber aus. */
const wasRunning = (c: ContainerItem) => ["running", "restarting", "paused"].includes((c.state ?? "").toLowerCase());
/** Geht bei "Alle aktualisieren" mit: neues Image, per Compose aktualisierbar, kein eigenes Update-Skript. */
const updatable = (c: ContainerItem) => c.updateAvailable && !c.updateBlocker && !c.recipe;

/** Ein Stack: Compose-Projekt, oder ein einzelner Container ohne Compose (eigene Einheit mit einem Container). */
interface ContainerStack {
  key: string;
  project: string | null;
  title: string;
  items: ContainerItem[];
}

/**
 * Karte eines Stacks (wie Portainer): Kopf mit Name, Status, Anzahl und Compose-Datei, neuen Images und "Stack aktualisieren",
 * darunter die Container. Am Desktop eine Tabelle mit festen Spaltenbreiten (in allen Karten gleich, die Spalten fluchten),
 * am Telefon eine Liste.
 */
function StackCard({ stack, showCves, canUpdate, loading, onUpdate, children, mobile }: {
  stack: ContainerStack; showCves: boolean; canUpdate: boolean; loading: boolean; onUpdate: () => void; children: ReactNode; mobile: ReactNode;
}) {
  const { t } = useTranslation();
  const { project, items } = stack;
  const running = items.filter((c) => (c.state ?? "").toLowerCase() === "running").length;
  const crashed = items.some((c) => ["exited", "dead"].includes((c.state ?? "").toLowerCase()) && c.exitCode !== 0);
  const [color, label] = running === items.length ? ["teal", t("containers.state_running")]
    : running === 0 ? [crashed ? "red" : "gray", t("containers.stackStopped")]
    : ["orange", t("containers.stackPartial", { running, count: items.length })];
  const fresh = items.filter((c) => c.updateAvailable).length;
  const todo = items.filter(updatable).length;
  const files = items.find((c) => c.composeFiles)?.composeFiles?.split(",") ?? [];
  const Icon = project ? IconStack2 : IconBox;
  const description = (
    <>
      {t("containers.stackContainers", { count: items.length })}
      {" · "}
      {project ? (
        files.length > 0 ? (
          <Tooltip label={files.join("\n")} disabled={files.length < 2} style={{ whiteSpace: "pre" }}>
            <Text span inherit ff="monospace" style={{ wordBreak: "break-all" }}>{files[0]}{files.length > 1 ? ` +${files.length - 1}` : ""}</Text>
          </Tooltip>
        ) : "Compose"
      ) : t("containers.standaloneHint")}
    </>
  );
  return (
    <Card p={0}>
      <CardTitle inset icon={<Icon size={18} style={{ flexShrink: 0 }} opacity={0.7} />} badge={<Badge size="sm" color={color} style={{ flexShrink: 0 }}>{label}</Badge>}
        description={description}
        actions={(fresh > 0 || (canUpdate && project && todo > 0)) && (
          <>
            {fresh > 0 && <Badge size="sm" color="blue" visibleFrom="sm">{t("containers.stackNew", { count: fresh })}</Badge>}
            {canUpdate && project && todo > 0 && (
              <Button size="compact-xs" variant="light" leftSection={<IconDownload size={12} />} loading={loading} onClick={onUpdate}>
                {t("containers.updateStack")}
              </Button>
            )}
          </>
        )}>
        {stack.title}
      </CardTitle>
      <Box style={{ borderTop: "1px solid var(--mantine-color-default-border)" }}>
        <Table.ScrollContainer minWidth={720} visibleFrom="sm">
          <Table layout="fixed">
            <colgroup>
              <col />
              <col />
              <col style={{ width: 160 }} />
              <col style={{ width: 130 }} />
              {showCves && <col style={{ width: 110 }} />}
              {canUpdate && <col style={{ width: 130 }} />}
            </colgroup>
            <Table.Tbody>{children}</Table.Tbody>
          </Table>
        </Table.ScrollContainer>
        <Box hiddenFrom="sm">{mobile}</Box>
      </Box>
    </Card>
  );
}

const sortLabels = {
  state: "jobs.colStatus",
  name: "sitesPage.name",
  registry: "security.registry",
  cves: "containers.cves",
} as const satisfies Record<Exclude<SortKey, "image">, string>;

/**
 * Container eines Hosts, je Stack eine Karte (Compose-Projekt oder einzelner Container ohne Compose): Status (auch gestoppte),
 * neuere Images in der Registry bzw. neues Basis-Image bei selbst gebauten, relevante Lücken im Image. Sortierung oben
 * (Standard: Auffälliges zuerst), die Stacks folgen ihrem ersten Container. Bringt seine Karten selbst mit.
 */
export function ContainerTable({ containers, hostId, onRechecked }: { containers: ContainerItem[]; hostId?: string; onRechecked?: () => void }) {
  const can = useCan();
  const { isOn } = useFeatures();
  const canUpdate = !!hostId && can.operate && isOn("AppUpdates");
  const showCves = containers.some((c) => c.vulns && c.vulns.state !== "off");
  const [busy, setBusy] = useState(false);
  const [vulnsOf, setVulnsOf] = useState<string | null>(null);
  const seen = containers.map((c) => c.seenAt).filter((x): x is string => !!x).sort().at(-1) ?? null;
  const checked = containers.map((c) => c.checkedAt).filter((x): x is string => !!x).sort().at(0) ?? null;

  // Neu prüfen: Registry sofort, der Agent meldet seine Container beim nächsten Abruf (Sekunden bis wenige Minuten)
  const recheck = async () => {
    if (!hostId) return;
    setBusy(true);
    try {
      await api.post(`/api/hosts/${hostId}/containers/recheck`);
      notifySuccess(tGlobal("security.recheckStarted"));
      onRechecked?.();
    } catch (e) {
      notifyError(e);
    } finally {
      setBusy(false);
    }
  };
  const { t } = useTranslation();
  const [updating, setUpdating] = useState<string | null>(null);
  const navigate = useNavigate();

  // Ein Compose-Container: Image ziehen bzw. neu bauen, nur diesen Dienst neu erstellen (Job mit Log)
  const update = (c: ContainerItem) =>
    confirmAction({
      title: c.baseImage ? t("containers.rebuildTitle", { name: c.name }) : t("containers.updateTitle", { name: c.name }),
      message: (
        <>
          {c.baseImage
            ? t("containers.rebuildText", { image: c.baseImage, compose: c.compose ?? "" })
            : t("containers.updateText", { image: c.image, compose: c.compose ?? "" })}
          {!wasRunning(c) && <><br /><br />{t("containers.updateStoppedNote")}</>}
        </>
      ),
      confirmLabel: c.baseImage ? t("containers.rebuild") : t("containers.update"),
      onConfirm: async () => {
        setUpdating(c.name);
        try {
          const job = await api.post<{ id: string }>(`/api/hosts/${hostId}/containers/${encodeURIComponent(c.name)}/update`);
          notifySuccess(t("containers.updateStarted", { name: c.name }));
          navigate(`/jobs/${job.id}`); // Live-Log des Jobs
        } catch (e) {
          notifyError(e);
        } finally {
          setUpdating(null);
        }
      },
    });
  // App mit eigenem Update-Skript (Update-Rezept, z. B. mailcow): ein Ablauf für das ganze Compose-Projekt
  const recipeProjects = Object.values(
    containers.reduce<Record<string, { recipe: ContainerRecipe; project: string; first: ContainerItem; count: number }>>((acc, c) => {
      if (!c.recipe) return acc;
      const project = c.compose?.split("/")[0] ?? c.name;
      const key = `${c.recipe.id}|${project}`;
      acc[key] = acc[key] ? { ...acc[key], count: acc[key].count + 1 } : { recipe: c.recipe, project, first: c, count: 1 };
      return acc;
    }, {}),
  );

  const updateViaRecipe = (recipe: ContainerRecipe, project: string, c: ContainerItem) =>
    confirmAction({
      title: t("containers.recipeTitle", { app: recipe.app }),
      message: (
        <>
          {t("containers.recipeText", { name: recipe.name, project })}
          {recipe.note && <><br /><br />{recipe.note}</>}
          {recipe.docs && <><br /><br /><Anchor href={recipe.docs} target="_blank" rel="noreferrer" size="sm">{t("containers.recipeDocs")}</Anchor></>}
        </>
      ),
      confirmLabel: t("containers.recipeUpdate", { name: recipe.name }),
      onConfirm: async () => {
        setUpdating(c.name);
        try {
          const job = await api.post<{ id: string }>(`/api/hosts/${hostId}/containers/${encodeURIComponent(c.name)}/update`);
          notifySuccess(t("containers.recipeStarted", { name: recipe.name }));
          navigate(`/jobs/${job.id}`); // Live-Log des Skripts
        } catch (e) {
          notifyError(e);
        } finally {
          setUpdating(null);
        }
      },
    });

  // Alle (bzw. alle eines Stacks) mit neuem Image in einem Job; der Agent bündelt je Compose-Projekt. Gestoppte bleiben gestoppt.
  const updateMany = (project: string | null) => {
    const scope = project === null ? containers : containers.filter((c) => projectOf(c) === project);
    const todo = scope.filter(updatable);
    const skipped = scope.filter((c) => c.updateAvailable && !updatable(c));
    const stopped = todo.filter((c) => !wasRunning(c));
    const names = (list: ContainerItem[]) => list.map((c) => c.name).join(", ");
    confirmAction({
      title: project === null ? t("containers.updateAllTitle", { count: todo.length }) : t("containers.updateStackTitle", { project }),
      message: (
        <>
          {t("containers.updateAllText")}
          <br /><br />
          <b>{t("containers.updateAllList")}</b> {names(todo)}
          {stopped.length > 0 && <><br /><b>{t("containers.updateAllStopped")}</b> {names(stopped)}</>}
          {skipped.length > 0 && (
            <><br /><b>{t("containers.updateAllSkipped")}</b> {skipped.map((c) => `${c.name} (${skipReason(c)})`).join(", ")}</>
          )}
        </>
      ),
      confirmLabel: project === null ? t("containers.updateAll", { count: todo.length }) : t("containers.updateStack"),
      onConfirm: async () => {
        setUpdating(project === null ? ALL : `${STACK}${project}`);
        try {
          const job = await api.post<{ id: string; containers: string[] }>(`/api/hosts/${hostId}/containers/update-all`, { project });
          notifySuccess(t("containers.updateAllStarted", { count: job.containers.length }));
          navigate(`/jobs/${job.id}`); // Live-Log des Jobs
        } catch (e) {
          notifyError(e);
        } finally {
          setUpdating(null);
        }
      },
    });
  };
  const skipReason = (c: ContainerItem) => (c.recipe ? t("containers.blockerShort.recipe") : t(`containers.blockerShort.${c.updateBlocker ?? "agent"}`));
  const updatableCount = containers.filter(updatable).length;

  const [sort, setSort] = useState<Exclude<SortKey, "image">>("state");
  const sorted = [...containers].sort((a, b) =>
    compare[sort](a, b) || (sort === "state" ? registryRank(a) - registryRank(b) : 0) || a.name.localeCompare(b.name));
  // Je Compose-Projekt ein Stack, ein Container ohne Compose ist ein Stack für sich; Reihenfolge nach dem ersten Container
  const stacks = sorted.reduce<ContainerStack[]>((acc, c) => {
    const project = projectOf(c);
    const key = project ? `compose:${project}` : `single:${c.name}`;
    const stack = acc.find((s) => s.key === key);
    if (stack) stack.items.push(c);
    else acc.push({ key, project, title: project ?? c.name, items: [c] });
    return acc;
  }, []);

  /** Letzte Spalte: Update-Knopf, sonst warum nicht (Update-Skript, kein Compose, Datei fehlt …). */
  const action = (c: ContainerItem) => c.updateAvailable && (c.recipe ? (
    <Tooltip label={t("containers.recipeRowHint", { app: c.recipe.app, name: c.recipe.name })} multiline w={300}>
      <Group gap={4} wrap="nowrap" c="dimmed" style={{ cursor: "help" }}>
        <IconScript size={14} />
        <Text size="xs">{t("containers.recipeRow", { name: c.recipe.name })}</Text>
      </Group>
    </Tooltip>
  ) : c.updateBlocker ? (
    <Tooltip label={t(`containers.blocker.${c.updateBlocker}`)} multiline w={300}>
      <Group gap={4} wrap="nowrap" c="dimmed" style={{ cursor: "help" }}>
        <IconInfoCircle size={14} />
        <Text size="xs">{t(`containers.blockerShort.${c.updateBlocker}`)}</Text>
      </Group>
    </Tooltip>
  ) : (
    <Button size="compact-xs" variant="light" leftSection={c.baseImage ? <IconHammer size={12} /> : <IconDownload size={12} />}
      loading={updating === c.name} onClick={() => update(c)}>
      {c.baseImage ? t("containers.rebuild") : t("containers.update")}
    </Button>
  ));

  const nameBlock = (c: ContainerItem) => (
    <>
      <Text size="sm" fw={600} style={{ overflowWrap: "anywhere" }}>{c.name}</Text>
      {serviceOf(c) && serviceOf(c) !== c.name && <Text size="xs" c="dimmed">{t("containers.service", { service: serviceOf(c) })}</Text>}
    </>
  );
  const imageBlock = (c: ContainerItem) => (
    <>
      <Text size="xs" ff="monospace" style={{ overflowWrap: "anywhere" }}>{c.image}</Text>
      {c.baseImage && <Text size="xs" c="dimmed" style={{ overflowWrap: "anywhere" }}>{t("containers.base", { image: c.baseImage })}</Text>}
    </>
  );

  return (
    <Stack gap="sm">
      {hostId && (
        <Card>
          <Group justify="space-between" gap="xs" wrap="wrap">
            <Text size="xs" c="dimmed">
              {tGlobal("security.freshness", { seen: formatRelative(seen), checked: formatRelative(checked) })}
            </Text>
            {/* Karten-Aktionen (Styleguide 9: compact-xs light); "Alle aktualisieren" rechts außen */}
            <Group gap="xs" wrap="wrap">
              <Select size="xs" w={170} aria-label={t("containers.sortBy")} leftSection={<IconArrowsSort size={14} />} allowDeselect={false}
                value={sort} onChange={(v) => v && setSort(v as typeof sort)}
                data={(Object.keys(sortLabels) as (keyof typeof sortLabels)[]).filter((k) => k !== "cves" || showCves)
                  .map((k) => ({ value: k, label: t("containers.sortOption", { column: t(sortLabels[k]) }) }))} />
              {can.operate && <Button size="compact-xs" variant="light" leftSection={<IconRefresh size={12} />} loading={busy} onClick={() => void recheck()}>{tGlobal("security.recheck")}</Button>}
              {canUpdate && updatableCount > 0 && (
                <Button size="compact-xs" variant="light" leftSection={<IconDownload size={12} />} loading={updating === ALL} onClick={() => updateMany(null)}>
                  {t("containers.updateAll", { count: updatableCount })}
                </Button>
              )}
            </Group>
          </Group>
          {can.operate && !isOn("AppUpdates") && containers.some((c) => c.updateAvailable && !c.updateBlocker) && <Box mt="sm"><FeatureOffHint feature="AppUpdates" /></Box>}
          {recipeProjects.map(({ recipe, project, first, count }) => (
            <Alert key={`${recipe.id}|${project}`} mt="sm" color="blue" icon={<IconScript size={16} />}>
              <Group justify="space-between" gap="xs" wrap="wrap">
                <Stack gap={4} style={{ flex: "1 1 260px" }}>
                  <Text size="sm">{t("containers.recipeDetected", { app: recipe.app, project, count })}</Text>
                  {!recipe.allowed && (
                    <Text size="xs" c="dimmed">
                      {t("containers.recipeNotAllowed")} <Code>sudo aiopatcher-agent policy set allowUpdateRecipes true</Code>
                    </Text>
                  )}
                </Stack>
                {canUpdate && recipe.allowed && (
                  <Button size="compact-xs" variant="light" leftSection={<IconScript size={12} />}
                    loading={updating === first.name} onClick={() => updateViaRecipe(recipe, project, first)}>
                    {t("containers.recipeUpdate", { name: recipe.name })}
                  </Button>
                )}
              </Group>
            </Alert>
          ))}
        </Card>
      )}
      {stacks.map((stack) => (
        <StackCard key={stack.key} stack={stack} showCves={showCves} canUpdate={canUpdate}
          loading={updating === `${STACK}${stack.project}`} onUpdate={() => updateMany(stack.project)}
          mobile={stack.items.map((c) => (
            <Group key={c.name} className="mobile-row" justify="space-between" align="flex-start" wrap="nowrap" gap="sm" style={{ cursor: "default" }}>
              <Stack gap={6} style={{ minWidth: 0, flex: 1 }}>
                <div>
                  {nameBlock(c)}
                  {imageBlock(c)}
                </div>
                <Group gap="xs" align="flex-start" wrap="wrap">
                  <StateCell c={c} />
                  <RegistryCell c={c} />
                  {showCves && <VulnCell c={c} onOpen={() => setVulnsOf(c.name)} />}
                </Group>
              </Stack>
              {canUpdate && <Box style={{ flexShrink: 0 }}>{action(c)}</Box>}
            </Group>
          ))}>
          {stack.items.map((c) => (
            <Table.Tr key={c.name}>
              <Table.Td pl={STACK_INDENT}>{nameBlock(c)}</Table.Td>
              <Table.Td>{imageBlock(c)}</Table.Td>
              <Table.Td><StateCell c={c} /></Table.Td>
              <Table.Td><RegistryCell c={c} /></Table.Td>
              {showCves && <Table.Td><VulnCell c={c} onOpen={() => setVulnsOf(c.name)} /></Table.Td>}
              {canUpdate && <Table.Td style={{ whiteSpace: "nowrap" }}>{action(c)}</Table.Td>}
            </Table.Tr>
          ))}
        </StackCard>
      ))}
      {hostId && <ContainerVulnModal hostId={hostId} name={vulnsOf} onClose={() => setVulnsOf(null)} />}
    </Stack>
  );
}
