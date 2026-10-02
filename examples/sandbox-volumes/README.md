# Multi-sandbox with shared and isolated volume storage

This example demonstrates how to configure multiple named sandboxes sharing persistent storage and ephemeral scratch space, alongside sandbox-isolated storage.

## Setup

Build the placeholder image whose default command keeps the sandbox running:

```bash
docker build -t sandbox-volumes-app:latest .
```

## How it works

1. **Explicit Storage**: Declares `volumes` with explicit persistence policy (`persistent: true|false`) and canonical Linux mount destinations.
2. **Sharing**: Mounting `shared-data` and `shared-scratch` in both `primary` and `secondary` sandboxes allows both containers to read and write to the same backend volume.
3. **Isolation**: `private-cache` is mounted only in `secondary`, so its data is isolated and invisible to `primary`.
4. **Lifecycle**:
   - `wip sandbox create primary`: ensures declared volumes exist and starts the container with `--mount` arguments.
   - `wip sandbox destroy primary`: removes the container. Persistent `shared-data` is retained. `shared-scratch` is also retained because `secondary` still references it (if running).
   - Once the last referencing container detaches, ephemeral volumes are automatically cleaned up on reconciliation.
