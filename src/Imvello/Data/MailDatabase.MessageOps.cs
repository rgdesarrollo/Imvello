using Dapper;
using Imvello.Models;

namespace Imvello.Data;

public sealed partial class MailDatabase
{
    public async Task DeleteByUidsAsync(long folderId, IEnumerable<long> uids)
    {
        using var c = Open();
        using var tx = c.BeginTransaction();
        foreach (var chunk in uids.Chunk(500))
            await c.ExecuteAsync("DELETE FROM messages WHERE folder_id=@folderId AND uid IN @chunk", new { folderId, chunk }, tx);
        tx.Commit();
    }

    public async Task DeleteMessagesAsync(IEnumerable<long> ids)
    {
        using var c = Open();
        using var tx = c.BeginTransaction();
        foreach (var chunk in ids.Chunk(500))
            await c.ExecuteAsync("DELETE FROM messages WHERE id IN @chunk", new { chunk }, tx);
        tx.Commit();
    }

    public async Task UpdateFlagsAsync(IEnumerable<long> ids, MessageFlags flags)
    {
        using var c = Open();
        using var tx = c.BeginTransaction();
        foreach (var chunk in ids.Chunk(500))
            await c.ExecuteAsync("UPDATE messages SET flags=@f WHERE id IN @chunk", new { f = (int)flags, chunk }, tx);
        tx.Commit();
    }

    public async Task SetFlagBitsAsync(IEnumerable<long> ids, MessageFlags bits, bool on)
    {
        var sql = on ? "UPDATE messages SET flags = flags | @b WHERE id IN @chunk" : "UPDATE messages SET flags = flags & ~@b WHERE id IN @chunk";
        using var c = Open();
        using var tx = c.BeginTransaction();
        foreach (var chunk in ids.Chunk(500))
            await c.ExecuteAsync(sql, new { b = (int)bits, chunk }, tx);
        tx.Commit();
    }

    public async Task UpdateFlagsByUidAsync(long folderId, IDictionary<long, MessageFlags> uidFlags)
    {
        if (uidFlags.Count == 0) return;
        await using var c = Open();
        await using var tx = c.BeginTransaction();
        var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "UPDATE messages SET flags=$fl WHERE folder_id=$f AND uid=$u AND flags<>$fl";
        var pFl = cmd.Parameters.Add("$fl", Microsoft.Data.Sqlite.SqliteType.Integer);
        cmd.Parameters.AddWithValue("$f", folderId);
        var pU = cmd.Parameters.Add("$u", Microsoft.Data.Sqlite.SqliteType.Integer);
        foreach (var kv in uidFlags)
        {
            pFl.Value = (int)kv.Value;
            pU.Value = kv.Key;
            await cmd.ExecuteNonQueryAsync();
        }
        await tx.CommitAsync();
    }

    public async Task MoveRowsAsync(long targetFolderId, IDictionary<long, long> idToNewUid)
    {
        using var c = Open();
        using var tx = c.BeginTransaction();
        foreach (var kv in idToNewUid)
            await c.ExecuteAsync("UPDATE messages SET folder_id=@targetFolderId, uid=@uid WHERE id=@id",
                new { targetFolderId, uid = kv.Value, id = kv.Key }, tx);
        tx.Commit();
    }

    public async Task MarkAllReadAsync(long folderId)
    {
        using var c = Open();
        await c.ExecuteAsync("UPDATE messages SET flags = flags | 1 WHERE folder_id=@folderId AND (flags & 1)=0", new { folderId });
    }

    public async Task SetBodyIndexedAsync(long messageId, string bodyText)
    {
        using var c = Open();
        await c.ExecuteAsync("UPDATE messages_fts SET body_text=@bodyText WHERE rowid=@messageId; UPDATE messages SET body_downloaded=1 WHERE id=@messageId",
            new { messageId, bodyText });
    }

    public async Task<List<long>> GetIdsWithoutBodyAsync(long folderId, int limit)
    {
        using var c = Open();
        return (await c.QueryAsync<long>("SELECT id FROM messages WHERE folder_id=@folderId AND body_downloaded=0 ORDER BY date DESC LIMIT @limit",
            new { folderId, limit })).ToList();
    }

    public async Task<List<MessageHeader>> GetFolderMessagesForExportAsync(long folderId, long afterId, int limit)
    {
        using var c = Open();
        return (await c.QueryAsync<MessageHeader>($"SELECT {HeaderCols} FROM messages m WHERE m.folder_id=@folderId AND m.id>@afterId ORDER BY m.id LIMIT @limit",
            new { folderId, afterId, limit })).ToList();
    }
}
