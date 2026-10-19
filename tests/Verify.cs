using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Documents;
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
    [DllImport("user32.dll")] static extern IntPtr GetSystemMenu(IntPtr hWnd,bool bRevert);
    [DllImport("user32.dll")] static extern int GetMenuItemCount(IntPtr hMenu);
    [DllImport("user32.dll")] static extern int GetMenuItemID(IntPtr hMenu,int nPos);
    [DllImport("user32.dll")] static extern bool PostMessage(IntPtr hWnd,int msg,IntPtr wParam,IntPtr lParam);
    static string VisualText(DependencyObject node) {
        var sb=new StringBuilder(); WalkText(node,sb); return sb.ToString();
    }
    static void WalkText(DependencyObject node,StringBuilder sb) {
        var text=node as TextBlock;
        if(text!=null) { sb.AppendLine(text.Text); foreach(var inline in text.Inlines) { var run=inline as Run; if(run!=null) sb.AppendLine(run.Text); } }
        foreach(object child in LogicalTreeHelper.GetChildren(node)) { var next=child as DependencyObject; if(next!=null) WalkText(next,sb); }
    }
    static string SampleBrief(string date,string headline,string project,string leftoverProject,string leftover,string generatedAt=null) {
        return "{\"date\":\""+date+"\",\"generated_at\":\""+(generatedAt??date+"T01:33:25Z")+"\",\"headline\":\""+headline+"\","
            +"\"projects\":[{\"project\":\""+project+"\",\"need\":\"把总结和潜在问题放进便签。\",\"progress\":\"已经接上 Apocalypse 的每日总结。\",\"status\":\"partial\"}],"
            +"\"leftovers\":[{\"project\":\""+leftoverProject+"\",\"item\":\""+leftover+"\",\"why\":\"结论里没有写。\"}],"
            +"\"notes\":\"证据偏薄。\",\"evidence\":{\"episodes\":4,\"live\":4,\"batch\":0}}";
    }
    static void BriefTests() {
        string directory=Path.Combine(folder,"briefs"); Directory.CreateDirectory(directory);
        string today=DateTime.Now.ToString("yyyy-MM-dd"),yesterday=DateTime.Now.AddDays(-1).ToString("yyyy-MM-dd");
        File.WriteAllText(Path.Combine(directory,today+".today.json"),SampleBrief(today,"今日标题","今日项目","今日遗留项目","今日遗留问题"),new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(directory,yesterday+".json"),SampleBrief(yesterday,"昨日标题","昨日项目","昨日遗留项目","昨日遗留问题"),new UTF8Encoding(false));
        var store=new ApocalypseBriefStore(directory); store.Load();
        Check(store.Today!=null && store.Yesterday!=null && store.Days().Count==2,"today and yesterday briefs load from the Apocalypse folder");
        Check(store.Today.Headline=="今日标题" && store.Today.Projects.Count==1 && store.Today.Projects[0].Project=="今日项目" && store.Today.Projects[0].Status=="partial" && store.Today.Episodes==4 && store.Today.Generated!=null,"brief projects evidence and generation time parse");
        var board=new StackPanel(); var status=new TextBlock(); var count=new TextBlock();
        ApocalypseBriefView.RenderSummary(board,status,count,store);
        string text=VisualText(board);
        Check(text.Contains("今日标题") && text.Contains("今日项目") && text.Contains("昨日标题") && text.Contains("昨日项目") && text.Contains("部分完成"),"summary lists today and yesterday projects with status");
        Check(text.Contains("需求") && text.Contains("进展") && !text.Contains("今日遗留问题"),"summary shows need and progress without leftovers");
        Check(count.Text=="2 个项目","summary counts projects");
        board=new StackPanel(); ApocalypseBriefView.RenderIssues(board,status,count,store); text=VisualText(board);
        Check(text.Contains("今日遗留问题") && text.Contains("昨日遗留问题") && !text.Contains("今日项目"),"issues page lists only leftovers");
        Check(count.Text=="2 条待解决","issues page counts leftovers");
        var missing=new ApocalypseBriefStore(Path.Combine(folder,"briefs-empty")); missing.Load();
        board=new StackPanel(); ApocalypseBriefView.RenderSummary(board,status,count,missing);
        Check(missing.Days().Count==0 && VisualText(board).Contains("还没有 Apocalypse") && count.Text=="0 个项目","missing briefs show an empty state");
    }
    // 替身脚本：不带参数写昨天，带 --today 写今天，内容取自 template.json。
    const string StubScript=@"import datetime, pathlib, sys
