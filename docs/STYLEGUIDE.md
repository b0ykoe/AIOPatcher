# Styleguide (Weboberfläche)

Verbindlich für alle Seiten in `src/web`. Ziel: Homelab und kleine Firmen sehen auf einen Blick, was los ist und was
zu tun ist. **Kompakt wie Proxmox/Portainer, aber freundlich.** Wer eine Seite baut oder ändert, hält sich an diese
Regeln; Abweichungen nur mit Begründung im Commit.

## 1. Grundsätze

- **Zustand vor Detail:** Oben steht, ob alles gut ist und was jemanden braucht; Details kommen darunter oder in Reitern.
- **Eine Hauptaktion je Bereich** (Verlaufsknopf), alles andere ist sekundär.
- **Gleiches sieht gleich aus:** Ein Status hat überall dieselbe Farbe, Größe und Formulierung (Abschnitt 6).
- **Theme statt Einzelwerte:** Was das Theme (`theme.ts`) schon vorgibt, wird nicht wiederholt (kein `withBorder` auf
  Card, kein `variant="light"` auf Badge/Alert, kein `withArrow` auf Tooltip, kein `verticalSpacing` auf Table).
- **Texte** auf Deutsch in Du-Form, kurz, ohne Fachjargon wo es geht; jede neue Zeichenkette in `i18n/de.ts` und `en.ts`.

## 2. Seitenaufbau

| Seitentyp | Aufbau |
|---|---|
| Bereichsseite (Hosts, Auslastung, Berichte …) | `PageHeader` (Titel, eine Zeile Beschreibung, Aktionen rechts), darunter Inhalt |
| Bereich mit Unterseiten (Sicherheit, Auto-Patch, Inventar) | `SectionPage`: Titel + Reiter, aktiver Reiter in `?tab=`; Unterseiten nutzen eigene Parameter (z. B. `?view=`) |
| Detailseite (Host, Job, Lauf, Plan, CVE) | `DetailHeader`: Zurück-Link (`BackLink`), Icon, Titel + Status, **eine** Zeile Metadaten, Aktionen rechts; darunter Reiter oder Inhalt |
| Einstellungen | Seitenmenü links (ab md), darunter `Select` auf dem Handy |

- Inhalt einer Seite steht in einem `Stack gap="md"`; keine Ketten aus `mb` an jedem Kind.
- Keine feste Maximalbreite für Seiten; breite Bildschirme werden mit Rastern genutzt (Abschnitt 4), nicht mit
  gestreckten Elementen.

## 3. Abstände und Dichte (kompakt)

Nur Mantine-Tokens: `xs` 10 px, `sm` 12 px, `md` 16 px, `lg` 20 px, `xl` 32 px. Zahlen nur für Feinabstände in einer
Zeile (Icon ↔ Text: `4`–`6`).

| Wo | Wert |
|---|---|
| Abschnitte einer Seite | `gap="md"` |
| Karten-Innenabstand | Theme-Standard `md` (nicht setzen) |
| Karte mit Tabelle | `p={0}`, Kopfzeile mit `CardTitle inset` (px `md`, py `sm`) |
| Elemente in einer Zeile (Badges, Knöpfe) | `gap="xs"` |
| Icon neben Text | `gap={6}` |
| Kartentitel → Inhalt | `mb="sm"` |
| Tabellenzeilen | Theme-Standard (kompakt, `xs`) |

## 4. Raster

| Inhalt | Raster |
|---|---|
| Kennzahlen (`StatCard`) | `SimpleGrid cols={{ base: 2, md: 4 }}` (6 Werte: `{ base: 2, md: 3, xl: 6 }`) |
| Gleich wichtige Karten nebeneinander | `SimpleGrid cols={{ base: 1, md: 2 }}` bzw. `md: 3` |
| Viele gleichartige Karten (Pläne, Sites) | `card-grid` (ab 22rem) bzw. `card-grid-wide` (ab 30rem) |
| Kleine Kacheln (Hosts auf der Startseite) | `host-grid` (ab 15rem) |
| Zwei große Abschnitte | `split-grid` |

Karten mit Wertezeilen (Abschnitt 8) sind höchstens ~560 px breit; mehr Platz wird mit weiteren Karten gefüllt.

## 5. Typografie

| Element | Stil |
|---|---|
| Seitentitel | `PageHeader` (Title order 2) |
| Titel Detailseite | `DetailHeader` (26 px, fw 800) |
| Kartentitel | `CardTitle` (Text fw 700, optional Icon/Badge, Aktionen rechts, eine Hinweiszeile per `description`, `mb="sm"`) |
| Dialogtitel | `Text fw={800}` |
| Zeilentext / Primärtext in Listen | `size="sm"`, wichtig `fw={600}` |
| Hinweise, Metadaten | `size="xs" c="dimmed"` (in Fließtext-Absätzen `sm`) |
| Überschrift-Label (selten) | `size="xs" fw={700} tt="uppercase"`, Buchstabenabstand 0.6 |
| IDs, Namen, Pfade, Versionen | `ff="monospace"` bzw. `Code` |
| Zahlen in Spalten/Werten | `fontVariantNumeric: "tabular-nums"`, rechtsbündig |

## 6. Farben (Bedeutung)

