# Style guide (web interface)

Binding for all pages in `src/web`. Goal: homelabs and small companies see at a glance what is going on and what needs
to be done. **Compact like Proxmox/Portainer, but friendly.** Anyone who builds or changes a page follows these
rules; deviations only with a reason in the commit.

## 1. Principles

- **State before detail:** At the top: whether everything is fine and what needs someone's attention; details go below or in tabs.
- **One main action per area** (gradient button), everything else is secondary.
- **The same thing looks the same:** A status has the same color, size and wording everywhere (section 6).
- **Theme instead of individual values:** What the theme (`theme.ts`) already sets is not repeated (no `withBorder` on
  Card, no `variant="light"` on Badge/Alert, no `withArrow` on Tooltip, no `verticalSpacing` on Table).
- **Texts** short and without jargon where possible; German in the informal "du" form, English plain and direct; every new string goes in `i18n/de.ts` and `en.ts`.

## 2. Page layout

| Page type | Layout |
|---|---|
| Area page (Hosts, Utilization, Reports …) | `PageHeader` (title, one line of description, actions on the right), content below |
| Area with subpages (Security, Auto-patch, Inventory) | `SectionPage`: title + tabs, active tab in `?tab=`; subpages use their own parameters (e.g. `?view=`) |
| Detail page (host, job, run, plan, CVE) | `DetailHeader`: back link (`BackLink`), icon, title + status, **one** line of metadata, actions on the right; tabs or content below |
| Settings | Side menu on the left (from md), a `Select` instead on phones |

- The content of a page sits in a `Stack gap="md"`; no chains of `mb` on every child.
- No fixed maximum width for pages; wide screens are used with grids (section 4), not with
  stretched elements.

## 3. Spacing and density (compact)

Mantine tokens only: `xs` 10 px, `sm` 12 px, `md` 16 px, `lg` 20 px, `xl` 32 px. Numbers only for fine spacing within a
row (icon ↔ text: `4`–`6`).

| Where | Value |
|---|---|
| Sections of a page | `gap="md"` |
| Card padding | Theme default `md` (do not set) |
| Card with table | `p={0}`, header row with `CardTitle inset` (px `md`, py `sm`) |
| Elements in a row (badges, buttons) | `gap="xs"` |
| Icon next to text | `gap={6}` |
| Card title → content | `mb="sm"` |
| Table rows | Theme default (compact, `xs`) |

## 4. Grids

| Content | Grid |
|---|---|
| Key figures (`StatCard`) | `SimpleGrid cols={{ base: 2, md: 4 }}` (6 values: `{ base: 2, md: 3, xl: 6 }`) |
| Equally important cards side by side | `SimpleGrid cols={{ base: 1, md: 2 }}` or `md: 3` |
| Many cards of the same kind (plans, sites) | `card-grid` (from 22rem) or `card-grid-wide` (from 30rem) |
| Small tiles (hosts on the home page) | `host-grid` (from 15rem) |
| Two large sections | `split-grid` |

Cards with value rows (section 8) are at most ~560 px wide; extra space is filled with more cards.

## 5. Typography

| Element | Style |
|---|---|
| Page title | `PageHeader` (Title order 2) |
| Detail page title | `DetailHeader` (26 px, fw 800) |
| Card title | `CardTitle` (Text fw 700, optional icon/badge, actions on the right, one hint line via `description`, `mb="sm"`) |
| Dialog title | `Text fw={800}` |
| Row text / primary text in lists | `size="sm"`, important ones `fw={600}` |
| Hints, metadata | `size="xs" c="dimmed"` (`sm` in running-text paragraphs) |
| Heading label (rare) | `size="xs" fw={700} tt="uppercase"`, letter spacing 0.6 |
| IDs, names, paths, versions | `ff="monospace"` or `Code` |
| Numbers in columns/values | `fontVariantNumeric: "tabular-nums"`, right-aligned |

## 6. Colors (meaning)

