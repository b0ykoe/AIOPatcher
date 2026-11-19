/**
 * Auswahl-Einträge für Hosts. Gibt es Hosts aus mehr als einer Site, steht die Site hinter dem Namen ("pbs · Homelab"),
 * damit gleichnamige Hosts in Auswahlfeldern und gewählten Einträgen unterscheidbar sind.
 */
export function hostOptions<T extends { id: string; name: string; siteName?: string | null }>(hosts: T[], suffix?: (h: T) => string | null) {
  const multiSite = new Set(hosts.map((h) => h.siteName ?? "")).size > 1;
  return hosts.map((h) => ({ value: h.id, label: hostLabel(h, multiSite) + (suffix?.(h) ? ` ${suffix(h)}` : "") }));
}

export function hostLabel(h: { name: string; siteName?: string | null }, withSite: boolean) {
  return withSite && h.siteName ? `${h.name} · ${h.siteName}` : h.name;
}
