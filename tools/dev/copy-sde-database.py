#!/usr/bin/env python3
"""Create a PostgreSQL database that holds the SDE schemas of another one, for a parallel test database.

    tools/dev/copy-sde-database.py <source-url> <new-database>
    tools/dev/copy-sde-database.py "$(tools/dev/pg18.sh url)" eve_p2

Copies the `sde` and `sde_mission` schemas (tables, primary keys and rows; the same content the server reads) from the
source database into a new one, with `psql` and no dump file. The `eve` database is only read. The new database starts
empty of everything else: the tests create their own scratch schemas. Requires `psql` on PATH; the server must be
PostgreSQL 18 (the `psql` must match it).
"""
import re
import subprocess
import sys
import time
import urllib.parse

SCHEMAS = ["sde", "sde_mission"]


def url_for(source, database):
    parts = urllib.parse.urlsplit(source)
    return urllib.parse.urlunsplit(parts._replace(path="/" + database))


def psql(url, *args, stdin=None, stdout=None, stderr=None):
    return subprocess.run(["psql", "-X", "-q", "-v", "ON_ERROR_STOP=1", url, *args],
                          stdin=stdin, stdout=stdout, stderr=stderr, text=True)


def query(url, sql):
    result = subprocess.run(["psql", "-X", "-At", url, "-c", sql], capture_output=True, text=True)
    if result.returncode:
        sys.exit("copy-sde-database: " + result.stderr.strip())
    return result.stdout.strip().splitlines()


def qualified(schema, table):
    return f'{schema}."{table}"'


def main(argv):
    if len(argv) != 3:
        print(__doc__, file=sys.stderr)
        return 2
    source, database = argv[1], argv[2]
    if not re.fullmatch(r"[a-z0-9_]+", database) or database == "eve":
        sys.exit("copy-sde-database: not a new database name (eve is never overwritten): " + database)
    started = time.time()
    admin = url_for(source, "postgres")
    exists = query(admin, f"select 1 from pg_database where datname = '{database}'")
    if exists:
        sys.exit(f"copy-sde-database: database {database} already exists; drop it first if it is not needed")
    psql(admin, "-c", f"CREATE DATABASE {database} TEMPLATE template0 ENCODING UTF8")
    target = url_for(source, database)
    for schema in SCHEMAS:
        psql(target, "-c", f"CREATE SCHEMA {schema}")
        tables = query(source, "select table_name from information_schema.tables "
                               f"where table_schema = '{schema}' and table_type = 'BASE TABLE' order by 1")
        for table in tables:
            name = schema + "." + f'"{table}"'
            columns = query(source, "select attname || ' ' || format_type(atttypid, atttypmod) || "
                                    "(case when attnotnull then ' NOT NULL' else '' end) "
                                    f"from pg_attribute where attrelid = '{name}'::regclass "
                                    "and attnum > 0 and not attisdropped order by attnum")
            key = query(source, "select pg_get_constraintdef(oid) from pg_constraint "
                                f"where conrelid = '{name}'::regclass and contype = 'p'")
            body = ", ".join(columns + key)
            psql(target, "-c", f"CREATE TABLE {name} ({body})")
            export = subprocess.Popen(["psql", "-X", "-q", "-v", "ON_ERROR_STOP=1", source, "-c",
                                       f"COPY (SELECT * FROM {name}) TO STDOUT"], stdout=subprocess.PIPE)
            loaded = psql(target, "-c", f"COPY {name} FROM STDIN", stdin=export.stdout)
            export.stdout.close()
            if export.wait() or loaded.returncode:
                sys.exit(f"copy-sde-database: copying {schema}.{table} failed")
        print(f"{schema}: {len(tables)} tables copied", file=sys.stderr)
    print(f"copy-sde-database: {database} ready in {round(time.time() - started)} s", file=sys.stderr)
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