here = pathlib.Path(__file__).resolve().parent
day = datetime.date.today()
if '--today' in sys.argv:
    name = day.isoformat() + '.today.json'
else:
    day = day - datetime.timedelta(days=1)
    name = day.isoformat() + '.json'
out = here / 'briefs'
out.mkdir(exist_ok=True)
now = datetime.datetime.now(datetime.timezone.utc).strftime('%Y-%m-%dT%H:%M:%SZ')
body = (here / 'template.json').read_text(encoding='utf-8').replace('DATE_PLACEHOLDER', day.isoformat()).replace('GENERATED_AT', now)
(out / name).write_text(body, encoding='utf-8')
";
    static string JsonText(string value) { return "\""+value.Replace("\\","\\\\").Replace("\"","\\\"")+"\""; }
    static string StubPython() {
        foreach(string candidate in new[]{"python","py -3"}) {
            try {
                var info=new ProcessStartInfo { FileName=candidate.Split(' ')[0], Arguments=(candidate.Contains(" ")?candidate.Substring(candidate.IndexOf(' ')+1)+" ":"")+"-c \"import zoneinfo;zoneinfo.ZoneInfo('Asia/Shanghai')\"", UseShellExecute=false, CreateNoWindow=true, RedirectStandardOutput=true, RedirectStandardError=true };
                using(var process=Process.Start(info)) { process.StandardOutput.ReadToEnd(); process.StandardError.ReadToEnd(); if(process.WaitForExit(10000) && process.ExitCode==0) return candidate; }
            } catch(Exception) { }
        }
        return null;
    }
    static string InstallStub(string root) {
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root,"template.json"),SampleBrief("DATE_PLACEHOLDER","重新生成标题","今日项目","今日遗留项目","今日遗留问题","GENERATED_AT"),new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(root,"daily_work_brief.py"),StubScript,new UTF8Encoding(false));
        string python=StubPython(); if(python==null) return null;
        string config=Path.Combine(root,"apocalypse.json");
        File.WriteAllText(config,"{\"script\":"+JsonText(Path.Combine(root,"daily_work_brief.py"))+",\"python\":"+JsonText(python)+"}",new UTF8Encoding(false));
        return config;
    }
    static void RunnerTests() {
        string root=Path.Combine(folder,"runner"),config=InstallStub(root);
        if(config==null) { checks.Add("SKIP brief runner needs a python with tzdata"); return; }
        string directory=Path.Combine(root,"briefs");
        var runner=new ApocalypseBriefRunner(config);
        Check(runner.Script==Path.Combine(root,"daily_work_brief.py"),"brief runner takes the script from its config");
        var store=new ApocalypseBriefStore(directory); store.Load();
        Check(ApocalypseBriefRunner.NeedsYesterday(null) && ApocalypseBriefRunner.NeedsToday(null,DateTime.Now),"missing briefs need generation");
        runner.Generate(true,true); store.Load();
        Check(store.Today!=null && store.Yesterday!=null && store.Today.Headline=="重新生成标题" && store.Yesterday.Headline=="重新生成标题","brief runner regenerates yesterday and today through Apocalypse");
        Check(!ApocalypseBriefRunner.NeedsYesterday(store.Yesterday) && !ApocalypseBriefRunner.NeedsToday(store.Today,DateTime.Now),"a fresh brief is not regenerated again");
        Check(ApocalypseBriefRunner.NeedsToday(store.Today,DateTime.Now.AddHours(2)),"a brief older than an hour is regenerated");
        File.WriteAllText(Path.Combine(root,"daily_work_brief.py"),"import sys"+Environment.NewLine+"print('Apocalypse analysis model is not configured', file=sys.stderr)"+Environment.NewLine+"raise SystemExit(1)"+Environment.NewLine,new UTF8Encoding(false));
        string message=null; try { runner.Generate(false,true); } catch(Exception e) { message=e.Message; }
        Check(message!=null && message.Contains("not configured"),"brief runner reports what Apocalypse said when it fails");
    }
    static void LinearBoardTests() {
        string json="{\"data\":{\"viewer\":{\"id\":\"u\",\"name\":\"Zoey\"},\"projects\":{\"nodes\":[{\"id\":\"p1\",\"name\":\"桌面便签\",\"url\":\"https://linear.app/p1\",\"updatedAt\":\"2026-10-18T00:00:00.000Z\",\"color\":\"#5e6ad2\",\"status\":{\"name\":\"In Progress\",\"type\":\"started\"},\"content\":\"第一性原理\"},{\"id\":\"p2\",\"name\":\"空项目\",\"description\":\"不应出现的描述\",\"status\":{\"name\":\"Planned\",\"type\":\"planned\"}}]},\"issues\":{\"nodes\":[{\"id\":\"i1\",\"identifier\":\"AOX-2\",\"title\":\"继续做\",\"url\":\"https://linear.app/i1\",\"updatedAt\":\"2026-10-18T01:00:00.000Z\",\"priority\":2,\"state\":{\"name\":\"Todo\",\"type\":\"unstarted\"},\"project\":{\"id\":\"p1\",\"name\":\"桌面便签\"}},{\"id\":\"i2\",\"identifier\":\"AOX-9\",\"title\":\"游离事项\",\"url\":\"https://linear.app/i2\",\"updatedAt\":\"2026-10-17T01:00:00.000Z\",\"priority\":0,\"state\":{\"name\":\"Backlog\",\"type\":\"backlog\"},\"project\":null},{\"id\":\"i3\",\"identifier\":\"AOX-1\",\"title\":\"做完了\",\"state\":{\"name\":\"Done\",\"type\":\"completed\"},\"project\":{\"id\":\"p2\",\"name\":\"空项目\"}}]}}}";
        var snapshot=LinearApiClient.ParseResponse(json);
        Check(snapshot.Projects.Count==2 && snapshot.Issues.Count==3 && snapshot.Projects[0].Color=="#5e6ad2","Linear response keeps projects and issues");
        var groups=LinearIssueGroups.Build(snapshot);
        Check(groups.Count==2 && groups[0].Title=="桌面便签" && groups[0].Issues.Count==1 && groups[0].Issues[0].Identifier=="AOX-2","open issues stay under the project title");
        Check(groups[1].Unassigned && groups[1].Title=="无项目" && groups[1].Issues[0].Identifier=="AOX-9","issues without a project are listed");
        Check(groups.All(g=>g.Title!="空项目"),"project without open issues is omitted");
        var board=new StackPanel(); var status=new TextBlock(); var count=new TextBlock();
        LinearBoardView.Render(board,status,count,snapshot);
        string text=VisualText(board);
        Check(text.Contains("桌面便签") && text.Contains("AOX-2") && text.Contains("继续做") && text.Contains("无项目") && text.Contains("AOX-9"),"board lists open issues and unassigned issues");
        Check(!text.Contains("空项目") && !text.Contains("不应出现的描述") && !text.Contains("第一性原理") && !text.Contains("做完了") && !text.Contains("暂无 Linear 文档"),"project descriptions and finished issues are hidden");
        Check(count.Text=="1 个项目 · 2 条待办","count reports visible projects and open issues");
    }
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
        var w=new NotesWindow(store) { ShowActivated=false,Left=-10000,Top=-10000 }; w.Show(); ShowWindow(new WindowInteropHelper(w).Handle,4); Pump();
        LinearIntegration.Attach(w,store); LinearIntegrationPolish.Attach(w); Pump();
        var flags=System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic;
        var root=(Grid)typeof(NotesWindow).GetField("root",flags).GetValue(w);
        var board=(StackPanel)typeof(NotesWindow).GetField("board",flags).GetValue(w);
        CardFlip.Run(root,board,()=> { board.Children.Clear(); var marker=new TextBlock { Text="翻转后" }; AutomationProperties.SetAutomationId(marker,"FlipMarker"); board.Children.Add(marker); },true);
        Check(Find(w,"FlipMarker")!=null,"card flip swaps the page immediately");
        Check(Find(w,"CardFlipFront")!=null,"card flip keeps the outgoing face visible");
        Pump(500);
        Check(Find(w,"CardFlipFront")==null,"card flip removes the outgoing face");
        Check(((FrameworkElement)board.Parent).RenderTransform==null,"card flip restores the page transform");
        w.RenderBoard();
        var notesBtn=Find(w,"NotesSource") as Button; var linearBtn=Find(w,"LinearSource") as Button;
        Check(notesBtn!=null && notesBtn.Content is System.Windows.Shapes.Path,"notes button has a drawn icon");
        Check(linearBtn!=null && linearBtn.Content is System.Windows.Shapes.Path,"Linear button has a drawn icon");
        Check(((TextBlock)Find(w,"AppTitle")).Text=="便签","notes view title is 便签");
        Capture(w,"empty");
        board=(StackPanel)typeof(NotesWindow).GetField("board",flags).GetValue(w);
        var status=(TextBlock)typeof(NotesWindow).GetField("status",flags).GetValue(w);
        var count=(TextBlock)typeof(NotesWindow).GetField("count",flags).GetValue(w);
        var integration=w.Tag; var linearType=integration.GetType();
        linearType.GetField("linearMode",flags).SetValue(integration,true); linearType.GetMethod("SetModeButtons",flags).Invoke(integration,null);
        LinearBoardView.Render(board,status,count,LinearApiClient.ParseResponse("{\"data\":{\"viewer\":{\"name\":\"Zoey\"},\"projects\":{\"nodes\":[{\"id\":\"p1\",\"name\":\"桌面便签\",\"url\":\"https://linear.app/p1\",\"color\":\"#5e6ad2\",\"status\":{\"name\":\"In Progress\"}},{\"id\":\"p2\",\"name\":\"空项目\",\"description\":\"不应出现\",\"status\":{\"name\":\"Planned\"}}]},\"issues\":{\"nodes\":[{\"id\":\"i1\",\"identifier\":\"AOX-7\",\"title\":\"Linear UI 改进\",\"url\":\"https://linear.app/i1\",\"updatedAt\":\"2026-10-18T01:00:00.000Z\",\"state\":{\"name\":\"Todo\",\"type\":\"unstarted\"},\"project\":{\"id\":\"p1\",\"name\":\"桌面便签\"}},{\"id\":\"i2\",\"identifier\":\"AOX-9\",\"title\":\"没有项目的事项\",\"url\":\"https://linear.app/i2\",\"updatedAt\":\"2026-10-17T01:00:00.000Z\",\"state\":{\"name\":\"Backlog\",\"type\":\"backlog\"}}]}}}"));
        Check(((TextBlock)Find(w,"AppTitle")).Text=="待办","Linear view title is 待办");
        Check(Find(w,"LinearRefresh").Visibility==Visibility.Visible,"refresh appears in Linear view");
        Capture(w,"linear-todo");
        string briefDirectory=Path.Combine(folder,"ui-runner","briefs"); Directory.CreateDirectory(briefDirectory);
        string briefDate=DateTime.Now.ToString("yyyy-MM-dd"),previousDate=DateTime.Now.AddDays(-1).ToString("yyyy-MM-dd");
        File.WriteAllText(Path.Combine(briefDirectory,briefDate+".today.json"),SampleBrief(briefDate,"今日标题","今日项目","今日遗留项目","今日遗留问题",DateTime.UtcNow.AddMinutes(-5).ToString("yyyy-MM-dd'T'HH:mm:ss'Z'")),new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(briefDirectory,previousDate+".json"),SampleBrief(previousDate,"昨日标题","昨日项目","昨日遗留项目","昨日遗留问题"),new UTF8Encoding(false));
        string uiConfig=InstallStub(Path.Combine(folder,"ui-runner"));
        if(uiConfig==null) checks.Add("SKIP brief page generation needs a python with tzdata");
        ApocalypseIntegration.Attach(w,new ApocalypseBriefStore(briefDirectory),uiConfig==null?null:new ApocalypseBriefRunner(uiConfig)); Pump();
        Check(Find(w,"SummarySource")!=null && Find(w,"IssuesSource")!=null && Find(w,"BriefRefresh")!=null,"brief pages appear in the toolbar");
        Click(w,"SummarySource"); Pump(450);
        Check((bool)linearType.GetField("linearMode",flags).GetValue(integration)==false && Find(w,"LinearRefresh").Visibility==Visibility.Collapsed,"opening a brief page leaves the Linear view");
        Check(((TextBlock)Find(w,"AppTitle")).Text=="总结" && Find(w,"BriefSummaryRoot")!=null && Find(w,"BriefRefresh").Visibility==Visibility.Visible,"summary page shows the daily brief");
        Check(VisualText(Find(w,"BriefSummaryRoot")).Contains("今日项目") && VisualText(Find(w,"BriefSummaryRoot")).Contains("今日标题"),"summary page lists the project and headline");
        Capture(w,"brief-summary");
        Click(w,"IssuesSource"); Pump(450);
        Check(((TextBlock)Find(w,"AppTitle")).Text=="潜在问题" && Find(w,"BriefIssuesRoot")!=null && VisualText(Find(w,"BriefIssuesRoot")).Contains("今日遗留问题"),"issues page shows leftovers");
        Capture(w,"brief-issues");
        Click(w,"NotesSource"); Pump(450);
        Check(((TextBlock)Find(w,"AppTitle")).Text=="便签" && Find(w,"BriefRefresh").Visibility==Visibility.Collapsed && Find(w,"Add-product")!=null,"notes button returns from the brief pages");
        File.WriteAllText(Path.Combine(briefDirectory,briefDate+".today.json"),SampleBrief(briefDate,"今日标题","今日项目","今日遗留项目","今日遗留问题",previousDate+"T01:33:25Z"),new UTF8Encoding(false));
        Click(w,"SummarySource"); Pump(450);
        if(uiConfig!=null) {
            FrameworkElement summary=Find(w,"BriefSummaryRoot");
            for(int i=0;i<40 && (summary==null || !VisualText(summary).Contains("重新生成标题"));i++) { Pump(150); summary=Find(w,"BriefSummaryRoot"); }
            Check(summary!=null && VisualText(summary).Contains("重新生成标题"),"a stale brief is regenerated through Apocalypse");
            Capture(w,"brief-regenerated");
        }
        Click(w,"NotesSource"); Pump(450);
        linearType.GetField("linearMode",flags).SetValue(integration,false); linearType.GetMethod("SetModeButtons",flags).Invoke(integration,null); w.RenderBoard();
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
    static bool MenuHasItem(IntPtr menu,int id) {
        int count=GetMenuItemCount(menu);
        for(int i=0;i<count;i++) if(GetMenuItemID(menu,i)==id) return true;
        return false;
    }
    static void CloseToTaskbarTests() {
        var store=new NotesStore(Path.Combine(folder,"close","notes.json")); store.Load();
        var w=new NotesWindow(store) { ShowActivated=false,Left=-10000,Top=-10000 }; w.Show(); ShowWindow(new WindowInteropHelper(w).Handle,4); Pump();
        w.Close(); Pump();
        Check(w.IsVisible && w.WindowState==WindowState.Minimized,"close minimizes to taskbar instead of exiting");
        Check(File.Exists(store.FilePath),"close still saves the notes before minimizing");
        var menu=GetSystemMenu(new WindowInteropHelper(w).Handle,false);
        Check(menu!=IntPtr.Zero && MenuHasItem(menu,0x1120),"taskbar context menu offers an exit command");
        w.Close(); Pump(200);
        Check(!w.IsVisible && !w.IsLoaded,"closing again while minimized exits the app");
    }
    static void ExitCommandTests() {
        var store=new NotesStore(Path.Combine(folder,"exit","notes.json")); store.Load();
        var w=new NotesWindow(store) { ShowActivated=false,Left=-10000,Top=-10000 }; w.Show(); ShowWindow(new WindowInteropHelper(w).Handle,4); Pump();
        w.Close(); Pump();
        w.WindowState=WindowState.Normal; Pump();
        PostMessage(new WindowInteropHelper(w).Handle,0x0112,new IntPtr(0x1120),IntPtr.Zero); Pump(400);
        Check(!w.IsVisible && !w.IsLoaded,"exit command from the taskbar menu closes the app");
    }
    [STAThread] public static int Main(string[] args) {
        folder=Path.GetFullPath(args[0]); Directory.CreateDirectory(folder); var app=new Application { ShutdownMode=ShutdownMode.OnExplicitShutdown };
        try { LinearBoardTests(); StoreTests(); SectionTests(); BriefTests(); RunnerTests(); if(args.Length<2 || args[1]!="--store-only") { UiTests(); CloseToTaskbarTests(); ExitCommandTests(); } File.WriteAllLines(Path.Combine(folder,"results.txt"),checks,new UTF8Encoding(false)); app.Shutdown(); return 0; }
        catch(Exception e) { checks.Add("FAIL "+e); File.WriteAllLines(Path.Combine(folder,"results.txt"),checks,new UTF8Encoding(false)); app.Shutdown(); return 1; }
    }
}
