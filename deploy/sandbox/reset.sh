#!/bin/sh
# Truncate every table in the three service schemas, keeping the schema and the migration
# history. Run by the remit-reset container once a day; safe to run by hand.
set -eu
psql -v ON_ERROR_STOP=1 -q <<'SQL'
DO $$
DECLARE r record;
BEGIN
  FOR r IN
    SELECT schemaname, tablename FROM pg_tables
    WHERE schemaname IN ('funding', 'ledger', 'reconciliation')
      AND tablename <> '__EFMigrationsHistory'
  LOOP
    EXECUTE format('TRUNCATE TABLE %I.%I RESTART IDENTITY CASCADE', r.schemaname, r.tablename);
  END LOOP;
END $$;
SQL
echo "$(date -u +%FT%TZ) sandbox wiped"
