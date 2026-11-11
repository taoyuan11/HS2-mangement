using System.Diagnostics;
using HS2Manager.App;
using HS2Manager.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.System;

internal static class CharacterEditorUiChecks
{
    internal static async Task RunAsync(string root,string game,string player)
    {
        string output=Path.Combine(root,"artifacts","editor-ui",Guid.NewGuid().ToString("N"));Directory.CreateDirectory(output);
        Console.WriteLine("Editor UI checks: "+output);
        using var model=new MainViewModel(Path.Combine(output,"Data"),new(){GamePath=Path.Combine(output,"Offline")}){UnityPlayerPath=player};
        var window=new MainWindow(model);window.Activate();var canvas=(FrameworkElement)window.Content;int pid=0;Task<bool>? pendingLeave=null;
        try
        {
            await UiTest.WaitUntil(()=>!model.IsBusy&&model.Status.StartsWith("扫描完成："),"Editor initialization failed");
            model.Settings.GamePath=game;
            string source=Path.Combine(game,"UserData","chara","navi","navi.png"),copy=Path.Combine(output,"编辑样本.png");File.Copy(source,copy);
            var sourceBytes=File.ReadAllBytes(copy);var card=new CardParser().Parse(copy);card.Root=output;card.Tags=["编辑验证"];card.Notes="继承元数据";card.Favorite=true;model.Repository.Save(card);
            await window.OpenEditorAsync(card);
            var editor=(CharacterEditorView)((ContentControl)canvas.FindName("EditorHost")).Content;
            var session=editor.Session!;
            await WaitPreview();pid=model.UnitySession!.ProcessId;
            var categories=(ListView)editor.FindName("Categories");var groups=(ListView)editor.FindName("Groups");var parameters=(StackPanel)editor.FindName("Parameters");
            var viewport=(UnityPreviewViewport)editor.FindName("EditorNativeViewport");await viewport.WaitForRenderAsync();
            if(!viewport.HasFrame)throw new Exception("Editor did not present a Unity frame: "+viewport.RenderError);
            var clothingVisibility=(ComboBox)editor.FindName("ClothingVisibility");
            var accessoryVisibility=(ToggleSwitch)editor.FindName("AccessoryVisibility");
            byte[] originalDraft=session.Document.ToBytes();long initialRevision=session.Revision;
            categories.SelectedIndex=1;await WaitVisibility(2);
            categories.SelectedIndex=4;
            foreach(string group in groups.Items.Cast<string>().Where(g=>g=="上衣"||g=="鞋子"||g=="内衣"||g.StartsWith("内衣 · ")).ToArray())
            {groups.SelectedItem=group;await WaitVisibility(group.StartsWith("内衣")?1:0);}
            clothingVisibility.SelectedIndex=3;await WaitVisibility(2);
            categories.SelectedIndex=0;await WaitVisibility(2);
            clothingVisibility.SelectedIndex=1;await WaitVisibility(0);
            accessoryVisibility.IsOn=false;await WaitVisibility(0,false);
            clothingVisibility.SelectedIndex=0;categories.SelectedIndex=1;await WaitVisibility(2,false);
            accessoryVisibility.IsOn=true;categories.SelectedIndex=0;await WaitVisibility(0);
            if(session.IsDirty||session.Revision!=initialRevision||session.CanUndo||!session.Document.ToBytes().SequenceEqual(originalDraft))throw new Exception("Preview visibility changed draft or undo history");
            Console.WriteLine("Editor automatic clothing, manual override, accessory visibility and draft isolation passed");
            foreach(string theme in new[]{"Light","Dark"})
            {
                ThemeService.Apply(theme);
                foreach(var size in new[]{(1100,700),(1440,900)})
                {
                    double dpi=canvas.XamlRoot.RasterizationScale;window.AppWindow.Resize(new((int)(size.Item1*dpi),(int)(size.Item2*dpi)));await Task.Delay(150);
                    for(int category=0;category<categories.Items.Count;category++)
                    {
                        categories.SelectedIndex=category;
                        for(int group=0;group<groups.Items.Count;group++){groups.SelectedIndex=group;canvas.UpdateLayout();if(parameters.Children.Count==0)throw new Exception("Empty parameter group");}
                        groups.SelectedIndex=0;canvas.UpdateLayout();await Task.Delay(180);
                        if(viewport.ActualWidth<300||viewport.ActualHeight<160||parameters.ActualWidth<250)throw new Exception("Editor layout is clipped");
                        await UiTest.CaptureAsync(canvas,Path.Combine(output,$"{theme}-{size.Item1}-{category}.png"));
                    }
                }
            }
            categories.SelectedIndex=1;groups.SelectedIndex=0;
            Console.WriteLine("Editor layouts passed; checking editing interactions");
            var slider=UiTest.Descendants<Slider>(parameters).First();double old=slider.Value;
            session.BeginGesture();for(int i=1;i<=15;i++)slider.Value=old+i*.2;session.EndGesture();
            if(!session.IsDirty)throw new Exception("Slider did not update document");
            await editor.HandleKeyAsync(VirtualKey.Z,true);if(session.IsDirty)throw new Exception("Slider drag was not one undo transaction");
            await editor.HandleKeyAsync(VirtualKey.Y,true);await WaitPreview();
            if(model.UnitySession.ProcessId!=pid||model.UnitySession.LatestFrame?.Revision!=session.Revision)throw new Exception("Edit revision did not reach frame");
            model.SetAngle(90);model.Zoom(120);model.PreviewLightIntensity=.7;model.TogglePlayback();var camera=model.PreviewCamera;string playback=model.PlaybackLabel;
            session.SetValue("body.shapeValueBody[0]",.77f);await WaitPreview();
            if(model.PreviewCamera!=camera||model.PreviewLightIntensity!=.7||model.PlaybackLabel!=playback)throw new Exception("Editing reset camera/environment/playback");
            categories.SelectedIndex=2;groups.SelectedIndex=1;
            await WaitPreview();
            Console.WriteLine("Editor revisions passed; checking asset picker");
            foreach(int sourceFilter in new[]{1,2})
            {
                await UiTest.WaitUntil(()=>editor.IsEnabled,"Editor remained disabled before asset selection");
                canvas.UpdateLayout();
                Console.WriteLine("Selecting hair source "+sourceFilter+"; editor enabled="+editor.IsEnabled);
                UiTest.Invoke(UiTest.Descendants<Button>(parameters).Single(b=>b.Tag?.ToString()=="hair.parts[0].id"));
                await UiTest.WaitUntil(()=>UiTest.OpenDialog(canvas.XamlRoot)!=null,"Asset picker missing");
                var dialog=UiTest.OpenDialog(canvas.XamlRoot)!;
                var assets=UiTest.Descendants<ListView>(dialog).Single();
                await UiTest.WaitUntil(()=>assets.SelectedItem!=null,"Asset picker did not locate initial selection");
                var filter=UiTest.Descendants<ComboBox>(dialog).Single();filter.SelectedIndex=sourceFilter;
                await UiTest.WaitUntil(()=>assets.Items.Count>0&&assets.Items.Cast<EditorAssetItem>().All(r=>r.Asset.IsMod==(sourceFilter==2)),"Asset source filter did not update");
                var rows=assets.Items.Cast<EditorAssetItem>().ToArray();
                if(rows.Length==0||rows.Any(r=>r.Asset.IsMod!=(sourceFilter==2)))throw new Exception("Asset source filter failed");
                var selected=rows.First(r=>r.Asset.Entry.Bundle.Length>0&&r.Asset.Entry.Asset is not ("" or "0" or "p_dummy"));
                var search=UiTest.Descendants<TextBox>(dialog).First();search.Text=selected.Label;
                if(!assets.Items.Cast<EditorAssetItem>().Any(r=>r.Asset.Key==selected.Asset.Key))throw new Exception("Asset search lost matching item");
                UiTest.Invoke(UiTest.Descendants<Button>(dialog).Single(b=>b.Content?.ToString()=="定位当前"));
                if(assets.SelectedItem==null||filter.SelectedIndex!=0||search.Text.Length!=0)throw new Exception("Locate current asset failed");
                filter.SelectedIndex=sourceFilter;
                assets.SelectedItem=assets.Items.Cast<EditorAssetItem>().First(r=>r.Asset.Key==selected.Asset.Key);
                UiTest.Invoke(UiTest.Descendants<Button>(dialog).Single(b=>b.Content?.ToString()=="使用素材"));
                await UiTest.WaitUntil(()=>UiTest.OpenDialog(canvas.XamlRoot)==null,"Asset picker did not close");
                await UiTest.WaitUntil(()=>Convert.ToInt32(session.Document.GetValue("hair.parts[0].id"))==selected.Asset.Id,"Asset selection did not complete: "+((TextBlock)editor.FindName("EditorMessage")).Text);
                await WaitPreview();
                if(Convert.ToInt32(session.Document.GetValue("hair.parts[0].id"))!=selected.Asset.Id)throw new Exception("UI selection did not update hair ID");
            }
            // Terminate only this check's owned kernel, then reconnect through the visible UI action.
            clothingVisibility.SelectedIndex=3;accessoryVisibility.IsOn=false;await WaitVisibility(2,false);
            using(var owned=Process.GetProcessById(pid)){owned.Kill();await owned.WaitForExitAsync();}
            await UiTest.WaitUntil(()=>model.HasPreviewError,"Kernel disconnection not reported");
            var beforeReconnect=session.Document.ToBytes();long revision=session.Revision;
            UiTest.Invoke(UiTest.Descendants<Button>(editor).Single(b=>b.Content?.ToString()=="重新连接"));
            await WaitPreview();
            if(model.UnitySession!.ProcessId==pid||session.Revision!=revision||!session.Document.ToBytes().SequenceEqual(beforeReconnect)||!session.IsDirty)throw new Exception("Reconnect lost draft or reused dead process");
            pid=model.UnitySession.ProcessId;
            await WaitVisibility(2,false);
            if(model.PreviewCamera!=camera||model.PreviewLightIntensity!=.7||model.PlaybackLabel!=playback)throw new Exception("Reconnect reset view state");
            Console.WriteLine("Editor assets and reconnect passed; checking saving");
            if(!await editor.SaveAsync())throw new Exception("Save-as failed: "+((TextBlock)editor.FindName("EditorMessage")).Text);
            if(session.IsDirty||session.Card.Id==card.Id||!session.Card.Tags.Contains("编辑验证")||!session.Card.Favorite)throw new Exception("Save baseline or metadata failed");
            if(!File.ReadAllBytes(copy).SequenceEqual(sourceBytes)||!CharacterDocument.Open(session.Card.Path).Cover.Span.SequenceEqual(CharacterDocument.Open(copy).Cover.Span))throw new Exception("Save modified original/cover");
            session.SetValue("body.shapeValueBody[0]",.72f);
            using(var locked=new FileStream(session.Card.Path,FileMode.Open,FileAccess.ReadWrite,FileShare.None))
                if(await editor.SaveAsync(true)||!session.IsDirty)throw new Exception("Failed save lost dirty state");
            pendingLeave=window.CloseEditorAsync();await UiTest.WaitUntil(()=>DialogButton("取消")!=null,"Unsaved prompt missing");
            UiTest.Invoke(UiTest.Descendants<Button>(UiTest.OpenDialog(canvas.XamlRoot)!).First(b=>b.Content?.ToString()=="取消"));
            if(await pendingLeave||!model.IsEditing||!session.IsDirty)throw new Exception("Cancel navigation lost draft");
            pendingLeave=window.CloseEditorAsync();await UiTest.WaitUntil(()=>DialogButton("放弃修改")!=null,"Discard prompt missing");
            UiTest.Invoke(UiTest.Descendants<Button>(UiTest.OpenDialog(canvas.XamlRoot)!).First(b=>b.Content?.ToString()=="放弃修改"));
            if(!await pendingLeave||model.IsEditing)throw new Exception("Discard navigation failed");
            if(Running(pid))throw new Exception("Editor leaked player after leaving");
            Console.WriteLine("PASS editor categories, light/dark layouts, grouped undo, live revisions, asset search/source/locate/stock-MOD switch, kernel reconnect preserving draft and view, save metadata and cover, file lock, leave/cancel/discard, owned process cleanup");
            Button? DialogButton(string text)
            {
                var dialog=UiTest.OpenDialog(canvas.XamlRoot);
                return dialog?.Title?.ToString()=="保存角色修改？"?UiTest.Descendants<Button>(dialog).FirstOrDefault(b=>b.Content?.ToString()==text):null;
            }
            async Task WaitVisibility(int clothing,bool accessories=true)
            {
                await WaitPreview();
                if(model.EditDefinition?.Visibility is not { } state||state.Clothing!=clothing||state.Accessories!=accessories)throw new Exception("Editor preview visibility did not reach kernel");
            }
            async Task WaitPreview()
            {
                var deadline=DateTime.UtcNow.AddMinutes(3);
                while(DateTime.UtcNow<deadline)
                {
                    if(model.HasPreviewError)throw new Exception(model.PreviewError);
                    if(!model.PreviewBusy&&model.UnitySession?.LatestFrame is { } frame&&frame.Revision==session.Revision&&frame.Load==model.UnitySession.CurrentLoad)return;
                    await Task.Delay(60);
                }
                throw new TimeoutException("Editor preview: "+model.PreviewStatus);
            }
        }
        catch(Exception error){Console.Error.WriteLine(error);throw;}
        finally
        {
            UiTest.OpenDialog(canvas.XamlRoot)?.Hide();
            if(pendingLeave!=null)await pendingLeave;
            if(((ContentControl)canvas.FindName("EditorHost")).Content is CharacterEditorView remaining)
            {remaining.Session?.RestoreAll();await window.CloseEditorAsync();}
            await UiTest.CloseAsync(window);if(pid>0&&Running(pid))throw new Exception("Editor left its player running");
        }
    }
    private static bool Running(int pid){try{using var p=Process.GetProcessById(pid);return !p.HasExited;}catch(ArgumentException){return false;}}
}
