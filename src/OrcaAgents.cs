using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace DesktopNotes {
    public sealed class OrcaAgentItem {
        public string PaneKey { get; set; }
        public string State { get; set; }
        public string AgentType { get; set; }
        public string Prompt { get; set; }
        public string LastAssistantMessage { get; set; }
        public string WorktreeId { get; set; }
        public string WorktreeName { get; set; }
        public string RepoName { get; set; }
        public string ConversationName { get; set; }
        public string TerminalHandle { get; set; }
        public bool Unread { get; set; }
        public long UpdatedAt { get; set; }
        public long StateStartedAt { get; set; }
        public string Title { get { return string.IsNullOrWhiteSpace(ConversationName)?WorktreeName:ConversationName; } }
        public string AgentName {
            get {
                string type=(AgentType??"").Trim();
                if(type.Length==0) return "智能体";
                if(type=="omp") return "OMP";
                return char.ToUpperInvariant(type[0])+type.Substring(1);
            }
        }
    }

    public sealed class OrcaSnapshot {
        public List<OrcaAgentItem> Agents { get; private set; }
        public OrcaSnapshot() { Agents=new List<OrcaAgentItem>(); }

        static JavaScriptSerializer Serializer() { return new JavaScriptSerializer { MaxJsonLength=32*1024*1024,RecursionLimit=80 }; }

        public static OrcaSnapshot Parse(string worktreesJson,string terminalsJson) {
            var snapshot=new OrcaSnapshot();
            var worktrees=Rows(Result(worktreesJson),"worktrees");
            foreach(var worktree in worktrees) {
                string id=Text(worktree,"id"),repo=Text(worktree,"repo"),name=Text(worktree,"displayName");
                bool unread=Bool(worktree,"unread");
                foreach(var agent in Rows(worktree,"agents")) {
                    snapshot.Agents.Add(new OrcaAgentItem {
                        PaneKey=Text(agent,"paneKey"),State=Text(agent,"state"),AgentType=Text(agent,"agentType"),
                        Prompt=Text(agent,"prompt"),LastAssistantMessage=Text(agent,"lastAssistantMessage"),
                        WorktreeId=id,WorktreeName=string.IsNullOrWhiteSpace(name)?repo:name,RepoName=repo,Unread=unread,
                        UpdatedAt=Long(agent,"updatedAt"),StateStartedAt=Long(agent,"stateStartedAt")
                    });
                }
            }
            if(!string.IsNullOrWhiteSpace(terminalsJson)) ApplyTitles(snapshot,Rows(Result(terminalsJson),"terminals"));
            return snapshot;
        }

        // Orca 的看板标题来自终端标签页标题；没有匹配的终端时退回工作区名称。
        static void ApplyTitles(OrcaSnapshot snapshot,List<Dictionary<string,object>> terminals) {
            var byPane=new Dictionary<string,Dictionary<string,object>>(StringComparer.Ordinal);
            foreach(var terminal in terminals) {
                string key=Text(terminal,"tabId")+":"+Text(terminal,"leafId");
                if(!string.IsNullOrEmpty(key) && !byPane.ContainsKey(key)) byPane[key]=terminal;
            }
            foreach(OrcaAgentItem agent in snapshot.Agents) {
                Dictionary<string,object> terminal;
                if(string.IsNullOrEmpty(agent.PaneKey) || !byPane.TryGetValue(agent.PaneKey,out terminal)) continue;
                string title=Text(terminal,"title");
                if(!string.IsNullOrWhiteSpace(title)) agent.ConversationName=title.Trim();
                agent.TerminalHandle=Text(terminal,"handle");
            }
        }

        // 工作中的一直显示；已经交还控制权、但还没在这页点开过的会话也留着。
        public List<OrcaAgentItem> Visible(OrcaSeenStore seen) {
            return Agents.Where(a=>a!=null && (a.State=="working" || seen==null || seen.Unseen(a)))
                .OrderByDescending(a=>a.StateStartedAt).ToList();
        }

        public static string Elapsed(long startedAt,DateTimeOffset now) {
            if(startedAt<=0) return "";
            long seconds=(now.ToUnixTimeMilliseconds()-startedAt)/1000;
            if(seconds<0) seconds=0;
            if(seconds<60) return "刚刚";
            long minutes=seconds/60;
            if(minutes<60) return minutes+" 分钟";
            long hours=minutes/60;
            if(hours<24) return hours+" 小时";
            return hours/24+" 天";
        }

        static Dictionary<string,object> Result(string json) {
            if(string.IsNullOrWhiteSpace(json)) throw new InvalidDataException("Orca 没有返回数据。");
            var root=Serializer().DeserializeObject(json.Trim().TrimStart('\uFEFF')) as Dictionary<string,object>;
            if(root==null) throw new InvalidDataException("Orca 返回了无法识别的数据。");
            var result=Dict(root,"result");
            if(result==null) throw new InvalidOperationException(Error(root));
            return result;
        }

        static string Error(Dictionary<string,object> root) {
            var error=Dict(root,"error");
            string message=error==null?Text(root,"error"):Text(error,"message");
            return string.IsNullOrWhiteSpace(message)?"Orca 命令执行失败。":message;
        }

        static Dictionary<string,object> Dict(Dictionary<string,object> source,string key) {
            object value; if(source==null || !source.TryGetValue(key,out value)) return null; return value as Dictionary<string,object>;
        }
        static string Text(Dictionary<string,object> source,string key) {
            object value; if(source==null || !source.TryGetValue(key,out value) || value==null) return ""; return Convert.ToString(value,CultureInfo.InvariantCulture)??"";
        }
        static bool Bool(Dictionary<string,object> source,string key) {
            object value; if(source==null || !source.TryGetValue(key,out value) || value==null) return false;
            try { return Convert.ToBoolean(value,CultureInfo.InvariantCulture); } catch { return false; }
        }
        static long Long(Dictionary<string,object> source,string key) {
            object value; if(source==null || !source.TryGetValue(key,out value) || value==null) return 0;
            try { return Convert.ToInt64(value,CultureInfo.InvariantCulture); } catch { return 0; }
        }
        static List<Dictionary<string,object>> Rows(Dictionary<string,object> source,string key) {
            var result=new List<Dictionary<string,object>>(); object raw;
            if(source==null || !source.TryGetValue(key,out raw) || raw==null) return result;
            object[] items=raw as object[]; if(items==null) return result;
            foreach(object item in items) { var row=item as Dictionary<string,object>; if(row!=null) result.Add(row); }
            return result;
        }
    }

    // 已经点开过的会话记在这里；同一会话跑了新的一轮，状态开始时间变新，会重新出现。
    public sealed class OrcaSeenStore {
        readonly string path;
        readonly Dictionary<string,long> seen=new Dictionary<string,long>(StringComparer.Ordinal);
        public OrcaSeenStore(string notesPath) {
            path=Path.Combine(Path.GetDirectoryName(Path.GetFullPath(notesPath)),"orca-seen.json");
            Load();
        }
        public bool Unseen(OrcaAgentItem agent) {
            long at;
            return !seen.TryGetValue(Key(agent),out at) || at<agent.StateStartedAt;
        }
        public void MarkSeen(OrcaAgentItem agent) {
            seen[Key(agent)]=agent.StateStartedAt;
            try { NotesStore.WriteAtomic(path,new JavaScriptSerializer().Serialize(seen),false); } catch(Exception) { }
        }
        void Load() {
            try {
                if(!File.Exists(path)) return;
                var data=new JavaScriptSerializer().DeserializeObject(File.ReadAllText(path,Encoding.UTF8)) as Dictionary<string,object>;
                if(data==null) return;
                foreach(var pair in data) {
                    try { seen[pair.Key]=Convert.ToInt64(pair.Value,CultureInfo.InvariantCulture); } catch(Exception) { }
                }
            } catch(Exception) { }
        }
        static string Key(OrcaAgentItem agent) { return string.IsNullOrEmpty(agent.PaneKey)?agent.WorktreeId+"|"+agent.AgentType:agent.PaneKey; }
    }

    // 上一轮还在工作、这一轮已经交还控制权的智能体，就是要震一下的。
    public sealed class OrcaAgentWatcher {
        readonly Dictionary<string,string> previous=new Dictionary<string,string>(StringComparer.Ordinal);
        public List<OrcaAgentItem> Observe(OrcaSnapshot snapshot) {
            var finished=new List<OrcaAgentItem>();
            if(snapshot==null) return finished;
            var next=new Dictionary<string,string>(StringComparer.Ordinal);
            foreach(OrcaAgentItem agent in snapshot.Agents) {
                string key=string.IsNullOrEmpty(agent.PaneKey)?agent.WorktreeId+"|"+agent.AgentType:agent.PaneKey;
                string state;
                if(previous.TryGetValue(key,out state) && Busy(state) && !Busy(agent.State)) finished.Add(agent);
                next[key]=agent.State;
            }
            previous.Clear();
            foreach(var pair in next) previous[pair.Key]=pair.Value;
            return finished;
        }
        static bool Busy(string state) { return state=="working" || state=="monitoring"; }
    }

    public sealed class OrcaCli {
        // 测试替身：参数是子命令，返回值是 JSON。为 null 时执行真正的 orca。
        public static Func<string,string> TestCommand;
        readonly string executable;
        public OrcaCli(string executable=null) { this.executable=executable??Detect(); }
        public bool Found { get { return TestCommand!=null || executable!=null; } }

        public OrcaSnapshot Load(bool titles) {
            string worktrees=Run("worktree ps --json");
            string terminals=null;
            if(titles) { try { terminals=Run("terminal list --json"); } catch(Exception) { } }
            return OrcaSnapshot.Parse(worktrees,terminals);
        }

        public string Run(string arguments) {
            if(TestCommand!=null) return TestCommand(arguments);
            if(executable==null) throw new InvalidOperationException("没有找到 Orca 命令行。请先安装 Orca。");
            var info=new ProcessStartInfo {
                FileName=executable,Arguments=arguments,UseShellExecute=false,CreateNoWindow=true,
                RedirectStandardOutput=true,RedirectStandardError=true,
                StandardOutputEncoding=new UTF8Encoding(false),StandardErrorEncoding=new UTF8Encoding(false)
            };
            var error=new StringBuilder();
            using(var process=new Process()) {
                process.StartInfo=info;
                process.ErrorDataReceived+=(s,e)=> { if(e.Data!=null) error.AppendLine(e.Data); };
                process.Start();
                process.BeginErrorReadLine();
                string output=process.StandardOutput.ReadToEnd();
                if(!process.WaitForExit(15000)) { try { process.Kill(); } catch(Exception) { } throw new TimeoutException("Orca 命令超时。"); }
                if(process.ExitCode!=0) throw new InvalidOperationException(LastLine(error));
                return output;
            }
        }

        static string LastLine(StringBuilder error) {
            string[] lines=error.ToString().Split('\n');
            for(int i=lines.Length-1;i>=0;i--) {
                string line=lines[i].Trim();
                if(line.Length>0) return line.Length>160?line.Substring(0,160)+"…":line;
            }
            return "Orca 命令执行失败。";
        }

        public static string Detect() {
            var candidates=new List<string>();
            string local=Environment.GetEnvironmentVariable("LOCALAPPDATA");
            if(!string.IsNullOrWhiteSpace(local)) candidates.Add(Path.Combine(local,"Programs","orca","resources","bin","orca.exe"));
            string programFiles=Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            if(!string.IsNullOrWhiteSpace(programFiles)) candidates.Add(Path.Combine(programFiles,"Orca","resources","bin","orca.exe"));
            foreach(string candidate in candidates) if(File.Exists(candidate)) return candidate;
            string path=Environment.GetEnvironmentVariable("PATH");
            if(!string.IsNullOrWhiteSpace(path)) {
                foreach(string folder in path.Split(';')) {
                    string trimmed=(folder??"").Trim().Trim('"');
                    if(trimmed.Length==0) continue;
                    string candidate=Path.Combine(trimmed,"orca.exe");
                    if(File.Exists(candidate)) return candidate;
                }
            }
            return null;
        }
    }

    public static class OrcaBoardView {
        public static void Render(StackPanel board,TextBlock status,TextBlock count,OrcaSnapshot snapshot,string error,OrcaSeenStore seen,Action<OrcaAgentItem> open) {
            board.Children.Clear();
            var root=new StackPanel(); AutomationProperties.SetAutomationId(root,"OrcaRoot"); board.Children.Add(root);
            List<OrcaAgentItem> agents=snapshot==null?new List<OrcaAgentItem>():snapshot.Visible(seen);
            int working=agents.Count(a=>a.State=="working"),pending=agents.Count-working;
            if(snapshot==null) root.Children.Add(Empty(error??"正在读取工作中的智能体…"));
            else if(agents.Count==0) root.Children.Add(Empty("现在没有工作中的智能体。"));
            else for(int i=0;i<agents.Count;i++) root.Children.Add(Card(agents[i],i,seen,open));
            if(count!=null) count.Text=snapshot==null?"":pending==0?working+" 个智能体":working>0?working+" 个工作中 · "+pending+" 个待查看":pending+" 个待查看";
            if(status!=null) {
                if(string.IsNullOrWhiteSpace(error)) status.Text="Orca · "+DateTime.Now.ToString("HH:mm")+" 更新";
                else status.Text=snapshot==null?"Orca 连接失败":"Orca 连接失败 · 显示上次数据";
            }
        }

        public static void Shake(FrameworkElement target) {
            if(target==null) return;
            var transform=new TranslateTransform();
            target.RenderTransform=transform;
            var animation=new DoubleAnimationUsingKeyFrames { Duration=TimeSpan.FromMilliseconds(520) };
            double[] offsets={ 0,-10,10,-8,8,-5,5,0 };
            for(int i=0;i<offsets.Length;i++) animation.KeyFrames.Add(new LinearDoubleKeyFrame(offsets[i],KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(i*70))));
            animation.Completed+=(s,e)=> { if(ReferenceEquals(target.RenderTransform,transform)) target.RenderTransform=null; };
            transform.BeginAnimation(TranslateTransform.XProperty,animation);
        }

        static FrameworkElement Card(OrcaAgentItem agent,int index,OrcaSeenStore seen,Action<OrcaAgentItem> open) {
            var card=new Border { CornerRadius=new CornerRadius(8),Padding=new Thickness(14,12,12,9) };
            card.SetResourceReference(Border.BackgroundProperty,"SurfaceBrush");
            if(agent.State!="working") { card.BorderThickness=new Thickness(1); card.BorderBrush=FinishedBrush(agent.State); }
            var stack=new StackPanel(); card.Child=stack;

            var head=new Grid(); head.ColumnDefinitions.Add(new ColumnDefinition { Width=GridLength.Auto }); head.ColumnDefinitions.Add(new ColumnDefinition()); head.ColumnDefinitions.Add(new ColumnDefinition { Width=GridLength.Auto });
            var icon=AgentIcon(agent.AgentType); icon.VerticalAlignment=VerticalAlignment.Center; icon.Margin=new Thickness(0,0,7,0); head.Children.Add(icon);
            bool emphasis=agent.State!="working" || agent.Unread;
            var title=Text(agent.Title,12.5,emphasis?"TextBrush":"BodyBrush",emphasis);
            title.TextWrapping=TextWrapping.NoWrap; title.TextTrimming=TextTrimming.CharacterEllipsis; title.VerticalAlignment=VerticalAlignment.Center; title.ToolTip=agent.Title;
            Grid.SetColumn(title,1); head.Children.Add(title);
            var dot=StateDot(agent.State); Grid.SetColumn(dot,2); head.Children.Add(dot); stack.Children.Add(head);

            string user=Snippet(agent.Prompt),answer=Snippet(agent.LastAssistantMessage);
            if(user.Length>0 || answer.Length>0) {
                var body=new StackPanel { Margin=new Thickness(0,7,0,0) };
                if(user.Length>0) body.Children.Add(Line("你 ",user,11,"MutedBrush",1));
                if(answer.Length>0) body.Children.Add(Line(agent.AgentName+" ",answer,12,"BodyBrush",2));
                stack.Children.Add(body);
            }
            stack.Children.Add(Foot(agent));

            var button=new Button { Content=card,Margin=new Thickness(0,0,0,8),Padding=new Thickness(0),BorderThickness=new Thickness(0),Background=Brushes.Transparent,HorizontalContentAlignment=HorizontalAlignment.Stretch,ToolTip="打开 Orca 中的这个会话" };
            AutomationProperties.SetAutomationId(button,"OrcaCard-"+index);
            AutomationProperties.SetName(button,agent.Title+" · 打开 Orca 会话");
            if(open!=null) button.Click+=(s,e)=>open(agent);
            return button;
        }

        static Brush FinishedBrush(string state) {
            Color color=state=="done"?Color.FromArgb(96,46,160,67):Color.FromArgb(96,217,119,87);
            var brush=new SolidColorBrush(color); brush.Freeze(); return brush;
        }

        static FrameworkElement Foot(OrcaAgentItem agent) {
            var foot=new Grid { Margin=new Thickness(0,10,0,0) };
            foot.ColumnDefinitions.Add(new ColumnDefinition()); foot.ColumnDefinitions.Add(new ColumnDefinition { Width=GridLength.Auto });
            var place=new Grid();
            place.ColumnDefinitions.Add(new ColumnDefinition { Width=GridLength.Auto }); place.ColumnDefinitions.Add(new ColumnDefinition());
            var folder=new System.Windows.Shapes.Path { Data=Geometry.Parse("M1.9,3.1 H6.1 L7.5,4.7 H14.1 V12.9 H1.9 Z"),Stretch=Stretch.Uniform,Width=12,Height=12,StrokeThickness=1.2,VerticalAlignment=VerticalAlignment.Center,Margin=new Thickness(1,0,7,0) };
            folder.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty,"MutedBrush"); place.Children.Add(folder);
            var name=Text(agent.WorktreeName,10.5,"MutedBrush"); name.TextWrapping=TextWrapping.NoWrap; name.TextTrimming=TextTrimming.CharacterEllipsis; name.VerticalAlignment=VerticalAlignment.Center;
            Grid.SetColumn(name,1); place.Children.Add(name); foot.Children.Add(place);
            string elapsed=OrcaSnapshot.Elapsed(agent.StateStartedAt,DateTimeOffset.UtcNow);
            string state=agent.State=="done"?"已完成":agent.State=="waiting"?"等待你":agent.State=="blocked"?"需要你":"";
            if(state.Length>0) elapsed=state+(elapsed.Length>0?" · "+elapsed:"");
            if(elapsed.Length>0) {
                var time=Text(elapsed,10.5,"MutedBrush"); time.VerticalAlignment=VerticalAlignment.Center; time.Margin=new Thickness(10,0,0,0);
                Grid.SetColumn(time,1); foot.Children.Add(time);
            }
            return foot;
        }

        static TextBlock Line(string prefix,string value,double size,string brush,int lines) {
            var text=new TextBlock { FontFamily=new FontFamily("Microsoft YaHei UI"),FontSize=size,TextWrapping=TextWrapping.Wrap,LineHeight=size*1.5,MaxHeight=size*1.5*lines };
            text.SetResourceReference(TextBlock.ForegroundProperty,brush);
            var lead=new Run(prefix); lead.SetResourceReference(TextElement.ForegroundProperty,"MutedBrush"); text.Inlines.Add(lead);
            text.Inlines.Add(new Run(value));
            return text;
        }

        static FrameworkElement AgentIcon(string type) {
            string glyph="●",color="#8A8F98";
            string name=(type??"").Trim().ToLowerInvariant();
            if(name=="claude") { glyph="✳"; color="#D97757"; }
            else if(name=="codex") { glyph="◎"; color="#10A37F"; }
            var text=new TextBlock { Text=glyph,FontFamily=new FontFamily("Segoe UI Symbol"),FontSize=13,Width=16,TextAlignment=TextAlignment.Center,VerticalAlignment=VerticalAlignment.Center };
            var brush=new SolidColorBrush((Color)ColorConverter.ConvertFromString(color)); brush.Freeze(); text.Foreground=brush;
            return text;
        }

        static FrameworkElement StateDot(string state) {
            string color="#8A8F98";
            if(state=="working") color="#D9A400"; else if(state=="done") color="#2EA043"; else if(state=="waiting" || state=="blocked") color="#D97757";
            var brush=new SolidColorBrush((Color)ColorConverter.ConvertFromString(color)); brush.Freeze();
            return new System.Windows.Shapes.Ellipse { Width=6,Height=6,Fill=brush,VerticalAlignment=VerticalAlignment.Center,Margin=new Thickness(8,0,2,0) };
        }

        static string Snippet(string value) {
            if(string.IsNullOrWhiteSpace(value)) return "";
            string text=Regex.Replace(value.Replace("\r"," ").Replace("\n"," "),@"\s+"," ").Trim();
            return text.Length>300?text.Substring(0,300)+"…":text;
        }

        static FrameworkElement Empty(string message) { return Text(message,11,"MutedBrush",false,new Thickness(2,6,0,12)); }

        static TextBlock Text(string value,double size,string brush,bool bold=false,Thickness? margin=null) {
            var text=new TextBlock { Text=value??"",FontFamily=new FontFamily("Microsoft YaHei UI"),FontSize=size,FontWeight=bold?FontWeights.SemiBold:FontWeights.Normal,TextWrapping=TextWrapping.Wrap,Margin=margin??new Thickness(0) };
            text.SetResourceReference(TextBlock.ForegroundProperty,brush); return text;
        }
    }

    public sealed class OrcaIntegration {
        readonly NotesWindow window;
        readonly Grid root;
        readonly StackPanel board;
        readonly TextBlock status;
        readonly TextBlock count;
        readonly DispatcherTimer timer=new DispatcherTimer { Interval=TimeSpan.FromSeconds(15) };
        readonly OrcaAgentWatcher watcher=new OrcaAgentWatcher();
        readonly OrcaSeenStore seen;
        Button notesButton,linearButton,sourceButton,refreshButton;
        OrcaSnapshot lastSnapshot;
        string error;
        bool page,loading,pending;

        OrcaIntegration(NotesWindow notesWindow,NotesStore notesStore) {
            window=notesWindow; seen=new OrcaSeenStore(notesStore.FilePath);
            root=Private<Grid>(notesWindow,"root"); board=Private<StackPanel>(notesWindow,"board"); status=Private<TextBlock>(notesWindow,"status"); count=Private<TextBlock>(notesWindow,"count");
        }

        public static void Attach(NotesWindow window,NotesStore store) { new OrcaIntegration(window,store).Install(); }

        static T Private<T>(object owner,string name) where T:class {
            FieldInfo field=owner.GetType().GetField(name,BindingFlags.Instance|BindingFlags.NonPublic);
            if(field==null) throw new MissingFieldException(owner.GetType().FullName,name);
            return field.GetValue(owner) as T;
        }

        void Install() {
            var tools=Toolbar(root); if(tools==null) return;
            sourceButton=IconButton(DashboardIcon(),"工作中 · 读取 Orca 智能体","AgentsSource",ShowPage);
            refreshButton=GlyphButton("","刷新工作中的智能体","AgentsRefresh",()=>Load(true));
            refreshButton.Visibility=Visibility.Collapsed;
            int at=Index(tools,"BriefRefresh"); if(at<0) at=Index(tools,"LinearRefresh");
            int insert=at<0?tools.Children.Count:at+1;
            tools.Children.Insert(insert,sourceButton); tools.Children.Insert(insert+1,refreshButton);

            notesButton=FindButton(root,"NotesSource"); if(notesButton!=null) notesButton.Click+=(s,e)=>BackToNotes();
            linearButton=FindButton(root,"LinearSource"); if(linearButton!=null) linearButton.Click+=(s,e)=>LeavePage();
            Hook("SummarySource",LeavePage); Hook("IssuesSource",LeavePage);
            Button settings=FindButton(root,"Settings");
            if(settings!=null) settings.Click+=(s,e)=> { if(page) { LeavePage(); window.PreserveStatusOnSave=false; window.SetHeading("便签"); status.Text="保存在此电脑"; } };

            timer.Tick+=(s,e)=>Load(page);
            window.Closed+=(s,e)=>timer.Stop();
            timer.Start();
            Load(false);
        }

        void Hook(string id,Action action) {
            Button button=FindButton(root,id);
            if(button!=null) button.Click+=(s,e)=>action();
        }

        // 读取是后台的；先显示手上的内容，读到再更新。
        void Load(bool titles) {
            if(loading) { if(titles) pending=true; return; }
            var cli=new OrcaCli();
            if(!cli.Found) {
                error="没有找到 Orca 命令行。请先安装 Orca。"; timer.Stop();
                if(page) Render();
                return;
            }
            loading=true;
            if(page) status.Text="正在读取工作中的智能体…";
            ThreadPool.QueueUserWorkItem(delegate {
                OrcaSnapshot snapshot=null; string failure=null;
                try { snapshot=cli.Load(titles); } catch(Exception e) { failure=e.Message; }
                window.Dispatcher.BeginInvoke(new Action(delegate {
                    loading=false;
                    if(!window.IsLoaded) return;
                    if(snapshot!=null) {
                        lastSnapshot=snapshot; error=null;
                        List<OrcaAgentItem> finished=watcher.Observe(snapshot);
                        if(page) Render();
                        if(finished.Count>0) Shake();
                    } else {
                        error=failure;
                        if(page) Render();
                    }
                    if(pending) { pending=false; Load(true); }
                }));
            });
        }

        void ShowPage() {
            if(page) { Load(true); return; }
            LeaveLinearMode();
            if(ApocalypseIntegration.Current!=null) ApocalypseIntegration.Current.Leave();
            page=true;
            Highlight(sourceButton,true); Highlight(notesButton,false); Highlight(linearButton,false);
            refreshButton.Visibility=Visibility.Visible;
            window.SetHeading("工作中"); window.PreserveStatusOnSave=true;
            CardFlip.Run(root,board,delegate { Render(); },true);
            Load(true);
        }

        void BackToNotes() {
            if(!page) return;
            LeavePage();
            window.PreserveStatusOnSave=false; window.SetHeading("便签"); status.Text="保存在此电脑"; window.RenderBoard();
        }

        void LeavePage() {
            if(!page) return;
            page=false;
            Highlight(sourceButton,false);
            refreshButton.Visibility=Visibility.Collapsed;
        }

        void Render() {
            if(!page) return;
            OrcaBoardView.Render(board,status,count,lastSnapshot,error,seen,Open);
        }

        // 点卡片：记成已看过，并让 Orca 切到这个会话所在的终端。
        void Open(OrcaAgentItem agent) {
            bool finished=agent.State!="working";
            seen.MarkSeen(agent);
            if(finished) Render();
            string handle=agent.TerminalHandle;
            ThreadPool.QueueUserWorkItem(delegate {
                string failure=null;
                try {
                    if(string.IsNullOrEmpty(handle)) throw new InvalidOperationException("这个会话的终端已经关闭。");
                    new OrcaCli().Run("terminal switch --terminal "+handle+" --json");
                } catch(Exception e) { failure=e.Message; }
                if(OrcaCli.TestCommand==null) FocusOrca();
                if(failure!=null) window.Dispatcher.BeginInvoke(new Action(delegate { if(page) status.Text="打开 Orca 会话失败："+failure; }));
            });
        }

        [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr handle);
        [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr handle,int command);
        static void FocusOrca() {
            try {
                foreach(Process process in Process.GetProcessesByName("Orca")) {
                    IntPtr handle=process.MainWindowHandle;
                    if(handle==IntPtr.Zero) continue;
                    ShowWindow(handle,9); SetForegroundWindow(handle);
                    return;
                }
            } catch(Exception) { }
        }

        void Shake() {
            try {
                if(!window.IsVisible) return;
                if(window.WindowState==WindowState.Minimized) window.WindowState=WindowState.Normal;
                OrcaBoardView.Shake(root);
            } catch(Exception) { }
        }

        static void LeaveLinearMode(object windowTag) {
            object integration=windowTag; if(integration==null) return;
            FieldInfo field=integration.GetType().GetField("linearMode",BindingFlags.Instance|BindingFlags.NonPublic);
            if(field==null || !(bool)field.GetValue(integration)) return;
            field.SetValue(integration,false);
            MethodInfo action=integration.GetType().GetMethod("SetModeButtons",BindingFlags.Instance|BindingFlags.NonPublic);
            if(action!=null) action.Invoke(integration,null);
        }
        void LeaveLinearMode() { LeaveLinearMode(window.Tag); }

        static void Highlight(Button button,bool active) { if(button!=null) button.SetResourceReference(Control.BackgroundProperty,active?"SelectionBrush":"CanvasBrush"); }

        static System.Windows.Shapes.Path DashboardIcon() {
            var path=new System.Windows.Shapes.Path {
                Data=Geometry.Parse("M2.9,2.9 H6.9 V6.9 H2.9 Z M9.1,2.9 H13.1 V5.6 H9.1 Z M9.1,7.7 H13.1 V13.1 H9.1 Z M2.9,9 H6.9 V13.1 H2.9 Z"),
                StrokeThickness=1.45,StrokeStartLineCap=PenLineCap.Round,StrokeEndLineCap=PenLineCap.Round,StrokeLineJoin=PenLineJoin.Round,
                Stretch=Stretch.Uniform,Width=16,Height=16,SnapsToDevicePixels=true,IsHitTestVisible=false
            };
            path.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty,"TextBrush");
            return path;
        }

        Button IconButton(UIElement icon,string label,string id,Action action) {
            var button=new Button { Width=32,Height=32,Padding=new Thickness(6),ToolTip=label,Content=icon };
            AutomationProperties.SetAutomationId(button,id); AutomationProperties.SetName(button,label);
            button.Click+=(s,e)=>action(); return button;
        }

        Button GlyphButton(string glyph,string label,string id,Action action) {
            return IconButton(new TextBlock { Text=glyph,FontFamily=new FontFamily("Segoe MDL2 Assets"),FontSize=14,VerticalAlignment=VerticalAlignment.Center },label,id,action);
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
