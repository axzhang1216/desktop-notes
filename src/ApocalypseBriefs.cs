using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace DesktopNotes {
    public sealed class ApocalypseBriefProject {
        public string Project { get; set; }
        public List<string> Done { get; set; }
        public List<string> Pending { get; set; }
        public string UserAction { get; set; }
        public ApocalypseBriefProject() { Project=""; Done=new List<string>(); Pending=new List<string>(); UserAction=""; }
    }

    public sealed class ApocalypseBrief {
        public string Date { get; set; }
        public string GeneratedAt { get; set; }
        public string Headline { get; set; }
        public string Notes { get; set; }
        public int Episodes { get; set; }
        public List<ApocalypseBriefProject> Projects { get; set; }
        public ApocalypseBrief() { Date=""; GeneratedAt=""; Headline=""; Notes=""; Projects=new List<ApocalypseBriefProject>(); }
        public DateTimeOffset? Generated {
            get {
                DateTimeOffset value;
                if(string.IsNullOrWhiteSpace(GeneratedAt)) return null;
                return DateTimeOffset.TryParse(GeneratedAt,CultureInfo.InvariantCulture,DateTimeStyles.RoundtripKind,out value)?(DateTimeOffset?)value:null;
            }
        }
    }

    public sealed class ApocalypseBriefDay {
        public string Label { get; private set; }
        public ApocalypseBrief Brief { get; private set; }
        public ApocalypseBriefDay(string label,ApocalypseBrief brief) { Label=label; Brief=brief; }
    }

    public static class ApocalypseBriefReader {
        static JavaScriptSerializer Serializer() { return new JavaScriptSerializer { MaxJsonLength=8*1024*1024, RecursionLimit=40 }; }
        public static ApocalypseBrief Parse(string json) {
            var root=Serializer().DeserializeObject(json) as Dictionary<string,object>;
            if(root==null) throw new InvalidDataException("每日总结的内容无法识别。");
            var brief=new ApocalypseBrief { Date=Text(root,"date"), GeneratedAt=Text(root,"generated_at"), Headline=Text(root,"headline"), Notes=Text(root,"notes") };
            brief.Episodes=Int(Dict(root,"evidence"),"episodes");
            foreach(var row in Rows(root,"projects")) {
                var project=new ApocalypseBriefProject { Project=Text(row,"project"), UserAction=Text(row,"user_action") };
                project.Done=Items(row,"done"); project.Pending=Items(row,"pending");
                // 旧格式只有一个 progress 字段，按 status 归到已完成或未完成。
                if(project.Done.Count==0 && project.Pending.Count==0) {
                    List<string> legacy=SplitLines(Text(row,"progress"));
                    if(legacy.Count>0) { if(Text(row,"status").Trim().ToLowerInvariant()=="done") project.Done=legacy; else project.Pending=legacy; }
                }
                brief.Projects.Add(project);
            }
            return brief;
        }
        static Dictionary<string,object> Dict(Dictionary<string,object> source,string key) { object value; if(source==null || !source.TryGetValue(key,out value)) return null; return value as Dictionary<string,object>; }
        static string Text(Dictionary<string,object> source,string key) { object value; if(source==null || !source.TryGetValue(key,out value) || value==null) return ""; return Convert.ToString(value,CultureInfo.InvariantCulture)??""; }
        static int Int(Dictionary<string,object> source,string key) { object value; if(source==null || !source.TryGetValue(key,out value) || value==null) return 0; try { return Convert.ToInt32(value,CultureInfo.InvariantCulture); } catch { return 0; } }
        static List<Dictionary<string,object>> Rows(Dictionary<string,object> source,string key) {
            var result=new List<Dictionary<string,object>>(); object raw; if(source==null || !source.TryGetValue(key,out raw)) return result;
            object[] items=raw as object[]; if(items==null) return result;
            foreach(object item in items) { var row=item as Dictionary<string,object>; if(row!=null) result.Add(row); }
            return result;
        }
        static List<string> Items(Dictionary<string,object> source,string key) {
            var result=new List<string>(); object raw; if(source==null || !source.TryGetValue(key,out raw)) return result;
            object[] items=raw as object[]; if(items==null) return result;
            foreach(object item in items) { string text=Convert.ToString(item,CultureInfo.InvariantCulture); if(!string.IsNullOrWhiteSpace(text)) result.Add(text.Trim()); }
            return result;
        }
        static List<string> SplitLines(string text) {
            var result=new List<string>();
            foreach(string part in Regex.Split(text??"","(?<=[。！？!?；;])\\s*")) {
                string item=part.Trim().TrimEnd('；',';').Trim();
                if(item.Length>0) result.Add(item);
            }
            return result;
        }
    }

    public sealed class ApocalypseBriefStore {
        public string Directory { get; private set; }
        public ApocalypseBrief Today { get; private set; }
        public ApocalypseBrief Yesterday { get; private set; }
        public ApocalypseBriefStore() : this(null) { }
        public ApocalypseBriefStore(string directory) { Directory=string.IsNullOrWhiteSpace(directory)?DefaultDirectory():directory; }
        public static string DefaultDirectory() { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),".claude","apocalypse","briefs"); }
        public void Load() {
            DateTime now=DateTime.Now;
            Today=Read(Path.Combine(Directory,now.ToString("yyyy-MM-dd")+".today.json"));
            if(Today==null) Today=Read(Path.Combine(Directory,now.ToString("yyyy-MM-dd")+".json"));
            Yesterday=Read(Path.Combine(Directory,now.AddDays(-1).ToString("yyyy-MM-dd")+".json"));
        }
        public List<ApocalypseBriefDay> Days() {
            var days=new List<ApocalypseBriefDay>();
            if(Today!=null) days.Add(new ApocalypseBriefDay("今日",Today));
            if(Yesterday!=null) days.Add(new ApocalypseBriefDay("昨日",Yesterday));
            return days;
        }
        static ApocalypseBrief Read(string path) {
            try { if(!File.Exists(path)) return null; return ApocalypseBriefReader.Parse(File.ReadAllText(path,Encoding.UTF8)); }
            catch(Exception) { return null; }
        }
    }

    public static class ApocalypseBriefView {
        public static void RenderSummary(StackPanel board,TextBlock status,TextBlock count,ApocalypseBriefStore store) { Render(board,status,count,store,"BriefSummaryRoot","总结",false); }
        public static void RenderIssues(StackPanel board,TextBlock status,TextBlock count,ApocalypseBriefStore store) { Render(board,status,count,store,"BriefIssuesRoot","潜在问题",true); }

        sealed class Card {
            public string Project;
            public List<string> Items;
            public string Action;
            public Card(string project,List<string> items,string action) { Project=project; Items=items; Action=action??""; }
            public int RowCount { get { return Items.Count+(Action.Length>0?1:0); } }
        }

        static void Render(StackPanel board,TextBlock status,TextBlock count,ApocalypseBriefStore store,string rootId,string title,bool issues) {
            board.Children.Clear();
            var root=new StackPanel(); AutomationProperties.SetAutomationId(root,rootId); board.Children.Add(root);
            var days=store.Days(); int total=0,shown=0; string page=issues?"issues":"summary";
            if(days.Count==0) root.Children.Add(Empty("还没有 Apocalypse 的每日总结。在 Apocalypse 生成后点刷新。"));
            Brush muted=board.TryFindResource("MutedBrush") as Brush;
            foreach(ApocalypseBriefDay day in days) {
                List<Card> cards=Cards(day.Brief,issues);
                int rows=0; foreach(Card card in cards) rows+=issues?card.RowCount:1;
                root.Children.Add(DayHeader(day,rows));
                if(cards.Count==0) root.Children.Add(Empty(issues?"这一天没有看出还没做完的事。":"这一天没有已完成的事项。"));
                else foreach(Card card in cards) root.Children.Add(ProjectCard(page,shown++,card,muted));
                if(!issues && !string.IsNullOrWhiteSpace(day.Brief.Notes)) root.Children.Add(Hint(day.Brief.Notes));
                total+=rows;
            }
            if(count!=null) count.Text=total+(issues?" 条待解决":" 个项目");
            if(status!=null) status.Text=Status(store,title);
        }

        static List<Card> Cards(ApocalypseBrief brief,bool issues) {
            var cards=new List<Card>();
            foreach(ApocalypseBriefProject project in brief.Projects) {
                var items=issues?project.Pending:project.Done;
                string action=issues?project.UserAction:"";
                if(items.Count>0 || !string.IsNullOrWhiteSpace(action)) cards.Add(new Card(project.Project,items,action));
            }
            return cards;
        }

        static FrameworkElement DayHeader(ApocalypseBriefDay day,int rows) {
            var panel=new StackPanel { Margin=new Thickness(0,0,0,11) };
            var row=new Grid(); row.ColumnDefinitions.Add(new ColumnDefinition()); row.ColumnDefinitions.Add(new ColumnDefinition { Width=GridLength.Auto });
            var title=new StackPanel { Orientation=Orientation.Horizontal, VerticalAlignment=VerticalAlignment.Center };
            var mark=new Border { Width=4, Height=16, CornerRadius=new CornerRadius(2), Margin=new Thickness(0,0,9,0) }; mark.SetResourceReference(Border.BackgroundProperty,"TextBrush"); title.Children.Add(mark);
            title.Children.Add(Text(day.Label,15,"TextBrush",true));
            title.Children.Add(Text(day.Brief.Date,10.5,"MutedBrush",false,new Thickness(9,3,0,0)));
            title.Children.Add(Text(rows.ToString("00"),10.5,"MutedBrush",false,new Thickness(9,3,0,0))); row.Children.Add(title);
            DateTimeOffset? generated=day.Brief.Generated;
            if(generated!=null) { var meta=Text("生成于 "+generated.Value.ToLocalTime().ToString("MM.dd HH:mm"),10,"MutedBrush"); meta.VerticalAlignment=VerticalAlignment.Center; Grid.SetColumn(meta,1); row.Children.Add(meta); }
            panel.Children.Add(row);
            if(!string.IsNullOrWhiteSpace(day.Brief.Headline)) panel.Children.Add(Text(day.Brief.Headline,12,"BodyBrush",false,new Thickness(13,9,0,0)));
            if(day.Brief.Episodes>0) panel.Children.Add(Text("来自 "+day.Brief.Episodes+" 段对话",10,"MutedBrush",false,new Thickness(13,7,0,0)));
            return panel;
        }

        static FrameworkElement ProjectCard(string page,int index,Card item,Brush muted) {
            var card=new Border { CornerRadius=new CornerRadius(8), Padding=new Thickness(15,13,12,12), Margin=new Thickness(0,0,0,8) }; card.SetResourceReference(Border.BackgroundProperty,"SurfaceBrush");
            var stack=new StackPanel(); card.Child=stack;
            var head=new Grid(); head.ColumnDefinitions.Add(new ColumnDefinition()); head.ColumnDefinitions.Add(new ColumnDefinition { Width=GridLength.Auto });
            string name=string.IsNullOrWhiteSpace(item.Project)?"未标明项目":item.Project;
            var title=Text(name,14,"TextBrush",true); title.VerticalAlignment=VerticalAlignment.Center; head.Children.Add(title);
            var glyph=Text("−",12,"MutedBrush"); glyph.VerticalAlignment=VerticalAlignment.Center; glyph.Margin=new Thickness(8,0,0,0); Grid.SetColumn(glyph,1); head.Children.Add(glyph);
            var toggle=new Button { Content=head, Padding=new Thickness(0), HorizontalContentAlignment=HorizontalAlignment.Stretch };
            AutomationProperties.SetAutomationId(toggle,"BriefToggle-"+page+"-"+index);
            AutomationProperties.SetName(toggle,name+" · 点击收起或展开");
            stack.Children.Add(toggle);
            var body=new StackPanel { Margin=new Thickness(0,7,0,0) };
            AutomationProperties.SetAutomationId(body,"BriefBody-"+page+"-"+index);
            foreach(string line in item.Items) body.Children.Add(Bullet(line,false,muted));
            if(item.Action.Length>0) body.Children.Add(Bullet(item.Action,true,muted));
            stack.Children.Add(body);
            toggle.Click+=(s,e)=> {
                bool show=body.Visibility!=Visibility.Visible;
                body.Visibility=show?Visibility.Visible:Visibility.Collapsed;
                glyph.Text=show?"−":"+";
            };
            return card;
        }

        static FrameworkElement Bullet(string value,bool action,Brush muted) {
            var row=new Grid { Margin=new Thickness(2,7,0,0) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width=GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition());
            var dot=new System.Windows.Shapes.Ellipse { Width=4, Height=4, VerticalAlignment=VerticalAlignment.Top, Margin=new Thickness(1,8,9,0) };
            dot.SetResourceReference(System.Windows.Shapes.Shape.FillProperty,"MutedBrush");
            row.Children.Add(dot);
            var text=new TextBlock { FontFamily=new FontFamily("Microsoft YaHei UI"), FontSize=12, TextWrapping=TextWrapping.Wrap, LineHeight=19 };
            text.SetResourceReference(TextBlock.ForegroundProperty,action?"TextBrush":"BodyBrush");
            if(action) { var lead=new Run("需要你做："); if(muted!=null) lead.Foreground=muted; text.Inlines.Add(lead); }
            text.Inlines.Add(new Run(value??""));
            Grid.SetColumn(text,1); row.Children.Add(text);
            return row;
        }

        static string Status(ApocalypseBriefStore store,string title) {
            var stamps=new List<string>();
            if(store.Today!=null && store.Today.Generated!=null) stamps.Add("今日 "+store.Today.Generated.Value.ToLocalTime().ToString("HH:mm"));
            if(store.Yesterday!=null && store.Yesterday.Generated!=null) stamps.Add("昨日 "+store.Yesterday.Generated.Value.ToLocalTime().ToString("MM.dd HH:mm"));
            if(stamps.Count==0) return title+" · 还没有 Apocalypse 记录";
            return title+" · "+string.Join(" · ",stamps.ToArray())+" 生成";
        }

        static FrameworkElement Empty(string message) { return Text(message,11,"MutedBrush",false,new Thickness(0,4,0,12)); }
        static FrameworkElement Hint(string message) { return Text(message,10.5,"MutedBrush",false,new Thickness(13,9,0,12)); }
        static TextBlock Text(string value,double size,string brush,bool bold=false,Thickness? margin=null) {
            var text=new TextBlock { Text=value??"", FontFamily=new FontFamily("Microsoft YaHei UI"), FontSize=size, FontWeight=bold?FontWeights.SemiBold:FontWeights.Normal, TextWrapping=TextWrapping.Wrap, Margin=margin??new Thickness(0) };
            text.SetResourceReference(TextBlock.ForegroundProperty,brush); return text;
        }
    }

    public sealed class ApocalypseBriefRunner {
        readonly string configPath;
        readonly object gate=new object();
        string script,python;
        bool resolved;

        public ApocalypseBriefRunner(string configPath) { this.configPath=string.IsNullOrWhiteSpace(configPath)?DefaultConfigPath():configPath; }

        public static string DefaultConfigPath() { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"ZoeyDesktopNotes","apocalypse.json"); }

        // 与 Apocalypse 的 work_briefs.py 同一套规则：今日超过一小时重算，昨日只在缺失时重算。
        public static bool NeedsToday(ApocalypseBrief brief,DateTime now) {
            if(brief==null || brief.Projects.Count==0) return true;
            DateTimeOffset? generated=brief.Generated;
            if(generated==null) return true;
            return now-generated.Value.ToLocalTime().DateTime>=TimeSpan.FromHours(1);
        }

        public static bool NeedsYesterday(ApocalypseBrief brief) { return brief==null || brief.Projects.Count==0; }

        public string Script { get { lock(gate) { Resolve(); return script; } } }

        public void Generate(bool yesterday,bool today) {
            string target;
            lock(gate) { Resolve(); target=script; }
            if(target==null) throw new InvalidOperationException("没有找到 Apocalypse 的每日总结脚本。");
            PollEvidence(target);
            if(yesterday) Run(target,"");
            if(today) Run(target,"--today");
        }

        // Apocalypse 开着时，聊天记录由它的 watcher 归档；没开时先自己跑一次 watcher，
        // 总结才有今天的证据。watcher 自带单实例锁，Apocalypse 在跑时这里会直接跳过。
        void PollEvidence(string script) {
            try {
                string pipeline=Path.GetDirectoryName(script);
                string backend=Path.Combine(Path.GetDirectoryName(pipeline),"backend");
                if(!File.Exists(Path.Combine(backend,"stream_watcher.py"))) return;
                string file,lead; Split(python,out file,out lead);
                string code="import sys;sys.path.insert(0,r'"+backend+"');import stream_watcher as w;ok=w._claim_watch_lock();(w._load_state(),w._startup_index(),w._poll_files()) if ok else None";
                var info=new ProcessStartInfo { FileName=file, Arguments=(lead.Length==0?"":lead+" ")+"-c \""+code+"\"", UseShellExecute=false, CreateNoWindow=true, RedirectStandardOutput=true, RedirectStandardError=true, WorkingDirectory=backend };
                using(var process=new Process()) {
                    process.StartInfo=info;
                    process.Start();
                    process.StandardOutput.ReadToEnd();
                    process.StandardError.ReadToEnd();
                    if(!process.WaitForExit(120000)) { try { process.Kill(); } catch(Exception) { } }
                }
            } catch(Exception) { }
        }

        void Run(string target,string day) {
            string file,lead; Split(python,out file,out lead);
            var info=new ProcessStartInfo { FileName=file, Arguments=(lead.Length==0?"":lead+" ")+Quote(target)+(day.Length==0?"":" "+day), UseShellExecute=false, CreateNoWindow=true, RedirectStandardOutput=true, RedirectStandardError=true, WorkingDirectory=Path.GetDirectoryName(target) };
            var error=new StringBuilder();
            using(var process=new Process()) {
                process.StartInfo=info;
                process.ErrorDataReceived+=(s,e)=> { if(e.Data!=null) error.AppendLine(e.Data); };
                process.Start();
                process.BeginErrorReadLine();
                process.StandardOutput.ReadToEnd();
                if(!process.WaitForExit(300000)) { try { process.Kill(); } catch(Exception) { } throw new TimeoutException("生成每日总结超时。"); }
                if(process.ExitCode!=0) throw new InvalidOperationException(LastLine(error));
            }
        }

        void Resolve() {
            if(resolved) return;
            resolved=true;
            Read();
            if(script==null || !File.Exists(script)) script=DetectScript();
            if(python==null) python=DetectPython();
            if(script!=null && python!=null) Write();
        }

        void Read() {
            try {
                if(!File.Exists(configPath)) return;
                var root=new JavaScriptSerializer().DeserializeObject(File.ReadAllText(configPath,Encoding.UTF8)) as Dictionary<string,object>;
                if(root==null) return;
                script=Value(root,"script"); python=Value(root,"python");
            } catch(Exception) { }
        }

        void Write() {
            try {
                Directory.CreateDirectory(Path.GetDirectoryName(configPath));
                var root=new Dictionary<string,object>(); root["script"]=script; root["python"]=python;
                File.WriteAllText(configPath,new JavaScriptSerializer().Serialize(root),new UTF8Encoding(false));
            } catch(Exception) { }
        }

        static string Value(Dictionary<string,object> root,string key) {
            object value; if(!root.TryGetValue(key,out value)) return null;
            string text=Convert.ToString(value,CultureInfo.InvariantCulture); return string.IsNullOrWhiteSpace(text)?null:text.Trim();
        }

        static string DetectScript() {
            string path=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),".claude","apocalypse","skills_apocalypse","pipeline","daily_work_brief.py");
            if(File.Exists(path)) return path;
            foreach(string root in ClaudeRepoPaths()) {
                path=Path.Combine(root,"skills_apocalypse","pipeline","daily_work_brief.py");
                if(File.Exists(path)) return path;
            }
            return null;
        }

        // Claude Code 记着每个 GitHub 仓库在本机的检出位置，Apocalypse 就在其中。
        static IEnumerable<string> ClaudeRepoPaths() {
            var found=new List<string>();
            try {
                string file=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),".claude.json");
                if(!File.Exists(file)) return found;
                var root=new JavaScriptSerializer { MaxJsonLength=64*1024*1024 }.DeserializeObject(File.ReadAllText(file,Encoding.UTF8)) as Dictionary<string,object>;
                object repos; if(root==null || !root.TryGetValue("githubRepoPaths",out repos)) return found;
                var map=repos as Dictionary<string,object>; if(map==null) return found;
                foreach(object paths in map.Values) {
                    var list=paths as object[]; if(list==null) continue;
                    foreach(object item in list) { string text=Convert.ToString(item,CultureInfo.InvariantCulture); if(!string.IsNullOrWhiteSpace(text)) found.Add(text.Trim()); }
                }
            } catch(Exception) { }
            return found;
        }

        // Windows 上 zoneinfo 需要 tzdata，Apocalypse 缺了它就报 "No time zone found"。
        static string DetectPython() {
            foreach(string candidate in new[] { "python","py -3" }) if(PythonWorks(candidate)) return candidate;
            return "python";
        }

        static bool PythonWorks(string command) {
            try {
                string file,lead; Split(command,out file,out lead);
                var info=new ProcessStartInfo { FileName=file, Arguments=(lead.Length==0?"":lead+" ")+"-c \"import zoneinfo;zoneinfo.ZoneInfo('Asia/Shanghai')\"", UseShellExecute=false, CreateNoWindow=true, RedirectStandardOutput=true, RedirectStandardError=true };
                using(var process=Process.Start(info)) {
                    process.StandardOutput.ReadToEnd(); process.StandardError.ReadToEnd();
                    if(!process.WaitForExit(10000)) { try { process.Kill(); } catch(Exception) { } return false; }
                    return process.ExitCode==0;
                }
            } catch(Exception) { return false; }
        }

        static void Split(string command,out string file,out string lead) {
            command=(command??"").Trim(); file=""; lead="";
            if(command.Length==0) { file="python"; return; }
            if(command[0]=='"') {
                int end=command.IndexOf('"',1);
                if(end<0) { file=command.Substring(1); return; }
                file=command.Substring(1,end-1); lead=command.Substring(end+1).Trim(); return;
            }
            int space=command.IndexOf(' ');
            if(space<0) { file=command; return; }
            file=command.Substring(0,space); lead=command.Substring(space+1).Trim();
        }

        static string Quote(string value) { return value.IndexOf(' ')<0?value:"\""+value+"\""; }

        static string LastLine(StringBuilder error) {
            string[] lines=error.ToString().Split('\n');
            for(int i=lines.Length-1;i>=0;i--) {
                string line=lines[i].Trim();
                if(line.Length>0) return line.Length>120?line.Substring(0,120)+"…":line;
            }
            return "Apocalypse 没有返回结果。";
        }
    }

    public sealed class ApocalypseIntegration {
        readonly NotesWindow window;
        readonly ApocalypseBriefStore briefs;
        readonly ApocalypseBriefRunner runner;
        readonly Grid root;
        readonly StackPanel board;
        readonly TextBlock status;
        readonly TextBlock count;
        Button notesButton,linearButton,summaryButton,issuesButton;
        string page,rendered;
        bool generating;

        ApocalypseIntegration(NotesWindow notesWindow,ApocalypseBriefStore store,ApocalypseBriefRunner generator) {
            window=notesWindow; briefs=store; runner=generator;
            root=Private<Grid>(notesWindow,"root"); board=Private<StackPanel>(notesWindow,"board"); status=Private<TextBlock>(notesWindow,"status"); count=Private<TextBlock>(notesWindow,"count");
        }

        public static ApocalypseIntegration Current { get; private set; }

        public static void Attach(NotesWindow window,ApocalypseBriefStore briefs,ApocalypseBriefRunner runner) {
            var integration=new ApocalypseIntegration(window,briefs,runner); Current=integration; integration.Install();
        }

        // 其他页面接管内容区时调用，避免每日总结的刷新按钮还留在工具栏。
        public void Leave() {
            if(page==null) return;
            LeavePage();
        }

        static T Private<T>(object owner,string name) where T:class {
            FieldInfo field=owner.GetType().GetField(name,BindingFlags.Instance|BindingFlags.NonPublic);
            if(field==null) throw new MissingFieldException(owner.GetType().FullName,name);
            return field.GetValue(owner) as T;
        }

        void Install() {
            var tools=Toolbar(root); if(tools==null) return;
            summaryButton=IconButton(StrokeIcon("M3.2,4.1 H12.8 M3.2,8 H12.8 M3.2,11.9 H8.4"),"总结","SummarySource",()=>ShowPage("summary"));
            issuesButton=IconButton(StrokeIcon("M8,2.9 L14.3,13.4 H1.7 Z M8,6.6 V9.7 M8,11.5 V11.6"),"潜在问题","IssuesSource",()=>ShowPage("issues"));
            int at=Index(tools,"LinearSource"); at=at<0?tools.Children.Count:at+1;
            tools.Children.Insert(at,summaryButton); tools.Children.Insert(at+1,issuesButton);
            notesButton=FindButton(root,"NotesSource"); if(notesButton!=null) notesButton.Click+=(s,e)=>ReturnToBoard();
            linearButton=FindButton(root,"LinearSource"); if(linearButton!=null) linearButton.Click+=(s,e)=>LeavePage();
            var settings=FindButton(root,"Settings"); if(settings!=null) settings.Click+=(s,e)=>ReturnToBoard();
        }

        void ShowPage(string which) {
            if(which==null) return;
            LeaveLinearMode();
            briefs.Load(); page=which;
            Highlight(summaryButton,which=="summary"); Highlight(issuesButton,which=="issues");
            Highlight(notesButton,false); Highlight(linearButton,false);
            window.SetHeading(which=="summary"?"总结":"潜在问题");
            window.PreserveStatusOnSave=true;
            if(rendered==which) Render(which);
            else { CardFlip.Run(root,board,()=>Render(which),true); rendered=which; }
            Generate();
        }

        // 读取是瞬时的，先显示；后台没有总结或总结过期时，交给 Apocalypse 重算。

        void Generate() {
            if(generating || runner==null) return;
            bool yesterday=ApocalypseBriefRunner.NeedsYesterday(briefs.Yesterday);
            bool today=ApocalypseBriefRunner.NeedsToday(briefs.Today,DateTime.Now);
            if(!yesterday && !today) return;
            generating=true;
            status.Text="正在生成每日总结…";
            var worker=new Thread(()=> {
                string error=null;
                try { runner.Generate(yesterday,today); } catch(Exception e) { error=e.Message; }
                window.Dispatcher.BeginInvoke(new Action(()=> {
                    generating=false;
                    briefs.Load();
                    if(page==null) return;
                    Render(page);
                    if(error!=null) status.Text="生成失败："+error;
                }));
            });
            worker.IsBackground=true; worker.Start();
        }

        void Render(string which) {
            if(which=="summary") ApocalypseBriefView.RenderSummary(board,status,count,briefs);
            else ApocalypseBriefView.RenderIssues(board,status,count,briefs);
        }

        void ReturnToBoard() {
            if(page==null) return;
            LeavePage(); window.PreserveStatusOnSave=false; status.Text="保存在此电脑"; window.SetHeading("便签"); window.RenderBoard();
        }

        void LeavePage() {
            if(page==null) return;
            page=null; rendered=null;
            Highlight(summaryButton,false); Highlight(issuesButton,false);
        }

        void LeaveLinearMode() {
            object integration=window.Tag; if(integration==null) return;
            FieldInfo field=integration.GetType().GetField("linearMode",BindingFlags.Instance|BindingFlags.NonPublic);
            if(field==null || !(bool)field.GetValue(integration)) return;
            field.SetValue(integration,false);
            MethodInfo action=integration.GetType().GetMethod("SetModeButtons",BindingFlags.Instance|BindingFlags.NonPublic);
            if(action!=null) action.Invoke(integration,null);
        }

        static void Highlight(Button button,bool active) { if(button!=null) button.SetResourceReference(Control.BackgroundProperty,active?"SelectionBrush":"CanvasBrush"); }

        static System.Windows.Shapes.Path StrokeIcon(string geometry) {
            var path=new System.Windows.Shapes.Path { Data=Geometry.Parse(geometry), StrokeThickness=1.45, StrokeStartLineCap=PenLineCap.Round, StrokeEndLineCap=PenLineCap.Round, StrokeLineJoin=PenLineJoin.Round, Stretch=Stretch.Uniform, Width=16, Height=16, SnapsToDevicePixels=true, IsHitTestVisible=false };
            path.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty,"TextBrush");
            RenderOptions.SetEdgeMode(path,EdgeMode.Unspecified);
            return path;
        }

        Button IconButton(UIElement icon,string label,string id,Action action) {
            var button=new Button { Width=32, Height=32, Padding=new Thickness(6), ToolTip=label, Content=icon };
            AutomationProperties.SetAutomationId(button,id); AutomationProperties.SetName(button,label);
            button.Click+=(s,e)=>action(); return button;
        }

        static int Index(StackPanel tools,string id) {
            for(int i=0;i<tools.Children.Count;i++) { var button=tools.Children[i] as Button; if(button!=null && AutomationProperties.GetAutomationId(button)==id) return i; }
            return -1;
        }

        static StackPanel Toolbar(DependencyObject node) {
            var pin=FindButton(node,"Pin");
            return pin==null?null:VisualTreeHelper.GetParent(pin) as StackPanel;
        }

        static Button FindButton(DependencyObject node,string id) {
            if(node==null) return null;
            var button=node as Button;
            if(button!=null && AutomationProperties.GetAutomationId(button)==id) return button;
            int children=VisualTreeHelper.GetChildrenCount(node);
            for(int i=0;i<children;i++) { var found=FindButton(VisualTreeHelper.GetChild(node,i),id); if(found!=null) return found; }
            return null;
        }
    }
}
