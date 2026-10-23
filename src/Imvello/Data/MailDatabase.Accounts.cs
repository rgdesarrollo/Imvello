using Dapper;
using Imvello.Models;

namespace Imvello.Data;

public sealed partial class MailDatabase
{
    private const string AccountCols = "id, display_name, email, kind, imap_host, imap_port, imap_security, smtp_host, smtp_port, smtp_security, user_name, auth_type, signature, color, sort_order, enabled";

    public List<Account> GetAccounts()
    {
        using var c = Open();
        return c.Query<Account>($"SELECT {AccountCols} FROM accounts ORDER BY sort_order, id").ToList();
    }

    public Account? GetAccount(long id)
    {
        using var c = Open();
        return c.QuerySingleOrDefault<Account>($"SELECT {AccountCols} FROM accounts WHERE id=@id", new { id });
    }

    public async Task<long> InsertAccountAsync(Account a)
    {
        using var c = Open();
        a.Id = await c.ExecuteScalarAsync<long>("""
            INSERT INTO accounts(display_name, email, kind, imap_host, imap_port, imap_security, smtp_host, smtp_port, smtp_security, user_name, auth_type, signature, color, sort_order, enabled)
            VALUES(@DisplayName, @Email, @Kind, @ImapHost, @ImapPort, @ImapSecurity, @SmtpHost, @SmtpPort, @SmtpSecurity, @UserName, @AuthType, @Signature, @Color, @SortOrder, @Enabled);
            SELECT last_insert_rowid();
            """, a);
        return a.Id;
    }

    public async Task UpdateAccountAsync(Account a)
    {
        using var c = Open();
        await c.ExecuteAsync("""
            UPDATE accounts SET display_name=@DisplayName, email=@Email, kind=@Kind, imap_host=@ImapHost, imap_port=@ImapPort,
            imap_security=@ImapSecurity, smtp_host=@SmtpHost, smtp_port=@SmtpPort, smtp_security=@SmtpSecurity, user_name=@UserName,
            auth_type=@AuthType, signature=@Signature, color=@Color, sort_order=@SortOrder, enabled=@Enabled WHERE id=@Id
            """, a);
    }

    public async Task DeleteAccountAsync(long id)
    {
        using var c = Open();
        await c.ExecuteAsync("DELETE FROM accounts WHERE id=@id", new { id });
    }

    public string? GetSecret(long accountId)
    {
        using var c = Open();
        var blob = c.ExecuteScalar<byte[]?>("SELECT secret FROM accounts WHERE id=@accountId", new { accountId });
        if (blob == null || blob.Length == 0) return null;
        try { return SecretProtector.Unprotect(blob); } catch { return null; }
    }

    public async Task SetSecretAsync(long accountId, string? secret)
    {
        using var c = Open();
        var blob = string.IsNullOrEmpty(secret) ? null : SecretProtector.Protect(secret);
        await c.ExecuteAsync("UPDATE accounts SET secret=@blob WHERE id=@accountId", new { blob, accountId });
    }
}
