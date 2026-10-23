using Dapper;
using Imvello.Models;

namespace Imvello.Data;

public sealed partial class MailDatabase
{
    private const string FolderCols = "id, account_id, full_name, display_name, parent_full_name, delimiter, special, selectable, uid_validity, last_uid, highest_mod_seq, unread_count, total_count";

    public List<MailFolder> GetFolders(long accountId)
    {
        using var c = Open();
        return c.Query<MailFolder>($"SELECT {FolderCols} FROM folders WHERE account_id=@accountId ORDER BY full_name COLLATE NOCASE", new { accountId }).ToList();
    }

    public List<MailFolder> GetAllFolders()
    {
        using var c = Open();
        return c.Query<MailFolder>($"SELECT {FolderCols} FROM folders ORDER BY account_id, full_name COLLATE NOCASE").ToList();
    }

    public MailFolder? GetFolder(long id)
    {
        using var c = Open();
        return c.QuerySingleOrDefault<MailFolder>($"SELECT {FolderCols} FROM folders WHERE id=@id", new { id });
    }

    public MailFolder? GetSpecialFolder(long accountId, SpecialFolder special)
    {
        using var c = Open();
        return c.QueryFirstOrDefault<MailFolder>($"SELECT {FolderCols} FROM folders WHERE account_id=@accountId AND special=@special ORDER BY id", new { accountId, special });
    }

    public async Task<long> UpsertFolderAsync(MailFolder f)
    {
        using var c = Open();
        f.Id = await c.ExecuteScalarAsync<long>("""
            INSERT INTO folders(account_id, full_name, display_name, parent_full_name, delimiter, special, selectable)
            VALUES(@AccountId, @FullName, @DisplayName, @ParentFullName, @Delimiter, @Special, @Selectable)
            ON CONFLICT(account_id, full_name) DO UPDATE SET display_name=excluded.display_name,
                parent_full_name=excluded.parent_full_name, delimiter=excluded.delimiter, special=excluded.special,
                selectable=excluded.selectable
            RETURNING id;
            """, f);
        return f.Id;
    }

    public async Task UpdateFolderSyncStateAsync(long folderId, long uidValidity, long lastUid, long highestModSeq)
    {
        using var c = Open();
        await c.ExecuteAsync("UPDATE folders SET uid_validity=@uidValidity, last_uid=@lastUid, highest_mod_seq=@highestModSeq WHERE id=@folderId",
            new { folderId, uidValidity, lastUid, highestModSeq });
    }

    public async Task DeleteFolderAsync(long folderId)
    {
        using var c = Open();
        await c.ExecuteAsync("DELETE FROM folders WHERE id=@folderId", new { folderId });
    }

    public async Task RenameFolderAsync(long folderId, string newFullName, string newDisplayName)
    {
        using var c = Open();
        using var tx = c.BeginTransaction();
        var f = c.QuerySingle<MailFolder>($"SELECT {FolderCols} FROM folders WHERE id=@folderId", new { folderId }, tx);
        var oldPrefix = f.FullName + f.Delimiter;
        var newPrefix = newFullName + f.Delimiter;
        var children = c.Query<MailFolder>($"SELECT {FolderCols} FROM folders WHERE account_id=@AccountId AND substr(full_name, 1, length(@oldPrefix))=@oldPrefix",
            new { f.AccountId, oldPrefix }, tx).ToList();
        foreach (var ch in children)
        {
            var full = newPrefix + ch.FullName[oldPrefix.Length..];
            var parent = ch.ParentFullName == null ? null
                : ch.ParentFullName == f.FullName ? newFullName
                : ch.ParentFullName.StartsWith(oldPrefix) ? newPrefix + ch.ParentFullName[oldPrefix.Length..] : ch.ParentFullName;
            await c.ExecuteAsync("UPDATE folders SET full_name=@full, parent_full_name=@parent WHERE id=@Id", new { full, parent, ch.Id }, tx);
        }
        await c.ExecuteAsync("UPDATE folders SET full_name=@newFullName, display_name=@newDisplayName WHERE id=@folderId",
            new { newFullName, newDisplayName, folderId }, tx);
        tx.Commit();
    }

    public async Task ClearFolderMessagesAsync(long folderId)
    {
        using var c = Open();
        await c.ExecuteAsync("DELETE FROM messages WHERE folder_id=@folderId; UPDATE folders SET unread_count=0, total_count=0, last_uid=0, highest_mod_seq=0 WHERE id=@folderId", new { folderId });
    }

    public async Task RecalcFolderCountsAsync(long folderId)
    {
        using var c = Open();
        await c.ExecuteAsync("""
            UPDATE folders SET total_count=(SELECT COUNT(*) FROM messages WHERE folder_id=@folderId),
            unread_count=(SELECT COUNT(*) FROM messages WHERE folder_id=@folderId AND (flags & 1)=0) WHERE id=@folderId
            """, new { folderId });
    }

    public async Task<(int total, int unread)> GetFolderCountsAsync(long folderId)
    {
        using var c = Open();
        var r = await c.QuerySingleAsync<(long, long)>("SELECT total_count, unread_count FROM folders WHERE id=@folderId", new { folderId });
        return ((int)r.Item1, (int)r.Item2);
    }
}
