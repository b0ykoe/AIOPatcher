import { useEffect, useRef, useState } from "react";
import { Link, useParams } from "react-router";
import { api, describeJob, formatDateTime, isActiveJob, type Job, type JobDetail, type JobLogLine } from "../api";
import { JobStatusBadge } from "../components/JobStatusBadge";

export function JobPage() {
  const { id } = useParams();
  const [job, setJob] = useState<Job | null>(null);
  const [lines, setLines] = useState<JobLogLine[]>([]);
  const [follow, setFollow] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const lastId = useRef(0);
  const logEnd = useRef<HTMLDivElement>(null);

  useEffect(() => {
    let cancelled = false;
    let timer: ReturnType<typeof setTimeout> | undefined;
    lastId.current = 0;
    setLines([]);

    // Live-Log: nur neue Zeilen holen, solange der Job aktiv ist.
    const poll = async () => {
      try {
        const detail = await api.get<JobDetail>(`/api/jobs/${id}?afterId=${lastId.current}`);
        if (cancelled) return;
        setJob(detail.job);
        if (detail.lines.length > 0) {
          lastId.current = detail.lines[detail.lines.length - 1].id;
          setLines((prev) => [...prev, ...detail.lines]);
        }
        if (isActiveJob(detail.job.status)) timer = setTimeout(poll, 2000);
      } catch (e) {
        if (!cancelled) setError(e instanceof Error ? e.message : String(e));
      }
    };
    void poll();
    return () => {
      cancelled = true;
      clearTimeout(timer);
    };
  }, [id]);

  useEffect(() => {
    if (follow) logEnd.current?.scrollIntoView({ block: "end" });
  }, [lines, follow]);

  if (error) return <p className="error">{error}</p>;
  if (!job) return <p className="muted">Lade …</p>;

  const cancel = async () => {
    await api.post(`/api/jobs/${job.id}/cancel`);
    setJob({ ...job, status: "Cancelled" });
  };

  return (
    <>
      <p className="small">
        <Link to={`/hosts/${job.hostId}`}>← {job.hostName}</Link>
      </p>
      <div className="page-head">
        <h1>{describeJob(job)}</h1>
        <JobStatusBadge status={job.status} />
        <span className="spacer" />
        {job.status === "Queued" && (
          <button className="ghost danger" onClick={() => void cancel()}>
            Abbrechen
          </button>
        )}
      </div>

      {job.status === "Queued" && <div className="notice info">Wartet auf den nächsten Check-in des Agents (normalerweise unter einer Minute).</div>}
      {job.summary && <div className={`notice ${job.status === "Succeeded" ? "info" : "warn"}`}>{job.summary}</div>}

      <div className="facts">
        <Fact label="Host" value={`${job.hostName} (${job.siteName})`} />
        <Fact label="Angefordert von" value={job.requestedBy} />
        <Fact label="Erstellt" value={formatDateTime(job.createdAt)} />
        <Fact label="Gestartet" value={formatDateTime(job.startedAt)} />
        <Fact label="Beendet" value={formatDateTime(job.finishedAt)} />
        <Fact label="Neustart" value={job.rebootPerformed ? "durchgeführt" : job.allowReboot ? "erlaubt, nicht nötig/erfolgt" : "nicht erlaubt"} />
      </div>

      <div className="page-head">
        <h2>Log</h2>
        <span className="spacer" />
        <label className="check">
          <input type="checkbox" checked={follow} onChange={(e) => setFollow(e.target.checked)} /> mitlaufen
        </label>
      </div>
      <div className="log">
        {lines.length === 0 && <span className="muted">Noch keine Ausgabe.</span>}
        {lines.map((l) => (
          <div key={l.id} className={l.text.startsWith("[agent]") ? "log-agent" : l.text.startsWith("$ ") ? "log-cmd" : undefined}>
            <span className="log-time">{new Date(l.timestamp).toLocaleTimeString("de-DE")}</span>
            {l.text}
          </div>
        ))}
        <div ref={logEnd} />
      </div>
    </>
  );
}

function Fact({ label, value }: { label: string; value: string }) {
  return (
    <div className="fact">
      <div className="fact-label">{label}</div>
      <div>{value}</div>
    </div>
  );
}
