import { Link } from "react-router";
import { describeJob, formatRelative, type Job } from "../api";
import { JobStatusBadge } from "./JobStatusBadge";

export function JobTable({ jobs, showHost }: { jobs: Job[]; showHost?: boolean }) {
  return (
    <div className="table-wrap">
      <table>
        <thead>
          <tr>
            <th>Job</th>
            {showHost && <th>Host</th>}
            <th>Status</th>
            <th>Ergebnis</th>
            <th>Erstellt</th>
            <th>Von</th>
          </tr>
        </thead>
        <tbody>
          {jobs.map((j) => (
            <tr key={j.id}>
              <td>
                <Link to={`/jobs/${j.id}`}>{describeJob(j)}</Link>
              </td>
              {showHost && (
                <td>
                  <Link to={`/hosts/${j.hostId}`}>{j.hostName}</Link> <span className="muted small">{j.siteName}</span>
                </td>
              )}
              <td>
                <JobStatusBadge status={j.status} />
              </td>
              <td className="small">{j.summary}</td>
              <td className="muted small">{formatRelative(j.createdAt)}</td>
              <td className="muted small">{j.requestedBy}</td>
            </tr>
          ))}
          {jobs.length === 0 && (
            <tr>
              <td colSpan={showHost ? 6 : 5} className="muted">
                Noch keine Jobs.
              </td>
            </tr>
          )}
        </tbody>
      </table>
    </div>
  );
}
