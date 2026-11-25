import { useEffect, useMemo, useRef, useState, type ReactNode } from "react";
import { Link, useNavigate } from "react-router";
import { ActionIcon, Alert, Anchor, Box, Button, Checkbox, Code, Group, Loader, Menu, Modal, Stack, Text, Textarea, TextInput } from "@mantine/core";
import {
  IconAlertTriangle, IconDots, IconEraser, IconFileDiff, IconPencil, IconPlayerPlay, IconPlayerStop, IconPlus, IconRefresh, IconRotateClockwise, IconTrash,
} from "@tabler/icons-react";
import { useTranslation } from "react-i18next";
import { api, formatRelative, isActiveJob, type CachedCompose, type ComposeFile, type ComposeRead, type ContainerItem, type ContainerManageRequest, type ContainerManagement } from "../api";
import { useCan } from "../App";
import { useFeatures } from "../features";
import type { ContainerStack } from "./ContainerTable";
import { errorText, notifyError, notifySuccess } from "./ui";
import { PruneDialog } from "./PruneDialog";

/** Seite mit der Anleitung (was es kann, warum es gefährlich ist, wie man es pro Host freischaltet). */
export const MANAGER_HELP = "/help/container-management";

const running = (c: ContainerItem) => ["running", "restarting", "paused"].includes((c.state ?? "").toLowerCase());

/** Docker rechnet mit 1000er-Einheiten ("1.2 GB"). */
export function formatBytes(bytes: number): string {
  const units = ["B", "kB", "MB", "GB", "TB"];
  let value = bytes;
  let i = 0;
  while (value >= 1000 && i < units.length - 1) {
    value /= 1000;
    i++;
  }
  return i === 0 ? `${bytes} B` : `${value.toLocaleString(undefined, { maximumFractionDigits: 1 })} ${units[i]}`;
}

/** Zeilen-Diff (LCS) für die Ansicht vor dem Speichern. */
export function lineDiff(before: string, after: string): { type: "same" | "add" | "del"; text: string }[] {
  const a = before.split("\n");
  const b = after.split("\n");
  const dp = Array.from({ length: a.length + 1 }, () => new Uint32Array(b.length + 1));
  for (let i = a.length - 1; i >= 0; i--) {
    for (let j = b.length - 1; j >= 0; j--) {
      dp[i][j] = a[i] === b[j] ? dp[i + 1][j + 1] + 1 : Math.max(dp[i + 1][j], dp[i][j + 1]);
    }
  }
  const out: { type: "same" | "add" | "del"; text: string }[] = [];
  let i = 0;
  let j = 0;
  while (i < a.length && j < b.length) {
    if (a[i] === b[j]) {
      out.push({ type: "same", text: a[i] });
      i++;
      j++;
    } else if (dp[i + 1][j] >= dp[i][j + 1]) {
      out.push({ type: "del", text: a[i++] });
    } else {
      out.push({ type: "add", text: b[j++] });
    }
  }
  while (i < a.length) out.push({ type: "del", text: a[i++] });
  while (j < b.length) out.push({ type: "add", text: b[j++] });
  return out;
}

const NEW_STACK = `services:
  app:
    image: nginx:stable
    restart: unless-stopped
    ports:
      - "8080:80"
`;

interface ConfirmState {
  title: string;
  text: ReactNode;
  label: string;
  danger: boolean;
  /** Haken "auch Volumes löschen" (mit Warnung), sonst keiner */
  volumes?: string;
  request: ContainerManageRequest;
}

/**
 * Mini-Docker-Manager im Containers-Reiter (Funktion "Container verwalten", nur Admins, Host muss es in seiner policy.json
 * erlauben): Menüs je Container und Stack, "Platz freigeben", "Neuer Stack", Compose-Editor mit Diff. Jede Aktion läuft als
 * Job mit Live-Log; ohne Freigabe auf dem Host gibt es statt der Knöpfe einen Hinweis mit Anleitung.
 */