| Color | Meaning | Examples |
|---|---|---|
| **teal** | OK, successful, up to date | online, Succeeded, up to date, met, update available (fixable) |
| **red** | critical, error, actively exploited | offline, Failed, KEV (always filled), CRITICAL, firewall off |
| **orange** | deviation, rejected, caution | drift, deviation, Rejected, skipped, active patch pause, double patching |
| **yellow** | reboot required, warning | "Reboot required", Paused, warning level of utilization, host's own update automation active |
| **pink** | security updates | "n Sec", security key figure |
| **blue** | update/new version available | new image, newer program version, OPNsense update, new release |
| **cyan** | currently running | Running (with `dot-live`), maintenance window open |
| **violet** | plan, brand, utilization normal | patch plan, approval required, Admin, utilization bars up to 75 % |
| **grape** | built-in/privileged | root/Administrator, SSO, snapshot |
| **gray** | neutral, scheduled, past | queue (Queued), scheduled, cancelled, unknown, tags, "+n" |

Use the status components, do not rebuild them: `HostStatusLabel`, `JobStatusBadge`, `RunStatusBadge`,
`levelOf`/`levelColor` (utilization: yellow from 75 %, red from 90 %).

## 7. Badges

- Variant: theme default (light); `filled` only for KEV, `outline` for tags and "accepted".
- Size: **`sm` in tables and lists**, `xs` in small tiles, `md` only in the header of a detail page.
- One badge = one short state (1–3 words). Explanations belong in a tooltip.

## 8. Values and bars (like Proxmox)

- **`MetricRow`:** icon + name on the left, value on the right (`59 % (4,3 / 7,2 GB)`), a thin bar (6 px) below.
  Several rows stacked in one card ("Current", "Disks"), card at most ~560 px wide (`metric-grid`, rows in
  `metric-rows`). Long lists (more than 6 rows, e.g. ZFS datasets): card `metric-wide` with `metric-columns`, i.e.
  twice as wide with two columns read top to bottom, as long as there is room; otherwise one column.
- Values without a bar (uptime, load, IPs) as a `MetricRow` without a percentage.
- In tables: a short bar (at most 160 px) with the value next to it, never stretched across the whole column;
  in list columns `UsageCompact`.
- Colors only via `levelOf`/`levelColor`; from the warning level on, also the warning icon.

## 9. Buttons

| Purpose | Style |
|---|---|
| Main action of a page/dialog (confirm, save) | `variant="gradient" className="gradient-button"` |
| Dangerous confirmation (delete, reboot) | `color="red"` (filled), in `confirmAction` with `danger` |
| Secondary action | `variant="light"` |
| Cancel in a dialog | `variant="default"` |
| Action in a card, list or table row | `size="compact-xs" variant="light"` |
| Several actions per table row | "…" menu (`ActionIcon` + `Menu`) in the last column |
| Remove in a row | red `ActionIcon` with a trash can and tooltip |

## 10. Tables

- In a card with `p={0}`, header row via `CardTitle inset`, table in `Table.ScrollContainer`
  (`minWidth` 600 / 760 / 960 depending on the number of columns).
- Numbers right-aligned; actions in the last column; rows are not clickable, navigation via links.
- On phones (below `sm`) long tables as a card list (`mobile-row`), see the host list.

## 11. States

| State | Display |
|---|---|
| Loading | `Loader` in place of the content |
| Empty | `EmptyState` (icon, title, one sentence, optionally one action) |
| Error while loading | `Alert color="red"` with `errorText` |
| Feature turned off | `FeatureOffHint` |
| Hint / explanation | `Alert color="blue"` (info) or text `c="dimmed"` |
| Feedback after an action | `notifySuccess` / `notifyError`, confirmations via `confirmAction` |

## 12. Phone

- Navigation as a burger menu below `sm`; header actions stack below 48em (`page-header`).
- Grids collapse to one column; no horizontal scrolling of the page (tables scroll within themselves).
- Check every new page at 375 px (`document.documentElement.scrollWidth === innerWidth`).

## Checklist for new or changed pages

1. Correct page type (section 2), content in `Stack gap="md"`.
2. Only theme defaults and tokens, no repeated default props.
3. Colors and badges per sections 6/7, existing status components used.
4. Values with bars as `MetricRow`, cards no wider than needed.
5. Empty, loading and error states present.
6. Texts in `de.ts` and `en.ts`.
7. Checked in the browser: wide, ~1280 px and 375 px.
