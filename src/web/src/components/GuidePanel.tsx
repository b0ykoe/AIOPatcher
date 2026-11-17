import { Accordion, ActionIcon, Code, CopyButton, Group, List, Text, Tooltip } from "@mantine/core";
import { IconBook, IconCheck, IconCopy } from "@tabler/icons-react";
import { useTranslation } from "react-i18next";
import { currentLanguage } from "../i18n";
import { guides, type GuideTopic } from "./guides";

const fill = (text: string) => text.replaceAll("{origin}", window.location.origin);

/** Schritt-für-Schritt-Anleitung mit kopierbaren Befehlen; eingeklappt, damit Formulare kurz bleiben. */
export function GuidePanel({ topic, defaultOpen = false }: { topic: GuideTopic; defaultOpen?: boolean }) {
  const { t } = useTranslation();
  const guide = guides[currentLanguage()][topic];
  return (
    <Accordion variant="contained" radius="md" defaultValue={defaultOpen ? "guide" : null}>
      <Accordion.Item value="guide">
        <Accordion.Control icon={<IconBook size={18} />}>
          <Text size="sm" fw={600}>{t("guides.howTo", { title: guide.title })}</Text>
        </Accordion.Control>
        <Accordion.Panel>
          <List type="ordered" spacing="sm" size="sm">
            {guide.steps.map((step, i) => (
              <List.Item key={i}>
                <Text size="sm">{fill(step.text)}</Text>
                {step.code && (
                  <Group gap={4} wrap="nowrap" mt={4} align="flex-start">
                    <Code block style={{ flex: 1, whiteSpace: "pre-wrap", wordBreak: "break-all" }}>{fill(step.code)}</Code>
                    <CopyButton value={fill(step.code)}>
                      {({ copied, copy }) => (
                        <Tooltip label={copied ? t("common.copied") : t("common.copy")}>
                          <ActionIcon variant="subtle" color={copied ? "teal" : "gray"} onClick={copy} aria-label={t("common.copy")}>
                            {copied ? <IconCheck size={16} /> : <IconCopy size={16} />}
                          </ActionIcon>
                        </Tooltip>
                      )}
                    </CopyButton>
                  </Group>
                )}
              </List.Item>
            ))}
          </List>
          {guide.note && (
            <Text size="xs" c="dimmed" mt="sm">
              {guide.note}
            </Text>
          )}
        </Accordion.Panel>
      </Accordion.Item>
    </Accordion>
  );
}
