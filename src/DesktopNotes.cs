using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shell;
using System.Windows.Threading;

[assembly: System.Reflection.AssemblyTitle("桌面便签")]
[assembly: System.Reflection.AssemblyDescription("可自定义分区的本地桌面便签")]
[assembly: System.Reflection.AssemblyVersion("1.1.0.0")]

namespace DesktopNotes {
    public sealed class NoteSection {
        public string Id { get; set; }
        public string Name { get; set; }
        public string Color { get; set; }
        public static List<NoteSection> LegacyDefaults(Palette theme) {
            return new List<NoteSection> {
                new NoteSection { Id="product", Name="标品", Color=theme.Product },
                new NoteSection { Id="custom", Name="定制", Color=theme.Custom },
                new NoteSection { Id="personal", Name="个人", Color=theme.Personal }
            };
        }
    }
    public sealed class Note {
        public string Id { get; set; }
        public string Section { get; set; }
        public string Title { get; set; }
        public string Content { get; set; }
        public string CreatedUtc { get; set; }
        public string UpdatedUtc { get; set; }
    }
    public sealed class Palette {
        public string Name { get; set; }
        public string Background { get; set; }
        public string Text { get; set; }
        public string Product { get; set; }
        public string Custom { get; set; }
        public string Personal { get; set; }
        public static Palette Preset(string name) {
            if (name == "雾灰") return new Palette { Name=name, Background="#F1F3F5", Text="#29333E", Product="#607B95", Custom="#9A7C70", Personal="#778672" };
            if (name == "鼠尾草") return new Palette { Name=name, Background="#EDF1EB", Text="#2E3B30", Product="#647A62", Custom="#9A826A", Personal="#768494" };
            if (name == "夜色") return new Palette { Name=name, Background="#232629", Text="#E7E8E6", Product="#A7B9A3", Custom="#CFAD94", Personal="#A3B2CA" };
            return new Palette { Name="纸白", Background="#F7F6F2", Text="#30332F", Product="#77856C", Custom="#AB8369", Personal="#7B8FA8" };
        }
    }
    public sealed class Placement {
        public double Left { get; set; }
        public double Top { get; set; }
        public double Width { get; set; }
        public double Height { get; set; }
        public bool Topmost { get; set; }
        public Placement() { Left=-1; Top=48; Width=438; Height=730; Topmost=false; }
    }
    public sealed class NotesData {
        public string Schema { get; set; }
        public List<NoteSection> Sections { get; set; }
        public List<Note> Notes { get; set; }
        public Palette Theme { get; set; }
        public Placement Window { get; set; }
        public NotesData() { Schema="desktop-notes.v2"; Notes=new List<Note>(); Theme=Palette.Preset("纸白"); Window=new Placement(); Sections=NoteSection.LegacyDefaults(Theme); }
    }
    public sealed class NotesStore {
        public string FilePath { get; private set; }
        public string RecoveryNotice { get; private set; }
        public NotesData Data { get; set; }
        static JavaScriptSerializer Serializer() { return new JavaScriptSerializer { MaxJsonLength=64*1024*1024, RecursionLimit=80 }; }
        public NotesStore(string path) { FilePath=path; Data=new NotesData(); }
        public static DateTimeOffset Time(string value) { return DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind); }
        public static bool ValidColor(string value) { if (value==null || value.Length!=7 || value[0]!='#') return false; int n; return int.TryParse(value.Substring(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out n); }
        public static void Validate(NotesData data) {
            if (data==null || data.Schema!="desktop-notes.v2") throw new InvalidDataException("请选择桌面便签导出的备份文件。");
            if (data.Notes==null || data.Theme==null || data.Window==null || data.Sections==null) throw new InvalidDataException("备份缺少便签、分区、配色或窗口信息。");
            var sectionIds=new HashSet<string>(StringComparer.Ordinal);
            foreach(NoteSection section in data.Sections) {
                if(section==null || string.IsNullOrWhiteSpace(section.Id) || !section.Id.All(c=>char.IsLetterOrDigit(c)||c=='-'||c=='_') || !sectionIds.Add(section.Id) || string.IsNullOrWhiteSpace(section.Name) || !ValidColor(section.Color)) throw new InvalidDataException("备份中的分区名称、颜色或标识无效。");
            }
            var ids=new HashSet<string>();
            foreach (Note n in data.Notes) {
                Guid id; DateTimeOffset created,updated;
                if(n==null || !Guid.TryParse(n.Id,out id) || !ids.Add(n.Id) || !sectionIds.Contains(n.Section) || n.Title==null || n.Content==null) throw new InvalidDataException("备份中有无效、重复或分区归属缺失的便签。");
                if(!DateTimeOffset.TryParse(n.CreatedUtc,CultureInfo.InvariantCulture,DateTimeStyles.RoundtripKind,out created) || !DateTimeOffset.TryParse(n.UpdatedUtc,CultureInfo.InvariantCulture,DateTimeStyles.RoundtripKind,out updated) || updated<created) throw new InvalidDataException("备份中的记录时间无效。");
            }
            var p=data.Theme;
            if(!ValidColor(p.Background)||!ValidColor(p.Text)||!ValidColor(p.Product)||!ValidColor(p.Custom)||!ValidColor(p.Personal)) throw new InvalidDataException("备份中的配色格式无效。");
            foreach(double n in new[]{data.Window.Left,data.Window.Top,data.Window.Width,data.Window.Height}) if(double.IsNaN(n)||double.IsInfinity(n)) throw new InvalidDataException("窗口位置无效。");
        }
        public static NotesData ParseJson(string json) {
            var fields=Serializer().DeserializeObject(json) as Dictionary<string,object>;
            if(fields==null || !new[]{"Schema","Notes","Theme","Window"}.All(fields.ContainsKey)) throw new InvalidDataException("备份缺少必要字段，请选择桌面便签导出的 JSON 文件。");
            string schema=fields["Schema"] as string;
            if(schema!="desktop-notes.v1" && schema!="desktop-notes.v2") throw new InvalidDataException("这份备份的版本暂时无法识别。");
            if(schema=="desktop-notes.v2" && !fields.ContainsKey("Sections")) throw new InvalidDataException("备份缺少分区信息。");
            var result=Serializer().Deserialize<NotesData>(json);
            if(schema=="desktop-notes.v1") {
                if(result.Theme==null) throw new InvalidDataException("备份缺少配色信息。");
                result.Sections=NoteSection.LegacyDefaults(result.Theme); result.Schema="desktop-notes.v2";
            }
            Validate(result); return result;
        }
        public static NotesData Read(string path) {
            if(new FileInfo(path).Length>64*1024*1024) throw new InvalidDataException("备份文件超过 64 MB，请选择桌面便签导出的文件。");
            return ParseJson(File.ReadAllText(path,Encoding.UTF8));
        }
        public void Load() {
            if(!File.Exists(FilePath)) { if(File.Exists(FilePath+".bak")) { Data=Read(FilePath+".bak"); RecoveryNotice="已恢复上一份有效数据。"; Save(); } return; }
            bool legacy=false;
            try { Data=Read(FilePath); var fields=Serializer().DeserializeObject(File.ReadAllText(FilePath,Encoding.UTF8)) as Dictionary<string,object>; legacy=(string)fields["Schema"]=="desktop-notes.v1"; }
            catch(Exception first) {
                try {
                    Data=Read(FilePath+".bak");
                    string preserved=FilePath+".recovered-"+DateTime.Now.ToString("yyyyMMdd-HHmmssfff")+".json";
                    File.Copy(FilePath,preserved,false);
                    RecoveryNotice="已恢复上一份有效数据。原文件已保留。";
                    File.Delete(FilePath);
                    Save();
                    return;
                } catch(Exception recovery) { throw new IOException("读取便签遇到问题，原始文件已保留。\n"+FilePath+"\n\n"+first.Message+"\n"+recovery.Message); }
            }
            if(legacy) { File.Copy(FilePath,Path.Combine(Path.GetDirectoryName(FilePath),"before-upgrade-v2-"+DateTime.Now.ToString("yyyyMMdd-HHmmssfff")+".json"),false); Save(); }
        }
        public NoteSection Section(string id) { var section=Data.Sections.FirstOrDefault(s=>s.Id==id); if(section==null) throw new InvalidOperationException("分区已发生变化，请重新打开分区管理。"); return section; }
        public NoteSection AddSection(string name,string color) {
            if(string.IsNullOrWhiteSpace(name)||!ValidColor(color)) throw new ArgumentException("请填写分区名称和有效颜色。");
            var section=new NoteSection { Id=Guid.NewGuid().ToString("N"),Name=name.Trim(),Color=color }; Data.Sections.Add(section); return section;
        }
        public void RenameSection(string id,string name) { if(string.IsNullOrWhiteSpace(name)) throw new ArgumentException("分区名称不能为空。"); Section(id).Name=name.Trim(); }
        public void MoveSection(string id,int destination) {
            if(destination<0 || destination>=Data.Sections.Count) throw new ArgumentOutOfRangeException("destination");
            var section=Section(id); Data.Sections.Remove(section); Data.Sections.Insert(destination,section);
        }
        public void RemoveSection(string id,string destination) {
            var section=Section(id); var notes=Data.Notes.Where(n=>n.Section==id).ToList();
            if(notes.Count>0) { if(destination==id) throw new InvalidOperationException("请选择另一个分区接收便签。"); Section(destination); }
            foreach(Note note in notes) note.Section=destination;
            Data.Sections.Remove(section);
        }
        public static string Json(NotesData data) { Validate(data); return Serializer().Serialize(data); }
        public static void WriteAtomic(string path,string text,bool backup) {
            string directory=Path.GetDirectoryName(Path.GetFullPath(path)); Directory.CreateDirectory(directory);
            string temporary=Path.Combine(directory,"."+Path.GetFileName(path)+"."+Guid.NewGuid().ToString("N")+".tmp");
            try {
                byte[] bytes=new UTF8Encoding(false).GetBytes(text);
                using(var file=new FileStream(temporary,FileMode.CreateNew,FileAccess.Write,FileShare.None,4096,FileOptions.WriteThrough)) { file.Write(bytes,0,bytes.Length); file.Flush(true); }
                if(File.Exists(path)) File.Replace(temporary,path,backup?path+".bak":null,true); else File.Move(temporary,path);
            } finally { if(File.Exists(temporary)) File.Delete(temporary); }
        }
        public void Save() { WriteAtomic(FilePath,Json(Data),true); }
        public void Export(string path) { WriteAtomic(path,Json(Data),false); }
        public string Restore(NotesData imported) {
            Validate(imported);
            string backup=Path.Combine(Path.GetDirectoryName(FilePath),"before-import-"+DateTime.Now.ToString("yyyyMMdd-HHmmssfff")+".json");
            Export(backup);
            NotesData old=Data;
            imported.Window=old.Window;
            try { Data=imported; Save(); } catch { Data=old; throw; }
            return backup;
        }
    }

