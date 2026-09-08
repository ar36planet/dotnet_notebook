# User / Order SQL Server sample

`schema.sql` 是 [[31-User-Order整合實作|User / Order 整合實作]] 的 learning schema，包含：

- `Users`
- `Orders`
- `OrderItems`
- primary key、foreign key、check constraint、unique index
- user order list 與 order item lookup 所需的 index
- `rowversion` optimistic concurrency 欄位

請在 local SQL Server / SQL Server container 的 learning database 執行，避免直接對 production database 執行。`GO` 是 SSMS / sqlcmd 的 batch separator，不是一般 application SQL command 的一部分。
