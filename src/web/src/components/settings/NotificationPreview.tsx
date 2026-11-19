import { useEffect, useState, type ReactNode } from "react";
import { Anchor, Badge, Box, Button, Code, Group, Loader, Stack, Text } from "@mantine/core";
import { useTranslation } from "react-i18next";
import { api, type ChannelType, type NotificationStyle } from "../../api";

/* eslint-disable @typescript-eslint/no-explicit-any -- Vorschau zeigt die Rohdaten der Dienste (Telegram, Discord …) */
type Payload = Record<string, any>;

/** Nur die Tags, die der Server für Telegram selbst erzeugt (b, i, a href); alles andere wird zu Text. */
function telegramHtml(html: string): ReactNode[] {
  const out: ReactNode[] = [];
  const re = /<(b|i)>([\s\S]*?)<\/\1>|<a href="([^"]*)">([\s\S]*?)<\/a>/g;
  let last = 0;
  let m: RegExpExecArray | null;
  const text = (s: string) => decode(s).split("\n").flatMap((line, i) => (i === 0 ? [line] : [<br key={`br${out.length}-${i}`} />, line]));
  while ((m = re.exec(html))) {
    if (m.index > last) out.push(...text(html.slice(last, m.index)));
    if (m[1] === "b") out.push(<b key={out.length}>{decode(m[2])}</b>);
    else if (m[1] === "i") out.push(<i key={out.length}>{decode(m[2])}</i>);
    else out.push(<Anchor key={out.length} href={decode(m[3])} target="_blank" size="sm">{decode(m[4])}</Anchor>);
    last = re.lastIndex;
  }
  if (last < html.length) out.push(...text(html.slice(last)));
  return out;
}

function decode(s: string) {
  return s.replace(/&lt;/g, "<").replace(/&gt;/g, ">").replace(/&quot;/g, '"').replace(/&#39;/g, "'").replace(/&amp;/g, "&");
}

/** **fett** bzw. *fett* (Slack) und _kursiv_ für die Text-Varianten. */
function markdownLite(s: string, boldMarker: "**" | "*"): ReactNode[] {
  const esc = boldMarker === "**" ? "\\*\\*" : "\\*";
  const re = new RegExp(`${esc}(.+?)${esc}|_(.+?)_|<([^|>]+)\\|([^>]+)>`, "g");
  const out: ReactNode[] = [];
  let last = 0;
  let m: RegExpExecArray | null;
  while ((m = re.exec(s))) {
    if (m.index > last) out.push(s.slice(last, m.index));
    if (m[1]) out.push(<b key={out.length}>{m[1]}</b>);
    else if (m[2]) out.push(<i key={out.length}>{m[2]}</i>);
    else out.push(<Anchor key={out.length} href={m[3]} target="_blank" size="sm">{m[4]}</Anchor>);
    last = re.lastIndex;
  }
  if (last < s.length) out.push(s.slice(last));
  return out;
}

const bubble = { background: "var(--mantine-color-dark-6)", borderRadius: 10, padding: "8px 12px", whiteSpace: "pre-wrap" as const, wordBreak: "break-word" as const };

/** Vorschau einer Beispielnachricht im gewählten Stil, so wie der Dienst sie ungefähr anzeigt (Daten vom Server). */
export function NotificationPreview({ type, style }: { type: ChannelType; style: NotificationStyle }) {
  const { t } = useTranslation();
  const [payload, setPayload] = useState<Payload | null>(null);

  useEffect(() => {
    let alive = true;
    setPayload(null);
    void api.post<Payload>("/api/notification-channels/preview", { type, style }).then((p) => alive && setPayload(p)).catch(() => {});
    return () => {
      alive = false;
    };
  }, [type, style]);

  return (
    <div>
      <Text size="sm" fw={500} mb={6}>{t("notifications.preview")}</Text>
      <Box p="sm" style={{ border: "1px solid var(--mantine-color-default-border)", borderRadius: 10 }}>
        {!payload ? <Loader size="sm" /> : <Rendered type={type} p={payload} />}
      </Box>
      <Text size="xs" c="dimmed" mt={4}>{t("notifications.previewHint")}</Text>
    </div>
  );
}

function Rendered({ type, p }: { type: ChannelType; p: Payload }) {
  const { t } = useTranslation();
  switch (type) {
    case "Telegram": {
      const button = p.reply_markup?.inline_keyboard?.[0]?.[0];
      return (
        <Stack gap={4} maw={420}>
          <Text size="sm" style={bubble}>{p.parse_mode === "HTML" ? telegramHtml(p.text) : p.text}</Text>
          {button && <Button size="xs" variant="default" component="a" href={button.url} target="_blank">{button.text}</Button>}
        </Stack>
      );
    }
    case "Discord": {
      const embed = p.embeds?.[0];
      if (!embed) return <Text size="sm" style={{ whiteSpace: "pre-wrap" }}>{markdownLite(p.content ?? "", "**")}</Text>;
      return (
        <Box maw={480} style={{ ...bubble, whiteSpace: "normal", borderLeft: `4px solid #${Number(embed.color).toString(16).padStart(6, "0")}` }}>
          <Anchor href={embed.url} target="_blank" fw={700} size="sm">{embed.title}</Anchor>
          <Text size="sm" mt={4}>{embed.description}</Text>
          <Group gap="lg" mt={8}>
            {(embed.fields ?? []).map((f: any) => (
              <div key={f.name}><Text size="xs" fw={700}>{f.name}</Text><Text size="xs">{f.value}</Text></div>
            ))}
          </Group>
          <Text size="xs" c="dimmed" mt={8}>{embed.footer?.text} · {new Date(embed.timestamp).toLocaleString()}</Text>
        </Box>
      );
    }
    case "Slack": {
      if (!p.blocks) return <Text size="sm" style={{ whiteSpace: "pre-wrap" }}>{markdownLite(p.text ?? "", "*")}</Text>;
      return (
        <Stack gap={6} maw={480}>
          {p.blocks.map((b: any, i: number) =>
            b.type === "header" ? <Text key={i} fw={800}>{b.text.text}</Text>
              : b.type === "section" ? <Text key={i} size="sm">{b.text.text}</Text>
                : b.type === "context" ? <Text key={i} size="xs" c="dimmed">{b.elements[0].text}</Text>
                  : <Group key={i}><Button size="xs" variant="default" component="a" href={b.elements[0].url} target="_blank">{b.elements[0].text.text}</Button></Group>)}
        </Stack>
      );
    }
    case "Email":
      return (
        <Stack gap={6}>
          <Text size="sm"><b>{t("notifications.previewSubject")}:</b> {p.subject}</Text>
          {p.html
            ? <iframe title="mail" srcDoc={p.html} sandbox="" style={{ width: "100%", height: 280, border: 0, borderRadius: 8, background: "#f1f3f5" }} />
            : <Text size="sm" style={{ whiteSpace: "pre-wrap" }}>{p.text}</Text>}
        </Stack>
      );
    case "Ntfy":
      return (
        <Box maw={420} style={{ ...bubble, whiteSpace: "normal" }}>
          {p.title && <Text fw={700} size="sm">{p.title}</Text>}
          <Text size="sm" style={{ whiteSpace: "pre-wrap" }}>{p.markdown ? markdownLite(p.body, "**") : p.body}</Text>
          {p.markdown && <Badge size="sm" variant="light" mt={6}>Open</Badge>}
        </Box>
      );
    default:
      return <Code block>{JSON.stringify(p, null, 2)}</Code>;
  }
}