export function useContainerManager(hostId: string | undefined, management: ContainerManagement | null | undefined) {
  const { t } = useTranslation();
  const can = useCan();
  const { isOn } = useFeatures();
  const navigate = useNavigate();
  const [confirm, setConfirm] = useState<ConfirmState | null>(null);
  const [volumes, setVolumes] = useState(false);
  const [busy, setBusy] = useState(false);
  const [editor, setEditor] = useState<{ mode: "edit" | "create"; project?: string } | null>(null);
  const [prune, setPrune] = useState(false);

  const shown = !!hostId && can.admin && isOn("ContainerManagement") && !!management;
  // null = vom Agent nicht gemeldet: Knöpfe zeigen, der Agent prüft selbst
  const active = shown && management!.agentSupports && management!.allowed !== false;
  const composeEdit = active && management!.composeEdit !== false;
  const root = management?.composeRoot ?? "/opt/stacks";

  const run = async (request: ContainerManageRequest) => {
    setBusy(true);
    try {
      const job = await api.post<{ id: string }>(`/api/hosts/${hostId}/containers/manage`, request);
      notifySuccess(t("manager.started"));
      navigate(`/jobs/${job.id}`); // Live-Log
    } catch (e) {
      notifyError(e);
    } finally {
      setBusy(false);
      setConfirm(null);
    }
  };

  const ask = (state: ConfirmState) => {
    setVolumes(false);
    setConfirm(state);
  };

  const names = (list: ContainerItem[]) => list.map((c) => c.name).join(", ");
  const lifecycle = (op: "start" | "stop" | "restart", list: ContainerItem[], title: string) =>
    ask({ title, text: t(`manager.${op}Text`, { names: names(list) }), label: t(`manager.${op}`), danger: op === "stop", request: { op, containers: list.map((c) => c.name) } });

  const hint: ReactNode = !shown ? null
    : !management!.agentSupports ? <Text size="xs" c="dimmed" mt="sm">{t("manager.agentOld")}</Text>
    : management!.allowed === false ? (
      <Alert mt="sm" color="blue" icon={<IconAlertTriangle size={16} />}>
        <Text size="sm">{t("manager.notAllowed")}</Text>
        <Code block fz="xs" mt={6}>sudo aiopatcher-agent policy set allowContainerManagement true</Code>
        <Anchor component={Link} to={MANAGER_HELP} size="xs">{t("manager.tutorial")}</Anchor>
      </Alert>
    ) : null;

  const toolbar: ReactNode = active ? (
    <>
      <Button size="compact-xs" variant="light" color="orange" leftSection={<IconEraser size={12} />} onClick={() => setPrune(true)}>
        {t("manager.prune")}
      </Button>
      {composeEdit && (
        <Button size="compact-xs" variant="light" leftSection={<IconPlus size={12} />} onClick={() => setEditor({ mode: "create" })}>{t("manager.newStack")}</Button>
      )}
    </>
  ) : null;

  const containerMenu = (c: ContainerItem): ReactNode => !active ? null : (
    <Menu position="bottom-end" withinPortal>
      <Menu.Target>
        <ActionIcon variant="subtle" color="gray" size="sm" aria-label={t("manager.actions", { name: c.name })}><IconDots size={16} /></ActionIcon>
      </Menu.Target>
      <Menu.Dropdown>
        {running(c) ? (
          <>
            <Menu.Item leftSection={<IconRotateClockwise size={14} />} onClick={() => lifecycle("restart", [c], t("manager.restartTitle", { name: c.name }))}>{t("manager.restart")}</Menu.Item>
            <Menu.Item leftSection={<IconPlayerStop size={14} />} onClick={() => lifecycle("stop", [c], t("manager.stopTitle", { name: c.name }))}>{t("manager.stop")}</Menu.Item>
          </>
        ) : (
          <Menu.Item leftSection={<IconPlayerPlay size={14} />} onClick={() => lifecycle("start", [c], t("manager.startTitle", { name: c.name }))}>{t("manager.start")}</Menu.Item>
        )}
        <Menu.Divider />
        <Menu.Item color="red" leftSection={<IconTrash size={14} />}
          onClick={() => ask({ title: t("manager.removeTitle", { name: c.name }), text: t("manager.removeText"), label: t("manager.remove"), danger: true,
            volumes: t("manager.removeVolumes"), request: { op: "remove", containers: [c.name] } })}>
          {t("manager.remove")}
        </Menu.Item>
      </Menu.Dropdown>
    </Menu>
  );

  const stackMenu = (stack: ContainerStack): ReactNode => {
    if (!active) return null;
    const anyRunning = stack.items.some(running);
    const anyStopped = stack.items.some((c) => !running(c));
    const title = stack.title;
    const files = stack.items.some((c) => c.composeFiles) && stack.items.every((c) => c.updateBlocker !== "noFile" && c.updateBlocker !== "portainer");
    return (
      <Menu position="bottom-end" withinPortal>
        <Menu.Target>
          <ActionIcon variant="light" size="sm" aria-label={t("manager.actions", { name: title })}><IconDots size={14} /></ActionIcon>
        </Menu.Target>
        <Menu.Dropdown>
          {anyStopped && <Menu.Item leftSection={<IconPlayerPlay size={14} />} onClick={() => lifecycle("start", stack.items.filter((c) => !running(c)), t("manager.startTitle", { name: title }))}>{t("manager.start")}</Menu.Item>}
          {anyRunning && <Menu.Item leftSection={<IconRotateClockwise size={14} />} onClick={() => lifecycle("restart", stack.items.filter(running), t("manager.restartTitle", { name: title }))}>{t("manager.restart")}</Menu.Item>}
          {anyRunning && <Menu.Item leftSection={<IconPlayerStop size={14} />} onClick={() => lifecycle("stop", stack.items.filter(running), t("manager.stopTitle", { name: title }))}>{t("manager.stop")}</Menu.Item>}
          {stack.project && composeEdit && files && (
            <Menu.Item leftSection={<IconPencil size={14} />} onClick={() => setEditor({ mode: "edit", project: stack.project! })}>{t("manager.edit")}</Menu.Item>
          )}
          <Menu.Divider />
          {stack.project && files ? (
            <Menu.Item color="red" leftSection={<IconTrash size={14} />}
              onClick={() => ask({ title: t("manager.downTitle", { name: title }), text: t("manager.downText", { count: stack.items.length }), label: t("manager.down"), danger: true,
                volumes: t("manager.downVolumes"), request: { op: "down", project: stack.project! } })}>
              {t("manager.down")}
            </Menu.Item>
          ) : (
            <Menu.Item color="red" leftSection={<IconTrash size={14} />}
              onClick={() => ask({ title: t("manager.removeTitle", { name: names(stack.items) }), text: t("manager.removeText"), label: t("manager.remove"), danger: true,
                volumes: t("manager.removeVolumes"), request: { op: "remove", containers: stack.items.map((c) => c.name) } })}>
              {t("manager.remove")}
            </Menu.Item>
          )}
        </Menu.Dropdown>
      </Menu>
    );
  };

  const disk = management?.disk ?? [];

  const dialogs: ReactNode = shown ? (
    <>
      <Modal opened={confirm !== null} onClose={() => setConfirm(null)} title={<Text fw={800}>{confirm?.title}</Text>}>
        {confirm && (
          <Stack gap="sm">
            <Text size="sm" c="dimmed">{confirm.text}</Text>
            {confirm.volumes && (
              <>
                <Checkbox label={confirm.volumes} checked={volumes} onChange={(e) => setVolumes(e.currentTarget.checked)} color="red" />
                {volumes && <Alert color="red" p="xs"><Text size="sm">{t("manager.volumesWarning")}</Text></Alert>}
              </>
            )}
            <Group justify="flex-end" gap="xs">
              <Button variant="default" onClick={() => setConfirm(null)}>{t("common.cancel")}</Button>
              <Button {...(confirm.danger ? { color: "red" } : { variant: "gradient", className: "gradient-button" })} loading={busy}
                onClick={() => void run({ ...confirm.request, volumes: confirm.volumes ? volumes : undefined })}>
                {confirm.label}
              </Button>
            </Group>
          </Stack>
        )}
      </Modal>

      {/* Platz freigeben: erst scannen, dann nur Ausgewähltes löschen */}
      {prune && hostId && <PruneDialog hostId={hostId} disk={disk} onClose={() => setPrune(false)} onSubmit={run} busy={busy} />}

      {editor && hostId && (
        <ComposeEditor hostId={hostId} mode={editor.mode} project={editor.project} root={root} onClose={() => setEditor(null)} onSubmit={run} busy={busy} />
      )}
    </>
  ) : null;

  return { active, hint, toolbar, containerMenu, stackMenu, dialogs };
}

