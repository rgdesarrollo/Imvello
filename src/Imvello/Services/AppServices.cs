using System.IO;
using Imvello.Data;
using Imvello.Models;

namespace Imvello.Services;

public static class AppServices
{
    public static AppSettings Settings { get; private set; } = null!;
    public static MailDatabase Db { get; private set; } = null!;
    public static MessageStore Store { get; private set; } = null!;
    public static OAuth2Service OAuth { get; private set; } = null!;
    public static MailClientFactory Clients { get; private set; } = null!;
    public static SyncService Sync { get; private set; } = null!;
    public static MessageService Messages { get; private set; } = null!;
    public static SendService Send { get; private set; } = null!;
    public static ComposeHelper Compose { get; private set; } = null!;
    public static MboxService Mbox { get; private set; } = null!;
    public static AccountService Accounts { get; private set; } = null!;

    public static async Task InitializeAsync()
    {
        Settings = AppSettings.Load();
        Directory.CreateDirectory(Settings.DataDirectory);
        Db = new MailDatabase(Settings.DataDirectory);
        Db.Initialize();
        Store = new MessageStore(Settings.DataDirectory);
        OAuth = new OAuth2Service(Settings);
        Clients = new MailClientFactory(Db, OAuth);
        Sync = new SyncService(Db, Store, Clients, Settings);
        Messages = new MessageService(Db, Store, Sync);
        Send = new SendService(Db, Clients, Sync, Messages);
        Compose = new ComposeHelper(Db, Messages);
        Mbox = new MboxService(Db, Store, Sync, Messages);
        Accounts = new AccountService(Db, Store, Sync, Clients, OAuth);
        await Accounts.EnsureLocalAccountAsync();
    }
}
