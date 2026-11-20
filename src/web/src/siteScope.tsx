import { createContext, useCallback, useContext, useEffect, useMemo, useRef, useState, type ReactNode } from "react";
import { useLocation, useSearchParams } from "react-router";
import { ActionIcon, Badge, Box, Button, Group, Menu, Text } from "@mantine/core";
import { IconBuildingCommunity, IconCheck, IconChevronDown, IconShare, IconWorld } from "@tabler/icons-react";
import { useTranslation } from "react-i18next";
import { api, setMirrorBase, type Site } from "./api";
import { remoteStatusColor, type RemoteSiteItem } from "./sharing";

// Globale Site-Auswahl: gilt für alle Seiten, steht in der URL (?site=) und wird im Browser gemerkt.
// Eigene Sites stehen als ID darin, geteilte Sites anderer Instanzen als "shared:<id>".

const storageKey = "aiop.site";
const param = "site";
const sharedPrefix = "shared:";

/** Seiten einer geteilten Site lesen: Aktionen ausblenden (useCan meldet keine Rechte). */
export const RemoteReadOnly = createContext(false);

/** API-Pfade, die eine geteilte Site mit dem Datenumfang „Pages“ liefert, und ihr Name im Protokoll. */
const remoteViews: Record<string, string> = { "/api/utilization": "utilization", "/api/vulnerabilities": "vulnerabilities", "/api/software": "software" };

/** API-Pfad auf die geteilte Site umlenken (nur die geteilten Seiten, alles andere bleibt). */
export function toRemote(path: string, remoteId: string): string {
  const [base, query] = path.split("?");
  const view = remoteViews[base];
  return view ? `/api/remote-sites/${remoteId}/views/${view}${query ? `?${query}` : ""}` : path;
}

/** Auswahl-Wert einer geteilten Site (für Links wie /?site=shared:…). */
export const sharedSelection = (remoteId: string) => `${sharedPrefix}${remoteId}`;

function readStored(): string | null {
  try {
    return window.localStorage.getItem(storageKey);
  } catch {
    return null;
  }
}

function store(selection: string | null) {
  try {
    if (selection) window.localStorage.setItem(storageKey, selection);
    else window.localStorage.removeItem(storageKey);
  } catch {
    // privates Fenster o. Ä.: Auswahl gilt dann nur für diese Sitzung
  }
}

/** Hängt siteId an einen API-Pfad an (nichts bei „Alle Sites“). */
export function withSite(path: string, siteId: string | null): string {
  if (!siteId) return path;
  return `${path}${path.includes("?") ? "&" : "?"}siteId=${encodeURIComponent(siteId)}`;
}

interface SiteScopeValue {
  /** Eigene Site; null = alle Sites (oder eine geteilte Site ist gewählt) */
  siteId: string | null;
  site: Site | null;
  /** null, solange noch nicht geladen */
  sites: Site[] | null;
  /** Geteilte Sites anderer Instanzen (Site-Sharing). */
  remoteSites: RemoteSiteItem[];
  /** Gewählte geteilte Site: die Seiten zeigen dann deren Daten statt der eigenen, soweit die andere Instanz sie teilt. */
  remoteId: string | null;
  remote: RemoteSiteItem | null;
  /** Geteilte Site mit allen Seiten: die normalen Seiten laufen, nur lesend, mit Daten der anderen Instanz. */
  mirror: boolean;
  setSiteId: (siteId: string | null) => void;
  setRemoteId: (remoteId: string) => void;
  reloadSites: () => void;
  /** API-Pfad mit siteId der aktuellen Auswahl bzw. auf die geteilte Site umgelenkt; ändert sich mit der Auswahl. */
  scoped: (path: string) => string;
}

const SiteScopeContext = createContext<SiteScopeValue>({
  siteId: null, site: null, sites: null, remoteSites: [], remoteId: null, remote: null, mirror: false,
  setSiteId: () => {}, setRemoteId: () => {}, reloadSites: () => {}, scoped: (p) => p,
});

export const useSiteScope = () => useContext(SiteScopeContext);

