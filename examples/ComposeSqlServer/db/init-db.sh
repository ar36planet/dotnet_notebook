#!/usr/bin/env bash
set -Eeuo pipefail

sa_password="$(cat /run/secrets/sa_password)"
app_password="$(cat /run/secrets/app_db_password)"
export MSSQL_SA_PASSWORD="$sa_password"

/opt/mssql/bin/sqlservr &
sqlservr_pid=$!

until SQLCMDPASSWORD="$sa_password" /opt/mssql-tools18/bin/sqlcmd \
    -S localhost -U sa -C -Q "SELECT 1" >/dev/null 2>&1; do
    sleep 1
done

escape_sql_literal() {
    printf '%s' "$1" | sed "s/'/''/g"
}

app_password_sql="$(escape_sql_literal "$app_password")"
cat >/tmp/init-orders.sql <<SQL
IF DB_ID(N'Orders') IS NULL
    CREATE DATABASE [Orders];
GO

IF NOT EXISTS (SELECT 1 FROM sys.server_principals WHERE name = N'orders_app')
    CREATE LOGIN [orders_app] WITH PASSWORD = N'$app_password_sql';
GO

USE [Orders];
IF NOT EXISTS (SELECT 1 FROM sys.database_principals WHERE name = N'orders_app')
    CREATE USER [orders_app] FOR LOGIN [orders_app];
ALTER ROLE [db_datareader] ADD MEMBER [orders_app];
ALTER ROLE [db_datawriter] ADD MEMBER [orders_app];
GO
SQL

SQLCMDPASSWORD="$sa_password" /opt/mssql-tools18/bin/sqlcmd \
    -S localhost -U sa -C -i /tmp/init-orders.sql
rm -f /tmp/init-orders.sql

SQLCMDPASSWORD="$app_password" /opt/mssql-tools18/bin/sqlcmd \
    -S localhost -U orders_app -d Orders -C -i /usr/local/bin/schema.sql

wait "$sqlservr_pid"
