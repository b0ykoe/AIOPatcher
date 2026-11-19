import { useEffect, useState } from "react";
import { Link, useNavigate } from "react-router";
import { Alert, Anchor, Badge, Box, Button, Code, Group, Loader, Modal, Stack, Table, Text, Tooltip, UnstyledButton } from "@mantine/core";
import { IconAlertTriangle, IconChevronDown, IconChevronUp, IconDownload, IconHammer, IconInfoCircle, IconRefresh, IconScript, IconSelector } from "@tabler/icons-react";
import { useTranslation } from "react-i18next";
import { api, formatRelative, type ContainerItem, type ContainerRecipe, type ContainerVulnList } from "../api";
import { useCan } from "../App";
import { FeatureOffHint, useFeatures } from "../features";
import { t as tGlobal } from "../i18n";
import { confirmAction, errorText, notifyError, notifySuccess } from "./ui";

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

/**
 * Container eines Hosts: Status (auch gestoppte), neuere Images in der Registry bzw. neues Basis-Image bei selbst
 * gebauten, relevante Lücken im Image. Spalten sortierbar, Standard: Auffälliges zuerst.
 * Gehört in eine Karte mit p={0} (Kopfzeile und Tabelle bringen ihren Innenabstand selbst mit).
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
      message: c.baseImage
        ? t("containers.rebuildText", { image: c.baseImage, compose: c.compose ?? "" })
        : t("containers.updateText", { image: c.image, compose: c.compose ?? "" }),
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

  const [sort, setSort] = useState<{ key: SortKey; desc: boolean }>({ key: "state", desc: false });

  const sorted = [...containers].sort((a, b) => {
    const result = compare[sort.key](a, b) || (sort.key === "state" ? registryRank(a) - registryRank(b) : 0);
    return (sort.desc ? -result : result) || a.name.localeCompare(b.name);
  });

  const header = (key: SortKey, label: string) => {
    const active = sort.key === key;
    const Icon = active ? (sort.desc ? IconChevronDown : IconChevronUp) : IconSelector;
    return (
      <Table.Th aria-sort={active ? (sort.desc ? "descending" : "ascending") : undefined}>
        <UnstyledButton onClick={() => setSort({ key, desc: active ? !sort.desc : false })}>
          <Group gap={4} wrap="nowrap">
            <Text size="sm" fw={600}>{label}</Text>
            <Icon size={14} opacity={active ? 1 : 0.5} />
          </Group>
        </UnstyledButton>
      </Table.Th>
    );
  };

  return (
    <>
    {hostId && (
      <Group justify="space-between" gap="xs" px="md" py="sm" wrap="wrap">
        <Text size="xs" c="dimmed">
          {tGlobal("security.freshness", { seen: formatRelative(seen), checked: formatRelative(checked) })}
        </Text>
        {can.operate && <Button size="compact-xs" variant="light" leftSection={<IconRefresh size={12} />} loading={busy} onClick={() => void recheck()}>{tGlobal("security.recheck")}</Button>}
      </Group>
    )}
    {hostId && can.operate && !isOn("AppUpdates") && containers.some((c) => c.updateAvailable && !c.updateBlocker) && <Box px="md" pb="sm"><FeatureOffHint feature="AppUpdates" /></Box>}
    {hostId && recipeProjects.map(({ recipe, project, first, count }) => (
      <Box key={`${recipe.id}|${project}`} px="md" pb="sm">
        <Alert color="blue" icon={<IconScript size={16} />}>
          <Group justify="space-between" gap="xs" wrap="wrap">
            <Stack gap={4} style={{ flex: "1 1 320px" }}>
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
      </Box>
    ))}
    <Table.ScrollContainer minWidth={760}>
      <Table>
        <Table.Thead>
          <Table.Tr>
            {header("name", t("sitesPage.name"))}
            {header("image", t("security.image"))}
            {header("state", t("jobs.colStatus"))}
            {header("registry", t("security.registry"))}
            {showCves && header("cves", t("containers.cves"))}
            {canUpdate && <Table.Th w={1} />}
          </Table.Tr>
        </Table.Thead>
        <Table.Tbody>
          {sorted.map((c) => (
            <Table.Tr key={c.name}>
              <Table.Td>
                <Text size="sm" fw={600}>{c.name}</Text>
                {c.compose && <Text size="xs" c="dimmed">Compose: {c.compose}</Text>}
              </Table.Td>
              <Table.Td>
                <Text size="xs" ff="monospace">{c.image}</Text>
                {c.baseImage && <Text size="xs" c="dimmed">{t("containers.base", { image: c.baseImage })}</Text>}
              </Table.Td>
              <Table.Td><StateCell c={c} /></Table.Td>
              <Table.Td><RegistryCell c={c} /></Table.Td>
              {showCves && <Table.Td><VulnCell c={c} onOpen={() => setVulnsOf(c.name)} /></Table.Td>}
              {canUpdate && (
                <Table.Td style={{ whiteSpace: "nowrap" }}>
                  {c.updateAvailable && (c.recipe ? (
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
                  ))}
                </Table.Td>
              )}
            </Table.Tr>
          ))}
        </Table.Tbody>
      </Table>
    </Table.ScrollContainer>
    {hostId && <ContainerVulnModal hostId={hostId} name={vulnsOf} onClose={() => setVulnsOf(null)} />}
    </>
  );
}