export function SiteScopeProvider({ children }: { children: ReactNode }) {
  const [params, setParams] = useSearchParams();
  const { pathname } = useLocation();
  const urlSite = params.get(param);
  const [selection, setState] = useState<string | null>(() => urlSite ?? readStored());
  const [sites, setSites] = useState<Site[] | null>(null);
  const [remoteSites, setRemoteSites] = useState<RemoteSiteItem[]>([]);
  // Erst nach erfolgreichem Laden prüfen, ob die gewählte geteilte Site noch existiert.
  const [remoteLoaded, setRemoteLoaded] = useState(false);
  // Ziel einer eigenen Änderung: bis die URL nachgezogen hat, nicht aus der (alten) URL übernehmen.
  const pending = useRef<string | null | undefined>(undefined);

  const remoteId = selection?.startsWith(sharedPrefix) ? selection.slice(sharedPrefix.length) : null;
  const siteId = remoteId ? null : selection;

  const reloadSites = useCallback(() => {
    void api.get<Site[]>("/api/sites").then(setSites).catch(() => {});
    void api.get<RemoteSiteItem[]>("/api/remote-sites")
      .then((r) => {
        setRemoteSites(r);
        setRemoteLoaded(true);
      })
      .catch(() => setRemoteSites([]));
  }, []);

  // Beim Seitenwechsel neu laden: neue oder gelöschte Sites (Einstellungen) sollen gleich auftauchen.
  useEffect(reloadSites, [reloadSites, pathname]);

  const select = useCallback((next: string | null) => {
    pending.current = next;
    setState(next);
    setParams((p) => {
      const copy = new URLSearchParams(p);
      if (next) copy.set(param, next);
      else copy.delete(param);
      return copy;
    }, { replace: true });
  }, [setParams]);

  const setSiteId = useCallback((next: string | null) => select(next), [select]);
  const setRemoteId = useCallback((next: string) => select(sharedSelection(next)), [select]);

  // Jede Auswahl merken, auch die aus einem Link (?site=) beim ersten Laden.
  useEffect(() => store(selection), [selection]);

  // URL und Auswahl abgleichen: Link mit ?site= übernehmen, fehlt der Parameter (Navigation), wieder anhängen.
  useEffect(() => {
    if (pending.current !== undefined) {
      if (urlSite !== pending.current) return;
      pending.current = undefined;
    }
    if (urlSite && urlSite !== selection) {
      setState(urlSite);
    } else if (!urlSite && selection) {
      setParams((p) => {
        const copy = new URLSearchParams(p);
        copy.set(param, selection);
        return copy;
      }, { replace: true });
    }
  }, [urlSite, selection, setParams]);

  // Gelöschte oder unbekannte Site: zurück auf „Alle Sites“.
  useEffect(() => {
    if (sites && siteId && !sites.some((s) => s.id === siteId)) select(null);
    if (remoteLoaded && remoteId && !remoteSites.some((r) => r.id === remoteId)) select(null);
  }, [sites, siteId, remoteLoaded, remoteSites, remoteId, select]);

  const remote = remoteSites.find((r) => r.id === remoteId) ?? null;
  const mirror = !!remote?.mirror;
  // Vor dem Rendern der Seiten setzen, damit schon ihre ersten Abfragen an die richtige Instanz gehen.
  setMirrorBase(mirror ? `/api/remote-sites/${remoteId}/mirror` : null);

  // Stabil, solange die Auswahl gleich bleibt: Seiten laden nicht neu, nur weil die Site-Liste neu geladen wurde.
  // Gespiegelt: jede Site-Liste an die andere Instanz (nicht Geteiltes lehnt sie ab, statt eigene Daten zu zeigen).
  const scoped = useCallback(
    (path: string) => (remoteId ? (mirror ? `/api/remote-sites/${remoteId}/mirror${path}` : toRemote(path, remoteId)) : withSite(path, siteId)),
    [remoteId, siteId, mirror],
  );

  const value = useMemo<SiteScopeValue>(() => ({
    siteId,
    site: sites?.find((s) => s.id === siteId) ?? null,
    sites,
    remoteSites,
    remoteId,
    remote,
    mirror,
    setSiteId,
    setRemoteId,
    reloadSites,
    scoped,
  }), [siteId, sites, remoteSites, remoteId, remote, mirror, setSiteId, setRemoteId, reloadSites, scoped]);

  return <SiteScopeContext.Provider value={value}>{children}</SiteScopeContext.Provider>;
}

