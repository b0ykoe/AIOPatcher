import { Alert, Button, Card, Code, List, SimpleGrid, Stack, Text } from "@mantine/core";
import { IconAlertTriangle, IconBrandDocker, IconListCheck, IconPower, IconSettings } from "@tabler/icons-react";
import { useTranslation } from "react-i18next";
import { Link } from "react-router";
import { useCan } from "../App";
import { CardTitle, PageHeader } from "../components/ui";

/**
 * Anleitung zum Mini-Docker-Manager: was er kann, warum er gefährlich ist, wie man ihn pro Host freischaltet und wieder
 * abschaltet. Verlinkt aus "Erste Schritte → Mehr entdecken" und aus dem Containers-Reiter, wenn der Host es nicht erlaubt.
 */
export function ContainerManagementHelp() {
  const { t } = useTranslation();
  const can = useCan();
  return (
    <Stack gap="md">
      <PageHeader title={t("managerHelp.title")} description={t("managerHelp.description")}
        actions={can.admin && (
          <Button variant="light" component={Link} to="/settings/features" leftSection={<IconSettings size={16} />}>{t("managerHelp.toFeatures")}</Button>
        )} />

      <Alert color="red" icon={<IconAlertTriangle size={18} />} title={t("managerHelp.whyTitle")}>
        <Text size="sm">{t("managerHelp.why")}</Text>
      </Alert>

      <SimpleGrid cols={{ base: 1, md: 2 }}>
        <Card>
          <CardTitle icon={<IconBrandDocker size={18} />}>{t("managerHelp.whatTitle")}</CardTitle>
          <List size="sm" spacing={6}>
            <List.Item>{t("managerHelp.what1")}</List.Item>
            <List.Item>{t("managerHelp.what2")}</List.Item>
            <List.Item>{t("managerHelp.what3")}</List.Item>
            <List.Item>{t("managerHelp.what4")}</List.Item>
          </List>
          <Text size="xs" c="dimmed" mt="sm">{t("managerHelp.stepsNote")}</Text>
        </Card>

        <Card>
          <CardTitle icon={<IconListCheck size={18} />}>{t("managerHelp.stepsTitle")}</CardTitle>
          <List type="ordered" size="sm" spacing="sm">
            <List.Item>{t("managerHelp.step1")}</List.Item>
            <List.Item>
              {t("managerHelp.step2")}
              <Code block fz="xs" mt={4}>sudo aiopatcher-agent policy set allowContainerManagement true</Code>
            </List.Item>
            <List.Item>
              {t("managerHelp.step3")}
              <Code block fz="xs" mt={4}>sudo aiopatcher-agent policy set allowComposeEdit true</Code>
            </List.Item>
            <List.Item>
              {t("managerHelp.step4")}
              <Code block fz="xs" mt={4}>sudo aiopatcher-agent policy set composeRoot /opt/stacks</Code>
            </List.Item>
          </List>
        </Card>
      </SimpleGrid>

      <Card>
        <CardTitle icon={<IconPower size={18} />}>{t("managerHelp.offTitle")}</CardTitle>
        <Text size="sm">{t("managerHelp.off")}</Text>
        <Code block fz="xs" mt="xs">{"sudo aiopatcher-agent policy set allowContainerManagement false\nsudo aiopatcher-agent policy set allowComposeEdit false"}</Code>
      </Card>
    </Stack>
  );
}
