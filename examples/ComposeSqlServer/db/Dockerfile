FROM mcr.microsoft.com/mssql/server:2022-CU16-ubuntu-22.04

USER root
COPY init-db.sh /usr/local/bin/init-db.sh
COPY schema.sql /usr/local/bin/schema.sql
RUN chmod 0755 /usr/local/bin/init-db.sh
ENTRYPOINT ["/usr/local/bin/init-db.sh"]
