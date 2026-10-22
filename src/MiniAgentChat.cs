using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace DesktopNotes {
    public sealed class MiniAgentConfig {
        public string Vault { get; set; }
        public string Folder { get; set; }
        public string Provider { get; set; }
        public string Model { get; set; }
        public string SessionDir { get; set; }
        public int TimeoutSeconds { get; set; }
        public string NodePath { get; set; }
        public string CliPath { get; set; }

        public static string FilePath {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"ZoeyDesktopNotes","miniagent.json"); }
        }
        [ScriptIgnore] public string ChatFolder { get { return Path.Combine(Vault,Folder); } }

        public static MiniAgentConfig Default() {
            string local=Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return new MiniAgentConfig {
                Vault=@"E:\BaiduSyncdisk\Obsidian Notes",
                Folder="MiniAgent",
                Provider="minimax",
                Model="MiniMax-M3.1-Flash",
                SessionDir=Path.Combine(local,"ZoeyDesktopNotes","miniagent-sessions"),
                TimeoutSeconds=300
            };
        }
        public static MiniAgentConfig Load() {
            var fallback=Default();
            try {
                if(File.Exists(FilePath)) {
                    var loaded=new JavaScriptSerializer { MaxJsonLength=1024*1024 }.Deserialize<MiniAgentConfig>(File.ReadAllText(FilePath,Encoding.UTF8));
                    if(loaded!=null) {
                        if(string.IsNullOrWhiteSpace(loaded.Vault)) loaded.Vault=fallback.Vault;
                        if(string.IsNullOrWhiteSpace(loaded.Folder)) loaded.Folder=fallback.Folder;
                        if(string.IsNullOrWhiteSpace(loaded.Provider)) loaded.Provider=fallback.Provider;
                        if(string.IsNullOrWhiteSpace(loaded.Model)) loaded.Model=fallback.Model;
                        if(string.IsNullOrWhiteSpace(loaded.SessionDir)) loaded.SessionDir=fallback.SessionDir;
                        if(loaded.TimeoutSeconds<20) loaded.TimeoutSeconds=fallback.TimeoutSeconds;
                        return loaded;
                    }
                }
            } catch(Exception) { }
            try { if(!File.Exists(FilePath)) fallback.Save(); } catch(Exception) { }
            return fallback;
        }
        public void Save() { NotesStore.WriteAtomic(FilePath,new JavaScriptSerializer().Serialize(this),false); }

        public string ResolveNode() {
            if(!string.IsNullOrWhiteSpace(NodePath) && File.Exists(NodePath)) return NodePath;
            string appData=Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            string[] candidates={
                string.IsNullOrWhiteSpace(appData)?null:Path.Combine(appData,"npm","node.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),"nodejs","node.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),"nodejs","node.exe")
            };
            foreach(string candidate in candidates) if(!string.IsNullOrWhiteSpace(candidate) && File.Exists(candidate)) return candidate;
            return "node";
        }
        public string ResolveCli() {
            if(!string.IsNullOrWhiteSpace(CliPath) && File.Exists(CliPath)) return CliPath;
            string appData=Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            if(!string.IsNullOrWhiteSpace(appData)) {
                string candidate=Path.Combine(appData,"npm","node_modules","@earendil-works","pi-coding-agent","dist","bundle","cli.js");
                if(File.Exists(candidate)) return candidate;
            }
            string local=Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if(!string.IsNullOrWhiteSpace(local)) {
                string candidate=Path.Combine(local,"npm","node_modules","@earendil-works","pi-coding-agent","dist","bundle","cli.js");
                if(File.Exists(candidate)) return candidate;
            }
            return null;
        }
    }

    public sealed class MiniAgentMessage {
        public bool User { get; set; }
        public string Text { get; set; }
        public bool Pending { get; set; }
        public bool Failed { get; set; }
        public int Citations { get; set; }
        public List<string> CitationPaths { get; set; }
    }

    public sealed class MiniAgentResult {
        public string Text { get; set; }
        public string Error { get; set; }
        public int ToolCalls { get; set; }
    }

    public static class MiniAgentStore {
        static readonly Regex QuotedCitation=new Regex(@"`([^`\r\n]+?\.md(?::\d+)?)`",RegexOptions.IgnoreCase);
        static readonly Regex BareCitation=new Regex(@"(?<![\w])([A-Za-z0-9_\-\./\\]+\.md(?::\d+)?)",RegexOptions.IgnoreCase);
        static readonly Regex Inline=new Regex(@"(\*\*.*?\*\*|`[^`]*`)",RegexOptions.Singleline);

        public static string SafeTitle(string text,int max=28) {
            string value=Regex.Replace((text??"").Replace('\r',' ').Replace('\n',' ').Trim(),@"\s+"," ");
            if(value.Length>max) {
                int cut=value.LastIndexOf(' ',Math.Min(max,value.Length-1)-1);
                if(cut<max/2) cut=max;
                value=value.Substring(0,cut).Trim()+"…";
            }
            return value.Length==0?"新对话":value;
        }
        public static string FileName(DateTime local,string title) {
            string safe=SafeTitle(title,40);
            foreach(char invalid in Path.GetInvalidFileNameChars()) safe=safe.Replace(invalid,'-');
            safe=safe.Replace('/','-').Replace('\\','-').Trim().Trim('.');
            if(safe.Length==0) safe="对话";
            return local.ToString("yyyy-MM-dd HH-mm-ss")+" "+safe+".md";
        }
        static string Yaml(string value) {
            return "\""+(value??"").Replace("\\","\\\\").Replace("\"","\\\"").Replace("\r"," ").Replace("\n"," ")+"\"";
        }
        public static string Markdown(string title,DateTimeOffset created,string session,string model,List<MiniAgentMessage> messages) {
            var text=new StringBuilder();
            text.AppendLine("---");
            text.AppendLine("title: "+Yaml(title));
            text.AppendLine("created: "+created.ToString("o",CultureInfo.InvariantCulture));
            text.AppendLine("updated: "+DateTimeOffset.Now.ToString("o",CultureInfo.InvariantCulture));
            text.AppendLine("session: "+session);
            text.AppendLine("model: "+model);
            text.AppendLine("---");
            text.AppendLine();
            text.AppendLine("# "+title);
            if(messages!=null) foreach(MiniAgentMessage message in messages) {
                if(message==null || message.Pending) continue;
                text.AppendLine();
                text.AppendLine(message.User?"## 你":"## 助手");
                text.AppendLine();
                if(message.Failed) text.AppendLine("> 这次检索没有完成。");
                text.AppendLine(message.Text??"");
            }
            return text.ToString();
        }
        public static List<string> Citations(string text) {
            var seen=new List<string>();
            if(string.IsNullOrEmpty(text)) return seen;
            foreach(Regex pattern in new[]{QuotedCitation,BareCitation}) foreach(Match match in pattern.Matches(text)) {
                string value=match.Groups[1].Value.Trim().Trim('`','"','\'',',','.',';',':','）',')','】',']');
                if(value.Length<=3) continue;
                if(!seen.Any(existing=>string.Equals(existing,value,StringComparison.OrdinalIgnoreCase))) seen.Add(value);
            }
            return seen;
        }
        public static int CitationCount(string text) { return Citations(text).Count; }
        public static IEnumerable<string> InlineParts(string value) { return Inline.Split(value??""); }
    }

    public static class MiniAgentRunner {
        public static string SystemPrompt() {
            return "今天是 "+DateTime.Now.ToString("yyyy-MM-dd")+"。你是桌面便签里的检索助手，只回答能从当前 Obsidian 笔记中找到依据的问题。当前目录是 Obsidian 库根目录。回答前必须先用 bash 运行 rg 搜索 markdown 文件（加 --glob '!MiniAgent/**' 排除对话记录），必要时再用 read 读取原文；不要凭记忆或预训练知识回答。每个事实性结论后面用反引号给出 相对路径:行号；找不到依据就明确说“没有在笔记中找到依据”，不要补充推测。不要修改、创建或删除任何文件，只允许读取和搜索。用中文简洁回答。";
        }
        public static string Quote(string value) {
            if(string.IsNullOrEmpty(value)) return "\"\"";
            if(value.IndexOfAny(new[]{' ','\t','\n','\v','"'})<0) return value;
            var result=new StringBuilder("\"");
            int slashes=0;
            foreach(char c in value) {
                if(c=='\\') { slashes++; continue; }
                if(c=='"') { result.Append('\\',slashes*2+1); result.Append('"'); slashes=0; }
                else { result.Append('\\',slashes); result.Append(c); slashes=0; }
            }
            result.Append('\\',slashes*2);
            result.Append('"');
            return result.ToString();
        }
        public static string Arguments(MiniAgentConfig config,string sessionId,string message) {
            var args=new List<string> {
                config.ResolveCli(),"--print","--mode","json","--session-dir",config.SessionDir,"--session-id",sessionId,
                "--no-context-files","--no-skills","--no-prompt-templates","--no-mcp",
                "--provider",config.Provider,"--model",config.Model,"--tools","read,bash",
                // 检索任务不需要长推理：MiniMax 只允许自适应思考，low 最快也最省。
                "--thinking","low",
                "--system-prompt",SystemPrompt(),"--","问题："+message
            };
            return string.Join(" ",args.Select(Quote));
        }
        public static MiniAgentResult Run(MiniAgentConfig config,string sessionId,string message,Action<string> progress,Func<bool> cancelled) {
            var result=new MiniAgentResult();
            string cli=config.ResolveCli();
            if(cli==null) { result.Error="没有找到 Pi 命令行。请确认已安装 Pi，或在 "+MiniAgentConfig.FilePath+" 里填写 CliPath。"; return result; }
            if(!Directory.Exists(config.Vault)) { result.Error="找不到 Obsidian 库："+config.Vault; return result; }
            try { Directory.CreateDirectory(config.SessionDir); } catch(Exception e) { result.Error="无法创建会话目录："+e.Message; return result; }
            var error=new StringBuilder();
            var info=new ProcessStartInfo {
                FileName=config.ResolveNode(),Arguments=Arguments(config,sessionId,message),WorkingDirectory=config.Vault,
                UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true,
                StandardOutputEncoding=new UTF8Encoding(false),StandardErrorEncoding=new UTF8Encoding(false),WindowStyle=ProcessWindowStyle.Hidden
            };
            int seconds=Math.Max(20,config.TimeoutSeconds);
            var watch=Stopwatch.StartNew();
            try {
                using(var process=new Process { StartInfo=info }) {
                    process.OutputDataReceived+=(s,e)=> { if(e.Data!=null) try { Parse(e.Data,result,progress); } catch(Exception) { } };
                    process.ErrorDataReceived+=(s,e)=> { if(e.Data!=null) error.AppendLine(e.Data); };
                    process.Start();
                    process.BeginOutputReadLine(); process.BeginErrorReadLine();
                    while(!process.WaitForExit(200)) {
                        if(cancelled!=null && cancelled()) { try { process.Kill(); } catch(Exception) { } result.Error="已取消。"; break; }
                        if(watch.Elapsed.TotalSeconds>seconds) { try { process.Kill(); } catch(Exception) { } result.Error="检索超时（"+seconds+" 秒）。"; break; }
                    }
                    try { process.WaitForExit(); } catch(Exception) { }
                    if(result.Error==null && process.ExitCode!=0) {
                        string detail=LastLine(error.ToString());
                        result.Error=detail.Length==0?"Pi 退出码 "+process.ExitCode+"。":detail;
                    }
                }
            } catch(Exception e) { result.Error="启动 Pi 失败："+e.Message; }
            if(result.Text==null && result.Error==null) {
                string detail=LastLine(error.ToString());
                result.Error=detail.Length==0?"Pi 没有返回内容。":detail;
            }
            return result;
        }
        static void Parse(string line,MiniAgentResult result,Action<string> progress) {
            var root=new JavaScriptSerializer { MaxJsonLength=32*1024*1024 }.DeserializeObject(line) as Dictionary<string,object>;
            if(root==null) return;
            string type=Str(root,"type");
            if(type=="tool_execution_start") {
                result.ToolCalls++;
                string tool=Str(root,"toolName");
                if(progress!=null) progress(tool=="read"?"正在阅读笔记…":"正在搜索笔记…");
            }
            if(type=="turn_end") {
                var message=Dict(root,"message"); if(message==null || Str(message,"role")!="assistant") return;
                string failure=Str(message,"errorMessage");
                if(Str(message,"stopReason")=="error" || !string.IsNullOrWhiteSpace(failure)) { result.Error=string.IsNullOrWhiteSpace(failure)?"模型返回错误。":failure; return; }
                string text=MessageText(message);
                if(!string.IsNullOrWhiteSpace(text)) { result.Text=text; result.Error=null; }
            }
            if(type=="agent_end") {
                var messages=root.ContainsKey("messages")?root["messages"] as IEnumerable:null;
                if(messages==null) return;
                foreach(object item in messages) {
                    var message=item as Dictionary<string,object>; if(message==null || Str(message,"role")!="assistant") continue;
                    string failure=Str(message,"errorMessage");
                    if(Str(message,"stopReason")=="error" || !string.IsNullOrWhiteSpace(failure)) { if(string.IsNullOrWhiteSpace(result.Text)) result.Error=string.IsNullOrWhiteSpace(failure)?"模型返回错误。":failure; continue; }
                    string text=MessageText(message);
                    if(!string.IsNullOrWhiteSpace(text)) { result.Text=text; result.Error=null; }
                }
            }
            if(type=="error") {
                string failure=Str(root,"errorMessage");
                if(!string.IsNullOrWhiteSpace(failure)) result.Error=failure;
            }
        }
        static string MessageText(Dictionary<string,object> message) {
            object content;
            if(!message.TryGetValue("content",out content) || content==null) return null;
            string plain=content as string; if(plain!=null) return plain;
            var items=content as IEnumerable; if(items==null) return null;
            var text=new StringBuilder();
            foreach(object item in items) {
                var part=item as Dictionary<string,object>; if(part==null) continue;
                if(Str(part,"type")=="text") text.Append(Str(part,"text"));
            }
            return text.ToString();
        }
        static Dictionary<string,object> Dict(Dictionary<string,object> source,string key) {
            object value; return source!=null && source.TryGetValue(key,out value)?value as Dictionary<string,object>:null;
        }
        static string Str(Dictionary<string,object> source,string key) {
            object value; return source!=null && source.TryGetValue(key,out value) && value!=null?value.ToString():null;
        }
        static string LastLine(string value) {
            string[] lines=(value??"").Split('\n');
            for(int i=lines.Length-1;i>=0;i--) { string line=lines[i].Trim(); if(line.Length>0) return line.Length>160?line.Substring(0,160)+"…":line; }
            return "";
        }
    }

    public sealed class MiniAgentChat {
        const string PromptText="问你的笔记…";
        readonly NotesWindow window;
        readonly MiniAgentConfig config;
        readonly Grid root;
        readonly StackPanel board;
        readonly TextBlock status,count;
        readonly List<MiniAgentMessage> messages=new List<MiniAgentMessage>();
        Button notesButton,linearButton,summaryButton,issuesButton,agentsButton;
        TextBox input;
        TextBlock placeholder,pendingText;
        Button send;
        string sessionId,filePath,title;
        DateTimeOffset created;
        int turns;
        bool busy,chatVisible,closing;

        MiniAgentChat(NotesWindow notesWindow,MiniAgentConfig miniConfig) {
            window=notesWindow; config=miniConfig;
            root=Private<Grid>(notesWindow,"root"); board=Private<StackPanel>(notesWindow,"board");
            status=Private<TextBlock>(notesWindow,"status"); count=Private<TextBlock>(notesWindow,"count");
        }
        public static void Attach(NotesWindow window,NotesStore store) { Attach(window,store,MiniAgentConfig.Load()); }
        public static void Attach(NotesWindow window,NotesStore store,MiniAgentConfig config) { new MiniAgentChat(window,config).Install(); }

        static T Private<T>(object owner,string name) where T:class {
            FieldInfo field=owner.GetType().GetField(name,BindingFlags.Instance|BindingFlags.NonPublic);
            if(field==null) throw new MissingFieldException(owner.GetType().FullName,name);
            return field.GetValue(owner) as T;
        }
        void Install() {
            BuildComposer();
            window.HomeView=delegate { ShowChat(true); };
            window.Closed+=(s,e)=>closing=true;
            window.Loaded+=(s,e)=> { if(chatVisible && input!=null) input.Focus(); };
            notesButton=FindButton(root,"NotesSource");
            if(notesButton!=null) {
                notesButton.Content=ChatIcon(); notesButton.ToolTip="聊天"; AutomationProperties.SetName(notesButton,"聊天");
                notesButton.Click+=(s,e)=>ShowChat(true);
            }
            linearButton=FindButton(root,"LinearSource"); summaryButton=FindButton(root,"SummarySource");
            issuesButton=FindButton(root,"IssuesSource"); agentsButton=FindButton(root,"AgentsSource");
            Hook(linearButton); Hook(summaryButton); Hook(issuesButton); Hook(agentsButton);
            ShowChat(false);
        }
        void Hook(Button button) { if(button!=null) button.Click+=(s,e)=>LeaveChat(); }

        void BuildComposer() {
            Border host=window.ChatComposerHost; if(host==null) return;
            var shell=new Border { Margin=new Thickness(22,0,22,10),Padding=new Thickness(5,3,4,3),CornerRadius=new CornerRadius(9),BorderThickness=new Thickness(1) };
            shell.SetResourceReference(Border.BackgroundProperty,"SurfaceBrush"); shell.SetResourceReference(Border.BorderBrushProperty,"LineBrush");
            var line=new Grid(); line.ColumnDefinitions.Add(new ColumnDefinition()); line.ColumnDefinitions.Add(new ColumnDefinition { Width=GridLength.Auto });
            var wrap=new Grid();
            input=new TextBox {
                FontSize=13,Padding=new Thickness(8,7,8,7),MinHeight=38,MaxHeight=110,MaxLength=8000,AcceptsReturn=true,TextWrapping=TextWrapping.Wrap,
                VerticalScrollBarVisibility=ScrollBarVisibility.Auto,HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled,
                Background=Brushes.Transparent,BorderThickness=new Thickness(0)
            };
            Id(input,"ChatInput","问你的笔记"); input.SetValue(TextBlock.LineHeightProperty,22.0);
            input.TextChanged+=(s,e)=>UpdateInput();
            input.PreviewKeyDown+=(s,e)=> { if(e.Key==Key.Enter && Keyboard.Modifiers!=ModifierKeys.Shift) { e.Handled=true; Send(); } };
            wrap.Children.Add(input);
            placeholder=Text(PromptText,12.5,"MutedBrush"); placeholder.Margin=new Thickness(10,8,0,0); placeholder.IsHitTestVisible=false; wrap.Children.Add(placeholder);
            line.Children.Add(wrap);
            send=IconButton("\uE724","发送","ChatSend",Send); send.Width=30; send.Height=30; send.Margin=new Thickness(3,0,0,0); send.VerticalAlignment=VerticalAlignment.Bottom; Grid.SetColumn(send,1); line.Children.Add(send);
            shell.Child=line; host.Child=shell; UpdateInput();
        }
        static System.Windows.Shapes.Path ChatIcon() {
            var path=new System.Windows.Shapes.Path {
                Data=Geometry.Parse("M3,3 H13 V10.4 H7.5 L4.4,13.2 V10.4 H3 Z"),
                StrokeThickness=1.45,StrokeStartLineCap=PenLineCap.Round,StrokeEndLineCap=PenLineCap.Round,StrokeLineJoin=PenLineJoin.Round,
                Stretch=Stretch.Uniform,Width=16,Height=16,SnapsToDevicePixels=true,IsHitTestVisible=false
            };
            path.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty,"TextBrush");
            return path;
        }
        void ShowChat(bool animate) {
            bool was=chatVisible; chatVisible=true;
            window.PreserveStatusOnSave=true; window.SetHeading("聊天");
            if(window.ChatComposerHost!=null) window.ChatComposerHost.Visibility=Visibility.Visible;
            if(notesButton!=null) { notesButton.ToolTip="聊天"; AutomationProperties.SetName(notesButton,"聊天"); }
            Highlight(notesButton,true); Highlight(linearButton,false); Highlight(summaryButton,false); Highlight(issuesButton,false); Highlight(agentsButton,false);
            Hide("LinearRefresh"); Hide("BriefRefresh"); Hide("AgentsRefresh");
            if(animate && !was) CardFlip.Run(root,board,RenderChat,false); else RenderChat();
            window.Dispatcher.BeginInvoke(new Action(delegate { if(input!=null) input.Focus(); }),DispatcherPriority.Input);
        }
        void LeaveChat() {
            if(!chatVisible) return;
            chatVisible=false;
            if(window.ChatComposerHost!=null) window.ChatComposerHost.Visibility=Visibility.Collapsed;
            if(notesButton!=null) { notesButton.ToolTip="聊天"; AutomationProperties.SetName(notesButton,"聊天"); }
            Highlight(notesButton,false);
        }
        void Hide(string id) { Button button=FindButton(root,id); if(button!=null) button.Visibility=Visibility.Collapsed; }

        void RenderChat() {
            board.Children.Clear();
            var page=new StackPanel(); AutomationProperties.SetAutomationId(page,"ChatRoot"); board.Children.Add(page);
            if(messages.Count>0) {
                var top=new Grid { Margin=new Thickness(0,0,0,8) };
                top.ColumnDefinitions.Add(new ColumnDefinition()); top.ColumnDefinitions.Add(new ColumnDefinition { Width=GridLength.Auto });
                var fresh=ActionButton("＋ 新对话","ChatNew",NewChat); fresh.FontSize=10.5; fresh.Padding=new Thickness(4,2,0,2); fresh.HorizontalAlignment=HorizontalAlignment.Right;
                fresh.SetResourceReference(Control.ForegroundProperty,"MutedBrush"); Grid.SetColumn(fresh,1); top.Children.Add(fresh); page.Children.Add(top);
            }
            if(messages.Count==0) page.Children.Add(EmptyState());
            else foreach(MiniAgentMessage message in messages) page.Children.Add(message.User?UserBubble(message):AssistantMessage(message));
            count.Text=turns==0?"":turns+" 个问题";
            if(!busy) status.Text="只查本地笔记";
            status.ToolTip=config.ChatFolder;
            var scroll=board.Parent as ScrollViewer;
            if(scroll!=null) window.Dispatcher.BeginInvoke(new Action(delegate { scroll.ScrollToEnd(); }),DispatcherPriority.Background);
        }
        FrameworkElement EmptyState() {
            var empty=new StackPanel { Margin=new Thickness(0,34,0,0),HorizontalAlignment=HorizontalAlignment.Center,MaxWidth=300 };
            AutomationProperties.SetAutomationId(empty,"ChatEmpty");
            var title=Text("问你的笔记",18,"TextBrush",true); title.TextAlignment=TextAlignment.Center; title.HorizontalAlignment=HorizontalAlignment.Center; empty.Children.Add(title);
            var hint=Text("只查 Obsidian 里的内容，回答会带上文件路径和行号。",11,"MutedBrush",false,new Thickness(0,8,0,0)); hint.TextAlignment=TextAlignment.Center; empty.Children.Add(hint);
            if(!Directory.Exists(config.Vault)) {
                var missing=Text("找不到库："+config.Vault+"\n请检查 "+MiniAgentConfig.FilePath,10.5,"MutedBrush",false,new Thickness(0,14,0,0)); missing.TextAlignment=TextAlignment.Center; empty.Children.Add(missing);
            } else if(config.ResolveCli()==null) {
                var missing=Text("没有找到 Pi。请安装 Pi，或在 "+MiniAgentConfig.FilePath+" 里填写 CliPath。",10.5,"MutedBrush",false,new Thickness(0,14,0,0)); missing.TextAlignment=TextAlignment.Center; empty.Children.Add(missing);
            }
            var saved=Text("对话记录保存在 Obsidian/"+config.Folder,10,"MutedBrush",false,new Thickness(0,16,0,0)); saved.TextAlignment=TextAlignment.Center; empty.Children.Add(saved);
            return empty;
        }
        Border UserBubble(MiniAgentMessage message) {
            var bubble=new Border { CornerRadius=new CornerRadius(8),Padding=new Thickness(13,10,13,10),Margin=new Thickness(0,0,0,10),MaxWidth=280,HorizontalAlignment=HorizontalAlignment.Right };
            bubble.SetResourceReference(Border.BackgroundProperty,"SelectionBrush");
            var body=Text(message.Text,12.5,"TextBrush"); body.LineHeight=21; bubble.Child=body;
            return bubble;
        }
        FrameworkElement AssistantMessage(MiniAgentMessage message) {
            var stack=new StackPanel { Margin=new Thickness(0,0,0,18) };
            var label=Text(message.Pending?"正在查笔记":"笔记助手",10,"MutedBrush"); label.Margin=new Thickness(0,0,0,4); stack.Children.Add(label);
            if(message.Pending) {
                pendingText=Text(message.Text,12.5,"MutedBrush"); pendingText.LineHeight=21; pendingText.Text=message.Text; stack.Children.Add(pendingText); return stack;
            }
            pendingText=null;
            stack.Children.Add(RichText(string.IsNullOrWhiteSpace(message.Text)?"（没有返回内容）":message.Text));
            if(message.Failed) {
                var failed=Text("这次没有完成",10,"MutedBrush",false,new Thickness(0,7,0,0)); failed.Foreground=Brushes.IndianRed; stack.Children.Add(failed);
            } else if(message.Citations>0) {
                var cited=Text("引用 "+message.Citations+" 处笔记",10,"MutedBrush",false,new Thickness(0,7,0,0));
                cited.ToolTip=message.CitationPaths==null?"":string.Join("\n",message.CitationPaths.ToArray()); stack.Children.Add(cited);
            } else if(!message.Text.Contains("没有在笔记中找到依据") && !message.Text.Contains("未找到")) {
                var missing=Text("未检测到笔记引用",10,"MutedBrush",false,new Thickness(0,7,0,0)); missing.Foreground=Brushes.IndianRed; stack.Children.Add(missing);
            }
            return stack;
        }
        TextBlock RichText(string value) {
            var block=Text("",12.5,"TextBrush"); block.LineHeight=21.5;
            string[] lines=(value??"").Replace("\r","").Split('\n');
            for(int i=0;i<lines.Length;i++) {
                string line=lines[i];
                if(i>0) block.Inlines.Add(new LineBreak());
                if(line.Trim().Length==0) continue;
                string trimmed=line.TrimStart();
                if(trimmed.StartsWith("#")) {
                    string heading=trimmed.TrimStart('#').Trim();
                    var run=new Run(heading) { FontWeight=FontWeights.SemiBold };
                    block.Inlines.Add(run); continue;
                }
                if(trimmed.StartsWith("- ") || trimmed.StartsWith("* ")) {
                    block.Inlines.Add(new Run("• ")); line=trimmed.Substring(2); trimmed=line;
                }
                foreach(string part in MiniAgentStore.InlineParts(line)) {
                    if(part.Length==0) continue;
                    if(part.Length>3 && part.StartsWith("**") && part.EndsWith("**")) block.Inlines.Add(new Run(part.Substring(2,part.Length-4)) { FontWeight=FontWeights.SemiBold });
                    else if(part.Length>1 && part.StartsWith("`") && part.EndsWith("`")) {
                        var code=new Run(part.Substring(1,part.Length-2)) { FontFamily=new FontFamily("Consolas"),FontSize=11.5 };
                        code.SetResourceReference(TextElement.ForegroundProperty,"BodyBrush"); block.Inlines.Add(code);
                    } else block.Inlines.Add(new Run(part));
                }
            }
            return block;
        }

        void Send() {
            if(busy || input==null) return;
            string text=input.Text.Trim();
            if(text.Length==0) return;
            if(!Directory.Exists(config.Vault)) { messages.Add(new MiniAgentMessage { User=false,Failed=true,Text="找不到 Obsidian 库："+config.Vault+"\n请检查 "+MiniAgentConfig.FilePath }); RenderChat(); return; }
            if(config.ResolveCli()==null) { messages.Add(new MiniAgentMessage { User=false,Failed=true,Text="没有找到 Pi。请安装 Pi，或在 "+MiniAgentConfig.FilePath+" 里填写 CliPath。" }); RenderChat(); return; }
            if(filePath==null) {
                sessionId=Guid.NewGuid().ToString();
                title=MiniAgentStore.SafeTitle(text,28);
                created=DateTimeOffset.Now;
                filePath=Path.Combine(config.ChatFolder,MiniAgentStore.FileName(DateTime.Now,title));
            }
            var user=new MiniAgentMessage { User=true,Text=text };
            var pending=new MiniAgentMessage { Pending=true,Text="正在查笔记…" };
            messages.Add(user); messages.Add(pending); turns++;
            input.Text=""; busy=true; UpdateInput(); RenderChat(); SaveChat();
            string question=text, session=sessionId;
            status.Text="正在查笔记…";
            ThreadPool.QueueUserWorkItem(delegate {
                MiniAgentResult result=MiniAgentRunner.Run(config,session,question,delegate(string progress) {
                    if(progress==null) return;
                    window.Dispatcher.BeginInvoke(new Action(delegate {
                        if(closing) return;
                        if(pendingText!=null) pendingText.Text=progress;
                        status.Text=progress;
                    }));
                },delegate { return closing; });
                window.Dispatcher.BeginInvoke(new Action(delegate {
                    if(closing) return;
                    Finish(pending,result);
                }));
            });
        }
        void Finish(MiniAgentMessage pending,MiniAgentResult result) {
            messages.Remove(pending);
            busy=false;
            if(result.Error!=null) {
                messages.Add(new MiniAgentMessage { User=false,Failed=true,Text=result.Error });
                status.Text="检索没有完成";
            } else {
                List<string> paths=MiniAgentStore.Citations(result.Text);
                messages.Add(new MiniAgentMessage { User=false,Text=result.Text,Citations=paths.Count,CitationPaths=paths });
                status.Text="只查本地笔记";
            }
            UpdateInput(); SaveChat(); RenderChat();
            if(input!=null) input.Focus();
        }
        void NewChat() {
            if(busy) return;
            messages.Clear(); turns=0; sessionId=null; filePath=null; title=null; created=default(DateTimeOffset);
            RenderChat(); if(input!=null) input.Focus();
        }
        void SaveChat() {
            if(filePath==null || sessionId==null) return;
            try { NotesStore.WriteAtomic(filePath,MiniAgentStore.Markdown(title,created,sessionId,config.Model,messages),false); }
            catch(Exception e) { status.Text="记录保存失败"; status.ToolTip=e.Message; }
        }
        void UpdateInput() {
            if(send==null || input==null) return;
            bool has=input.Text.Trim().Length>0;
            send.IsEnabled=!busy && has;
            if(placeholder!=null) placeholder.Visibility=input.Text.Length==0?Visibility.Visible:Visibility.Collapsed;
        }

        static TextBlock Text(string value,double size=13,string brush="TextBrush",bool bold=false,Thickness? margin=null) {
            var text=new TextBlock { Text=value??"",FontFamily=new FontFamily("Microsoft YaHei UI"),FontSize=size,FontWeight=bold?FontWeights.SemiBold:FontWeights.Normal,TextWrapping=TextWrapping.Wrap,Margin=margin??new Thickness(0) };
            text.SetResourceReference(TextBlock.ForegroundProperty,brush); return text;
        }
        static void Id(DependencyObject control,string id,string name=null) { AutomationProperties.SetAutomationId(control,id); if(name!=null) AutomationProperties.SetName(control,name); }
        Button ActionButton(string text,string id,Action action) {
            var button=new Button { Content=text }; Id(button,id,text); button.Click+=(s,e)=>action(); return button;
        }
        Button IconButton(string glyph,string label,string id,Action action) {
            var button=ActionButton("",id,action); button.Content=new TextBlock { Text=glyph,FontFamily=new FontFamily("Segoe MDL2 Assets"),FontSize=14,VerticalAlignment=VerticalAlignment.Center };
            button.Width=30; button.Height=30; button.Padding=new Thickness(5); button.ToolTip=label; AutomationProperties.SetName(button,label); return button;
        }
        static void Highlight(Button button,bool active) { if(button!=null) button.SetResourceReference(Control.BackgroundProperty,active?"SelectionBrush":"CanvasBrush"); }
        static Button FindButton(DependencyObject node,string id) {
            if(node==null) return null;
            var button=node as Button;
            if(button!=null && AutomationProperties.GetAutomationId(button)==id) return button;
            int children=VisualTreeHelper.GetChildrenCount(node);
            for(int i=0;i<children;i++) { Button found=FindButton(VisualTreeHelper.GetChild(node,i),id); if(found!=null) return found; }
            return null;
        }
    }
}