    public sealed class NotesWindow : Window {
        readonly NotesStore store;
        readonly Grid root=new Grid();
        readonly Grid overlay=new Grid();
        readonly StackPanel board=new StackPanel();
        readonly TextBlock status=new TextBlock();
        readonly TextBlock count=new TextBlock();
        readonly DispatcherTimer editTimer=new DispatcherTimer { Interval=TimeSpan.FromMilliseconds(550) };
        readonly DispatcherTimer positionTimer=new DispatcherTimer { Interval=TimeSpan.FromMilliseconds(650) };
        Button pin;
        Note editing;
        string editingSection;
        TextBox titleInput,contentInput;
        TextBlock editorStatus,settingsStatus;
        bool settingUp, composing, loaded, savingError;
        string view="board";
        Dictionary<string,TextBox> colorInputs=new Dictionary<string,TextBox>();
        Dictionary<string,TextBox> sectionNames=new Dictionary<string,TextBox>();
        Dictionary<string,TextBox> sectionColors=new Dictionary<string,TextBox>();
        public NotesWindow(NotesStore notesStore) {
            store=notesStore;
            Title="桌面便签"; MinWidth=340; MinHeight=440;
            using(var appIcon=System.Drawing.Icon.ExtractAssociatedIcon(System.Reflection.Assembly.GetExecutingAssembly().Location)) { if(appIcon!=null) Icon=Imaging.CreateBitmapSourceFromHIcon(appIcon.Handle,Int32Rect.Empty,BitmapSizeOptions.FromEmptyOptions()); }
            FontFamily=new FontFamily("Microsoft YaHei UI"); FontSize=13; Language=XmlLanguage.GetLanguage("zh-CN"); UseLayoutRounding=true; SnapsToDevicePixels=true; TextOptions.SetTextFormattingMode(this,TextFormattingMode.Display);
            WindowStyle=WindowStyle.None; ResizeMode=ResizeMode.CanResize;
            WindowChrome.SetWindowChrome(this,new WindowChrome { CaptionHeight=0, ResizeBorderThickness=new Thickness(6), GlassFrameThickness=new Thickness(0), CornerRadius=new CornerRadius(12) });
            Resources=(ResourceDictionary)XamlReader.Parse(@"<ResourceDictionary xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml'>
<Style TargetType='Button'><Setter Property='Background' Value='Transparent'/><Setter Property='Foreground' Value='{DynamicResource TextBrush}'/><Setter Property='BorderBrush' Value='Transparent'/><Setter Property='BorderThickness' Value='1'/><Setter Property='Padding' Value='10,7'/><Setter Property='Cursor' Value='Hand'/><Setter Property='VerticalContentAlignment' Value='Center'/><Setter Property='HorizontalContentAlignment' Value='Center'/><Setter Property='Template'><Setter.Value><ControlTemplate TargetType='Button'><Border x:Name='b' Background='{TemplateBinding Background}' BorderBrush='{TemplateBinding BorderBrush}' BorderThickness='{TemplateBinding BorderThickness}' CornerRadius='6' Padding='{TemplateBinding Padding}'><ContentPresenter HorizontalAlignment='{TemplateBinding HorizontalContentAlignment}' VerticalAlignment='Center'/></Border><ControlTemplate.Triggers><Trigger Property='IsMouseOver' Value='True'><Setter TargetName='b' Property='Opacity' Value='0.68'/></Trigger><Trigger Property='IsPressed' Value='True'><Setter TargetName='b' Property='Opacity' Value='0.45'/></Trigger><Trigger Property='IsKeyboardFocused' Value='True'><Setter TargetName='b' Property='BorderBrush' Value='{DynamicResource TextBrush}'/></Trigger><Trigger Property='IsEnabled' Value='False'><Setter Property='Opacity' Value='0.4'/></Trigger></ControlTemplate.Triggers></ControlTemplate></Setter.Value></Setter></Style>
<Style TargetType='TextBox'><Setter Property='Background' Value='{DynamicResource SurfaceBrush}'/><Setter Property='Foreground' Value='{DynamicResource TextBrush}'/><Setter Property='BorderBrush' Value='{DynamicResource LineBrush}'/><Setter Property='BorderThickness' Value='1'/><Setter Property='Padding' Value='12,10'/><Setter Property='CaretBrush' Value='{DynamicResource TextBrush}'/><Setter Property='SelectionBrush' Value='{DynamicResource SelectionBrush}'/><Setter Property='Template'><Setter.Value><ControlTemplate TargetType='TextBox'><Border x:Name='b' CornerRadius='7' BorderThickness='{TemplateBinding BorderThickness}' BorderBrush='{TemplateBinding BorderBrush}' Background='{TemplateBinding Background}'><ScrollViewer x:Name='PART_ContentHost' Margin='{TemplateBinding Padding}'/></Border><ControlTemplate.Triggers><Trigger Property='IsKeyboardFocusWithin' Value='True'><Setter TargetName='b' Property='BorderBrush' Value='{DynamicResource TextBrush}'/></Trigger></ControlTemplate.Triggers></ControlTemplate></Setter.Value></Setter></Style>
<Style TargetType='ToolTip'><Setter Property='Background' Value='{DynamicResource SurfaceBrush}'/><Setter Property='Foreground' Value='{DynamicResource TextBrush}'/><Setter Property='BorderBrush' Value='{DynamicResource LineBrush}'/><Setter Property='Padding' Value='8,5'/></Style>
<Style x:Key='TrackButton' TargetType='RepeatButton'><Setter Property='Focusable' Value='False'/><Setter Property='Template'><Setter.Value><ControlTemplate TargetType='RepeatButton'><Border Background='Transparent'/></ControlTemplate></Setter.Value></Setter></Style>
<Style TargetType='ScrollBar'><Setter Property='Width' Value='9'/><Setter Property='Background' Value='Transparent'/><Setter Property='Template'><Setter.Value><ControlTemplate TargetType='ScrollBar'><Grid Background='{TemplateBinding Background}'><Track x:Name='PART_Track' IsDirectionReversed='True' Orientation='{TemplateBinding Orientation}' Minimum='{TemplateBinding Minimum}' Maximum='{TemplateBinding Maximum}' Value='{TemplateBinding Value}' ViewportSize='{TemplateBinding ViewportSize}'><Track.DecreaseRepeatButton><RepeatButton Command='ScrollBar.PageUpCommand' Style='{StaticResource TrackButton}'/></Track.DecreaseRepeatButton><Track.Thumb><Thumb><Thumb.Template><ControlTemplate TargetType='Thumb'><Border x:Name='thumb' Background='{DynamicResource LineBrush}' CornerRadius='3' Margin='2,1'/><ControlTemplate.Triggers><Trigger Property='IsMouseOver' Value='True'><Setter TargetName='thumb' Property='Background' Value='{DynamicResource MutedBrush}'/></Trigger></ControlTemplate.Triggers></ControlTemplate></Thumb.Template></Thumb></Track.Thumb><Track.IncreaseRepeatButton><RepeatButton Command='ScrollBar.PageDownCommand' Style='{StaticResource TrackButton}'/></Track.IncreaseRepeatButton></Track></Grid></ControlTemplate></Setter.Value></Setter></Style>
</ResourceDictionary>");
            ApplyTheme();
            SetResourceReference(BackgroundProperty,"CanvasBrush"); SetResourceReference(ForegroundProperty,"TextBrush");
            var frame=new Border { BorderThickness=new Thickness(1), Child=root }; frame.SetResourceReference(Border.BorderBrushProperty,"LineBrush"); Content=frame;
            var main=new Grid(); main.RowDefinitions.Add(new RowDefinition { Height=GridLength.Auto }); main.RowDefinitions.Add(new RowDefinition()); main.RowDefinitions.Add(new RowDefinition { Height=GridLength.Auto }); root.Children.Add(main);
            var header=new Grid { Margin=new Thickness(22,17,14,18), Background=Brushes.Transparent }; header.ColumnDefinitions.Add(new ColumnDefinition()); header.ColumnDefinitions.Add(new ColumnDefinition { Width=GridLength.Auto });
            header.MouseLeftButtonDown+=(s,e)=> { if(e.ClickCount==2) return; if(e.OriginalSource is TextBlock || e.OriginalSource==header) { try { DragMove(); } catch(InvalidOperationException){} } };
            var heading=new StackPanel { Margin=new Thickness(0,0,8,0) }; heading.Children.Add(Text("便签",24,"TextBrush",true)); heading.Children.Add(Text(DateTime.Now.ToString("yyyy年M月d日",CultureInfo.GetCultureInfo("zh-CN")),11,"MutedBrush",false,new Thickness(1,7,0,0))); header.Children.Add(heading);
            var tools=new StackPanel { Orientation=Orientation.Horizontal, VerticalAlignment=VerticalAlignment.Top, Margin=new Thickness(0,1,0,0) }; Grid.SetColumn(tools,1); header.Children.Add(tools);
            pin=IconButton("\uE718","置顶窗口","Pin",()=> { Topmost=!Topmost; store.Data.Window.Topmost=Topmost; UpdatePin(); SyncNativePin(); Persist(); }); tools.Children.Add(pin);
            tools.Children.Add(IconButton("\uE713","设置","Settings",OpenSettings));
            tools.Children.Add(IconButton("\uE921","最小化","Minimize",()=>WindowState=WindowState.Minimized));
            tools.Children.Add(IconButton("\uE8BB","关闭窗口","Close",Close)); main.Children.Add(header);
            var scroll=new ScrollViewer { Content=board, VerticalScrollBarVisibility=ScrollBarVisibility.Auto, HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled, Padding=new Thickness(22,0,22,12), CanContentScroll=false }; Grid.SetRow(scroll,1); main.Children.Add(scroll);
            var footer=new Grid { Margin=new Thickness(22,11,22,13) }; footer.ColumnDefinitions.Add(new ColumnDefinition()); footer.ColumnDefinitions.Add(new ColumnDefinition { Width=GridLength.Auto });
            status.FontSize=10.5; status.SetResourceReference(TextBlock.ForegroundProperty,"MutedBrush"); status.Text="保存在此电脑"; status.TextTrimming=TextTrimming.CharacterEllipsis; status.ToolTip=store.FilePath;
            count.FontSize=10.5; count.SetResourceReference(TextBlock.ForegroundProperty,"MutedBrush"); Grid.SetColumn(count,1); count.Margin=new Thickness(12,0,0,0); footer.Children.Add(status); footer.Children.Add(count); Grid.SetRow(footer,2); main.Children.Add(footer);
            overlay.SetResourceReference(Panel.BackgroundProperty,"CanvasBrush"); overlay.Visibility=Visibility.Collapsed; root.Children.Add(overlay);
            editTimer.Tick+=(s,e)=> { editTimer.Stop(); if(!composing) FlushEditor(); };
            positionTimer.Tick+=(s,e)=> { positionTimer.Stop(); SavePlacement(); Persist(); };
            LocationChanged+=(s,e)=>SchedulePosition(); SizeChanged+=(s,e)=>SchedulePosition();
            Loaded+=(s,e)=> { loaded=true; ApplyNativeFrame(); SyncNativePin(); if(!string.IsNullOrEmpty(store.RecoveryNotice)) MessageBox.Show(this,store.RecoveryNotice,"数据恢复",MessageBoxButton.OK,MessageBoxImage.Information); };
            StateChanged+=(s,e)=> { if(loaded && WindowState==WindowState.Normal) SyncNativePin(); };
            Closing+=(s,e)=> { positionTimer.Stop(); if(!FlushEditor() || (view=="sections" && !CommitSections())) { e.Cancel=true; return; } SavePlacement(); if(!Persist()) e.Cancel=true; };
            Closed+=(s,e)=> { editTimer.Stop(); positionTimer.Stop(); };
            PreviewKeyDown+=(s,e)=> { if(e.Key==Key.Escape && view!="board") { CloseOverlay(); e.Handled=true; } if(e.Key==Key.S && Keyboard.Modifiers==ModifierKeys.Control) { FlushEditor(); Persist(); e.Handled=true; } };
            RestorePlacement(); RenderBoard(); UpdatePin();
        }
        static TextBlock Text(string value,double size=13,string brush="TextBrush",bool bold=false,Thickness? margin=null) {
            var t=new TextBlock { Text=value, FontFamily=new FontFamily("Microsoft YaHei UI"), FontSize=size, FontWeight=bold?FontWeights.SemiBold:FontWeights.Normal, TextWrapping=TextWrapping.Wrap, Margin=margin??new Thickness(0) }; t.SetResourceReference(TextBlock.ForegroundProperty,brush); return t;
        }
        static void Id(DependencyObject control,string id,string name=null) { AutomationProperties.SetAutomationId(control,id); if(name!=null) AutomationProperties.SetName(control,name); }
        Button ActionButton(string text,string id,Action action,bool primary=false) {
            var b=new Button { Content=text }; Id(b,id,text); if(primary) { b.SetResourceReference(BackgroundProperty,"TextBrush"); b.SetResourceReference(ForegroundProperty,"CanvasBrush"); } b.Click+=(s,e)=>action(); return b;
        }
        Button IconButton(string glyph,string label,string id,Action action) {
            var b=ActionButton("",id,action); b.Content=new TextBlock { Text=glyph, FontFamily=new FontFamily("Segoe MDL2 Assets"), FontSize=14, VerticalAlignment=VerticalAlignment.Center }; b.Width=32; b.Height=32; b.Padding=new Thickness(5); b.ToolTip=label; AutomationProperties.SetName(b,label); return b;
        }
        string Label(string section) { return store.Section(section).Name; }
        static string SectionBrush(string section) { return "Section-"+section+"-Brush"; }
        void UpdatePin() { pin.SetResourceReference(BackgroundProperty,Topmost?"SelectionBrush":"CanvasBrush"); pin.ToolTip=Topmost?"取消置顶":"置顶窗口"; AutomationProperties.SetName(pin,Topmost?"取消置顶":"置顶窗口"); }
        [DllImport("user32.dll",SetLastError=true)] static extern bool SetWindowPos(IntPtr handle,IntPtr after,int x,int y,int width,int height,uint flags);
        void SyncNativePin() {
            var currentHandle=new WindowInteropHelper(this).Handle;
            if(currentHandle!=IntPtr.Zero) SetWindowPos(currentHandle,new IntPtr(Topmost?-1:-2),0,0,0,0,0x13);
            Dispatcher.BeginInvoke(new Action(()=> {
                var handle=new WindowInteropHelper(this).Handle;
                if(handle!=IntPtr.Zero) SetWindowPos(handle,new IntPtr(Topmost?-1:-2),0,0,0,0,0x13);
            }),DispatcherPriority.ContextIdle);
        }
        void RestorePlacement() {
            var p=store.Data.Window;
            Width=Math.Max(MinWidth,Math.Min(p.Width,SystemParameters.VirtualScreenWidth)); Height=Math.Max(MinHeight,Math.Min(p.Height,SystemParameters.WorkArea.Height));
            Left=p.Left==-1?SystemParameters.WorkArea.Right-Width-24:p.Left; Top=p.Top;
            var area=new Rect(SystemParameters.VirtualScreenLeft,SystemParameters.VirtualScreenTop,SystemParameters.VirtualScreenWidth,SystemParameters.VirtualScreenHeight);
            if(!area.IntersectsWith(new Rect(Left+30,Top+10,Math.Max(40,Width-60),50))) { Left=SystemParameters.WorkArea.Right-Width-24; Top=SystemParameters.WorkArea.Top+32; }
            Topmost=p.Topmost;
        }
        void SchedulePosition() { if(!loaded || WindowState!=WindowState.Normal) return; positionTimer.Stop(); positionTimer.Start(); }
        void SavePlacement() { if(WindowState!=WindowState.Normal) return; store.Data.Window=new Placement { Left=Left,Top=Top,Width=Width,Height=Height,Topmost=Topmost }; }
        public bool Persist() {
            try { store.Save(); savingError=false; status.Text="已保存到此电脑"; if(editorStatus!=null) editorStatus.Text="已自动保存 · "+DateTime.Now.ToString("HH:mm"); return true; }
            catch(Exception e) { status.Text="保存遇到问题，请导出备份"; if(editorStatus!=null) editorStatus.Text="保存遇到问题"; if(!savingError) MessageBox.Show(this,"便签仍保留在当前窗口。请通过「配色与备份」导出，再检查本地保存位置。\n\n"+e.Message,"保存遇到问题",MessageBoxButton.OK,MessageBoxImage.Error); savingError=true; return false; }
        }
        public void RenderBoard() {
            board.Children.Clear();
            if(store.Data.Sections.Count==0) { var empty=ActionButton("＋ 新建第一个分区","FirstSection",OpenSections,true); empty.Margin=new Thickness(0,12,0,0); board.Children.Add(empty); }
            foreach(NoteSection definition in store.Data.Sections) {
                string section=definition.Id;
                var group=new StackPanel { Margin=new Thickness(0,0,0,23) };
                var row=new Grid { Margin=new Thickness(0,0,0,10) }; row.ColumnDefinitions.Add(new ColumnDefinition()); row.ColumnDefinitions.Add(new ColumnDefinition { Width=GridLength.Auto });
                var title=new StackPanel { Orientation=Orientation.Horizontal, VerticalAlignment=VerticalAlignment.Center };
                var mark=new Border { Width=4,Height=16,CornerRadius=new CornerRadius(2),Margin=new Thickness(0,0,9,0) }; mark.SetResourceReference(Border.BackgroundProperty,SectionBrush(section)); title.Children.Add(mark); title.Children.Add(Text(Label(section),15,"TextBrush",true));
                var notes=store.Data.Notes.Where(n=>n.Section==section).OrderByDescending(n=>NotesStore.Time(n.CreatedUtc)).ToList();
                title.Children.Add(Text(notes.Count.ToString("00"),10.5,"MutedBrush",false,new Thickness(9,3,0,0))); row.Children.Add(title);
                string captured=section; var add=IconButton("\uE710","添加"+Label(section)+"便签","Add-"+section,()=>OpenEditor(captured,null)); add.Width=28; add.Height=28; Grid.SetColumn(add,1); row.Children.Add(add); group.Children.Add(row);
                if(notes.Count==0) {
                    var empty=ActionButton("","Empty-"+section,()=>OpenEditor(captured,null)); empty.HorizontalContentAlignment=HorizontalAlignment.Stretch; empty.Padding=new Thickness(16,18,16,18); empty.SetResourceReference(BackgroundProperty,"SurfaceBrush");
                    var emptyText=new StackPanel(); emptyText.Children.Add(Text("写下第一条便签",12,"MutedBrush")); emptyText.Children.Add(Text("标题与内容，随手记录",10.5,"MutedBrush",false,new Thickness(0,7,0,0))); empty.Content=emptyText; group.Children.Add(empty);
                }
                foreach(Note note in notes) group.Children.Add(NoteCard(note));
                board.Children.Add(group);
            }
            count.Text=store.Data.Notes.Count+" 条便签";
        }
        Border NoteCard(Note note) {
            var card=new Border { CornerRadius=new CornerRadius(8),Padding=new Thickness(15,13,12,10),Margin=new Thickness(0,0,0,8) }; card.SetResourceReference(Border.BackgroundProperty,"SurfaceBrush"); Id(card,"Note-"+note.Id);
            var stack=new StackPanel(); card.Child=stack;
            var line=new Grid(); line.ColumnDefinitions.Add(new ColumnDefinition()); line.ColumnDefinitions.Add(new ColumnDefinition { Width=GridLength.Auto });
            var editTitle=ActionButton("","Edit-"+note.Id,()=>OpenEditor(note.Section,note)); editTitle.HorizontalContentAlignment=HorizontalAlignment.Left; editTitle.Padding=new Thickness(0,1,6,5); editTitle.Content=Text(string.IsNullOrWhiteSpace(note.Title)?"无标题":note.Title,14,"TextBrush",true); line.Children.Add(editTitle);
            var del=IconButton("\uE74D","删除便签","Delete-"+note.Id,()=>DeleteNote(note)); del.Width=25; del.Height=25; del.VerticalAlignment=VerticalAlignment.Top; del.Margin=new Thickness(0,-2,-3,0); Grid.SetColumn(del,1); line.Children.Add(del); stack.Children.Add(line);
            if(!string.IsNullOrEmpty(note.Content)) {
                var body=Text(note.Content,12,"BodyBrush",false,new Thickness(0,1,3,0)); body.LineHeight=21; body.TextTrimming=TextTrimming.CharacterEllipsis; stack.Children.Add(body);
                bool lengthy=note.Content.Length>110 || note.Content.Count(c=>c=='\n')>3;
                if(lengthy) { body.MaxHeight=84; bool expanded=false; var expand=ActionButton("展开全文","Expand-"+note.Id,()=>{}); expand.Padding=new Thickness(0,5,0,2); expand.FontSize=10.5; expand.HorizontalAlignment=HorizontalAlignment.Left; expand.SetResourceReference(ForegroundProperty,"MutedBrush"); expand.Click+=(s,e)=> { expanded=!expanded; body.MaxHeight=expanded?double.PositiveInfinity:84; expand.Content=expanded?"收起":"展开全文"; }; stack.Children.Add(expand); }
            }
            var foot=new Grid { Margin=new Thickness(0,10,0,0) }; foot.ColumnDefinitions.Add(new ColumnDefinition()); foot.ColumnDefinitions.Add(new ColumnDefinition { Width=GridLength.Auto });
            DateTimeOffset created=NotesStore.Time(note.CreatedUtc).ToLocalTime(),updated=NotesStore.Time(note.UpdatedUtc).ToLocalTime();
            var time=Text(created.ToString("MM.dd HH:mm")+" 创建"+(note.CreatedUtc!=note.UpdatedUtc?" · 已编辑":""),10,"MutedBrush");
            time.ToolTip="创建："+created.ToString("yyyy-MM-dd HH:mm:ss zzz")+"\n更新："+updated.ToString("yyyy-MM-dd HH:mm:ss zzz"); time.VerticalAlignment=VerticalAlignment.Center; foot.Children.Add(time);
            var edit=IconButton("\uE70F","编辑便签","EditIcon-"+note.Id,()=>OpenEditor(note.Section,note)); edit.Width=24; edit.Height=24; edit.Margin=new Thickness(0,-3,-3,-3); Grid.SetColumn(edit,1); foot.Children.Add(edit); stack.Children.Add(foot); return card;
        }
        void DeleteNote(Note note) {
            if(MessageBox.Show(this,"删除「"+(string.IsNullOrWhiteSpace(note.Title)?"无标题":note.Title)+"」？","删除便签",MessageBoxButton.OKCancel,MessageBoxImage.Question,MessageBoxResult.Cancel)!=MessageBoxResult.OK) return;
            int index=store.Data.Notes.IndexOf(note); store.Data.Notes.Remove(note); if(!Persist()) store.Data.Notes.Insert(index,note); RenderBoard();
        }
        Grid OverlayPage(string title,string subtitle) {
            overlay.Children.Clear(); overlay.Visibility=Visibility.Visible;
            var page=new Grid { Margin=new Thickness(22,18,22,20) }; page.RowDefinitions.Add(new RowDefinition { Height=GridLength.Auto }); page.RowDefinitions.Add(new RowDefinition()); page.RowDefinitions.Add(new RowDefinition { Height=GridLength.Auto }); overlay.Children.Add(page);
            var h=new Grid { Margin=new Thickness(0,0,0,19) }; h.ColumnDefinitions.Add(new ColumnDefinition { Width=GridLength.Auto }); h.ColumnDefinitions.Add(new ColumnDefinition());
            h.Background=Brushes.Transparent; h.MouseLeftButtonDown+=(s,e)=> { if(e.OriginalSource is TextBlock || e.OriginalSource==h) { try { DragMove(); } catch(InvalidOperationException){} } };
            var back=IconButton("\uE72B","返回","Back",CloseOverlay); back.Margin=new Thickness(-5,0,10,0); h.Children.Add(back);
            var text=new StackPanel(); text.Children.Add(Text(title,19,"TextBrush",true)); text.Children.Add(Text(subtitle,10.5,"MutedBrush",false,new Thickness(0,6,0,0))); Grid.SetColumn(text,1); h.Children.Add(text); page.Children.Add(h); return page;
        }
        public void OpenEditor(string section,Note note) {
            if(!FlushEditor()) return; view="editor"; editing=note; editingSection=section; settingUp=true;
            var page=OverlayPage(Label(section)+" · "+(note==null?"新便签":"编辑便签"),note==null?"开始输入后自动保存":FullTimes(note));
            var form=new Grid(); form.RowDefinitions.Add(new RowDefinition { Height=GridLength.Auto }); form.RowDefinitions.Add(new RowDefinition { Height=GridLength.Auto }); form.RowDefinitions.Add(new RowDefinition { Height=GridLength.Auto }); form.RowDefinitions.Add(new RowDefinition());
            form.Children.Add(Text("标题",11,"MutedBrush",false,new Thickness(0,0,0,8)));
            titleInput=new TextBox { Text=note==null?"":note.Title,FontSize=16,TextWrapping=TextWrapping.Wrap,MinHeight=47,MaxHeight=112,VerticalScrollBarVisibility=ScrollBarVisibility.Auto }; Id(titleInput,"TitleInput","标题"); Grid.SetRow(titleInput,1); form.Children.Add(titleInput);
            var label=Text("内容",11,"MutedBrush",false,new Thickness(0,18,0,8)); Grid.SetRow(label,2); form.Children.Add(label);
            contentInput=new TextBox { Text=note==null?"":note.Content,FontSize=13,AcceptsReturn=true,TextWrapping=TextWrapping.Wrap,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled }; contentInput.SetValue(TextBlock.LineHeightProperty,24.0); Id(contentInput,"ContentInput","内容"); Grid.SetRow(contentInput,3); form.Children.Add(contentInput); Grid.SetRow(form,1); page.Children.Add(form);
            foreach(TextBox input in new[]{titleInput,contentInput}) {
                input.TextChanged+=(s,e)=> { if(settingUp) return; editorStatus.Text="正在保存…"; editTimer.Stop(); editTimer.Start(); };
                TextCompositionManager.AddPreviewTextInputStartHandler(input,(s,e)=>composing=true);
                TextCompositionManager.AddPreviewTextInputHandler(input,(s,e)=> { composing=false; editTimer.Stop(); editTimer.Start(); });
            }
            var footer=new Grid { Margin=new Thickness(0,16,0,0) }; footer.ColumnDefinitions.Add(new ColumnDefinition()); footer.ColumnDefinitions.Add(new ColumnDefinition { Width=GridLength.Auto });
            editorStatus=Text(note==null?"支持多行内容":"已保存",10.5,"MutedBrush"); editorStatus.VerticalAlignment=VerticalAlignment.Center; footer.Children.Add(editorStatus);
            var done=ActionButton("完成","EditorDone",CloseOverlay,true); done.MinWidth=78; Grid.SetColumn(done,1); footer.Children.Add(done); Grid.SetRow(footer,2); page.Children.Add(footer); settingUp=false;
            Dispatcher.BeginInvoke(new Action(()=>titleInput.Focus()),DispatcherPriority.Input);
        }
        static string FullTimes(Note note) { return "创建 "+NotesStore.Time(note.CreatedUtc).ToLocalTime().ToString("yyyy.MM.dd HH:mm")+"\n更新 "+NotesStore.Time(note.UpdatedUtc).ToLocalTime().ToString("yyyy.MM.dd HH:mm"); }
        public bool FlushEditor() {
            editTimer.Stop(); if(view!="editor" || titleInput==null || settingUp) return true;
            string title=titleInput.Text,content=contentInput.Text;
            if(editing==null && string.IsNullOrWhiteSpace(title) && string.IsNullOrWhiteSpace(content)) return true;
            if(editing!=null && editing.Title==title && editing.Content==content) return !savingError || Persist();
            string now=DateTimeOffset.UtcNow.ToString("o",CultureInfo.InvariantCulture);
            if(editing==null) { editing=new Note { Id=Guid.NewGuid().ToString(),Section=editingSection,Title=title,Content=content,CreatedUtc=now,UpdatedUtc=now }; store.Data.Notes.Add(editing); }
            else { editing.Title=title; editing.Content=content; editing.UpdatedUtc=NotesStore.Time(editing.CreatedUtc)>NotesStore.Time(now)?editing.CreatedUtc:now; }
            bool ok=Persist(); RenderBoard(); return ok;
        }
        public void CloseOverlay() {
            FlushEditor();
            if(view=="delete-section") { OpenSections(); return; }
            if(view=="sections") { if(CommitSections()) OpenSettings(); return; }
            if(view=="settings" && !CommitColors()) return;
            view="board"; titleInput=null; contentInput=null; editing=null; editorStatus=null; overlay.Children.Clear(); overlay.Visibility=Visibility.Collapsed; RenderBoard();
        }
        Color C(string hex) { return (Color)ColorConverter.ConvertFromString(hex); }
        static Color Mix(Color a,Color b,double ratio) { return Color.FromRgb((byte)(a.R*(1-ratio)+b.R*ratio),(byte)(a.G*(1-ratio)+b.G*ratio),(byte)(a.B*(1-ratio)+b.B*ratio)); }
        void Brush(string key,Color color) { var brush=new SolidColorBrush(color); brush.Freeze(); Resources[key]=brush; }
        public void ApplyTheme() {
            Palette p=store.Data.Theme; Color bg=C(p.Background),fg=C(p.Text); bool dark=(bg.R*0.2126+bg.G*0.7152+bg.B*0.0722)<128;
            Brush("CanvasBrush",bg); Brush("TextBrush",fg); Brush("SurfaceBrush",Mix(bg,Colors.White,dark?0.045:0.66)); Brush("BodyBrush",Mix(bg,fg,0.87)); Brush("MutedBrush",Mix(bg,fg,dark?0.69:0.70)); Brush("LineBrush",Mix(bg,fg,0.13)); Brush("SelectionBrush",Mix(bg,fg,0.12)); foreach(NoteSection section in store.Data.Sections) Brush(SectionBrush(section.Id),C(section.Color)); if(loaded) ApplyNativeFrame();
        }
        [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd,int attr,ref int val,int size);
        void ApplyNativeFrame() { try { int round=2; var handle=new WindowInteropHelper(this).Handle; DwmSetWindowAttribute(handle,33,ref round,4); Color c=C(store.Data.Theme.Background); int dark=c.R+c.G+c.B<384?1:0; DwmSetWindowAttribute(handle,20,ref dark,4); } catch(DllNotFoundException){} }
        bool SectionChange(Action change,bool keepBackup=false) {
            string before=NotesStore.Json(store.Data);
            try {
                if(keepBackup) store.Export(Path.Combine(Path.GetDirectoryName(store.FilePath),"before-section-delete-"+DateTime.Now.ToString("yyyyMMdd-HHmmssfff")+".json"));
                change();
                if(!Persist()) { store.Data=NotesStore.ParseJson(before); ApplyTheme(); return false; }
                ApplyTheme(); RenderBoard(); return true;
            } catch(Exception e) { store.Data=NotesStore.ParseJson(before); ApplyTheme(); MessageBox.Show(this,e.Message,"分区保存遇到问题",MessageBoxButton.OK,MessageBoxImage.Error); return false; }
        }
        bool CommitSections() {
            if(view!="sections" || settingUp) return true;
            foreach(var item in sectionNames) {
                if(string.IsNullOrWhiteSpace(item.Value.Text)) { item.Value.BorderBrush=Brushes.IndianRed; settingsStatus.Text="分区名称不能为空"; return false; }
                item.Value.SetResourceReference(TextBox.BorderBrushProperty,"LineBrush");
            }
            foreach(var item in sectionColors) {
                if(!NotesStore.ValidColor(item.Value.Text.Trim())) { item.Value.BorderBrush=Brushes.IndianRed; settingsStatus.Text="颜色格式：#RRGGBB"; return false; }
                item.Value.SetResourceReference(TextBox.BorderBrushProperty,"LineBrush");
            }
            bool changed=sectionNames.Any(x=>store.Section(x.Key).Name!=x.Value.Text.Trim()) || sectionColors.Any(x=>store.Section(x.Key).Color!=x.Value.Text.Trim().ToUpperInvariant());
            if(!changed) return true;
            bool ok=SectionChange(()=> {
                foreach(var item in sectionNames) store.RenameSection(item.Key,item.Value.Text);
                foreach(var item in sectionColors) { string color=item.Value.Text.Trim().ToUpperInvariant(); if(store.Section(item.Key).Color!=color) store.Data.Theme.Name="自定义"; store.Section(item.Key).Color=color; }
            });
            if(ok) settingsStatus.Text="分区更改已保存"; return ok;
        }
        public void OpenSections() {
            FlushEditor(); view="sections"; settingUp=true; sectionNames.Clear(); sectionColors.Clear(); editorStatus=null;
            var page=OverlayPage("管理分区","调整名称、顺序和颜色");
            var list=new StackPanel(); var scroll=new ScrollViewer { Content=list,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled }; Grid.SetRow(scroll,1); page.Children.Add(scroll);
            foreach(NoteSection section in store.Data.Sections) {
                string id=section.Id; int position=store.Data.Sections.IndexOf(section);
                var card=new Border { CornerRadius=new CornerRadius(8),Padding=new Thickness(13),Margin=new Thickness(0,0,0,10) }; card.SetResourceReference(Border.BackgroundProperty,"SurfaceBrush"); var fields=new StackPanel(); card.Child=fields;
                var name=new TextBox { Text=section.Name,FontSize=14,Padding=new Thickness(9,7,9,7),TextWrapping=TextWrapping.Wrap }; Id(name,"SectionName-"+id,"分区名称"); sectionNames[id]=name; name.LostKeyboardFocus+=(s,e)=>CommitSections(); name.KeyDown+=(s,e)=> { if(e.Key==Key.Enter) { CommitSections(); e.Handled=true; } }; fields.Children.Add(name);
                fields.Children.Add(Text(store.Data.Notes.Count(n=>n.Section==id)+" 条便签",10.5,"MutedBrush",false,new Thickness(1,7,0,9)));
                var row=new Grid(); row.ColumnDefinitions.Add(new ColumnDefinition()); row.ColumnDefinitions.Add(new ColumnDefinition { Width=GridLength.Auto });
                var colorGroup=new StackPanel { Orientation=Orientation.Horizontal }; var picker=ActionButton("","PickSection-"+id,()=>PickSectionColor(id)); picker.Width=29; picker.Height=31; picker.Padding=new Thickness(3); picker.ToolTip="选择分区颜色";
                var swatch=new Border { Width=21,Height=21,CornerRadius=new CornerRadius(4),BorderThickness=new Thickness(1) }; swatch.SetResourceReference(Border.BackgroundProperty,SectionBrush(id)); swatch.SetResourceReference(Border.BorderBrushProperty,"LineBrush"); picker.Content=swatch; colorGroup.Children.Add(picker);
                var hex=new TextBox { Text=section.Color,Width=96,Padding=new Thickness(8,6,8,6),FontFamily=new FontFamily("Consolas"),FontSize=12,MaxLength=7,Margin=new Thickness(5,0,0,0) }; Id(hex,"SectionColor-"+id,"分区颜色"); sectionColors[id]=hex; hex.LostKeyboardFocus+=(s,e)=>CommitSections(); hex.KeyDown+=(s,e)=> { if(e.Key==Key.Enter) { CommitSections(); e.Handled=true; } }; colorGroup.Children.Add(hex); row.Children.Add(colorGroup);
                var actions=new StackPanel { Orientation=Orientation.Horizontal,VerticalAlignment=VerticalAlignment.Center }; Grid.SetColumn(actions,1);
                var up=IconButton("\uE74A","上移分区","SectionUp-"+id,()=> { if(CommitSections() && SectionChange(()=>store.MoveSection(id,store.Data.Sections.IndexOf(store.Section(id))-1))) OpenSections(); }); up.Width=27; up.IsEnabled=position>0; actions.Children.Add(up);
                var down=IconButton("\uE74B","下移分区","SectionDown-"+id,()=> { if(CommitSections() && SectionChange(()=>store.MoveSection(id,store.Data.Sections.IndexOf(store.Section(id))+1))) OpenSections(); }); down.Width=27; down.IsEnabled=position<store.Data.Sections.Count-1; actions.Children.Add(down);
                var delete=IconButton("\uE74D","删除分区","DeleteSection-"+id,()=> { if(CommitSections()) OpenDeleteSection(id); }); delete.Width=27; actions.Children.Add(delete); row.Children.Add(actions); fields.Children.Add(row); list.Children.Add(card);
            }
            if(store.Data.Sections.Count==0) list.Children.Add(Text("创建一个分区，开始记录你的便签。",13,"MutedBrush",false,new Thickness(0,15,0,0)));
            var bottom=new StackPanel { Margin=new Thickness(0,14,0,0) }; settingsStatus=Text("更改自动保存",10.5,"MutedBrush",false,new Thickness(0,0,0,9)); bottom.Children.Add(settingsStatus);
            var buttons=new Grid(); buttons.ColumnDefinitions.Add(new ColumnDefinition()); buttons.ColumnDefinitions.Add(new ColumnDefinition { Width=GridLength.Auto });
            var add=ActionButton("＋ 新建分区","AddSection",()=> {
                if(!CommitSections()) return;
                string name="新分区"; int suffix=2; while(store.Data.Sections.Any(s=>s.Name==name)) name="新分区 "+suffix++;
                NoteSection added=null; var palette=store.Data.Theme; string[] colors={palette.Product,palette.Custom,palette.Personal};
                if(!SectionChange(()=>added=store.AddSection(name,colors[store.Data.Sections.Count%colors.Length]))) return;
                OpenSections(); var input=sectionNames[added.Id]; Dispatcher.BeginInvoke(new Action(()=> { input.BringIntoView(); input.Focus(); input.SelectAll(); }),DispatcherPriority.Input);
            }); add.HorizontalAlignment=HorizontalAlignment.Left; add.SetResourceReference(BackgroundProperty,"SurfaceBrush"); buttons.Children.Add(add);
            var done=ActionButton("完成","SectionsDone",CloseOverlay,true); done.MinWidth=78; Grid.SetColumn(done,1); buttons.Children.Add(done); bottom.Children.Add(buttons); Grid.SetRow(bottom,2); page.Children.Add(bottom); settingUp=false;
        }
        void PickSectionColor(string id) {
            if(!CommitSections()) return; Color current=C(store.Section(id).Color);
            using(var dialog=new System.Windows.Forms.ColorDialog { FullOpen=true,Color=System.Drawing.Color.FromArgb(current.R,current.G,current.B) }) {
                if(dialog.ShowDialog(new DialogOwner(new WindowInteropHelper(this).Handle))!=System.Windows.Forms.DialogResult.OK) return;
                sectionColors[id].Text="#"+dialog.Color.R.ToString("X2")+dialog.Color.G.ToString("X2")+dialog.Color.B.ToString("X2"); CommitSections();
            }
        }
        public void OpenDeleteSection(string id) {
            view="delete-section"; var section=store.Section(id); int noteCount=store.Data.Notes.Count(n=>n.Section==id);
            var page=OverlayPage("删除分区",section.Name); var content=new StackPanel(); Grid.SetRow(content,1); page.Children.Add(content);
            content.Children.Add(Text("这个分区有 "+noteCount+" 条便签。",14,"TextBrush",true,new Thickness(0,5,0,13)));
            var destinations=store.Data.Sections.Where(s=>s.Id!=id).ToList(); ComboBox select=null;
            if(noteCount>0 && destinations.Count>0) {
                content.Children.Add(Text("选择接收便签的分区。迁移会保留标题、内容和原有时间。",12,"BodyBrush",false,new Thickness(0,0,0,14)));
                select=new ComboBox { ItemsSource=destinations,DisplayMemberPath="Name",SelectedIndex=0,Padding=new Thickness(10),FontSize=14 }; Id(select,"SectionDestination","接收便签的分区"); content.Children.Add(select);
            } else if(noteCount>0) content.Children.Add(Text("请先新增一个分区，用于接收这些便签。",12,"BodyBrush"));
            else content.Children.Add(Text("删除后，你可以随时创建新的分区。",12,"BodyBrush"));
            content.Children.Add(Text("确认操作前，会自动保存一份分区调整备份。",10.5,"MutedBrush",false,new Thickness(0,18,0,0)));
            var buttons=new Grid { Margin=new Thickness(0,16,0,0) }; buttons.ColumnDefinitions.Add(new ColumnDefinition()); buttons.ColumnDefinitions.Add(new ColumnDefinition { Width=GridLength.Auto }); var cancel=ActionButton("返回管理","CancelSectionDelete",OpenSections); cancel.HorizontalAlignment=HorizontalAlignment.Left; buttons.Children.Add(cancel);
            var confirm=ActionButton(noteCount>0?"迁移并删除":"删除分区","ConfirmSectionDelete",()=> {
                string destination=select==null?null:((NoteSection)select.SelectedItem).Id;
                if(SectionChange(()=>store.RemoveSection(id,destination),true)) { Resources.Remove(SectionBrush(id)); OpenSections(); }
            },true); confirm.IsEnabled=noteCount==0 || destinations.Count>0; Grid.SetColumn(confirm,1); buttons.Children.Add(confirm); Grid.SetRow(buttons,2); page.Children.Add(buttons);
        }
        public void OpenSettings() {
            FlushEditor(); view="settings"; colorInputs.Clear(); editorStatus=null;
            var page=OverlayPage("设置","分区、配色与本地备份");
            var content=new StackPanel(); var scroll=new ScrollViewer { Content=content,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled }; Grid.SetRow(scroll,1); page.Children.Add(scroll);
            var manage=ActionButton("管理分区 · "+store.Data.Sections.Count+" 个","ManageSections",()=> { if(CommitColors()) OpenSections(); }); manage.HorizontalContentAlignment=HorizontalAlignment.Left; manage.SetResourceReference(BackgroundProperty,"SurfaceBrush"); manage.Margin=new Thickness(0,0,0,20); content.Children.Add(manage);
            content.Children.Add(Text("主题",11,"MutedBrush",false,new Thickness(0,0,0,9)));
            var presets=new System.Windows.Controls.Primitives.UniformGrid { Columns=4,Margin=new Thickness(-3,0,-3,20) };
            foreach(string name in new[]{"纸白","雾灰","鼠尾草","夜色"}) {
                string picked=name; Palette p=Palette.Preset(name); var button=ActionButton("","Theme-"+name,()=> { store.Data.Theme=Palette.Preset(picked); var presetColors=new[]{store.Data.Theme.Product,store.Data.Theme.Custom,store.Data.Theme.Personal}; for(int i=0;i<store.Data.Sections.Count;i++) store.Data.Sections[i].Color=presetColors[i%presetColors.Length]; ApplyTheme(); Persist(); OpenSettings(); }); button.Margin=new Thickness(3,0,3,0); button.Padding=new Thickness(6,11,6,9); button.Background=new SolidColorBrush(C(p.Background)); button.BorderThickness=new Thickness(store.Data.Theme.Name==name?2:1); button.BorderBrush=new SolidColorBrush(C(store.Data.Theme.Name==name?p.Text:p.Product));
                var swatch=new StackPanel(); var colors=new StackPanel { Orientation=Orientation.Horizontal,HorizontalAlignment=HorizontalAlignment.Center,Margin=new Thickness(0,0,0,8) };
                foreach(string hex in new[]{p.Product,p.Custom,p.Personal}) colors.Children.Add(new Border { Width=12,Height=12,CornerRadius=new CornerRadius(3),Background=new SolidColorBrush(C(hex)),Margin=new Thickness(2,0,2,0) }); swatch.Children.Add(colors); swatch.Children.Add(new TextBlock { Text=name,FontSize=11,Foreground=new SolidColorBrush(C(p.Text)),HorizontalAlignment=HorizontalAlignment.Center }); button.Content=swatch; presets.Children.Add(button);
            }
            content.Children.Add(presets); content.Children.Add(Text("应用主题会更新背景、文字与分区颜色。",10.5,"MutedBrush",false,new Thickness(0,-9,0,17))); content.Children.Add(Text("自定义颜色",11,"MutedBrush",false,new Thickness(0,0,0,8)));
            foreach(string property in new[]{"Background","Text"}) content.Children.Add(ColorRow(property));
            content.Children.Add(Text("分区颜色可在「管理分区」中调整。",10.5,"MutedBrush",false,new Thickness(0,9,0,0)));
            var line=new Border { Height=1,Margin=new Thickness(0,18,0,17) }; line.SetResourceReference(Border.BackgroundProperty,"LineBrush"); content.Children.Add(line);
            content.Children.Add(Text("本地备份",13,"TextBrush",true)); content.Children.Add(Text("导出保存一份副本，导入恢复内容与配色。",10.5,"MutedBrush",false,new Thickness(0,7,0,11)));
            var backups=new Grid(); backups.ColumnDefinitions.Add(new ColumnDefinition()); backups.ColumnDefinitions.Add(new ColumnDefinition());
            var export=ActionButton("导出备份","Export",ExportBackup); export.Margin=new Thickness(0,0,5,0); export.SetResourceReference(BackgroundProperty,"SurfaceBrush"); backups.Children.Add(export);
            var import=ActionButton("导入备份","Import",ImportBackup); import.Margin=new Thickness(5,0,0,0); import.SetResourceReference(BackgroundProperty,"SurfaceBrush"); Grid.SetColumn(import,1); backups.Children.Add(import); content.Children.Add(backups);
            var folder=ActionButton("打开数据文件夹","DataFolder",()=> { Directory.CreateDirectory(Path.GetDirectoryName(store.FilePath)); System.Diagnostics.Process.Start("explorer.exe","\""+Path.GetDirectoryName(store.FilePath)+"\""); }); folder.FontSize=10.5; folder.Margin=new Thickness(0,8,0,0); folder.HorizontalAlignment=HorizontalAlignment.Left; folder.Padding=new Thickness(0,6,0,6); content.Children.Add(folder);
            var bottom=new Grid { Margin=new Thickness(0,16,0,0) }; bottom.ColumnDefinitions.Add(new ColumnDefinition()); bottom.ColumnDefinitions.Add(new ColumnDefinition { Width=GridLength.Auto }); settingsStatus=Text("当前主题 · "+store.Data.Theme.Name,10.5,"MutedBrush"); settingsStatus.VerticalAlignment=VerticalAlignment.Center; bottom.Children.Add(settingsStatus); var done=ActionButton("完成","SettingsDone",CloseOverlay,true); done.MinWidth=78; Grid.SetColumn(done,1); bottom.Children.Add(done); Grid.SetRow(bottom,2); page.Children.Add(bottom);
        }
        FrameworkElement ColorRow(string property) {
            string label=property=="Background"?"背景":"文字";
            string key=property=="Background"?"CanvasBrush":property=="Text"?"TextBrush":property+"Brush";
            var row=new Grid { Margin=new Thickness(0,3,0,3) }; row.ColumnDefinitions.Add(new ColumnDefinition()); row.ColumnDefinitions.Add(new ColumnDefinition { Width=GridLength.Auto }); row.ColumnDefinitions.Add(new ColumnDefinition { Width=GridLength.Auto });
            var title=Text(label,12); title.VerticalAlignment=VerticalAlignment.Center; row.Children.Add(title);
            var picker=ActionButton("","Pick-"+property,()=>PickColor(property)); picker.Padding=new Thickness(4); picker.Margin=new Thickness(0,0,7,0); picker.ToolTip="选择"+label+"颜色";
            var square=new Border { Width=23,Height=23,CornerRadius=new CornerRadius(4),BorderThickness=new Thickness(1) }; square.SetResourceReference(Border.BackgroundProperty,key); square.SetResourceReference(Border.BorderBrushProperty,"LineBrush"); picker.Content=square; Grid.SetColumn(picker,1); row.Children.Add(picker);
            var input=new TextBox { Text=(string)typeof(Palette).GetProperty(property).GetValue(store.Data.Theme,null),Width=107,FontFamily=new FontFamily("Consolas"),FontSize=12,Padding=new Thickness(9,7,9,7),MaxLength=7 }; Id(input,"Color-"+property,label+"颜色"); colorInputs[property]=input; input.LostKeyboardFocus+=(s,e)=>CommitColor(property,input); input.KeyDown+=(s,e)=> { if(e.Key==Key.Enter) { CommitColor(property,input); e.Handled=true; } }; Grid.SetColumn(input,2); row.Children.Add(input); return row;
        }
        bool CommitColor(string property,TextBox input) {
            string value=input.Text.Trim().ToUpperInvariant(); if(!NotesStore.ValidColor(value)) { if(settingsStatus!=null) settingsStatus.Text="颜色格式：#RRGGBB"; input.BorderBrush=Brushes.IndianRed; return false; }
            input.SetResourceReference(TextBox.BorderBrushProperty,"LineBrush"); var prop=typeof(Palette).GetProperty(property); if((string)prop.GetValue(store.Data.Theme,null)==value) return true;
            prop.SetValue(store.Data.Theme,value,null); store.Data.Theme.Name="自定义"; ApplyTheme(); if(settingsStatus!=null) settingsStatus.Text="当前主题 · 自定义"; return Persist();
        }
        bool CommitColors() { foreach(var pair in colorInputs) if(!CommitColor(pair.Key,pair.Value)) return false; return true; }
        sealed class DialogOwner : System.Windows.Forms.IWin32Window { public IntPtr Handle { get; private set; } public DialogOwner(IntPtr h) { Handle=h; } }
        void PickColor(string property) {
            Color current=C((string)typeof(Palette).GetProperty(property).GetValue(store.Data.Theme,null));
            using(var dialog=new System.Windows.Forms.ColorDialog { FullOpen=true,Color=System.Drawing.Color.FromArgb(current.R,current.G,current.B) }) {
                if(dialog.ShowDialog(new DialogOwner(new WindowInteropHelper(this).Handle))!=System.Windows.Forms.DialogResult.OK) return;
                colorInputs[property].Text="#"+dialog.Color.R.ToString("X2")+dialog.Color.G.ToString("X2")+dialog.Color.B.ToString("X2"); CommitColor(property,colorInputs[property]);
            }
        }
        void ExportBackup() {
            if(!CommitColors()) return;
            var dialog=new Microsoft.Win32.SaveFileDialog { Title="导出便签备份",Filter="便签备份 (*.json)|*.json",FileName="桌面便签备份-"+DateTime.Now.ToString("yyyyMMdd-HHmm")+".json",DefaultExt=".json",AddExtension=true };
            if(dialog.ShowDialog(this)!=true) return;
            try { store.Export(dialog.FileName); settingsStatus.Text="备份已导出"; } catch(Exception e) { MessageBox.Show(this,e.Message,"导出遇到问题",MessageBoxButton.OK,MessageBoxImage.Error); }
        }
        void ImportBackup() {
            if(!CommitColors()) return;
            var dialog=new Microsoft.Win32.OpenFileDialog { Title="导入便签备份",Filter="便签备份 (*.json)|*.json",CheckFileExists=true };
            if(dialog.ShowDialog(this)!=true) return;
            try {
                NotesData imported=NotesStore.Read(dialog.FileName);
                string prompt="恢复备份中的 "+imported.Sections.Count+" 个分区、"+imported.Notes.Count+" 条便签和配色？\n\n当前 "+store.Data.Notes.Count+" 条便签会先保存为恢复前备份。恢复后，窗口展示所选备份的内容。";
                if(MessageBox.Show(this,prompt,"恢复备份",MessageBoxButton.OKCancel,MessageBoxImage.Question,MessageBoxResult.Cancel)!=MessageBoxResult.OK) return;
                string backup=store.Restore(imported); ApplyTheme(); RenderBoard(); OpenSettings(); settingsStatus.Text="已恢复，原内容已备份"; settingsStatus.ToolTip=backup;
            } catch(Exception e) { MessageBox.Show(this,e.Message,"导入遇到问题",MessageBoxButton.OK,MessageBoxImage.Error); }
        }
    }
    public static class Program {
        [DllImport("user32.dll",CharSet=CharSet.Unicode)] static extern IntPtr FindWindow(string className,string name);
        [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr handle,int command);
        [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr handle);
        [STAThread] public static void Main(string[] args) {
            string path=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"ZoeyDesktopNotes","notes.json");
            if(args.Length==2 && args[0]=="--data") path=Path.GetFullPath(args[1]);
            string hash; using(var sha=SHA256.Create()) hash=BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(path.ToUpperInvariant()))).Replace("-","").Substring(0,24);
            bool first; using(var mutex=new Mutex(true,"Local\\ZoeyDesktopNotes-"+hash,out first)) {
                if(!first) { var handle=FindWindow(null,"桌面便签"); if(handle!=IntPtr.Zero) { ShowWindow(handle,9); SetForegroundWindow(handle); } return; }
                try { var store=new NotesStore(path); store.Load(); var app=new Application { ShutdownMode=ShutdownMode.OnMainWindowClose }; app.Run(new NotesWindow(store)); }
                catch(Exception e) { MessageBox.Show(e.Message,"桌面便签",MessageBoxButton.OK,MessageBoxImage.Error); }
                finally { mutex.ReleaseMutex(); }
            }
        }
    }
}
