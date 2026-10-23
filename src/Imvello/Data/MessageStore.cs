using System.IO;
using MimeKit;

namespace Imvello.Data;

public sealed class MessageStore
{
    private readonly string _root;

    public MessageStore(string dataDirectory)
    {
        _root = Path.Combine(dataDirectory, "Store");
        Directory.CreateDirectory(_root);
    }

    public string GetPath(long accountId, long id) =>
        Path.Combine(_root, accountId.ToString(), (id % 256).ToString("x2"), id + ".eml");

    public bool Exists(long accountId, long id) => File.Exists(GetPath(accountId, id));

    public async Task SaveAsync(long accountId, long id, MimeMessage message, CancellationToken ct = default)
    {
        var path = GetPath(accountId, id);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + ".tmp";
        await using (var fs = File.Create(tmp))
            await message.WriteToAsync(fs, ct);
        File.Move(tmp, path, true);
    }

    public async Task SaveAsync(long accountId, long id, Stream source, CancellationToken ct = default)
    {
        var path = GetPath(accountId, id);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + ".tmp";
        await using (var fs = File.Create(tmp))
            await source.CopyToAsync(fs, ct);
        File.Move(tmp, path, true);
    }

    public async Task<MimeMessage?> LoadAsync(long accountId, long id, CancellationToken ct = default)
    {
        var path = GetPath(accountId, id);
        if (!File.Exists(path)) return null;
        await using var fs = File.OpenRead(path);
        return await MimeMessage.LoadAsync(fs, ct);
    }

    public Stream? OpenRead(long accountId, long id)
    {
        var path = GetPath(accountId, id);
        return File.Exists(path) ? File.OpenRead(path) : null;
    }

    public void Delete(long accountId, long id)
    {
        try { File.Delete(GetPath(accountId, id)); } catch { }
    }

    public void DeleteAccount(long accountId)
    {
        try { Directory.Delete(Path.Combine(_root, accountId.ToString()), true); } catch { }
    }
}
