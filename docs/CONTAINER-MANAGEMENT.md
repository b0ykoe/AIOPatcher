# Container management (mini Docker manager)

> **Highly insecure by design.** Whoever can edit a compose file can start a privileged container that mounts the
> host's root file system, which is root on the host. Whoever takes over the dashboard (stolen login, compromised
> AIOPatcher server) could do the same, and deleting containers or volumes can destroy data. That is why the feature
> is off by default and sits behind three locks.

The same guide is in the dashboard under **Getting started → Discover more → Manage containers**
(`/help/container-management`).

## What it can do

| Level | Actions | Needs on the host |
|---|---|---|
| Low | Start, stop, restart a container or a whole stack | `allowContainerManagement` |
| Medium | Delete a container (`docker rm`), take a stack down (`compose down`), reclaim space (`docker image prune --all`, `docker builder prune`) | `allowContainerManagement` |
| High | Edit a stack's compose file, create a new stack | `allowContainerManagement` **and** `allowComposeEdit` |

- Volumes are never deleted unless you tick the extra box (with a warning) in the dialog.
- "Reclaim space" shows how much can be freed (from `docker system df` in the last container report).
- Compose editing: the dashboard reads the file from the host, shows a diff before saving, and the agent checks the new
  file with `docker compose config`. Only if that passes does it keep a backup (`<file>.bak-<timestamp>`) and replace
  the file; optionally it runs `compose up -d` afterwards. If the file changed on the host since it was opened, nothing
  is written.
- Only files below the compose root (default `/opt/stacks`, symlinks resolved) can be edited; new stacks are created
  there as `<root>/<name>/compose.yaml`.
- Every action is admin-only, asks for confirmation, runs as a job with a live log and is written to the audit log. The
  content of a compose file is not kept in the job or the audit log.

## Turn it on

1. **Settings → Features & security:** turn on *Manage containers (mini Docker manager)*.
2. **On each host** that should allow it, as root (the dashboard and the install command cannot set this):

   ```bash
   sudo aiopatcher-agent policy set allowContainerManagement true
   ```

3. Only if you also want to edit compose files and create stacks:

   ```bash
   sudo aiopatcher-agent policy set allowComposeEdit true
   sudo aiopatcher-agent policy set composeRoot /opt/stacks   # optional, the folder with your stacks
   ```

The host's values show up under Host → Settings → Local policy after the agent's next report.

## Turn it off again

```bash
sudo aiopatcher-agent policy set allowContainerManagement false
sudo aiopatcher-agent policy set allowComposeEdit false
```

or turn the feature off in the settings. Both take effect immediately.