| Farbe | Bedeutung | Beispiele |
|---|---|---|
| **teal** | in Ordnung, erfolgreich, aktuell | online, Succeeded, aktuell, erfüllt, Update da (behebbar) |
| **rot** | kritisch, Fehler, aktiv ausgenutzt | offline, Failed, KEV (immer gefüllt), CRITICAL, Firewall aus |
| **orange** | Abweichung, abgelehnt, Achtung | Drift, Abweichung, Rejected, übersprungen, aktive Patch-Pause, doppelt patchen |
| **gelb** | Neustart nötig, Warnung | „Neustart nötig“, Paused, Warnstufe der Auslastung, eigene Update-Automatik aktiv |
| **pink** | Security-Updates | „n Sec“, Security-Kennzahl |
| **blau** | Update/neue Version verfügbar | neues Image, neuere Programmversion, OPNsense-Update, neue Release |
| **cyan** | läuft gerade | Running (mit `dot-live`), Wartungsfenster offen |
| **violett** | Plan, Marke, Auslastung normal | Patch-Plan, Freigabe nötig, Admin, Auslastungsbalken bis 75 % |
| **grape** | eingebaut/privilegiert | root/Administrator, SSO, Snapshot |
| **grau** | neutral, geplant, vorbei | Warteschlange (Queued), geplant, abgebrochen, unbekannt, Tags, „+n“ |

Status-Komponenten benutzen, nicht nachbauen: `HostStatusLabel`, `JobStatusBadge`, `RunStatusBadge`,
`levelOf`/`levelColor` (Auslastung: ab 75 % gelb, ab 90 % rot).

## 7. Badges

- Variante: Theme-Standard (light); `filled` nur für KEV, `outline` für Tags und „akzeptiert“.
- Größe: **`sm` in Tabellen und Listen**, `xs` in kleinen Kacheln, `md` nur im Kopf einer Detailseite.
- Ein Badge = ein kurzer Zustand (1–3 Wörter). Erklärungen gehören in einen Tooltip.

## 8. Werte und Balken (wie Proxmox)

- **`MetricRow`:** Icon + Name links, Wert rechts (`59 % (4,3 / 7,2 GB)`), darunter ein dünner Balken (6 px).
  Mehrere Zeilen untereinander in einer Karte („Aktuell“, „Datenträger“), Karte höchstens ~560 px breit.
- Werte ohne Balken (Uptime, Load, IPs) als `MetricRow` ohne Prozent.
- In Tabellen: kurzer Balken (höchstens 160 px) mit Wert daneben, nie über die ganze Spalte gestreckt;
  in Listenspalten `UsageCompact`.
- Farben nur über `levelOf`/`levelColor`; ab Warnstufe zusätzlich das Warnsymbol.

## 9. Knöpfe

| Zweck | Stil |
|---|---|
| Hauptaktion einer Seite/eines Dialogs (Bestätigen, Speichern) | `variant="gradient" className="gradient-button"` |
| Gefährliche Bestätigung (Löschen, Neustart) | `color="red"` (gefüllt), in `confirmAction` mit `danger` |
| Sekundäre Aktion | `variant="light"` |
| Abbrechen im Dialog | `variant="default"` |
| Aktion in Karte, Liste oder Tabellenzeile | `size="compact-xs" variant="light"` |
| Mehrere Aktionen je Tabellenzeile | „…“-Menü (`ActionIcon` + `Menu`) in der letzten Spalte |
| Entfernen in einer Zeile | rotes `ActionIcon` mit Mülleimer und Tooltip |

## 10. Tabellen

- In einer Karte mit `p={0}`, Kopfzeile per `CardTitle inset`, Tabelle in `Table.ScrollContainer`
  (`minWidth` 600 / 760 / 960 je nach Spaltenzahl).
- Zahlen rechtsbündig; Aktionen in der letzten Spalte; Zeilen sind nicht klickbar, Navigation über Links.
- Auf dem Handy (unter `sm`) lange Tabellen als Kartenliste (`mobile-row`), siehe Hostliste.

## 11. Zustände

| Zustand | Darstellung |
|---|---|
| Lädt | `Loader` an der Stelle des Inhalts |
| Leer | `EmptyState` (Icon, Titel, ein Satz, optional eine Aktion) |
| Fehler beim Laden | `Alert color="red"` mit `errorText` |
| Funktion abgeschaltet | `FeatureOffHint` |
| Hinweis / Erklärung | `Alert color="blue"` (Info) bzw. Text `c="dimmed"` |
| Rückmeldung nach Aktion | `notifySuccess` / `notifyError`, Rückfragen über `confirmAction` |

## 12. Handy

- Navigation als Burger unter `sm`; Kopf-Aktionen stapeln sich unter 48em (`page-header`).
- Raster fallen auf eine Spalte; kein waagrechtes Scrollen der Seite (Tabellen scrollen in sich).
- Jede neue Seite bei 375 px prüfen (`document.documentElement.scrollWidth === innerWidth`).

## Checkliste für neue oder geänderte Seiten

1. Richtiger Seitentyp (Abschnitt 2), Inhalt in `Stack gap="md"`.
2. Nur Theme-Standards und Tokens, keine wiederholten Standard-Props.
3. Farben und Badges nach Abschnitt 6/7, vorhandene Status-Komponenten benutzt.
4. Werte mit Balken als `MetricRow`, Karten nicht breiter als nötig.
5. Leer-, Lade- und Fehlerzustand vorhanden.
6. Texte in `de.ts` und `en.ts`.
7. Im Browser geprüft: breit, ~1280 px und 375 px.
