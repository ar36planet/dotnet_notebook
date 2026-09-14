# Local Compose secrets

Create these untracked files before starting the stack:

```bash
umask 077
printf '%s' 'a-local-only-sa-password' > examples/ComposeSqlServer/secrets/sa_password.txt
printf '%s' 'a-local-only-app-password' > examples/ComposeSqlServer/secrets/app_db_password.txt
```

Use real generated values locally; the example values above are placeholders and must not be committed.
