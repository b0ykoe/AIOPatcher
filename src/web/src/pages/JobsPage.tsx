import { useEffect, useState } from "react";
import { api, type Job } from "../api";
import { JobTable } from "../components/JobTable";

export function JobsPage() {
  const [jobs, setJobs] = useState<Job[] | null>(null);

  useEffect(() => {
    const load = () => void api.get<Job[]>("/api/jobs?limit=100").then(setJobs);
    load();
    const timer = setInterval(load, 5000);
    return () => clearInterval(timer);
  }, []);

  return (
    <>
      <h1>Jobs</h1>
      {jobs ? <JobTable jobs={jobs} showHost /> : <p className="muted">Lade …</p>}
    </>
  );
}
