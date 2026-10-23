using System.Data;
using System.IO;
using Dapper;
using Microsoft.Data.Sqlite;

namespace Imvello.Data;

public sealed partial class MailDatabase
{
    private readonly string _connectionString;

    static MailDatabase()
    {
        DefaultTypeMap.MatchNamesWithUnderscores = true;
        SqlMapper.AddTypeHandler(new DateTimeOffsetHandler());
    }

    public MailDatabase(string dataDirectory)
    {
        Directory.CreateDirectory(dataDirectory);
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(dataDirectory, "letmail.db"),
            Pooling = true,
            Cache = SqliteCacheMode.Private
        }.ToString();
    }

    public SqliteConnection Open()
    {
        var c = new SqliteConnection(_connectionString);
        c.Open();
        c.Execute("PRAGMA foreign_keys=ON; PRAGMA synchronous=NORMAL; PRAGMA temp_store=MEMORY; PRAGMA cache_size=-65536; PRAGMA mmap_size=268435456; PRAGMA busy_timeout=10000;");
        return c;
    }

    public void Initialize()
    {
        using var c = Open();
        c.Execute("PRAGMA journal_mode=WAL;");
        var version = c.ExecuteScalar<long>("PRAGMA user_version;");
        if (version < 1)
        {
            using var tx = c.BeginTransaction();
            c.Execute(Schema, transaction: tx);
            c.Execute("PRAGMA user_version=1;", transaction: tx);
            tx.Commit();
        }
        if (version < 2)
        {
            using var tx = c.BeginTransaction();
            c.Execute(SchemaV2, transaction: tx);
            c.Execute("PRAGMA user_version=2;", transaction: tx);
            tx.Commit();
        }
    }

    private const string SchemaV2 = """
        CREATE TABLE IF NOT EXISTS contacts(
            id INTEGER PRIMARY KEY, name TEXT NOT NULL DEFAULT '', email TEXT NOT NULL COLLATE NOCASE UNIQUE,
            phone TEXT NOT NULL DEFAULT '', company TEXT NOT NULL DEFAULT '', notes TEXT NOT NULL DEFAULT '',
            use_count INTEGER NOT NULL DEFAULT 0, last_used INTEGER NOT NULL DEFAULT 0, created INTEGER NOT NULL DEFAULT 0);
        CREATE INDEX IF NOT EXISTS ix_contacts_name ON contacts(name COLLATE NOCASE);
        CREATE TABLE IF NOT EXISTS events(
            id INTEGER PRIMARY KEY, title TEXT NOT NULL DEFAULT '', location TEXT NOT NULL DEFAULT '', notes TEXT NOT NULL DEFAULT '',
            start INTEGER NOT NULL, end INTEGER NOT NULL, all_day INTEGER NOT NULL DEFAULT 0, color TEXT NOT NULL DEFAULT '');
        CREATE INDEX IF NOT EXISTS ix_events_start ON events(start);
        """;

    private const string Schema = """
        CREATE TABLE IF NOT EXISTS accounts(
            id INTEGER PRIMARY KEY, display_name TEXT, email TEXT, kind INTEGER, imap_host TEXT, imap_port INTEGER,
            imap_security INTEGER, smtp_host TEXT, smtp_port INTEGER, smtp_security INTEGER, user_name TEXT,
            auth_type INTEGER, signature TEXT, color TEXT, sort_order INTEGER, enabled INTEGER, secret BLOB);
        CREATE TABLE IF NOT EXISTS folders(
            id INTEGER PRIMARY KEY, account_id INTEGER NOT NULL REFERENCES accounts(id) ON DELETE CASCADE,
            full_name TEXT NOT NULL, display_name TEXT, parent_full_name TEXT, delimiter TEXT, special INTEGER,
            selectable INTEGER, uid_validity INTEGER DEFAULT 0, last_uid INTEGER DEFAULT 0, highest_mod_seq INTEGER DEFAULT 0,
            unread_count INTEGER DEFAULT 0, total_count INTEGER DEFAULT 0, UNIQUE(account_id, full_name));
        CREATE TABLE IF NOT EXISTS messages(
            id INTEGER PRIMARY KEY, folder_id INTEGER NOT NULL REFERENCES folders(id) ON DELETE CASCADE,
            account_id INTEGER, uid INTEGER, message_id TEXT, in_reply_to TEXT, refs TEXT, subject TEXT,
            from_name TEXT, from_address TEXT, to_list TEXT, cc_list TEXT, date INTEGER, size INTEGER, flags INTEGER,
            has_attachments INTEGER, preview TEXT, importance INTEGER, body_downloaded INTEGER DEFAULT 0,
            UNIQUE(folder_id, uid));
        CREATE INDEX IF NOT EXISTS ix_messages_folder_date ON messages(folder_id, date DESC);
        CREATE INDEX IF NOT EXISTS ix_messages_account_msgid ON messages(account_id, message_id);
        CREATE INDEX IF NOT EXISTS ix_messages_folder_flags ON messages(folder_id, flags);
        CREATE VIRTUAL TABLE IF NOT EXISTS messages_fts USING fts5(subject, from_text, to_text, body_text, tokenize='unicode61 remove_diacritics 2');
        CREATE TRIGGER IF NOT EXISTS trg_messages_delete AFTER DELETE ON messages BEGIN
            DELETE FROM messages_fts WHERE rowid = old.id;
        END;
        """;

    private sealed class DateTimeOffsetHandler : SqlMapper.TypeHandler<DateTimeOffset>
    {
        public override void SetValue(IDbDataParameter parameter, DateTimeOffset value)
        {
            parameter.DbType = DbType.Int64;
            parameter.Value = value.ToUnixTimeSeconds();
        }

        public override DateTimeOffset Parse(object value) =>
            DateTimeOffset.FromUnixTimeSeconds(Convert.ToInt64(value)).ToLocalTime();
    }
}
