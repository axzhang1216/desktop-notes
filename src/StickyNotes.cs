using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace DesktopNotes {
    public sealed class StickyAttachment {
        public string Extension { get; set; }
        public byte[] Bytes { get; set; }
        public BitmapSource Preview { get; set; }
    }

    public sealed class StickyNoteItem {
        public string Title { get; set; }
        public string Body { get; set; }
        public string Path { get; set; }
        public DateTimeOffset Created { get; set; }
        public List<string> Tags { get; set; }
        public int Images { get; set; }
        public StickyNoteItem() { Tags=new List<string>(); }
    }

    public sealed class StickySuggestion {
        public string Text { get; set; }
        public string Answer { get; set; }
        public int Count { get; set; }
        public DateTimeOffset Updated { get; set; }
    }

    public static class StickyNoteStore {
        public const string DefaultFolder="便签";
        public const int MaxTitleLength=50;
        static readonly Regex TagPattern=new Regex(@"(?:^|[\s(（])#([\p{L}\p{N}_-]{1,30})",RegexOptions.Compiled);
        static readonly Regex ImagePattern=new Regex(@"!\[[^\]]*\]\(([^)]+)\)",RegexOptions.Compiled);

        public static string Folder(MiniAgentConfig config) {
            string name=config==null || string.IsNullOrWhiteSpace(config.NotesFolder)?DefaultFolder:config.NotesFolder.Trim();
            return Path.Combine(config.Vault,name);
        }

        public static string TitleFrom(string text) {
            string line=(text??"").Replace("\r","").Split('\n').FirstOrDefault(value=>!string.IsNullOrWhiteSpace(value));
            if(line==null) return "便签";
            line=line.Trim().TrimStart('#').Trim();
            line=Regex.Replace(line,@"\s+"," ");
            if(line.Length==0) return "便签";
            return line.Length>MaxTitleLength?line.Substring(0,MaxTitleLength).Trim()+"…":line;
        }

        public static string BodyFrom(string text,string title) {
            string[] lines=(text??"").Replace("\r","").Split('\n');
            int first=-1;
            for(int i=0;i<lines.Length;i++) if(!string.IsNullOrWhiteSpace(lines[i])) { first=i; break; }
            if(first<0) return "";
            int length=lines.Length-first;
            var rest=new string[length];
            Array.Copy(lines,first,rest,0,length);
            string body=string.Join("\n",rest).Trim();
            string firstLine=lines[first].Trim().TrimStart('#').Trim();
            if(firstLine==title) {
                int cut=body.IndexOf('\n');
                body=cut<0?"":body.Substring(cut+1).Trim();
            }
            return body;
        }

        public static string DisplayTitle(DateTimeOffset now,string title) {
            return now.ToString("yyyy-MM-dd",CultureInfo.InvariantCulture)+" "+TitleFrom(title);
        }

        public static string SafeFileName(string value) {
            string safe=value??"";
            foreach(char invalid in Path.GetInvalidFileNameChars()) safe=safe.Replace(invalid,'-');
            safe=safe.Replace('/','-').Replace('\\','-').Trim().Trim('.');
            return safe.Length==0?"便签":safe;
        }

        public static string FileName(DateTimeOffset now,string title) {
            string name=SafeFileName(TitleFrom(title));
            return now.ToString("yyyy-MM-dd HHmm",CultureInfo.InvariantCulture)+" "+name+".md";
        }

        public static List<string> Tags(string text) {
            var tags=new List<string>();
            if(string.IsNullOrEmpty(text)) return tags;
            foreach(Match match in TagPattern.Matches(text)) {
                string tag=match.Groups[1].Value.Trim();
                if(tag.Length>0 && !tags.Contains(tag,StringComparer.OrdinalIgnoreCase)) tags.Add(tag);
            }
            return tags;
        }

        static string Yaml(string value) {
            return "\""+(value??"").Replace("\\","\\\\").Replace("\"","\\\"").Replace("\r"," ").Replace("\n"," ")+"\"";
        }

        public static string Markdown(DateTimeOffset now,string title,string body,List<string> tags,List<string> images) {
            string shown=DisplayTitle(now,title);
            var text=new StringBuilder();
            text.AppendLine("---");
            text.AppendLine("title: "+Yaml(shown));
            text.AppendLine("created: "+now.ToString("o",CultureInfo.InvariantCulture));
            if(tags!=null && tags.Count>0) {
                text.AppendLine("tags:");
                foreach(string tag in tags) text.AppendLine("  - "+tag);
            }
            text.AppendLine("---");
            text.AppendLine();
            text.AppendLine("# "+shown);
            if(!string.IsNullOrWhiteSpace(body)) { text.AppendLine(); text.AppendLine(body.Trim()); }
            if(images!=null) for(int i=0;i<images.Count;i++) {
                text.AppendLine();
                text.AppendLine("![图片 "+(i+1)+"](attachments/"+images[i]+")");
            }
            return text.ToString();
        }

        public static string Save(MiniAgentConfig config,string text,List<StickyAttachment> attachments,out string path) {
            if(config==null) throw new ArgumentNullException("config");
            DateTimeOffset now=DateTimeOffset.Now;
            string title=TitleFrom(text);
            string body=BodyFrom(text,title);
            string folder=Folder(config);
            string attachmentsFolder=Path.Combine(folder,"attachments");
            List<string> imageNames=new List<string>();
            if(attachments!=null && attachments.Count>0) {
                Directory.CreateDirectory(attachmentsFolder);
                for(int i=0;i<attachments.Count;i++) {
                    StickyAttachment image=attachments[i];
                    if(image==null || image.Bytes==null || image.Bytes.Length==0) continue;
                    string extension=string.IsNullOrWhiteSpace(image.Extension)?"png":image.Extension.Trim().TrimStart('.').ToLowerInvariant();
                    if(!Regex.IsMatch(extension,@"^[a-z0-9]{1,5}$")) extension="png";
                    string name=now.ToString("yyyyMMdd-HHmmss",CultureInfo.InvariantCulture)+"-"+i.ToString("00")+"."+extension;
                    int suffix=2;
                    while(File.Exists(Path.Combine(attachmentsFolder,name))) name=now.ToString("yyyyMMdd-HHmmss",CultureInfo.InvariantCulture)+"-"+i.ToString("00")+"-"+suffix+++"."+extension;
                    File.WriteAllBytes(Path.Combine(attachmentsFolder,name),image.Bytes);
                    imageNames.Add(name);
                }
            }
            string target=Path.Combine(folder,FileName(now,title));
            Directory.CreateDirectory(folder);
            int duplicate=2;
            while(File.Exists(target)) {
                string name=Path.GetFileNameWithoutExtension(FileName(now,title))+" ("+duplicate+++").md";
                target=Path.Combine(folder,name);
            }
            NotesStore.WriteAtomic(target,Markdown(now,title,body,Tags(text),imageNames),false);
            path=target;
            return target;
        }

        public static List<StickyNoteItem> Recent(MiniAgentConfig config,int max) {
            var notes=new List<StickyNoteItem>();
            string folder=Folder(config);
            if(!Directory.Exists(folder)) return notes;
            foreach(string file in Directory.GetFiles(folder,"*.md").OrderByDescending(File.GetLastWriteTimeUtc).Take(Math.Max(1,max))) {
                try { notes.Add(Read(file)); } catch(Exception) { }
            }
            return notes;
        }

        public static StickyNoteItem Read(string file) {
            string text=File.ReadAllText(file,Encoding.UTF8);
            var item=new StickyNoteItem { Path=file,Created=new DateTimeOffset(File.GetLastWriteTime(file)),Body=text };
            string[] lines=text.Replace("\r","").Split('\n');
            int start=0,end=-1;
            if(lines.Length>0 && lines[0].Trim()=="---") {
                for(int i=1;i<lines.Length;i++) if(lines[i].Trim()=="---") { end=i; break; }
                if(end>0) {
                    for(int i=1;i<end;i++) {
                        string line=lines[i];
                        if(line.StartsWith("title:",StringComparison.OrdinalIgnoreCase)) item.Title=Unquote(line.Substring(6).Trim());
                        else if(line.StartsWith("created:",StringComparison.OrdinalIgnoreCase)) {
                            DateTimeOffset created;
                            if(DateTimeOffset.TryParse(Unquote(line.Substring(8).Trim()),CultureInfo.InvariantCulture,DateTimeStyles.RoundtripKind,out created)) item.Created=created;
                        } else if(line.TrimStart().StartsWith("- ",StringComparison.Ordinal)) item.Tags.Add(line.Trim().Substring(2).Trim());
                    }
                    start=end+1;
                }
            }
            var body=new List<string>();
            bool skippedHeading=false;
            for(int i=start;i<lines.Length;i++) {
                string line=lines[i];
                if(!skippedHeading && line.StartsWith("# ",StringComparison.Ordinal)) { skippedHeading=true; if(string.IsNullOrWhiteSpace(item.Title)) item.Title=line.Substring(2).Trim(); continue; }
                body.Add(line);
            }
            item.Body=string.Join("\n",body).Trim();
            foreach(string tag in Tags(item.Body)) if(!item.Tags.Contains(tag,StringComparer.OrdinalIgnoreCase)) item.Tags.Add(tag);
            item.Images=ImagePattern.Matches(item.Body).Count;
            if(string.IsNullOrWhiteSpace(item.Title)) item.Title=Path.GetFileNameWithoutExtension(file);
            return item;
        }

        static string Unquote(string value) {
            value=(value??"").Trim();
            if(value.Length>=2 && value[0]=='"' && value[value.Length-1]=='"') value=value.Substring(1,value.Length-2).Replace("\\\"","\"").Replace("\\\\","\\");
            return value;
        }

        public static List<StickySuggestion> RecentTags(MiniAgentConfig config,int max) {
            var map=new Dictionary<string,StickySuggestion>(StringComparer.OrdinalIgnoreCase);
            foreach(StickyNoteItem note in Recent(config,Math.Max(max*5,30))) {
                foreach(string tag in note.Tags) {
                    StickySuggestion item;
                    if(!map.TryGetValue(tag,out item)) { item=new StickySuggestion { Text=tag,Updated=note.Created }; map[tag]=item; }
                    item.Count++;
                    if(note.Created>item.Updated) item.Updated=note.Created;
                }
            }
            return map.Values.OrderByDescending(x=>x.Updated).ThenByDescending(x=>x.Count).Take(Math.Max(1,max)).ToList();
        }

        // 从 MiniAgent 的对话记录里挑出问过不止一次或最近问过的问题，点击直接看当时的回答。
        public static List<StickySuggestion> Questions(MiniAgentConfig config,int max) {
            var map=new Dictionary<string,StickySuggestion>(StringComparer.OrdinalIgnoreCase);
            if(config==null || !Directory.Exists(config.ChatFolder)) return new List<StickySuggestion>();
            foreach(string file in Directory.GetFiles(config.ChatFolder,"*.md").OrderByDescending(File.GetLastWriteTimeUtc).Take(120)) {
                try {
                    DateTimeOffset updated=new DateTimeOffset(File.GetLastWriteTime(file));
                    foreach(var pair in ParsePairs(File.ReadAllText(file,Encoding.UTF8))) {
                        string key=Normalize(pair.Key);
                        if(key.Length<2) continue;
                        StickySuggestion item;
                        if(!map.TryGetValue(key,out item)) { item=new StickySuggestion { Text=pair.Key.Trim(),Updated=updated }; map[key]=item; }
                        item.Count++;
                        if(updated>=item.Updated) { item.Updated=updated; item.Answer=pair.Value; }
                    }
                } catch(Exception) { }
            }
            return map.Values.OrderByDescending(x=>x.Count).ThenByDescending(x=>x.Updated).Take(Math.Max(1,max)).ToList();
        }

        static IEnumerable<KeyValuePair<string,string>> ParsePairs(string markdown) {
            string[] lines=(markdown??"").Replace("\r","").Split('\n');
            for(int i=0;i<lines.Length;i++) {
                if(lines[i].Trim()!="## 你") continue;
                var question=new StringBuilder();
                int j=i+1;
                for(;j<lines.Length && lines[j].Trim()!="## 助手";j++) question.AppendLine(lines[j]);
                if(j>=lines.Length) break;
                var answer=new StringBuilder();
                int k=j+1;
                for(;k<lines.Length && !lines[k].StartsWith("## ",StringComparison.Ordinal);k++) answer.AppendLine(lines[k]);
                string query=question.ToString().Trim(),reply=answer.ToString().Trim();
                if(query.Length>0 && reply.Length>0) yield return new KeyValuePair<string,string>(query,reply);
                i=k-1;
            }
        }

        public static string Normalize(string question) {
            string value=Regex.Replace((question??"").Trim(),@"\s+"," ").Trim().TrimEnd('？','?','。','.','!','！').ToLowerInvariant();
            return value.Length>160?value.Substring(0,160):value;
        }

        public static string PlainText(string markdown) {
            string value=(markdown??"").Replace("\r","");
            value=Regex.Replace(value,@"!\[[^\]]*\]\([^)]+\)","[图片]");
            value=Regex.Replace(value,@"`([^`]*)`","$1");
            value=Regex.Replace(value,@"\*\*([^*]+)\*\*","$1");
            value=Regex.Replace(value,@"^#{1,6}\s*","",RegexOptions.Multiline);
            return value.Trim();
        }
    }

    public static class StickyNotesView {
        public static TextBlock Markdown(string value) {
            var block=Text("",12.5,"TextBrush"); block.LineHeight=21.5;
            string[] lines=(value??"").Replace("\r","").Split('\n');
            for(int i=0;i<lines.Length;i++) {
                string line=lines[i];
                if(i>0) block.Inlines.Add(new LineBreak());
                if(line.Trim().Length==0) continue;
                string trimmed=line.TrimStart();
                if(trimmed.StartsWith("#")) {
                    block.Inlines.Add(new Run(trimmed.TrimStart('#').Trim()) { FontWeight=FontWeights.SemiBold });
                    continue;
                }
                if(trimmed.StartsWith("- ") || trimmed.StartsWith("* ")) { block.Inlines.Add(new Run("• ")); line=trimmed.Substring(2); trimmed=line; }
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

        public static TextBlock Text(string value,double size=13,string brush="TextBrush",bool bold=false,Thickness? margin=null) {
            var text=new TextBlock { Text=value??"",FontFamily=new FontFamily("Microsoft YaHei UI"),FontSize=size,FontWeight=bold?FontWeights.SemiBold:FontWeights.Normal,TextWrapping=TextWrapping.Wrap,Margin=margin??new Thickness(0) };
            text.SetResourceReference(TextBlock.ForegroundProperty,brush); return text;
        }
    }
}

namespace DesktopNotes {
    public sealed class StickyNotesPage {
        readonly NotesWindow window;
        readonly MiniAgentConfig config;
        readonly Grid root;
        readonly StackPanel board;
        readonly TextBlock status,count;
        readonly List<StickyAttachment> attachments=new List<StickyAttachment>();
        readonly StackPanel pagePanel=new StackPanel();
        readonly WrapPanel imagesPanel=new WrapPanel { Margin=new Thickness(0,8,0,0) };
        readonly StackPanel suggestionsPanel=new StackPanel();
        readonly StackPanel resultPanel=new StackPanel();
        Button sourceButton,refresh,send;
        TextBox input;
        Action previousHome;
        List<StickySuggestion> allQuestions=new List<StickySuggestion>();
        List<StickySuggestion> allTags=new List<StickySuggestion>();
        int batch;
        bool page;

        public static StickyNotesPage Current { get; private set; }

        StickyNotesPage(NotesWindow notesWindow,MiniAgentConfig miniConfig) {
            window=notesWindow; config=miniConfig;
            root=Private<Grid>(notesWindow,"root"); board=Private<StackPanel>(notesWindow,"board");
            status=Private<TextBlock>(notesWindow,"status"); count=Private<TextBlock>(notesWindow,"count");
        }

        public static void Attach(NotesWindow window,NotesStore store,MiniAgentConfig config) { new StickyNotesPage(window,config).Install(); }

        static T Private<T>(object owner,string name) where T:class {
            FieldInfo field=owner.GetType().GetField(name,BindingFlags.Instance|BindingFlags.NonPublic);
            if(field==null) throw new MissingFieldException(owner.GetType().FullName,name);
            return field.GetValue(owner) as T;
        }

        void Install() {
            Current=this;
            previousHome=window.HomeView;
            var tools=FindToolbar(root);
            if(tools!=null) {
                sourceButton=IconButton(StickyIcon(),"便签","StickySource",Show);
                int at=Index(tools,"NotesSource"); if(at<0) at=0;
                tools.Children.Insert(at,sourceButton);
            }
            foreach(string id in new[]{"NotesSource","LinearSource","SummarySource","IssuesSource","AgentsSource"}) Hook(id,Leave);
            BuildPage();
            if(config==null || string.IsNullOrWhiteSpace(config.Vault) || !Directory.Exists(config.Vault)) {
                status.Text="找不到 Obsidian 库，便签无法保存";
            }
        }

        void Hook(string id,Action action) {
            Button button=FindButton(root,id);
            if(button!=null) button.Click+=(s,e)=>action();
        }

        string FolderName() {
            return config==null || string.IsNullOrWhiteSpace(config.NotesFolder)?StickyNoteStore.DefaultFolder:config.NotesFolder.Trim();
        }

        void BuildPage() {
            pagePanel.Children.Clear();
            var heading=new Grid { Margin=new Thickness(0,0,0,12) };
            heading.ColumnDefinitions.Add(new ColumnDefinition());
            heading.ColumnDefinitions.Add(new ColumnDefinition { Width=GridLength.Auto });
            var title=StickyNotesView.Text("写一条便签",15,"TextBrush",true); title.VerticalAlignment=VerticalAlignment.Center; heading.Children.Add(title);
            var hint=StickyNotesView.Text("发到 Obsidian/"+FolderName(),10.5,"MutedBrush"); hint.VerticalAlignment=VerticalAlignment.Center; hint.Margin=new Thickness(8,1,0,0); Grid.SetColumn(hint,1); heading.Children.Add(hint);
            pagePanel.Children.Add(heading);

            var shell=new Border { CornerRadius=new CornerRadius(9),Padding=new Thickness(4,4,4,7),BorderThickness=new Thickness(1) };
            shell.SetResourceReference(Border.BackgroundProperty,"SurfaceBrush"); shell.SetResourceReference(Border.BorderBrushProperty,"LineBrush");
            var stack=new StackPanel(); shell.Child=stack;
            input=new TextBox {
                FontSize=13,Padding=new Thickness(9,8,9,8),MinHeight=118,MaxHeight=220,MaxLength=12000,AcceptsReturn=true,TextWrapping=TextWrapping.Wrap,
                VerticalScrollBarVisibility=ScrollBarVisibility.Auto,HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled,
                Background=Brushes.Transparent,BorderThickness=new Thickness(0),AllowDrop=true
            };
            Id(input,"StickyInput","便签内容");
            input.SetValue(TextBlock.LineHeightProperty,22.0);
            input.TextChanged+=(s,e)=>UpdateSendState();
            input.PreviewKeyDown+=(s,e)=> {
                if(e.Key==Key.V && Keyboard.Modifiers==ModifierKeys.Control) { if(PasteImages()) e.Handled=true; }
                if(e.Key==Key.Enter && Keyboard.Modifiers==ModifierKeys.Control) { e.Handled=true; Send(); }
            };
            input.PreviewDragOver+=(s,e)=> { if(HasImages(e.Data)) { e.Effects=DragDropEffects.Copy; e.Handled=true; } };
            input.Drop+=(s,e)=> { if(AddDroppedImages(e.Data)) e.Handled=true; };
            stack.Children.Add(input);
            imagesPanel.Visibility=Visibility.Collapsed; stack.Children.Add(imagesPanel);

            var actions=new Grid { Margin=new Thickness(5,5,3,0) };
            actions.ColumnDefinitions.Add(new ColumnDefinition());
            actions.ColumnDefinitions.Add(new ColumnDefinition { Width=GridLength.Auto });
            actions.ColumnDefinitions.Add(new ColumnDefinition { Width=GridLength.Auto });
            var hint2=StickyNotesView.Text("图片可粘贴或拖入，Ctrl+Enter 发送",10,"MutedBrush"); hint2.VerticalAlignment=VerticalAlignment.Center; hint2.TextTrimming=TextTrimming.CharacterEllipsis; actions.Children.Add(hint2);
            var clear=ButtonFor("清空当前对话框","StickyClear",Clear); clear.Padding=new Thickness(10,6,10,6); clear.FontSize=11; clear.SetResourceReference(Control.ForegroundProperty,"MutedBrush"); Grid.SetColumn(clear,1); actions.Children.Add(clear);
            send=ButtonFor("发送","StickySend",Send,true); send.MinWidth=68; send.Padding=new Thickness(13,7,13,7); Grid.SetColumn(send,2); actions.Children.Add(send);
            stack.Children.Add(actions);
            shell.AllowDrop=true;
            shell.PreviewDragOver+=(s,e)=> { if(HasImages(e.Data)) { e.Effects=DragDropEffects.Copy; e.Handled=true; } };
            shell.Drop+=(s,e)=> { if(AddDroppedImages(e.Data)) e.Handled=true; };
            pagePanel.Children.Add(shell);

            var suggestHead=new Grid { Margin=new Thickness(0,19,0,7) };
            suggestHead.ColumnDefinitions.Add(new ColumnDefinition()); suggestHead.ColumnDefinitions.Add(new ColumnDefinition { Width=GridLength.Auto });
            var suggestTitle=StickyNotesView.Text("常问与最近标签",11,"MutedBrush"); suggestTitle.VerticalAlignment=VerticalAlignment.Center; suggestHead.Children.Add(suggestTitle);
            refresh=ButtonFor("换一批","StickyRefresh",Refresh); refresh.Padding=new Thickness(8,3,4,3); refresh.FontSize=10.5; refresh.SetResourceReference(Control.ForegroundProperty,"MutedBrush"); Grid.SetColumn(refresh,1); suggestHead.Children.Add(refresh);
            pagePanel.Children.Add(suggestHead);
            pagePanel.Children.Add(suggestionsPanel);
            pagePanel.Children.Add(resultPanel);
            UpdateSendState();
        }

        Button IconButton(UIElement icon,string label,string id,Action action) {
            var button=new Button { Width=32,Height=32,Padding=new Thickness(6),ToolTip=label,Content=icon };
            Id(button,id,label); button.Click+=(s,e)=>action(); return button;
        }

        Button ButtonFor(string text,string id,Action action,bool primary=false) {
            var button=new Button { Content=text,HorizontalContentAlignment=HorizontalAlignment.Center };
            Id(button,id,text); button.Click+=(s,e)=>action();
            if(primary) { button.SetResourceReference(Control.BackgroundProperty,"TextBrush"); button.SetResourceReference(Control.ForegroundProperty,"CanvasBrush"); }
            return button;
        }

        static void Id(DependencyObject control,string id,string name=null) { AutomationProperties.SetAutomationId(control,id); if(name!=null) AutomationProperties.SetName(control,name); }
        static void Highlight(Button button,bool active) { if(button!=null) button.SetResourceReference(Control.BackgroundProperty,active?"SelectionBrush":"CanvasBrush"); }

        void Show() {
            LeaveOthers();
            page=true;
            window.HomeView=delegate { if(page) Render(); };
            window.PreserveStatusOnSave=true;
            window.SetHeading("便签");
            if(window.ChatComposerHost!=null) window.ChatComposerHost.Visibility=Visibility.Collapsed;
            Highlight(sourceButton,true);
            Highlight(FindButton(root,"NotesSource"),false);
            CardFlip.Run(root,board,Render,false);
            window.Dispatcher.BeginInvoke(new Action(delegate { if(input!=null) input.Focus(); }),DispatcherPriority.Input);
        }

        void Leave() {
            if(!page) return;
            page=false;
            window.HomeView=previousHome;
            Highlight(sourceButton,false);
        }

        void LeaveOthers() {
            if(MiniAgentChat.Current!=null) MiniAgentChat.Current.LeaveChat();
            if(ApocalypseIntegration.Current!=null) ApocalypseIntegration.Current.Leave();
            if(OrcaIntegration.Current!=null) OrcaIntegration.Current.LeavePage();
            LeaveLinearMode();
        }

        void LeaveLinearMode() {
            object integration=window.Tag; if(integration==null) return;
            FieldInfo field=integration.GetType().GetField("linearMode",BindingFlags.Instance|BindingFlags.NonPublic);
            if(field==null || !(bool)field.GetValue(integration)) return;
            field.SetValue(integration,false);
            MethodInfo action=integration.GetType().GetMethod("SetModeButtons",BindingFlags.Instance|BindingFlags.NonPublic);
            if(action!=null) action.Invoke(integration,null);
        }

        void Render() {
            if(!page) return;
            board.Children.Clear();
            board.Children.Add(pagePanel);
            int notes=0;
            try { string folder=StickyNoteStore.Folder(config); if(Directory.Exists(folder)) notes=Directory.GetFiles(folder,"*.md").Length; } catch(Exception) { }
            count.Text=notes+" 条便签";
            status.Text="便签 · 保存在 Obsidian";
            status.ToolTip=config==null?"":StickyNoteStore.Folder(config);
            refresh.Visibility=Visibility.Visible;
            UpdateSuggestions();
            window.Dispatcher.BeginInvoke(new Action(delegate { if(input!=null) input.Focus(); }),DispatcherPriority.Input);
        }

        void UpdateSuggestions() {
            suggestionsPanel.Children.Clear();
            if(config==null || string.IsNullOrWhiteSpace(config.Vault)) {
                suggestionsPanel.Children.Add(StickyNotesView.Text("先配置 Obsidian 库，再记录便签。",11,"MutedBrush",false,new Thickness(0,4,0,0)));
                refresh.Visibility=Visibility.Collapsed;
                return;
            }
            allQuestions=StickyNoteStore.Questions(config,60);
            allTags=StickyNoteStore.RecentTags(config,60);
            int questionPages=Math.Max(1,(allQuestions.Count+5)/6);
            int tagPages=Math.Max(1,(allTags.Count+7)/8);
            int pages=Math.Max(questionPages,tagPages);
            if(batch>=pages) batch=0;
            int questionStart=batch*6,tagStart=batch*8;
            var questions=allQuestions.Skip(questionStart).Take(6).ToList();
            var tags=allTags.Skip(tagStart).Take(8).ToList();
            if(questions.Count>0) {
                suggestionsPanel.Children.Add(StickyNotesView.Text("常问的问题 · 点击看回答",10.5,"MutedBrush",false,new Thickness(0,1,0,5)));
                foreach(StickySuggestion question in questions) suggestionsPanel.Children.Add(QuestionButton(question,questionStart++));
            }
            if(tags.Count>0) {
                suggestionsPanel.Children.Add(StickyNotesView.Text("最近发送的标签",10.5,"MutedBrush",false,new Thickness(0,questions.Count>0?10:1,0,5)));
                var wrap=new WrapPanel(); suggestionsPanel.Children.Add(wrap);
                for(int i=0;i<tags.Count;i++) wrap.Children.Add(TagButton(tags[i],tagStart+i));
            }
            if(questions.Count==0 && tags.Count==0) suggestionsPanel.Children.Add(StickyNotesView.Text("多聊几次，或给便签加上 #标签，这里就会出现可点的常用问题。",11,"MutedBrush",false,new Thickness(0,4,0,0)));
            refresh.Visibility=pages>1?Visibility.Visible:Visibility.Collapsed;
        }

        FrameworkElement QuestionButton(StickySuggestion question,int index) {
            var button=new Button { HorizontalContentAlignment=HorizontalAlignment.Stretch,HorizontalAlignment=HorizontalAlignment.Stretch,Padding=new Thickness(11,8,9,8),Margin=new Thickness(0,0,0,5) };
            button.SetResourceReference(Control.BackgroundProperty,"SurfaceBrush"); AutomationProperties.SetAutomationId(button,"StickyQuestion-"+index);
            AutomationProperties.SetName(button,"查看回答："+question.Text);
            var grid=new Grid(); grid.ColumnDefinitions.Add(new ColumnDefinition()); grid.ColumnDefinitions.Add(new ColumnDefinition { Width=GridLength.Auto });
            string shown=question.Text.Length>42?question.Text.Substring(0,42)+"…":question.Text;
            var text=StickyNotesView.Text(shown,11.5,"TextBrush"); text.TextTrimming=TextTrimming.CharacterEllipsis; text.TextWrapping=TextWrapping.NoWrap; text.ToolTip=question.Text; grid.Children.Add(text);
            if(question.Count>1) { var times=StickyNotesView.Text("问过 "+question.Count+" 次",9.5,"MutedBrush"); times.VerticalAlignment=VerticalAlignment.Center; times.Margin=new Thickness(8,0,0,0); Grid.SetColumn(times,1); grid.Children.Add(times); }
            button.Content=grid; button.Click+=(s,e)=>ShowAnswer(question);
            return button;
        }

        FrameworkElement TagButton(StickySuggestion tag,int index) {
            var button=new Button { Content="#"+tag.Text,Padding=new Thickness(9,4,9,4),Margin=new Thickness(0,0,5,5),FontSize=10.5 };
            button.SetResourceReference(Control.BackgroundProperty,"SelectionBrush");
            AutomationProperties.SetAutomationId(button,"StickyTag-"+index); AutomationProperties.SetName(button,"查看标签 "+tag.Text);
            button.Click+=(s,e)=>ShowTag(tag.Text);
            return button;
        }

        void ShowAnswer(StickySuggestion question) {
            resultPanel.Children.Clear();
            var card=new Border { CornerRadius=new CornerRadius(8),Padding=new Thickness(13,11,13,12),Margin=new Thickness(0,14,0,0),BorderThickness=new Thickness(1) };
            card.SetResourceReference(Border.BackgroundProperty,"SurfaceBrush"); card.SetResourceReference(Border.BorderBrushProperty,"LineBrush");
            var stack=new StackPanel(); card.Child=stack;
            stack.Children.Add(StickyNotesView.Text("回答"+(question.Count>1?" · 问过 "+question.Count+" 次":""),10,"MutedBrush"));
            stack.Children.Add(StickyNotesView.Text(question.Text,12.5,"TextBrush",true,new Thickness(0,5,0,8)));
            stack.Children.Add(StickyNotesView.Markdown(question.Answer));
            resultPanel.Children.Add(card);
            resultPanel.BringIntoView();
        }

        void ShowTag(string tag) {
            resultPanel.Children.Clear();
            var notes=StickyNoteStore.Recent(config,80).Where(n=>n.Tags.Any(t=>string.Equals(t,tag,StringComparison.OrdinalIgnoreCase))).ToList();
            var head=new Grid { Margin=new Thickness(0,14,0,7) };
            head.ColumnDefinitions.Add(new ColumnDefinition()); head.ColumnDefinitions.Add(new ColumnDefinition { Width=GridLength.Auto });
            head.Children.Add(StickyNotesView.Text("标签 #"+tag,12,"TextBrush",true));
            var close=ButtonFor("收起","StickyResultClose",()=>resultPanel.Children.Clear()); close.FontSize=10; close.Padding=new Thickness(6,2,4,2); close.SetResourceReference(Control.ForegroundProperty,"MutedBrush"); Grid.SetColumn(close,1); head.Children.Add(close);
            resultPanel.Children.Add(head);
            if(notes.Count==0) { resultPanel.Children.Add(StickyNotesView.Text("还没有带这个标签的便签。",11,"MutedBrush")); return; }
            foreach(StickyNoteItem note in notes) {
                var card=new Border { CornerRadius=new CornerRadius(8),Padding=new Thickness(12,10,12,10),Margin=new Thickness(0,0,0,6) };
                card.SetResourceReference(Border.BackgroundProperty,"SurfaceBrush");
                var stack=new StackPanel(); card.Child=stack;
                stack.Children.Add(StickyNotesView.Text(note.Title,12,"TextBrush",true));
                string body=StickyNoteStore.PlainText(note.Body);
                if(note.Images>0) body+=(body.Length>0?"\n":"")+"（"+note.Images+" 张图片）";
                if(body.Length>0) { var text=StickyNotesView.Text(body,11.5,"BodyBrush",false,new Thickness(0,5,0,0)); text.MaxHeight=96; text.TextTrimming=TextTrimming.CharacterEllipsis; stack.Children.Add(text); }
                stack.Children.Add(StickyNotesView.Text(note.Created.ToLocalTime().ToString("yyyy-MM-dd HH:mm"),9.5,"MutedBrush",false,new Thickness(0,7,0,0)));
                resultPanel.Children.Add(card);
            }
            resultPanel.BringIntoView();
        }

        void ShowSaved(string path) {
            resultPanel.Children.Clear();
            var card=new Border { CornerRadius=new CornerRadius(8),Padding=new Thickness(12,10,12,10),Margin=new Thickness(0,14,0,0) };
            card.SetResourceReference(Border.BackgroundProperty,"SurfaceBrush");
            var stack=new StackPanel(); card.Child=stack;
            stack.Children.Add(StickyNotesView.Text("已保存",11,"TextBrush",true));
            string shown=config==null?path:path.StartsWith(config.Vault,StringComparison.OrdinalIgnoreCase)?path.Substring(config.Vault.Length).TrimStart('\\','/'):path;
            stack.Children.Add(StickyNotesView.Text(shown,10.5,"MutedBrush",false,new Thickness(0,4,0,0)));
            var open=ButtonFor("打开便签文件夹","StickyOpenFolder",()=> { try { Directory.CreateDirectory(StickyNoteStore.Folder(config)); System.Diagnostics.Process.Start("explorer.exe","\""+StickyNoteStore.Folder(config)+"\""); } catch(Exception) { } });
            open.FontSize=10; open.Padding=new Thickness(6,3,6,3); open.HorizontalAlignment=HorizontalAlignment.Left; open.Margin=new Thickness(0,7,0,0); stack.Children.Add(open);
            resultPanel.Children.Add(card);
        }

        void Send() {
            if(config==null || string.IsNullOrWhiteSpace(config.Vault)) { status.Text="找不到 Obsidian 库，便签无法保存"; return; }
            string text=input==null?"":input.Text;
            if(string.IsNullOrWhiteSpace(text) && attachments.Count==0) { status.Text="写点内容或插入图片再发送"; return; }
            send.IsEnabled=false;
            try {
                string path;
                StickyNoteStore.Save(config,text,attachments,out path);
                input.Text="";
                attachments.Clear(); RebuildImages();
                status.Text="已保存到 Obsidian/"+FolderName();
                ShowSaved(path);
                UpdateSuggestions();
                int notes=0; try { string folder=StickyNoteStore.Folder(config); if(Directory.Exists(folder)) notes=Directory.GetFiles(folder,"*.md").Length; } catch(Exception) { }
                count.Text=notes+" 条便签";
            } catch(Exception e) { status.Text="保存失败："+e.Message; status.ToolTip=e.ToString(); }
            finally { UpdateSendState(); if(input!=null) input.Focus(); }
        }

        void Clear() {
            if(input!=null) input.Text="";
            attachments.Clear(); RebuildImages(); resultPanel.Children.Clear();
            status.Text="已清空当前对话框";
            UpdateSendState();
            if(input!=null) input.Focus();
        }

        void Refresh() {
            batch++; UpdateSuggestions();
            status.Text="换了一批常用问题";
        }

        void UpdateSendState() {
            if(send==null) return;
            bool hasText=input!=null && !string.IsNullOrWhiteSpace(input.Text);
            send.IsEnabled=hasText || attachments.Count>0;
        }

        bool HasImages(IDataObject data) {
            if(data==null) return false;
            try {
                if(data.GetDataPresent(DataFormats.Bitmap) || data.GetDataPresent("PNG")) return true;
                if(data.GetDataPresent(DataFormats.FileDrop)) {
                    var files=data.GetData(DataFormats.FileDrop) as string[];
                    return files!=null && files.Any(IsImageFile);
                }
            } catch(Exception) { }
            return false;
        }

        static bool IsImageFile(string file) {
            string extension=Path.GetExtension(file??"").ToLowerInvariant();
            return extension==".png" || extension==".jpg" || extension==".jpeg" || extension==".gif" || extension==".bmp" || extension==".webp";
        }

        bool PasteImages() {
            bool added=false;
            try {
                if(Clipboard.ContainsImage()) {
                    BitmapSource image=Clipboard.GetImage();
                    if(image!=null) { AddImage(image); added=true; }
                }
                if(Clipboard.ContainsFileDropList()) {
                    foreach(string file in Clipboard.GetFileDropList()) if(IsImageFile(file) && AddFile(file)) added=true;
                }
            } catch(Exception e) { status.Text="粘贴图片失败："+e.Message; }
            return added && !Clipboard.ContainsText();
        }

        bool AddDroppedImages(IDataObject data) {
            if(data==null) return false;
            bool added=false;
            try {
                if(data.GetDataPresent(DataFormats.FileDrop)) {
                    var files=data.GetData(DataFormats.FileDrop) as string[];
                    if(files!=null) foreach(string file in files) if(IsImageFile(file) && AddFile(file)) added=true;
                }
                if(!added && data.GetDataPresent(DataFormats.Bitmap)) {
                    BitmapSource image=data.GetData(DataFormats.Bitmap) as BitmapSource;
                    if(image!=null) { AddImage(image); added=true; }
                }
                if(!added && data.GetDataPresent("PNG")) {
                    using(var stream=data.GetData("PNG") as Stream) if(stream!=null) { var image=new BitmapImage(); image.BeginInit(); image.CacheOption=BitmapCacheOption.OnLoad; image.StreamSource=stream; image.EndInit(); image.Freeze(); AddImage(image); added=true; }
                }
            } catch(Exception e) { status.Text="读取图片失败："+e.Message; }
            return added;
        }

        bool AddFile(string file) {
            try {
                var image=new BitmapImage(); image.BeginInit(); image.CacheOption=BitmapCacheOption.OnLoad; image.UriSource=new Uri(file); image.EndInit(); image.Freeze();
                AddImage(image); return true;
            } catch(Exception e) { status.Text="读取图片失败："+e.Message; return false; }
        }

        void AddImage(BitmapSource image) {
            if(attachments.Count>=12) { status.Text="一条便签最多插入 12 张图片"; return; }
            byte[] bytes=EncodePng(image);
            if(bytes==null || bytes.Length==0) { status.Text="这张图片无法读取"; return; }
            attachments.Add(new StickyAttachment { Bytes=bytes,Extension="png",Preview=image });
            RebuildImages(); UpdateSendState();
            status.Text="已插入 "+attachments.Count+" 张图片";
        }

        static byte[] EncodePng(BitmapSource source) {
            try {
                BitmapSource target=source;
                double scale=Math.Min(1.0,2400.0/Math.Max(1,Math.Max(source.PixelWidth,source.PixelHeight)));
                if(scale<1) target=new TransformedBitmap(source,new ScaleTransform(scale,scale));
                var encoder=new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(target));
                using(var stream=new MemoryStream()) { encoder.Save(stream); return stream.ToArray(); }
            } catch(Exception) { return null; }
        }

        void RebuildImages() {
            imagesPanel.Children.Clear();
            imagesPanel.Visibility=attachments.Count==0?Visibility.Collapsed:Visibility.Visible;
            for(int i=0;i<attachments.Count;i++) {
                int index=i;
                var cell=new Grid { Width=62,Height=62,Margin=new Thickness(2,0,5,4) };
                var frame=new Border { CornerRadius=new CornerRadius(5),BorderThickness=new Thickness(1) }; frame.SetResourceReference(Border.BorderBrushProperty,"LineBrush");
                var picture=new Image { Source=attachments[i].Preview,Stretch=Stretch.UniformToFill };
                frame.Child=picture; cell.Children.Add(frame);
                var remove=new Button { Content="×",Width=17,Height=17,Padding=new Thickness(0),FontSize=11,HorizontalAlignment=HorizontalAlignment.Right,VerticalAlignment=VerticalAlignment.Top,Margin=new Thickness(0,-2,-3,0) };
                remove.SetResourceReference(Control.BackgroundProperty,"CanvasBrush"); remove.SetResourceReference(Control.ForegroundProperty,"TextBrush"); remove.ToolTip="移除图片";
                AutomationProperties.SetAutomationId(remove,"StickyRemoveImage-"+index); AutomationProperties.SetName(remove,"移除图片");
                remove.Click+=(s,e)=> { if(index<attachments.Count) { attachments.RemoveAt(index); RebuildImages(); UpdateSendState(); } };
                cell.Children.Add(remove); imagesPanel.Children.Add(cell);
            }
        }

        static System.Windows.Shapes.Path StickyIcon() {
            var path=new System.Windows.Shapes.Path { Data=Geometry.Parse("M3,2.9 H9.2 L13,6.7 V13.1 H3 Z M9.2,2.9 V6.7 H13 M7.9,8.1 V11.5 M6.2,9.8 H9.6"),StrokeThickness=1.45,StrokeStartLineCap=PenLineCap.Round,StrokeEndLineCap=PenLineCap.Round,StrokeLineJoin=PenLineJoin.Round,Stretch=Stretch.Uniform,Width=16,Height=16,SnapsToDevicePixels=true,IsHitTestVisible=false };
            path.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty,"TextBrush");
            return path;
        }

        static int Index(StackPanel tools,string id) {
            for(int i=0;i<tools.Children.Count;i++) { var button=tools.Children[i] as Button; if(button!=null && AutomationProperties.GetAutomationId(button)==id) return i; }
            return -1;
        }
        static StackPanel FindToolbar(DependencyObject node) {
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
