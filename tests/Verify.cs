using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using DesktopNotes;

public static class Verify {
    static readonly List<string> checks=new List<string>();
    static string folder;
    static void Check(bool condition,string name) { if(!condition) throw new Exception(name); checks.Add("PASS "+name); }
    static void Pump(int milliseconds=80) { var frame=new DispatcherFrame(); var t=new DispatcherTimer { Interval=TimeSpan.FromMilliseconds(milliseconds) }; t.Tick+=(s,e)=> { t.Stop(); frame.Continue=false; }; t.Start(); Dispatcher.PushFrame(frame); }
    static FrameworkElement Find(DependencyObject parent,string id) { if(AutomationProperties.GetAutomationId(parent)==id) return parent as FrameworkElement; for(int i=0;i<VisualTreeHelper.GetChildrenCount(parent);i++) { var found=Find(VisualTreeHelper.GetChild(parent,i),id); if(found!=null) return found; } return null; }
    static void Click(NotesWindow w,string id) { var b=Find(w,id) as Button; if(b==null) throw new Exception("Missing button "+id); b.Focus(); b.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump(); }
    static void Input(NotesWindow w,string id,string text) { var input=Find(w,id) as TextBox; if(input==null) throw new Exception("Missing input "+id); input.Text=text; }
    static void Capture(NotesWindow w,string name) { w.UpdateLayout(); Pump(); var bitmap=new RenderTargetBitmap((int)w.ActualWidth*2,(int)w.ActualHeight*2,192,192,PixelFormats.Pbgra32); var visual=new DrawingVisual(); using(var drawing=visual.RenderOpen()) { drawing.DrawRectangle(w.Background,null,new Rect(0,0,w.ActualWidth,w.ActualHeight)); drawing.DrawRectangle(new VisualBrush((Visual)w.Content),null,new Rect(0,0,w.ActualWidth,w.ActualHeight)); } bitmap.Render(visual); var encoder=new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); using(var file=File.Create(Path.Combine(folder,name+".png"))) encoder.Save(file); }
    [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr h,int command);
    [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr h,int index);
    static void StoreTests() {
        var store=new NotesStore(Path.Combine(folder,"storage","notes.json")); store.Load();
        Check(store.Data.Notes.Count==0,"fresh state is empty");
        string now=DateTimeOffset.UtcNow.ToString("o");
        store.Data.Notes.Add(new Note { Id=Guid.NewGuid().ToString(),Section="product",Title="中文与 Unicode",Content="项目现状\n第二行 café / 数据 Δ\n\t保留缩进",CreatedUtc=now,UpdatedUtc=now }); store.Save();
        var copy=new NotesStore(store.FilePath); copy.Load(); Check(copy.Data.Notes[0].Content==store.Data.Notes[0].Content,"UTF-8 and multiline round trip");
        copy.Data.Notes[0].Title="第二版"; copy.Save(); Check(NotesStore.Read(copy.FilePath+".bak").Notes[0].Title=="中文与 Unicode","previous valid state is backed up");
        File.WriteAllText(copy.FilePath,"broken json",new UTF8Encoding(false)); var recovered=new NotesStore(copy.FilePath); recovered.Load();
        Check(recovered.Data.Notes[0].Title=="中文与 Unicode" && Directory.GetFiles(Path.GetDirectoryName(copy.FilePath),"*.recovered-*.json").Length==1,"corrupt primary recovers with original preserved");
        File.Delete(copy.FilePath); var missing=new NotesStore(copy.FilePath); missing.Load(); Check(missing.Data.Notes.Count==1,"missing primary recovers from backup");
        string export=Path.Combine(folder,"export.json"); store.Data.Theme=Palette.Preset("夜色"); store.Export(export); var imported=NotesStore.Read(export); Check(imported.Notes[0].CreatedUtc==now && imported.Theme.Name=="夜色","export retains timestamps and theme");
        missing.Data.Notes[0].Title="恢复前内容"; missing.Save(); string before=missing.Restore(imported);
        Check(NotesStore.Read(before).Notes[0].Title=="恢复前内容" && missing.Data.Theme.Name=="夜色","import creates recovery backup before replacing data");
        string valid=NotesStore.Json(missing.Data); var duplicate=NotesStore.Read(export); duplicate.Notes.Add(duplicate.Notes[0]); bool rejected=false; try { missing.Restore(duplicate); } catch(InvalidDataException) { rejected=true; }
        Check(rejected && NotesStore.Json(missing.Data)==valid,"invalid duplicate import preserves active data");
        var bad=NotesStore.Read(export); bad.Schema="unknown"; rejected=false; try { NotesStore.Validate(bad); } catch(InvalidDataException) { rejected=true; } Check(rejected,"unknown backup schema rejected");
        Check(!NotesStore.ValidColor("#12345Z") && NotesStore.ValidColor("#aabbcc"),"custom hex colors validated");
        string malformed=Path.Combine(folder,"incomplete.json"); File.WriteAllText(malformed,"{}",Encoding.UTF8); rejected=false; try { NotesStore.Read(malformed); } catch(InvalidDataException) { rejected=true; } Check(rejected,"incomplete JSON is rejected before import");
    }
    static void SectionTests() {
        var store=new NotesStore(Path.Combine(folder,"migration","notes.json"));
        string now=DateTimeOffset.UtcNow.ToString("o"); store.Data.Theme=Palette.Preset("夜色"); store.Data.Theme.Product="#334455";
        store.Data.Notes.Add(new Note { Id=Guid.NewGuid().ToString(),Section="product",Title="迁移测试",Content="保留完整内容\n第二行",CreatedUtc=now,UpdatedUtc=now });
        var serializer=new JavaScriptSerializer(); var legacy=serializer.Deserialize<Dictionary<string,object>>(NotesStore.Json(store.Data)); legacy["Schema"]="desktop-notes.v1"; legacy.Remove("Sections");
        Directory.CreateDirectory(Path.GetDirectoryName(store.FilePath)); File.WriteAllText(store.FilePath,serializer.Serialize(legacy),new UTF8Encoding(false)); store.Load();
        Check(store.Data.Schema=="desktop-notes.v2" && store.Section("product").Color=="#334455" && store.Data.Notes[0].CreatedUtc==now && store.Data.Notes[0].Section=="product","v1 migration preserves note membership timestamps and custom section colors");
        Check(Directory.GetFiles(Path.GetDirectoryName(store.FilePath),"before-upgrade-v2-*.json").Length==1,"v1 migration preserves original file before write");
        Check(NotesStore.Read(store.FilePath+".bak").Notes[0].Content==store.Data.Notes[0].Content,"legacy backup remains importable");
        string snapshot=NotesStore.Json(store.Data); bool rejected=false; try { store.RemoveSection("product",null); } catch(InvalidOperationException) { rejected=true; } Check(rejected && NotesStore.Json(store.Data)==snapshot,"occupied section requires valid transfer target");
        store.RemoveSection("custom",null); store.RemoveSection("personal",null); rejected=false; try { store.RemoveSection("product","product"); } catch(InvalidOperationException) { rejected=true; } Check(rejected && store.Data.Notes[0].Section=="product","final occupied section cannot orphan its notes");
        for(int i=0;i<8;i++) store.AddSection("分区 "+i,"#6A7B8C"); store.MoveSection("product",8); store.Save(); var copy=NotesStore.Read(store.FilePath); Check(copy.Sections.Count==9 && copy.Sections[8].Id=="product","arbitrary section count and ordering round trip");
        copy.Notes[0].Section="unknown"; rejected=false; try { NotesStore.Validate(copy); } catch(InvalidDataException) { rejected=true; } Check(rejected,"orphan references rejected on import");
        var empty=new NotesStore(Path.Combine(folder,"empty-sections.json")); foreach(string id in empty.Data.Sections.Select(x=>x.Id).ToArray()) empty.RemoveSection(id,null); empty.Save(); Check(NotesStore.Read(empty.FilePath).Sections.Count==0,"all empty sections can be removed");
    }
    static void UiTests() {
        var store=new NotesStore(Path.Combine(folder,"ui","notes.json")); store.Load();
        var w=new NotesWindow(store) { ShowActivated=false,Left=-10000,Top=-10000 }; w.Show(); ShowWindow(new WindowInteropHelper(w).Handle,4); Pump(); Capture(w,"empty");
        foreach(string section in new[]{"product","custom","personal"}) Check(Find(w,"Add-"+section)!=null,"add control for "+section);
        Click(w,"Add-product"); Input(w,"TitleInput","示例 · 标品项目"); Input(w,"ContentInput","现状：核心流程已完成。\n下一步：核对范围与验收清单。\n待确认：发布安排。"); Pump(750);
        Check(NotesStore.Read(store.FilePath).Notes.Count==1,"editor autosaves before Done"); string original=store.Data.Notes[0].CreatedUtc; string noteId=store.Data.Notes[0].Id; Click(w,"EditorDone");
        Click(w,"Add-custom"); Input(w,"TitleInput","示例 · 客户交付"); Input(w,"ContentInput","周五前整理反馈与修改清单。\n下次沟通：确认交付范围。"); Click(w,"EditorDone");
        Click(w,"Add-personal"); Input(w,"TitleInput","示例 · 留一点时间给自己"); Input(w,"ContentInput","取快递，买花。\n晚上读完手边那一章。"); Click(w,"EditorDone");
        Check(store.Data.Notes.Select(n=>n.Section).Distinct().Count()==3,"all three sections accept title and content"); Capture(w,"preview-light");
        Click(w,"Edit-"+noteId); Input(w,"ContentInput",store.Data.Notes[0].Content+"\n补充：保留这条便签的创建时间。"); Pump(700); Capture(w,"editor"); Click(w,"EditorDone");
        Check(store.Data.Notes[0].CreatedUtc==original && NotesStore.Time(store.Data.Notes[0].UpdatedUtc)>NotesStore.Time(original),"editing preserves creation and updates modification time");
        Click(w,"Add-personal"); Click(w,"EditorDone"); Check(store.Data.Notes.Count==3,"empty draft creates no blank record");
        Click(w,"Pin"); Pump(150); Check(w.Topmost && store.Data.Window.Topmost,"pin preference saves");
        Click(w,"Settings"); Click(w,"Theme-夜色"); Capture(w,"settings-dark"); Click(w,"SettingsDone"); Capture(w,"preview-dark");
        Check(store.Data.Theme.Name=="夜色","preset applies and persists");
        Click(w,"Settings"); Input(w,"Color-Background","invalid"); Click(w,"SettingsDone"); Check(Find(w,"SettingsDone")!=null && store.Data.Theme.Background=="#232629","invalid custom color keeps settings open and preserves color");
        Input(w,"Color-Background","#F1F3EE"); Input(w,"Color-Text","#29322A"); Click(w,"ManageSections"); Input(w,"SectionColor-product","#617358"); Input(w,"SectionColor-custom","#A57655"); Input(w,"SectionColor-personal","#657F99"); Click(w,"SectionsDone"); Click(w,"SettingsDone");
        Check(store.Data.Theme.Name=="自定义" && store.Section("personal").Color=="#657F99","all custom palette fields save");
        Click(w,"Settings"); Click(w,"ManageSections"); Input(w,"SectionName-product","工作"); Click(w,"SectionsDone"); Click(w,"SettingsDone");
        Check(store.Section("product").Name=="工作" && store.Data.Notes[0].Section=="product" && store.Data.Notes[0].CreatedUtc==original,"rename preserves note identity membership and creation time");
        Click(w,"Settings"); Click(w,"ManageSections"); Click(w,"AddSection"); string addedId=store.Data.Sections.Last().Id; Input(w,"SectionName-"+addedId,"阅读计划"); Input(w,"SectionColor-"+addedId,"#807299"); Click(w,"SectionUp-"+addedId);
        Check(store.Data.Sections.Count==4 && store.Data.Sections[2].Id==addedId && store.Section(addedId).Name=="阅读计划" && store.Section(addedId).Color=="#807299","section creation rename color and reorder through UI");
        Capture(w,"sections");
        Note moved=store.Data.Notes.First(n=>n.Section=="custom"); string movedCreated=moved.CreatedUtc,movedUpdated=moved.UpdatedUtc,movedBody=moved.Content;
        Click(w,"DeleteSection-custom"); ((ComboBox)Find(w,"SectionDestination")).SelectedItem=store.Section(addedId); Click(w,"ConfirmSectionDelete");
        Check(store.Data.Sections.Count==3 && moved.Section==addedId && moved.CreatedUtc==movedCreated && moved.UpdatedUtc==movedUpdated && moved.Content==movedBody,"deleting occupied section transfers notes with original content and timestamps");
        Check(Directory.GetFiles(Path.GetDirectoryName(store.FilePath),"before-section-delete-*.json").Length==1,"section deletion saves recovery backup");
        Click(w,"DeleteSection-personal"); Click(w,"CancelSectionDelete"); Check(store.Data.Sections.Count==3,"cancel section deletion preserves state");
        Click(w,"SectionsDone"); Click(w,"SettingsDone"); Capture(w,"custom-sections");
        w.Width=340; w.Height=480; Pump(); Capture(w,"narrow"); Check(w.ActualWidth==340 && Find(w,"Add-personal")!=null,"minimum-width window retains every section");
        Click(w,"Add-product"); Input(w,"TitleInput","长内容检查"); Input(w,"ContentInput",string.Join("\n",Enumerable.Repeat("完整保留所有输入内容，支持滚动、展开与编辑。",30))); Click(w,"EditorDone"); string longId=store.Data.Notes.First(n=>n.Title=="长内容检查").Id; Check(Find(w,"Expand-"+longId)!=null,"long note exposes expand control"); Click(w,"Expand-"+longId);
        Check(store.Data.Notes.First(n=>n.Id==longId).Content.Split('\n').Length==30,"long body is stored without truncation");
        w.Width=470; w.Height=760; w.Left=110; w.Top=80; Pump(700); w.Close();
        var reopened=new NotesStore(store.FilePath); reopened.Load(); var next=new NotesWindow(reopened) { ShowActivated=false };
        Check(next.Width==470 && next.Height==760 && next.Left==110 && next.Top==80 && next.Topmost,"window size position and pin survive restart");
        Check(reopened.Data.Notes.Count==4 && reopened.Data.Theme.Name=="自定义" && reopened.Data.Notes[0].CreatedUtc==original,"notes palette and timestamps survive restart"); Check(reopened.Section("product").Name=="工作" && reopened.Data.Sections[1].Id==addedId,"section names colors and order survive restart"); next.Left=-10000; next.Top=-10000; next.Show(); Pump(); next.Close();
    }
    [STAThread] public static int Main(string[] args) {
        folder=Path.GetFullPath(args[0]); Directory.CreateDirectory(folder); var app=new Application { ShutdownMode=ShutdownMode.OnExplicitShutdown };
        try { StoreTests(); SectionTests(); if(args.Length<2 || args[1]!="--store-only") UiTests(); File.WriteAllLines(Path.Combine(folder,"results.txt"),checks,new UTF8Encoding(false)); app.Shutdown(); return 0; }
        catch(Exception e) { checks.Add("FAIL "+e); File.WriteAllLines(Path.Combine(folder,"results.txt"),checks,new UTF8Encoding(false)); app.Shutdown(); return 1; }
    }
}
