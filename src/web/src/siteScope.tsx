import { createContext, useCallback, useContext, useEffect, useMemo, useRef, useState, type ReactNode } from "react";
import { useLocation, useNavigate, useSearchParams } from "react-router";
import { ActionIcon, Badge, Box, Button, Group, Menu, Text } from "@mantine/core";
import { IconBuildingCommunity, IconCheck, IconChevronDown, IconShare, IconWorld } from "@tabler/icons-react";
import { useTranslation } from "react-i18next";
import { api, type Site } from "./api";
import { remoteStatusColor, type RemoteSiteItem } from "./sharing";

// Globale Site-Auswahl: gilt für alle Seiten, steht in der URL (?site=) und wird im Browser gemerkt.

const storageKey = "aiop.site";
const param = "site";

function readStored(): string | null {
  try {
    return window.localStorage.getItem(storageKey);
  } catch {
    return null;
  }
}

function store(siteId: string | null) {
  try {
    if (siteId) window.localStorage.setItem(storageKey, siteId);
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
  /** null = alle Sites */
  siteId: string | null;
  site: Site | null;
  /** null, solange noch nicht geladen */
  sites: Site[] | null;
  /** Geteilte Sites anderer Instanzen (Site-Sharing): kein Filter, sondern eine eigene Seite (/shared/:id). */
  remoteSites: RemoteSiteItem[];
  setSiteId: (siteId: string | null) => void;
  reloadSites: () => void;
  /** API-Pfad mit siteId der aktuellen Auswahl; ändert sich mit der Auswahl (taugt als Effekt-Abhängigkeit). */
  scoped: (path: string) => string;
}

const SiteScopeContext = createContext<SiteScopeValue>({
  siteId: null, site: null, sites: null, remoteSites: [], setSiteId: () => {}, reloadSites: () => {}, scoped: (p) => p,
});

export const useSiteScope = () => useContext(SiteScopeContext);

export function SiteScopeProvider({ children }: { children: ReactNode }) {
  const [params, setParams] = useSearchParams();
  const { pathname } = useLocation();
  const urlSite = params.get(param);
  const [siteId, setState] = useState<string | null>(() => urlSite ?? readStored());
  const [sites, setSites] = useState<Site[] | null>(null);
  const [remoteSites, setRemoteSites] = useState<RemoteSiteItem[]>([]);
  // Ziel einer eigenen Änderung: bis die URL nachgezogen hat, nicht aus der (alten) URL übernehmen.
  const pending = useRef<string | null | undefined>(undefined);

  const reloadSites = useCallback(() => {
    void api.get<Site[]>("/api/sites").then(setSites).catch(() => {});
    void api.get<RemoteSiteItem[]>("/api/remote-sites").then(setRemoteSites).catch(() => setRemoteSites([]));
  }, []);

  // Beim Seitenwechsel neu laden: neue oder gelöschte Sites (Einstellungen) sollen gleich auftauchen.
  useEffect(reloadSites, [reloadSites, pathname]);

  const setSiteId = useCallback((next: string | null) => {
    pending.current = next;
    setState(next);
    setParams((p) => {
      const copy = new URLSearchParams(p);
      if (next) copy.set(param, next);
      else copy.delete(param);
      return copy;
    }, { replace: true });
  }, [setParams]);

  // Jede Auswahl merken, auch die aus einem Link (?site=) beim ersten Laden.
  useEffect(() => store(siteId), [siteId]);

  // URL und Auswahl abgleichen: Link mit ?site= übernehmen, fehlt der Parameter (Navigation), wieder anhängen.
  useEffect(() => {
    if (pending.current !== undefined) {
      if (urlSite !== pending.current) return;
      pending.current = undefined;
    }
    if (urlSite && urlSite !== siteId) {
      setState(urlSite);
    } else if (!urlSite && siteId) {
      setParams((p) => {
        const copy = new URLSearchParams(p);
        copy.set(param, siteId);
        return copy;
      }, { replace: true });
    }
  }, [urlSite, siteId, setParams]);

  // Gelöschte oder unbekannte Site: zurück auf „Alle Sites“.
  useEffect(() => {
    if (sites && siteId && !sites.some((s) => s.id === siteId)) setSiteId(null);
  }, [sites, siteId, setSiteId]);

  const value = useMemo<SiteScopeValue>(() => ({
    siteId,
    site: sites?.find((s) => s.id === siteId) ?? null,
    sites,
    remoteSites,
    setSiteId,
    reloadSites,
    scoped: (path: string) => withSite(path, siteId),
  }), [siteId, sites, remoteSites, setSiteId, reloadSites]);

  return <SiteScopeContext.Provider value={value}>{children}</SiteScopeContext.Provider>;
}

/**
 * Auswahl in der Kopfleiste: ab xs mit Namen, auf dem Handy als Symbol (violett, solange gefiltert). Erst sichtbar, wenn es
 * mehr als eine Site gibt; mit nur einer Site gibt es nichts zu wählen.
 */
export function SitePicker({ compact = false }: { compact?: boolean }) {
  const { t } = useTranslation();
  const { siteId, site, sites, remoteSites, setSiteId, reloadSites } = useSiteScope();
  const navigate = useNavigate();
  if (!sites || (sites.length < 2 && !siteId && remoteSites.length === 0)) return null;

  const label = site?.name ?? t("siteScope.all");
  const icon = siteId ? <IconBuildingCommunity size={compact ? 20 : 16} /> : <IconWorld size={compact ? 20 : 16} opacity={0.7} />;
  return (
    <Box hiddenFrom={compact ? "xs" : undefined} visibleFrom={compact ? undefined : "xs"}>
    <Menu position="bottom-end" width={260} onOpen={reloadSites}>
      <Menu.Target>
        {compact ? (
          <ActionIcon size={40} variant={siteId ? "light" : "default"} color={siteId ? "violet" : undefined} aria-label={`${t("siteScope.choose")}: ${label}`}>
            {icon}
          </ActionIcon>
        ) : (
          <Button
            variant={siteId ? "light" : "default"}
            color={siteId ? "violet" : undefined}
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
          rightSection={<IconCheck size={16} opacity={siteId ? 0 : 1} />}
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
                rightSection={<Badge size="sm" color={remoteStatusColor[r.status]}>{r.hostCount}</Badge>}
                onClick={() => navigate(`/shared/${r.id}`)}
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
