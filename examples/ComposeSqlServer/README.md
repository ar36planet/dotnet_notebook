# Compose SQL Server sample

This sample contains one API and one SQL Server service. The database image creates an application login during local initialization; the API never connects as `sa`. It is a localhost-only development sample. Production should use a managed secret provider, TLS certificate validation, a reviewed migration job, and pinned image digests.

From the repository root:

```bash
umask 077
printf '%s' 'a-local-only-sa-password' > examples/ComposeSqlServer/secrets/sa_password.txt
printf '%s' 'a-local-only-app-password' > examples/ComposeSqlServer/secrets/app_db_password.txt
docker compose -f examples/ComposeSqlServer/compose.yaml config --quiet
docker compose -f examples/ComposeSqlServer/compose.yaml up --build --wait
curl http://127.0.0.1:8081/health/live
curl http://127.0.0.1:8081/health/ready
curl 'http://127.0.0.1:8081/api/orders?pageSize=20'
docker compose -f examples/ComposeSqlServer/compose.yaml down
```

The Compose file intentionally does not publish SQL Server to the host. The database is reachable from the API as `db,1433`; host tools require a separate localhost-only override.