/**
 * Compose-Datei bearbeiten (liest sie per Job vom Host, dauert bis zum nächsten Abruf des Agents) oder neuen Stack anlegen.
 * Vor dem Speichern zeigt ein Diff, was sich ändert; der Agent prüft mit "compose config", sichert die alte Datei und
 * schreibt bei einem Fehler nichts.
 */
function ComposeEditor({ hostId, mode, project, root, onClose, onSubmit, busy }: {
  hostId: string; mode: "edit" | "create"; project?: string; root: string; onClose: () => void;
  onSubmit: (request: ContainerManageRequest) => Promise<void>; busy: boolean;
}) {
  const { t } = useTranslation();
  const [file, setFile] = useState<ComposeFile | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [content, setContent] = useState(mode === "create" ? NEW_STACK : "");
  const [name, setName] = useState("");
  const [review, setReview] = useState(false);
  const [up, setUp] = useState(mode === "create");
  /** Stand aus dem Vorrat des Servers (Zeitpunkt), null = frisch vom Host */
  const [cachedAt, setCachedAt] = useState<string | null>(null);
  const [fresh, setFresh] = useState(false);
  const [notice, setNotice] = useState<string | null>(null);
  /** Host hat eine andere Fassung, während hier schon geändert wurde */
  const [conflict, setConflict] = useState<ComposeFile | null>(null);
  const base = useRef<ComposeFile | null>(null);
  const text = useRef(content);
  text.current = content;
  const stopped = useRef(false);

  const take = (f: ComposeFile) => {
    base.current = f;
    setFile(f);
    setContent(f.content);
    setConflict(null);
  };

  /** Frisch vom Host lesen (Job, bis zum nächsten Abruf des Agents) und mit dem geöffneten Stand abgleichen. */
  const reload = async () => {
    setFresh(true);
    setNotice(null);
    try {
      const job = await api.post<{ id: string }>(`/api/hosts/${hostId}/containers/manage`, { op: "read", project });
      for (;;) {
        if (stopped.current) return;
        const read = await api.get<ComposeRead>(`/api/hosts/${hostId}/containers/compose/${job.id}`);
        if (read.file) {
          const current = base.current;
          if (!current || text.current === current.content) {
            take(read.file); // noch nichts geändert: einfach übernehmen
            setNotice(current && current.sha256 !== read.file.sha256 ? t("manager.reloadedChanged") : t("manager.reloadedSame"));
          } else if (current.sha256 !== read.file.sha256) {
            setConflict(read.file); // eigene Änderungen nicht überschreiben
          } else {
            setNotice(t("manager.reloadedSame"));
          }
          setCachedAt(null);
          return;
        }
        if (!isActiveJob(read.status)) {
          if (!base.current) setError(read.summary ?? t("manager.readFailed"));
          else setNotice(read.summary ?? t("manager.readFailed"));
          return;
        }
        await new Promise((r) => setTimeout(r, 2000));
      }
    } catch (e) {
      if (!base.current) setError(errorText(e));
      else setNotice(errorText(e));
    } finally {
      setFresh(false);
    }
  };

  // Sofort aus dem Vorrat öffnen; nur wenn keiner da ist oder der Host inzwischen einen anderen Hash meldet, vom Host lesen
  useEffect(() => {
    if (mode !== "edit" || !project) return;
    stopped.current = false;
    (async () => {
      let cached: CachedCompose | null = null;
      try {
        cached = await api.get<CachedCompose>(`/api/hosts/${hostId}/containers/compose-cache/${encodeURIComponent(project)}`);
      } catch {
        cached = null; // 404: kein Vorrat
      }
      if (stopped.current) return;
      if (cached) {
        take(cached.file);
        setCachedAt(cached.cachedAt);
      }
      if (!cached || !cached.current) await reload();
    })();
    return () => {
      stopped.current = true;
    };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [hostId, mode, project]);

  const diff = useMemo(() => (review && file ? lineDiff(file.content, content) : []), [review, file, content]);
  const changed = mode === "create" || (file !== null && content !== file.content);
  const nameOk = /^[a-z0-9][a-z0-9_-]{0,62}$/.test(name);

  const save = () => mode === "edit"
    ? onSubmit({ op: "write", project, content, expectedSha256: file?.sha256, up })
    : onSubmit({ op: "create", project: name, content, up });

  return (
    <Modal opened onClose={onClose} size="xl" title={<Text fw={800}>{mode === "edit" ? t("manager.editTitle", { name: project }) : t("manager.newStack")}</Text>}>
      <Stack gap="sm">
        <Alert color="red" p="xs" icon={<IconAlertTriangle size={16} />}><Text size="xs">{t("manager.composeWarning")}</Text></Alert>
        {mode === "create" && (
          <TextInput label={t("manager.stackName")} description={t("manager.stackFolder", { path: `${root}/${name || "…"}/compose.yaml` })}
            value={name} onChange={(e) => setName(e.currentTarget.value.toLowerCase())} error={name && !nameOk ? t("manager.stackNameInvalid") : undefined} data-autofocus />
        )}
        {mode === "edit" && !file && !error && (
          <Group gap="xs"><Loader size="sm" /><Text size="sm" c="dimmed">{t("manager.reading")}</Text></Group>
        )}
        {error && <Alert color="red">{error}</Alert>}
        {file && (
          <Group justify="space-between" gap="xs" wrap="wrap">
            <Text size="xs" c="dimmed">
              <Text span inherit ff="monospace">{file.path}</Text>
              {" · "}
              {fresh ? t("manager.reloading") : cachedAt ? t("manager.cachedAt", { when: formatRelative(cachedAt) }) : t("manager.fromHost")}
            </Text>
            <Button size="compact-xs" variant="subtle" leftSection={<IconRefresh size={12} />} loading={fresh} onClick={() => void reload()}>
              {t("manager.reload")}
            </Button>
          </Group>
        )}
        {notice && <Text size="xs" c="dimmed">{notice}</Text>}
        {conflict && (
          <Alert color="orange" p="xs" icon={<IconAlertTriangle size={16} />}>
            <Group justify="space-between" gap="xs" wrap="wrap">
              <Text size="xs" style={{ flex: "1 1 260px" }}>{t("manager.conflict")}</Text>
              <Button size="compact-xs" variant="light" color="orange" onClick={() => take(conflict)}>{t("manager.loadHostVersion")}</Button>
            </Group>
          </Alert>
        )}
        {(mode === "create" || file) && (review ? (
          <Box style={{ maxHeight: "55vh", overflow: "auto", border: "1px solid var(--mantine-color-default-border)", borderRadius: "var(--mantine-radius-sm)" }}>
            {diff.map((line, i) => (
              <Text key={i} size="xs" ff="monospace" px="xs" style={{
                whiteSpace: "pre-wrap", overflowWrap: "anywhere",
                background: line.type === "add" ? "var(--mantine-color-teal-light)" : line.type === "del" ? "var(--mantine-color-red-light)" : undefined,
              }}>
                {line.type === "add" ? "+ " : line.type === "del" ? "- " : "  "}{line.text}
              </Text>
            ))}
          </Box>
        ) : (
          <Textarea value={content} onChange={(e) => setContent(e.currentTarget.value)} autosize minRows={14} maxRows={28} spellCheck={false}
            styles={{ input: { fontFamily: "var(--mantine-font-family-monospace)", fontSize: 12 } }} aria-label={t("manager.composeFile")} />
        ))}
        {(mode === "create" || file) && (
          <Checkbox label={t("manager.upAfter")} checked={up} onChange={(e) => setUp(e.currentTarget.checked)} />
        )}
        <Group justify="space-between" gap="xs">
          <Text size="xs" c="dimmed">{t("manager.saveHint")}</Text>
          <Group gap="xs">
            <Button variant="default" onClick={review ? () => setReview(false) : onClose}>{review ? t("manager.backToEditor") : t("common.cancel")}</Button>
            {mode === "edit" && !review ? (
              <Button variant="light" leftSection={<IconFileDiff size={14} />} disabled={!changed} onClick={() => setReview(true)}>{t("manager.review")}</Button>
            ) : (
              <Button variant="gradient" className="gradient-button" loading={busy} disabled={!changed || conflict !== null || (mode === "create" && !nameOk)} onClick={() => void save()}>
                {mode === "edit" ? t("manager.save") : t("manager.create")}
              </Button>
            )}
          </Group>
        </Group>
      </Stack>
    </Modal>
  );
}
