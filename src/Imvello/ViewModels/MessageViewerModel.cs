using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Imvello.Models;
using Imvello.Services;

namespace Imvello.ViewModels;

public interface IReadingSource : INotifyPropertyChanged
{
    MessageContent? CurrentContent { get; }
    bool IsLoadingContent { get; }
    string? ContentError { get; }
}

public partial class MessageViewerModel : ObservableObject, IReadingSource
{
    [ObservableProperty] private MessageContent? _currentContent;
    [ObservableProperty] private bool _isLoadingContent;
    [ObservableProperty] private string? _contentError;

    public async Task LoadAsync(long messageId)
    {
        IsLoadingContent = true;
        ContentError = null;
        try
        {
            CurrentContent = await Task.Run(() => AppServices.Messages.GetContentAsync(messageId));
        }
        catch (Exception ex)
        {
            ContentError = Loc.F("Main.MessageDisplayError", ex.Message);
        }
        finally { IsLoadingContent = false; }
    }
}
