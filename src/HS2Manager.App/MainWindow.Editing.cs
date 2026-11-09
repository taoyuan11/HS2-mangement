using HS2Manager.Core;
using Microsoft.UI.Xaml;

namespace HS2Manager.App;
public partial class MainWindow
{
    private CharacterEditorView? editor;
    private bool openingEditor,closingEditor;
    private async void EditCharacter_Click(object sender,RoutedEventArgs e)
    {
        var card=ViewModel.IsPreview?ViewModel.CurrentPreviewCard:ViewModel.SelectedCard?.Record;
        if(card!=null)await OpenEditorAsync(card);
    }
    public async Task OpenEditorAsync(CardRecord card)
    {
        if(openingEditor||card.Status!=CardStatus.Valid||card.Availability!=FileAvailability.Available)return;
        openingEditor=true;
        try
        {
            if(editor!=null&&!await CloseEditorAsync())return;
            await ViewModel.ClosePreviewAsync();
            editor=new();EditorHost.Content=editor;ViewModel.IsEditing=true;
            await editor.OpenAsync(this,ViewModel,card);
        }
        catch(OperationCanceledException){}
        catch(Exception error)
        {
            if(editor!=null){await editor.StopAsync();editor=null;EditorHost.Content=null;ViewModel.IsEditing=false;}
            await Dialogs.Report(this,"无法编辑角色",error.Message);
        }
        finally{openingEditor=false;}
    }
    public async Task<bool> CloseEditorAsync()
    {
        if(editor==null)return true;if(closingEditor)return false;closingEditor=true;
        try
        {
            if(!await editor.ConfirmLeaveAsync())return false;
            await editor.StopAsync();editor=null;EditorHost.Content=null;ViewModel.IsEditing=false;ViewModel.ReloadCards();return true;
        }
        finally{closingEditor=false;}
    }
}
