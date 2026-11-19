import { useEffect, useState } from "react";
import { Card, Loader, SegmentedControl } from "@mantine/core";
import { useTranslation } from "react-i18next";
import { useSiteScope } from "../siteScope";
import { api, isActiveJob, type Job } from "../api";
import { JobTable } from "../components/JobTable";
import { PageHeader } from "../components/ui";

export function JobsPage() {
  const { t } = useTranslation();
  const [jobs, setJobs] = useState<Job[] | null>(null);
  const [filter, setFilter] = useState("all");
  const { scoped } = useSiteScope();

  useEffect(() => {
    const load = () => void api.get<Job[]>(scoped("/api/jobs?limit=200")).then(setJobs).catch(() => {});
    load();
    const timer = setInterval(load, 5000);
    return () => clearInterval(timer);
  }, [scoped]);

  const shown = jobs?.filter((j) =>
    filter === "active" ? isActiveJob(j.status) : filter === "auto" ? j.requestedBy === "scheduler" : filter === "failed" ? j.status === "Failed" || j.status === "Rejected" : true,
  );

  return (
    <>
      <PageHeader
        title={t("nav.jobs")}
        description={t("jobsPage.description")}
        actions={
          <SegmentedControl
            value={filter}
            onChange={setFilter}
            data={[
              { value: "all", label: t("jobsPage.filterAll") },
              { value: "active", label: t("jobsPage.filterActive") },
              { value: "auto", label: t("jobsPage.filterAuto") },
              { value: "failed", label: t("jobsPage.filterFailed") },
            ]}
          />
        }
      />
      <Card p={0}>{shown ? <JobTable jobs={shown} showHost /> : <Loader m="lg" />}</Card>
    </>
  );
}
