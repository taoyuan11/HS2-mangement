using System.Globalization;
using System.IO;
using HS2Manager.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Foundation;
using Windows.System;
using Windows.UI;

namespace HS2Manager.App;

public sealed partial class CharacterEditorView : UserControl
{
    public sealed record CategoryItem(string Id,string Label);
    private MainWindow owner=null!;
    private MainViewModel model=null!;
    private CharacterAssetCatalog catalog=null!;
    public CharacterEditSession? Session {get;private set;}
    private CharacterSliderLimits limits=new(0,1,0,1);
    private IReadOnlyList<CharacterField> fields=[];
    private readonly CancellationTokenSource lifetime=new();
    private CancellationTokenSource? previewDelay;
    private readonly HashSet<Task> tasks=[];
    private bool saving,disposed,building,dragging,panning;
    private Point lastPointer;
    private string capabilitiesKey="";
    private string Category => (Categories.SelectedItem as CategoryItem)?.Id??"face";
    public CharacterEditorView(){InitializeComponent();}
    public async Task OpenAsync(MainWindow window,MainViewModel viewModel,CardRecord card)
    {
        owner=window;model=viewModel;DataContext=model;
        model.PropertyChanged+=PreviewChanged;
        EditorTitle.Text="编辑角色 · "+card.Name;EditorMessage.Text="正在读取角色参数和本机素材…";
        var document=await Task.Run(()=>CharacterDocument.Open(card.Path,lifetime.Token),lifetime.Token);
        Session=new(card,document);Session.Changed+=DraftChanged;
        limits=CharacterSliderLimits.Read(model.Settings.GamePath);catalog=new(model.Settings.GamePath,model.DataDirectory);
        Categories.ItemsSource=CharacterEditSchema.Categories.Select(c=>new CategoryItem(c.Id,c.Label)).ToArray();
        Categories.SelectedIndex=0;RefreshFields();UpdateState();
        QueuePreview(true);
        try
        {
            await catalog.LoadAsync(new Progress<string>(text=>{if(!disposed)EditorMessage.Text=text;}),lifetime.Token);
            if(disposed)return;RefreshFields();
            EditorMessage.Text="封面保持原始字节 · 拖动旋转，右键拖动平移，滚轮缩放";
            int extra=CharacterEditSchema.ExtraAccessoryCount(document);
            if(extra>0)EditorMessage.Text+=$" · 另有 {extra} 个扩展配饰槽，已保留，专属参数不在本页编辑";
        }
        catch(OperationCanceledException){}
        catch(Exception error){if(!disposed)EditorMessage.Text="素材目录读取失败："+error.Message;}
    }
    private void DraftChanged(){UpdateState();QueuePreview();}
    private void PreviewChanged(object? sender,System.ComponentModel.PropertyChangedEventArgs e)
    {
        if(e.PropertyName!=nameof(MainViewModel.EditDefinition)||model.EditDefinition==null)return;
        string key=string.Join("|",model.EditDefinition.Parts.Select(p=>p.Role+":"+p.Slot+":"+p.Prefab.Bundle+":"+p.Prefab.Asset));
        if(key==capabilitiesKey)return;capabilitiesKey=key;RefreshFields();
    }
    private void UpdateState()
    {
        if(Session==null)return;
        SaveState.Text=(Session.IsDirty?"● 未保存":"已保存")+" · 草稿 r"+Session.Revision+" · "+Session.Card.FileName;
        UndoButton.IsEnabled=!saving&&Session.CanUndo;RedoButton.IsEnabled=!saving&&Session.CanRedo;
        IsEnabled=!saving;
    }
    private void QueuePreview(bool immediate=false,bool reconnect=false)
    {
        if(disposed||Session==null)return;previewDelay?.Cancel();
        var delay=CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);previewDelay=delay;
        var task=Run();tasks.Add(task);_ = Track();
        async Task Track(){try{await task;}finally{tasks.Remove(task);}}
        async Task Run()
        {
            try
            {
                if(!immediate)await Task.Delay(220,delay.Token);
                delay.Token.ThrowIfCancellationRequested();
                await model.UpdateEditPreviewAsync(Session.Card,Session.Snapshot(),reconnect);
            }
            catch(OperationCanceledException){}
            catch(Exception error){if(!disposed)EditorMessage.Text=error.Message;}
            finally{if(ReferenceEquals(previewDelay,delay))previewDelay=null;delay.Dispose();}
        }
    }
    private void RefreshFields()
    {
        if(Session==null||building)return;building=true;
        try
        {
            string? group=Groups.SelectedItem as string;fields=CharacterEditSchema.Build(Session.Document,limits)
                .Where(f=>CharacterFieldCapabilities.Visible(f,model.EditDefinition)).ToArray();
            var groups=fields.Where(f=>f.Category==Category).Select(f=>f.Group).Distinct().ToArray();
            Groups.ItemsSource=groups;Groups.SelectedItem=groups.Contains(group)?group:groups.FirstOrDefault();
        }
        finally{building=false;}
        BuildParameters();
    }
    private void Category_Changed(object sender,SelectionChangedEventArgs e){if(!building){FieldSearch.Text="";RefreshFields();}}
    private void Group_Changed(object sender,SelectionChangedEventArgs e){if(!building)BuildParameters();}
    private void Search_Changed(object sender,TextChangedEventArgs e){if(!building)BuildParameters();}
    private void BuildParameters()
    {
        if(Session==null||building)return;building=true;
        try
        {
            Session.EndGesture();Parameters.Children.Clear();string search=FieldSearch.Text.Trim();
            string group=Groups.SelectedItem as string??"";GroupTitle.Text=search.Length==0?group:"搜索结果";
            foreach(var field in fields.Where(f=>f.Category==Category&&(search.Length>0?(f.Label+f.Group).Contains(search,StringComparison.CurrentCultureIgnoreCase):f.Group==group)))
                Parameters.Children.Add(FieldControl(field));
            if(Parameters.Children.Count==0)Parameters.Children.Add(new TextBlock{Text="此角色没有相应参数",TextWrapping=TextWrapping.Wrap});
        }
        finally{building=false;}
    }
    private FrameworkElement FieldControl(CharacterField field)
    {
        var panel=new StackPanel{Spacing=6};
        var header=new Grid();header.ColumnDefinitions.Add(new(){Width=new GridLength(1,GridUnitType.Star)});header.ColumnDefinitions.Add(new(){Width=GridLength.Auto});
        header.Children.Add(new TextBlock{Text=field.Label,VerticalAlignment=VerticalAlignment.Center,TextWrapping=TextWrapping.Wrap,FontSize=13});
        var restore=new Button{Content="↶",Padding=new Thickness(6,2,6,2),MinWidth=28,IsEnabled=Session!.Baseline.Contains(field.Path)};
        ToolTipService.SetToolTip(restore,"恢复此项到最近保存");Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(restore,"恢复"+field.Label);
        restore.Click+=(_,_)=>{Session.EndGesture();Session.Restore(field.Path);RefreshFields();};Grid.SetColumn(restore,1);header.Children.Add(restore);panel.Children.Add(header);
        if(field.Kind==CharacterFieldKind.Number)
        {
            double current=Convert.ToDouble(Session.Document.GetValue(field.Path),CultureInfo.InvariantCulture)*field.DisplayScale;
            var slider=new Slider{Minimum=Math.Min(field.Minimum*field.DisplayScale,current),Maximum=Math.Max(field.Maximum*field.DisplayScale,current),Value=current,StepFrequency=.1,IsThumbToolTipEnabled=false};
            bool extended=field.Path.Contains("shapeValue")||field.Path.Contains("dictBundle");
            var input=new NumberBox{Value=current,Minimum=Math.Min(current,(extended?limits.InputMinimum:field.Minimum)*field.DisplayScale),Maximum=Math.Max(current,(extended?limits.InputMaximum:field.Maximum)*field.DisplayScale),SpinButtonPlacementMode=NumberBoxSpinButtonPlacementMode.Compact,SmallChange=1,LargeChange=10,NumberFormatter=new Windows.Globalization.NumberFormatting.DecimalFormatter{FractionDigits=2,IntegerDigits=1}};
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(slider,field.Label);Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(input,field.Label+"数值");
            bool sync=false;
            slider.AddHandler(UIElement.PointerPressedEvent,new PointerEventHandler((_,_)=>Session.BeginGesture()),true);
            slider.AddHandler(UIElement.PointerReleasedEvent,new PointerEventHandler((_,_)=>{Session.EndGesture();UpdateState();}),true);
            slider.AddHandler(UIElement.PointerCaptureLostEvent,new PointerEventHandler((_,_)=>{Session.EndGesture();UpdateState();}),true);
            slider.LostFocus+=(_,_)=>{Session.EndGesture();UpdateState();};
            slider.ValueChanged+=(_,args)=>{if(sync||building||saving)return;sync=true;input.Value=args.NewValue;SetNumber(field,args.NewValue/field.DisplayScale);sync=false;};
            input.ValueChanged+=(_,args)=>{if(sync||building||saving||!double.IsFinite(args.NewValue))return;sync=true;Session.EndGesture();slider.Minimum=Math.Min(slider.Minimum,args.NewValue);slider.Maximum=Math.Max(slider.Maximum,args.NewValue);slider.Value=args.NewValue;SetNumber(field,args.NewValue/field.DisplayScale);sync=false;};
            panel.Children.Add(slider);panel.Children.Add(input);
        }
        else if(field.Kind==CharacterFieldKind.Toggle)
        {
            var toggle=new ToggleSwitch{IsOn=Session.Document.GetValue(field.Path) is true,OnContent="开启",OffContent="关闭"};
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(toggle,field.Label);
            toggle.Toggled+=(_,_)=>{if(!building&&!saving){Session.SetValue(field.Path,toggle.IsOn);}};panel.Children.Add(toggle);
        }
        else if(field.Kind==CharacterFieldKind.Choice)
        {
            object? current=Session.Document.GetValue(field.Path);var choices=field.Choices?.ToList()??[];
            var selected=choices.FirstOrDefault(c=>Equals(c.Value,current));
            if(selected==null){selected=new(current??"","原值 · "+current);choices.Insert(0,selected);}
            var choice=new ComboBox{ItemsSource=choices,DisplayMemberPath="Label",SelectedItem=selected,HorizontalAlignment=HorizontalAlignment.Stretch};
            choice.SelectionChanged+=async(_,_)=>
            {
                if(building||saving||choice.SelectedItem is not CharacterChoice item)return;
                if(field.Path.StartsWith("accessory.",StringComparison.Ordinal)&&field.Path.EndsWith(".type",StringComparison.Ordinal))
                {
                    saving=true;UpdateState();Session.BeginGesture();
                    try
                    {
                        Session.SetValue(field.Path,item.Value);
                        int category=Convert.ToInt32(item.Value);
                        var initial=catalog.Query(category).FirstOrDefault(a=>!a.IsMod&&a.Id==0);
                        if(category>350&&initial!=null)Session.Apply(await catalog.SelectAsync(Session.Document,field.Path[..^4]+"id",initial,lifetime.Token));
                    }
                    catch(Exception error){EditorMessage.Text="配饰类别已更改；默认素材无法初始化："+error.Message;}
                    finally{Session.EndGesture();saving=false;UpdateState();}
                }
                else Session.SetValue(field.Path,item.Value);
                DispatcherQueue.TryEnqueue(RefreshFields);
            };panel.Children.Add(choice);
        }
        else if(field.Kind is CharacterFieldKind.Asset or CharacterFieldKind.Preset)
        {
            var button=new Button{Tag=field.Path,Content=new TextBlock{Text=catalog.SelectionLabel(Session.Document,field.Path),TextWrapping=TextWrapping.Wrap,MaxLines=3,TextTrimming=TextTrimming.CharacterEllipsis},HorizontalAlignment=HorizontalAlignment.Stretch,HorizontalContentAlignment=HorizontalAlignment.Left};
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button,"选择"+field.Label);
            button.Click+=async(_,_)=>await ChooseAssetAsync(field);panel.Children.Add(button);
        }
        else if(field.Kind==CharacterFieldKind.Color)
        {
            float[] value=CharacterEditSchema.ColorValue(Session.Document,field.Path);
            var swatch=new Border{Width=30,Height=18,Background=new SolidColorBrush(ToColor(value)),CornerRadius=new CornerRadius(3)};
            var content=new StackPanel{Orientation=Orientation.Horizontal,Spacing=8};content.Children.Add(swatch);content.Children.Add(new TextBlock{Text="选择颜色"});
            var button=new Button{Content=content,HorizontalAlignment=HorizontalAlignment.Stretch};button.Click+=async(_,_)=>await ChooseColorAsync(field);panel.Children.Add(button);
        }
        return panel;
    }
    private void SetNumber(CharacterField field,double value)
    {
        object? original=Session!.Document.GetValue(field.Path);
        Session.SetValue(field.Path,original is float?(object)(float)value:original is double?value:original is long?(long)Math.Round(value):value);
    }
    private static Color ToColor(float[] values)=>Color.FromArgb((byte)Math.Clamp((values.Length>3?values[3]:1)*255,0,255),(byte)Math.Clamp(values[0]*255,0,255),(byte)Math.Clamp(values[1]*255,0,255),(byte)Math.Clamp(values[2]*255,0,255));
    private async Task ChooseColorAsync(CharacterField field)
    {
        if(Session==null||saving)return;var before=Session.Document;
        var picker=new ColorPicker{Color=ToColor(CharacterEditSchema.ColorValue(before,field.Path)),IsAlphaEnabled=true,IsColorChannelTextInputVisible=true};
        Session.BeginGesture();picker.ColorChanged+=(_,args)=>{var c=args.NewColor;Session.SetColor(field.Path,[c.R/255f,c.G/255f,c.B/255f,c.A/255f]);};
        var result=await Dialogs.ShowAsync(owner,new(){Title=field.Label,Content=picker,PrimaryButtonText="应用",CloseButtonText="取消"});
        if(result!=ContentDialogResult.Primary)Session.Apply(before);Session.EndGesture();RefreshFields();UpdateState();
    }
    private async Task ChooseAssetAsync(CharacterField field)
    {
        if(Session==null||saving)return;
        using var cancel=CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        var query=new TextBox{PlaceholderText="搜索名称、编号或来源",MinWidth=290};
        var source=new ComboBox{ItemsSource=new[]{"全部来源","原版","MOD"},SelectedIndex=0,MinWidth=120};
        var locate=new Button{Content="定位当前"};var filters=new StackPanel{Orientation=Orientation.Horizontal,Spacing=8};filters.Children.Add(query);filters.Children.Add(source);filters.Children.Add(locate);
        var info=new TextBlock{Text=catalog.SelectionLabel(Session.Document,field.Path),TextWrapping=TextWrapping.Wrap,FontSize=12};
        var list=new ListView{Height=360,SelectionMode=ListViewSelectionMode.Single};
        list.ItemTemplate=(DataTemplate)XamlReader.Load("<DataTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'><Grid Padding='4' ColumnSpacing='10'><Grid.ColumnDefinitions><ColumnDefinition Width='56'/><ColumnDefinition Width='*'/></Grid.ColumnDefinitions><Border Width='56' Height='56' Background='#203C4444' CornerRadius='4'><Image Source='{Binding Image}' Stretch='Uniform'/></Border><StackPanel Grid.Column='1' VerticalAlignment='Center'><TextBlock Text='{Binding Label}' TextTrimming='CharacterEllipsis'/><TextBlock Text='{Binding Source}' FontSize='12' Opacity='0.65' TextTrimming='CharacterEllipsis'/></StackPanel></Grid></DataTemplate>");
        var current=catalog.Selected(Session.Document,field.Path);
        void Filter()
        {
            string? selectedKey=(list.SelectedItem as EditorAssetItem)?.Asset.Key;
            var rows=catalog.Query(CharacterAssetCatalog.Category(Session.Document,field.Path),query.Text.Trim(),source.SelectedIndex).Select(a=>new EditorAssetItem(a)).ToArray();
            list.ItemsSource=rows;
            if(selectedKey!=null)list.SelectedItem=rows.FirstOrDefault(r=>r.Asset.Key==selectedKey);
        }
        void Locate(){query.Text="";source.SelectedIndex=0;Filter();var row=((IEnumerable<EditorAssetItem>)list.ItemsSource).FirstOrDefault(r=>r.Asset.Key==current?.Key);if(row!=null){list.SelectedItem=row;list.ScrollIntoView(row);}}
        query.TextChanged+=(_,_)=>Filter();source.SelectionChanged+=(_,_)=>Filter();locate.Click+=(_,_)=>Locate();
        list.ContainerContentChanging+=async(_,args)=>
        {
            if(args.InRecycleQueue||args.Item is not EditorAssetItem row||row.Requested)return;row.Requested=true;
            try{string? path=await catalog.ThumbnailAsync(row.Asset,cancel.Token);if(!cancel.IsCancellationRequested&&path!=null)row.Image=new BitmapImage(new Uri(path));}
            catch(OperationCanceledException){}
            catch(Exception){/* Missing thumbnails do not prevent resource selection. */}
        };
        var panel=new StackPanel{Spacing=12,Width=540};panel.Children.Add(filters);panel.Children.Add(info);panel.Children.Add(list);Filter();
        var dialog=new ContentDialog{Title=field.Label,Content=panel,PrimaryButtonText="使用素材",CloseButtonText="取消",IsPrimaryButtonEnabled=false};
        list.SelectionChanged+=(_,_)=>dialog.IsPrimaryButtonEnabled=list.SelectedItem!=null;
        Locate();dialog.Opened+=(_,_)=>{if(list.SelectedItem!=null)list.ScrollIntoView(list.SelectedItem);};
        var result=await Dialogs.ShowAsync(owner,dialog);cancel.Cancel();
        if(result!=ContentDialogResult.Primary||list.SelectedItem is not EditorAssetItem selected)return;
        saving=true;UpdateState();
        try{Session.Apply(await catalog.SelectAsync(Session.Document,field.Path,selected.Asset,lifetime.Token));RefreshFields();}
        catch(Exception error){EditorMessage.Text="素材未应用："+error.Message;}
        finally{saving=false;UpdateState();}
    }
    public async Task<bool> SaveAsync(bool overwrite=false,bool chooseFolder=false)
    {
        if(Session==null||saving)return false;Session.EndGesture();
        string? destination=null;
        if(chooseFolder)
        {string? folder=await FilePickers.Folder(owner,"保存编辑人物卡");if(folder==null)return false;destination=CardFileService.UniqueDestination(folder,Path.GetFileNameWithoutExtension(Session.Card.FileName)+"-编辑.png");}
        saving=true;UpdateState();
        try
        {
            var snapshot=Session.Snapshot();
            var result=await new CardEditService(model.Repository).SaveAsync(snapshot,Session.Card,overwrite,destination,lifetime.Token);
            Session.AcceptSaved(result.Card,result.Document,snapshot.Revision);model.ReloadCards();RefreshFields();
            EditorMessage.Text=(overwrite?"已备份并覆盖：":"已另存新卡：")+result.Card.Path;return true;
        }
        catch(Exception error){EditorMessage.Text="保存失败，草稿已保留："+error.Message;return false;}
        finally{saving=false;UpdateState();}
    }
    public async Task<bool> ConfirmLeaveAsync()
    {
        if(saving)return false;if(Session?.IsDirty!=true)return true;
        var result=await Dialogs.ShowAsync(owner,new(){Title="保存角色修改？",Content="此角色有未保存的修改。保存将另存为新人物卡，并保留原始封面。",PrimaryButtonText="保存并离开",SecondaryButtonText="放弃修改",CloseButtonText="取消",DefaultButton=ContentDialogButton.Primary});
        return result==ContentDialogResult.Secondary||result==ContentDialogResult.Primary&&await SaveAsync();
    }
    public async Task StopAsync()
    {
        if(disposed)return;disposed=true;lifetime.Cancel();previewDelay?.Cancel();if(Session!=null)Session.Changed-=DraftChanged;model.PropertyChanged-=PreviewChanged;
        await model.ClosePreviewAsync();await Task.WhenAll(tasks.ToArray());await EditorNativeViewport.StopAsync();lifetime.Dispose();
    }
    public async Task HandleKeyAsync(VirtualKey key,bool control)
    {
        if(Dialogs.IsOpen||saving)return;
        if(key==VirtualKey.Escape)await owner.CloseEditorAsync();
        else if(control&&key==VirtualKey.S)await SaveAsync();
        else if(control&&key==VirtualKey.Z){Session?.Undo();RefreshFields();}
        else if(control&&key==VirtualKey.Y){Session?.Redo();RefreshFields();}
    }
    private async void Back_Click(object sender,RoutedEventArgs e)=>await owner.CloseEditorAsync();
    private void Undo_Click(object sender,RoutedEventArgs e){Session?.Undo();RefreshFields();}
    private void Redo_Click(object sender,RoutedEventArgs e){Session?.Redo();RefreshFields();}
    private void RestoreCategory_Click(object sender,RoutedEventArgs e){Session?.RestoreCategory(Category);RefreshFields();}
    private void RestoreAll_Click(object sender,RoutedEventArgs e){Session?.RestoreAll();RefreshFields();}
    private async void Save_Click(object sender,RoutedEventArgs e)=>await SaveAsync();
    private async void Overwrite_Click(object sender,RoutedEventArgs e)=>await SaveAsync(true);
    private async void SaveFolder_Click(object sender,RoutedEventArgs e)=>await SaveAsync(chooseFolder:true);
    private async void SwitchCard_Click(object sender,RoutedEventArgs e)
    {
        var cards=model.Repository.All().Where(c=>c.Status==CardStatus.Valid&&c.Availability==FileAvailability.Available).ToArray();
        var list=new ListView{ItemsSource=cards,DisplayMemberPath="Name",Height=360,Width=380};
        if(await Dialogs.ShowAsync(owner,new(){Title="切换人物卡",Content=list,PrimaryButtonText="编辑",CloseButtonText="取消"})==ContentDialogResult.Primary&&list.SelectedItem is CardRecord card)
            await owner.OpenEditorAsync(card);
    }
    private async void RestoreBackup_Click(object sender,RoutedEventArgs e)
    {
        if(Session==null)return;
        var backups=model.Files.Operations().Where(o=>o.BackupPath.Length>0&&o.Card?.Id==Session.Card.Id&&o.Phase=="Indexed").ToArray();
        var list=new ListView{ItemsSource=backups.Select(o=>new BackupItem(o,$"{o.CreatedUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss} · {Path.GetFileName(o.Source)}")).ToArray(),DisplayMemberPath="Label",Height=260,Width=450};
        if(await Dialogs.ShowAsync(owner,new(){Title="恢复编辑备份",Content=list,PrimaryButtonText="恢复所选备份",CloseButtonText="取消"})!=ContentDialogResult.Primary||list.SelectedItem is not BackupItem item)return;
        if(!await ConfirmLeaveAsync())return;saving=true;UpdateState();
        try{var result=await new CardEditService(model.Repository).RestoreBackupAsync(item.Operation,lifetime.Token);Session=new(result.Card,result.Document);Session.Changed+=DraftChanged;RefreshFields();DraftChanged();model.ReloadCards();EditorMessage.Text="已恢复备份；恢复前的人物卡也已备份。";}
        catch(Exception error){EditorMessage.Text="恢复失败："+error.Message;}
        finally{saving=false;UpdateState();}
    }
    private sealed record BackupItem(FileOperationRecord Operation,string Label);
    private void Retry_Click(object sender,RoutedEventArgs e)=>QueuePreview(true,true);
    private void Angle_Click(object sender,RoutedEventArgs e)=>model.SetAngle(float.Parse((string)((Button)sender).Tag,CultureInfo.InvariantCulture));
    private void Face_Click(object sender,RoutedEventArgs e)=>model.FocusFace();
    private void Frame_Click(object sender,RoutedEventArgs e)=>model.ResetCamera();
    private void Play_Click(object sender,RoutedEventArgs e)=>model.TogglePlayback();
    private void Environment_Click(object sender,RoutedEventArgs e)=>model.RestoreEnvironment();
    private void Viewport_SizeChanged(object sender,SizeChangedEventArgs e){if(model!=null)model.SetPreviewAspect(e.NewSize.Width,e.NewSize.Height);}
    private void Viewport_Pressed(object sender,PointerRoutedEventArgs e){var p=e.GetCurrentPoint(EditorViewport);if(!p.Properties.IsLeftButtonPressed&&!p.Properties.IsRightButtonPressed)return;dragging=true;panning=p.Properties.IsRightButtonPressed;lastPointer=p.Position;EditorViewport.CapturePointer(e.Pointer);e.Handled=true;}
    private void Viewport_Moved(object sender,PointerRoutedEventArgs e){if(!dragging)return;var p=e.GetCurrentPoint(EditorViewport).Position;if(panning)model.Pan(p.X-lastPointer.X,p.Y-lastPointer.Y);else model.Rotate(p.X-lastPointer.X,p.Y-lastPointer.Y);lastPointer=p;}
    private void Viewport_Released(object sender,PointerRoutedEventArgs e){dragging=false;EditorViewport.ReleasePointerCapture(e.Pointer);}
    private void Viewport_Wheel(object sender,PointerRoutedEventArgs e){model.Zoom(e.GetCurrentPoint(EditorViewport).Properties.MouseWheelDelta);e.Handled=true;}
    private void Viewport_DoubleTapped(object sender,DoubleTappedRoutedEventArgs e)=>model.ResetCamera();
}

public sealed class EditorAssetItem(CharacterAsset asset):ObservableObject
{
    public CharacterAsset Asset {get;}=asset;
    public string Label=>Asset.Label;
    public string Source=>Asset.Source+" · "+Asset.Id;
    public bool Requested {get;set;}
    private ImageSource? image;
    public ImageSource? Image {get=>image;set=>Set(ref image,value);}
}