/**
 * Auswahl in der Kopfleiste: ab xs mit Namen, auf dem Handy als Symbol (violett, solange gefiltert). Erst sichtbar, wenn es
 * mehr als eine Site gibt; mit nur einer Site gibt es nichts zu wählen.
 */
export function SitePicker({ compact = false }: { compact?: boolean }) {
  const { t } = useTranslation();
  const { siteId, site, sites, remoteSites, remoteId, remote, setSiteId, setRemoteId, reloadSites } = useSiteScope();
  if (!sites || (sites.length < 2 && !siteId && !remoteId && remoteSites.length === 0)) return null;

  const filtered = siteId !== null || remoteId !== null;
  const label = remote?.name ?? site?.name ?? t("siteScope.all");
  const size = compact ? 20 : 16;
  const icon = remoteId ? <IconShare size={size} /> : siteId ? <IconBuildingCommunity size={size} /> : <IconWorld size={size} opacity={0.7} />;
  return (
    <Box hiddenFrom={compact ? "xs" : undefined} visibleFrom={compact ? undefined : "xs"}>
    <Menu position="bottom-end" width={260} onOpen={reloadSites}>
      <Menu.Target>
        {compact ? (
          <ActionIcon size={40} variant={filtered ? "light" : "default"} color={filtered ? "violet" : undefined} aria-label={`${t("siteScope.choose")}: ${label}`}>
            {icon}
          </ActionIcon>
        ) : (
          <Button
            variant={filtered ? "light" : "default"}
            color={filtered ? "violet" : undefined}
            h={40}
            px="sm"
            aria-label={t("siteScope.choose")}
            leftSection={icon}
            rightSection={<IconChevronDown size={14} opacity={0.6} />}
            styles={{ label: { maxWidth: 180 } }}
          >
            <Text span size="sm" fw={600} truncate>{label}</Text>
          </Button>
        )}
      </Menu.Target>
      <Menu.Dropdown>
        <Menu.Label>{t("siteScope.title")}</Menu.Label>
        <Menu.Item
          leftSection={<IconWorld size={16} />}
          rightSection={<IconCheck size={16} opacity={filtered ? 0 : 1} />}
          onClick={() => setSiteId(null)}
        >
          {t("siteScope.all")}
        </Menu.Item>
        <Menu.Divider />
        {sites.map((s) => (
          <Menu.Item
            key={s.id}
            leftSection={<IconBuildingCommunity size={16} />}
            rightSection={
              <Group gap={6} wrap="nowrap">
                <Badge size="sm" color="gray">{s.hostCount}</Badge>
                <IconCheck size={16} opacity={s.id === siteId ? 1 : 0} />
              </Group>
            }
            onClick={() => setSiteId(s.id)}
          >
            <Text size="sm" truncate>{s.name}</Text>
          </Menu.Item>
        ))}
        {remoteSites.length > 0 && (
          <>
            <Menu.Divider />
            <Menu.Label>{t("sharing.sharedWithMe")}</Menu.Label>
            {remoteSites.map((r) => (
              <Menu.Item
                key={r.id}
                leftSection={<IconShare size={16} />}
                rightSection={
                  <Group gap={6} wrap="nowrap">
                    <Badge size="sm" color={remoteStatusColor[r.status]}>{r.hostCount}</Badge>
                    <IconCheck size={16} opacity={r.id === remoteId ? 1 : 0} />
                  </Group>
                }
                onClick={() => setRemoteId(r.id)}
              >
                <Text size="sm" truncate>{r.name}</Text>
                <Text size="xs" c="dimmed" truncate>{r.instanceName}</Text>
              </Menu.Item>
            ))}
          </>
        )}
        <Text size="xs" c="dimmed" px="sm" py={6}>{t("siteScope.hint")}</Text>
      </Menu.Dropdown>
    </Menu>
    </Box>
  );
}
