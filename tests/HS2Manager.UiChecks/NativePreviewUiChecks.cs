using System.Diagnostics;
using System.Text.Json;
using HS2Manager.App;
using HS2Manager.Core;
using HS2Manager.Rendering;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

internal static class NativePreviewUiChecks
{
    public static async Task RunAsync(string root,string game)
    {
        ThemeService.Apply("Dark");
        string output=Path.Combine(root,"artifacts","native-ui",Guid.NewGuid().ToString("N"));Directory.CreateDirectory(output);
        Console.WriteLine("Native UI checks: "+output);
        string data=Path.Combine(root,"Data");
        Console.WriteLine("Native UI data: "+data);
        using var model=new MainViewModel(data,new(){GamePath=Path.Combine(output,"Offline")});
        model.PropertyChanged+=(_,change)=>{if(change.PropertyName==nameof(model.PreviewStatus))Console.WriteLine("Preview: "+model.PreviewStatus);};
        var window=new MainWindow(model);var canvas=(FrameworkElement)window.Content;window.Activate();
        var viewport=(NativePreviewViewport)canvas.FindName("CompositionViewport");
        try
        {
            await UiTest.WaitUntil(()=>!model.IsBusy&&model.Status.StartsWith("扫描完成："),"Native UI initialization failed");model.Settings.GamePath=game;
            var parser=new CardParser();var female=parser.Parse(Path.Combine(game,"UserData","chara","navi","navi.png"));
            var male=parser.Parse(Directory.EnumerateFiles(Path.Combine(game,"UserData","chara","male"),"*.png",SearchOption.AllDirectories).First());
            await model.OpenPreviewAsync(female);await Ready(viewport);var session=(NativePreviewSession)model.NativeSession!;
            model.TogglePlayback();await Task.Delay(75);
            var paused=await session.CaptureAsync();await Task.Delay(75);var still=await session.CaptureAsync();
            if(!paused.BgraPixels.SequenceEqual(still.BgraPixels))throw new Exception("UI pause did not stop animation.");
            await model.SaveScreenshotAsync(Path.Combine(output,"front.png"));model.FocusFace();await Task.Delay(100);await model.SaveScreenshotAsync(Path.Combine(output,"face.png"));
            foreach(var size in new[]{new Windows.Graphics.SizeInt32(1100,700),new Windows.Graphics.SizeInt32(1440,900)})
            {
                window.AppWindow.Resize(size);
                for(int i=0;i<20;i++){model.Rotate(2,i%2);model.Pan(i%2,1);model.Zoom(i%2==0?120:-120);await Task.Delay(16);}
                await viewport.WaitForRenderAsync();
            }
            model.RestoreEnvironment();var original=await session.CaptureAsync();model.PreviewLightIntensity=.7;model.PreviewBackgroundIndex=2;await Task.Delay(100);
            var custom=await session.CaptureAsync();if(original.BgraPixels.SequenceEqual(custom.BgraPixels)||model.EnvironmentLabel!="自定义环境")throw new Exception("Native environment controls did not change the frame.");
            model.RestoreEnvironment();await Task.Delay(100);
            viewport.Visibility=Visibility.Collapsed;await Task.Delay(100);long stopped=session.RenderedFrames;await Task.Delay(100);
            if(stopped!=session.RenderedFrames||viewport.HasFrame)throw new Exception("Hidden native viewport continued drawing.");
            viewport.Visibility=Visibility.Visible;await viewport.WaitForRenderAsync();
            var obsolete=model.OpenPreviewAsync(female);var latest=model.OpenPreviewAsync(male);await Task.WhenAll(obsolete,latest);await Ready(viewport);
            if(model.PreviewCardId!=male.Id||!ReferenceEquals(model.NativeSession,session))throw new Exception("Native rapid switch committed an obsolete card or replaced its session.");
            await session.RetryAsync();await Task.Delay(100);await viewport.WaitForRenderAsync();
            Console.WriteLine("PASS native SwapChainPanel, camera, DPI-sized resize, screenshot, pause, environment, hide/resume, rapid switch and device recovery");

            // Exercise the production editor with unsaved drafts and its separate viewport.
            string copy=Path.Combine(output,"native-editor.png");File.Copy(female.Path,copy);var source=File.ReadAllBytes(copy);var card=parser.Parse(copy);card.Root=output;model.Repository.Save(card);
            var editorClock=Stopwatch.StartNew();
            await window.OpenEditorAsync(card);
            Console.WriteLine("Editor catalog loaded in "+editorClock.Elapsed.TotalSeconds.ToString("F2")+" s");
            var editor=(CharacterEditorView)((ContentControl)canvas.FindName("EditorHost")).Content;
            var editorViewport=(NativePreviewViewport)editor.FindName("EditorCompositionViewport");var draft=editor.Session!;
            await EditReady();draft.SetValue("body.shapeValueBody[0]",.72f);draft.SetValue("body.shapeValueBody[0]",.31f);draft.SetValue("body.shapeValueBody[0]",.58f);await EditReady();
            long revision=model.AppliedEditRevision;draft.Undo();await EditReady();draft.Redo();await EditReady();
            if(model.AppliedEditRevision<=revision||!draft.IsDirty)throw new Exception("Native undo/redo did not apply its new revision.");
            var camera=model.PreviewCamera;await model.UpdateEditPreviewAsync(card,draft.Snapshot(),true);await EditReady();
            if(model.PreviewCamera!=camera)throw new Exception("Native editor reconnect reset the camera.");
            if(!File.ReadAllBytes(copy).SequenceEqual(source))throw new Exception("Unsaved native editing modified the source card.");
            draft.RestoreAll();await window.CloseEditorAsync();
            if(session.IsUsable)throw new Exception("Closed native editor retained its old render thread.");
            Console.WriteLine("PASS native editor first frame, rapid revisions, undo/redo, reconnect preserving camera, source preservation and shutdown");
            await model.OpenPreviewAsync(female);await Ready(viewport);
            var interrupted=model.UpdateEditPreviewAsync(card,draft.Snapshot(),true);
            await model.ClosePreviewAsync();await interrupted;
            var retained=typeof(MainViewModel).GetField("ownedNativeSession",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic)!.GetValue(model);
            if(model.NativeSession!=null||retained!=null)throw new Exception("Closing during reconnect created an unbound native render thread.");
            Console.WriteLine("PASS closing during reconnect leaves no bound or unbound native session");
            await model.OpenPreviewAsync(female);await Ready(viewport);
            var disposing=model.NativeSession!;
            model.Dispose();
            if(disposing.IsUsable)throw new Exception("Synchronous model disposal retained its native session.");
            Console.WriteLine("PASS native model disposal from the XAML dispatcher without a shutdown deadlock");
            File.WriteAllText(Path.Combine(output,"report.json"),JsonSerializer.Serialize(new{PreviewControls=true,SwapChainPanel=true,Editing=true,Shutdown=true,VisualParityVerified=false}));
            async Task EditReady()
            {
                var deadline=DateTime.UtcNow.AddMinutes(5);
                while(DateTime.UtcNow<deadline)
                {
                    if(model.HasPreviewError)throw new Exception(model.PreviewError);
                    if(!model.PreviewBusy&&model.NativeSession!=null&&model.AppliedEditRevision==draft.Revision)
                    {await editorViewport.WaitForRenderAsync();return;}
                    await Task.Delay(50);
                }
                throw new TimeoutException("Native editor: "+model.PreviewStatus);
            }
        }
        finally
        {
            if(((ContentControl)canvas.FindName("EditorHost")).Content is CharacterEditorView remaining)
            {remaining.Session?.RestoreAll();await window.CloseEditorAsync();}
            await model.ClosePreviewAsync();await viewport.StopAsync();await UiTest.CloseAsync(window);
        }
        async Task Ready(NativePreviewViewport view)
        {
            if(model.HasPreviewError||model.NativeSession==null)throw new Exception("Native UI load failed: "+model.PreviewError);
            await view.WaitForRenderAsync();
        }
    }
}
