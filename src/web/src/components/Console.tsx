import { useEffect, useMemo, useRef, useState } from "react";
import { ActionIcon, CopyButton, Group, Switch, TextInput, Tooltip } from "@mantine/core";
import { IconCheck, IconCopy, IconSearch } from "@tabler/icons-react";
import { useTranslation } from "react-i18next";
import type { JobLogLine } from "../api";
import { locale } from "../i18n";

/** Job-Log in Terminal-Optik: farbige Zeilen, Suche, Kopieren, Mitlaufen. */
export function Console({ title, lines, live }: { title: string; lines: JobLogLine[]; live: boolean }) {
  const { t } = useTranslation();
  const [query, setQuery] = useState("");
  const [follow, setFollow] = useState(true);
  const end = useRef<HTMLDivElement>(null);

  useEffect(() => {
    if (follow) end.current?.scrollIntoView({ block: "nearest" });
  }, [lines, follow]);

  const filtered = useMemo(() => {
    const q = query.trim().toLowerCase();
    return q ? lines.filter((l) => l.text.toLowerCase().includes(q)) : lines;
  }, [lines, query]);

  const plain = lines.map((l) => `${new Date(l.timestamp).toLocaleTimeString(locale())} ${l.text}`).join("\n");

  return (
    <div className="console">
      <div className="console-bar">
        <span className="console-light" style={{ background: "#f87171" }} />
        <span className="console-light" style={{ background: "#fbbf24" }} />
        <span className="console-light" style={{ background: "#34d399" }} />
        <span style={{ marginLeft: 8 }}>{title}</span>
        {live && <span className="dot dot-live" style={{ marginLeft: 6 }} />}
        <span style={{ flex: 1 }} />
        <Group gap="xs" wrap="nowrap">
          <TextInput
            size="xs"
            placeholder={t("console.search")}
            leftSection={<IconSearch size={12} />}
            value={query}
            onChange={(e) => setQuery(e.currentTarget.value)}
            w={150}
            styles={{ input: { background: "rgba(255,255,255,0.05)", borderColor: "rgba(255,255,255,0.1)", color: "#cbd2e1" } }}
          />
          <Switch size="xs" label={t("console.follow")} checked={follow} onChange={(e) => setFollow(e.currentTarget.checked)} styles={{ label: { color: "#8a91ad" } }} />
          <CopyButton value={plain} timeout={2000}>
            {({ copied, copy }) => (
              <Tooltip label={copied ? t("common.copied") : t("console.copy")}>
                <ActionIcon size="sm" color={copied ? "teal" : "gray"} variant="subtle" onClick={copy} aria-label={t("console.copy")}>
                  {copied ? <IconCheck size={14} /> : <IconCopy size={14} />}
                </ActionIcon>
              </Tooltip>
            )}
          </CopyButton>
        </Group>
      </div>
      <div className="console-body">
        {filtered.length === 0 && <span style={{ color: "#4a5170" }}>{lines.length === 0 ? t("console.empty") : t("console.noMatch")}</span>}
        {filtered.map((l) => (
          <div key={l.id} className={lineClass(l.text)}>
            <span className="console-time">{new Date(l.timestamp).toLocaleTimeString(locale())}</span>
            <Highlight text={l.text} query={query} />
          </div>
        ))}
        {live && <span className="cursor" />}
        <div ref={end} />
      </div>
    </div>
  );
}

function lineClass(text: string): string | undefined {
  if (text.startsWith("$ ")) return "console-cmd";
  if (/fehler|error|failed|fehlgeschlagen|^e: /i.test(text)) return "console-err";
  if (text.startsWith("[agent]")) return /(ergebnis|result): succeeded|ok:|installiert|installed/i.test(text) ? "console-ok" : "console-agent";
  if (/\d+ upgraded, \d+ newly installed/.test(text)) return "console-ok";
  return undefined;
}

function Highlight({ text, query }: { text: string; query: string }) {
  const q = query.trim();
  if (!q) return <>{text}</>;
  const index = text.toLowerCase().indexOf(q.toLowerCase());
  if (index < 0) return <>{text}</>;
  return (
    <>
      {text.slice(0, index)}
      <mark className="console-mark">{text.slice(index, index + q.length)}</mark>
      {text.slice(index + q.length)}
    </>
  );
}
