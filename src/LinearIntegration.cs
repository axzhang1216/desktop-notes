using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace DesktopNotes {
    public sealed class LinearProjectItem {
        public string Id { get; set; }
        public string Name { get; set; }
        public string Summary { get; set; }
        public string Description { get; set; }
        public string Url { get; set; }
        public string UpdatedAt { get; set; }
        public string Status { get; set; }
        public List<LinearDocumentItem> Documents { get; set; }
        public LinearProjectItem() { Documents=new List<LinearDocumentItem>(); }
    }

    public sealed class LinearDocumentItem {
        public string Id { get; set; }
        public string Title { get; set; }
        public string Content { get; set; }
        public string Url { get; set; }
        public string UpdatedAt { get; set; }
        public string ProjectId { get; set; }
    }

    public sealed class LinearSnapshot {
        public string ViewerName { get; set; }
        public List<LinearProjectItem> Projects { get; set; }
        public List<LinearDocumentItem> LooseDocuments { get; set; }
        public LinearSnapshot() { Projects=new List<LinearProjectItem>(); LooseDocuments=new List<LinearDocumentItem>(); }
    }

    public sealed class LinearConnectionData {
        public string ProtectedApiKey { get; set; }
        public string ViewerName { get; set; }
        public string UpdatedUtc { get; set; }
    }

    public sealed class LinearConnectionStore {
        readonly string path;
        static JavaScriptSerializer Serializer() { return new JavaScriptSerializer { MaxJsonLength=4*1024*1024, RecursionLimit=40 }; }
        public LinearConnectionStore(string notesPath) { path=Path.Combine(Path.GetDirectoryName(Path.GetFullPath(notesPath)),"linear.json"); }
        public bool HasKey() { try { return !string.IsNullOrWhiteSpace(ReadKey()); } catch { return false; } }
        public string ReadKey() {
            if(!File.Exists(path)) return null;
            var data=Serializer().Deserialize<LinearConnectionData>(File.ReadAllText(path,Encoding.UTF8));
            if(data==null || string.IsNullOrWhiteSpace(data.ProtectedApiKey)) return null;
            byte[] encrypted=Convert.FromBase64String(data.ProtectedApiKey);
            byte[] clear=ProtectedData.Unprotect(encrypted,null,DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(clear);
        }
        public void Save(string apiKey,string viewerName) {
            if(string.IsNullOrWhiteSpace(apiKey)) throw new ArgumentException("Linear API Key 不能为空。");
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            byte[] clear=Encoding.UTF8.GetBytes(apiKey.Trim());
            byte[] encrypted=ProtectedData.Protect(clear,null,DataProtectionScope.CurrentUser);
            var data=new LinearConnectionData {
                ProtectedApiKey=Convert.ToBase64String(encrypted),
                ViewerName=viewerName??"",
                UpdatedUtc=DateTimeOffset.UtcNow.ToString("o",CultureInfo.InvariantCulture)
            };
            NotesStore.WriteAtomic(path,Serializer().Serialize(data),true);
        }
        public void Clear() {
            if(File.Exists(path)) File.Delete(path);
            if(File.Exists(path+".bak")) File.Delete(path+".bak");
        }
    }

    public sealed class LinearApiClient {
        const string Endpoint="https://api.linear.app/graphql";
        const string Query=@"query DesktopNotesLinear {
  viewer { id name }
  projects(first: 50, orderBy: updatedAt) {
    nodes { id name summary description url updatedAt status { name type } }
  }
  documents(first: 100, orderBy: updatedAt) {
    nodes { id title content url updatedAt project { id name } }
  }
}";
        readonly string apiKey;
        readonly JavaScriptSerializer serializer=new JavaScriptSerializer { MaxJsonLength=16*1024*1024, RecursionLimit=80 };
        public LinearApiClient(string key) { apiKey=key; }

        public async Task<LinearSnapshot> LoadAsync() {
            if(string.IsNullOrWhiteSpace(apiKey)) throw new InvalidOperationException("尚未连接 Linear。");
            using(var client=new HttpClient()) {
                client.Timeout=TimeSpan.FromSeconds(20);
                using(var request=new HttpRequestMessage(HttpMethod.Post,Endpoint)) {
                    request.Headers.TryAddWithoutValidation("Authorization",apiKey.Trim());
                    string payload=serializer.Serialize(new Dictionary<string,object> { { "query",Query } });
                    request.Content=new StringContent(payload,Encoding.UTF8,"application/json");
                    using(var response=await client.SendAsync(request)) {
                        string json=await response.Content.ReadAsStringAsync();
                        if(!response.IsSuccessStatusCode) throw new InvalidOperationException("Linear 返回 "+(int)response.StatusCode+"。请检查 API Key 和网络连接。");
                        return Parse(json);
                    }
                }
            }
        }

        LinearSnapshot Parse(string json) {
            var root=serializer.DeserializeObject(json) as Dictionary<string,object>;
            if(root==null) throw new InvalidDataException("Linear 返回了无法识别的数据。");
            object errorObject;
            if(root.TryGetValue("errors",out errorObject)) {
                object[] errors=errorObject as object[];
                if(errors!=null && errors.Length>0) {
                    var first=errors[0] as Dictionary<string,object>;
                    string message=first==null?null:String(first,"message");
                    throw new InvalidOperationException(string.IsNullOrWhiteSpace(message)?"Linear API 请求失败。":message);
                }
            }
            var data=Dict(root,"data");
            if(data==null) throw new InvalidDataException("Linear 返回结果缺少 data。");
            var snapshot=new LinearSnapshot();
            var viewer=Dict(data,"viewer");
            snapshot.ViewerName=viewer==null?"":String(viewer,"name");
            var projects=ConnectionNodes(data,"projects");
            foreach(var item in projects) {
                var status=Dict(item,"status");
                snapshot.Projects.Add(new LinearProjectItem {
                    Id=String(item,"id"), Name=String(item,"name"), Summary=String(item,"summary"), Description=String(item,"description"),
                    Url=String(item,"url"), UpdatedAt=String(item,"updatedAt"), Status=status==null?"":String(status,"name")
                });
            }
            var byId=snapshot.Projects.Where(p=>!string.IsNullOrEmpty(p.Id)).ToDictionary(p=>p.Id,p=>p);
            var documents=ConnectionNodes(data,"documents");
            foreach(var item in documents) {
                var project=Dict(item,"project");
                var doc=new LinearDocumentItem {
                    Id=String(item,"id"), Title=String(item,"title"), Content=String(item,"content"), Url=String(item,"url"), UpdatedAt=String(item,"updatedAt"),
                    ProjectId=project==null?null:String(project,"id")
                };
                LinearProjectItem owner;
                if(!string.IsNullOrEmpty(doc.ProjectId) && byId.TryGetValue(doc.ProjectId,out owner)) owner.Documents.Add(doc); else snapshot.LooseDocuments.Add(doc);
            }
            return snapshot;
        }

        static Dictionary<string,object> Dict(Dictionary<string,object> source,string key) {
            object value; if(source==null || !source.TryGetValue(key,out value)) return null; return value as Dictionary<string,object>;
        }
        static string String(Dictionary<string,object> source,string key) {
            object value; if(source==null || !source.TryGetValue(key,out value) || value==null) return ""; return Convert.ToString(value,CultureInfo.InvariantCulture)??"";
        }
        static List<Dictionary<string,object>> ConnectionNodes(Dictionary<string,object> source,string key) {
            var connection=Dict(source,key); var result=new List<Dictionary<string,object>>(); if(connection==null) return result;
            object raw; if(!connection.TryGetValue("nodes",out raw)) return result; object[] nodes=raw as object[]; if(nodes==null) return result;
            foreach(object item in nodes) { var dict=item as Dictionary<string,object>; if(dict!=null) result.Add(dict); }
            return result;
        }
    }

    public sealed class LinearIntegration {
        readonly NotesWindow window;
        readonly NotesStore notesStore;
        readonly LinearConnectionStore connectionStore;
        readonly Grid root;
        readonly StackPanel board;
        readonly TextBlock status;
        readonly TextBlock count;
        Button sourceButton;
        Button refreshButton;
        Grid setupOverlay;
        bool linearMode;
        bool loading;
        LinearSnapshot lastSnapshot;

        LinearIntegration(NotesWindow notesWindow,NotesStore store) {
            window=notesWindow; notesStore=store; connectionStore=new LinearConnectionStore(store.FilePath);
            root=Private<Grid>(window,"root"); board=Private<StackPanel>(window,"board"); status=Private<TextBlock>(window,"status"); count=Private<TextBlock>(window,"count");
        }

        public static void Attach(NotesWindow window,NotesStore store) {
            var integration=new LinearIntegration(window,store); integration.InstallToolbar();
            window.Tag=integration;
        }

        static T Private<T>(object owner,string name) where T:class {
            FieldInfo field=owner.GetType().GetField(name,BindingFlags.Instance|BindingFlags.NonPublic);
            if(field==null) throw new MissingFieldException(owner.GetType().FullName,name);
            return field.GetValue(owner) as T;
        }

        void InstallToolbar() {
            StackPanel tools=FindToolbar(root);
            if(tools==null) return;
            sourceButton=ToolbarButton("L","Linear","LinearSource"); sourceButton.FontFamily=new FontFamily("Segoe UI"); sourceButton.FontSize=12; sourceButton.FontWeight=FontWeights.SemiBold;
            sourceButton.Click+=async (s,e)=> { if(linearMode) ShowLocal(); else await ShowLinearAsync(false); };
            refreshButton=ToolbarButton("\uE72C","刷新 Linear","LinearRefresh"); refreshButton.Visibility=Visibility.Collapsed; refreshButton.Click+=async (s,e)=>await ShowLinearAsync(true);
            int insert=Math.Min(1,tools.Children.Count); tools.Children.Insert(insert,sourceButton); tools.Children.Insert(insert+1,refreshButton);
        }

        Button ToolbarButton(string glyph,string label,string id) {
            var button=new Button { Width=32,Height=32,Padding=new Thickness(5),ToolTip=label,Content=new TextBlock { Text=glyph,FontFamily=new FontFamily("Segoe MDL2 Assets"),FontSize=14,VerticalAlignment=VerticalAlignment.Center } };
            AutomationProperties.SetAutomationId(button,id); AutomationProperties.SetName(button,label); return button;
        }

        static StackPanel FindToolbar(DependencyObject node) {
            if(node==null) return null;
            var panel=node as StackPanel;
            if(panel!=null && panel.Orientation==Orientation.Horizontal) {
                foreach(UIElement child in panel.Children) {
                    var button=child as Button;
                    if(button!=null && AutomationProperties.GetAutomationId(button)=="Pin") return panel;
                }
            }
            int count=System.Windows.Media.VisualTreeHelper.GetChildrenCount(node);
            for(int i=0;i<count;i++) { var found=FindToolbar(System.Windows.Media.VisualTreeHelper.GetChild(node,i)); if(found!=null) return found; }
            return null;
        }

        async Task ShowLinearAsync(bool refresh) {
            if(loading) return;
            string key=null;
            try { key=connectionStore.ReadKey(); } catch(Exception e) { ShowError("读取 Linear 连接信息失败。",e); return; }
            if(string.IsNullOrWhiteSpace(key)) { ShowSetup(); return; }
            if(!refresh && lastSnapshot!=null) { linearMode=true; RenderLinear(lastSnapshot); return; }
            loading=true; linearMode=true; SetModeButtons(); status.Text="正在读取 Linear…"; count.Text="";
            try {
                var snapshot=await new LinearApiClient(key).LoadAsync(); lastSnapshot=snapshot; RenderLinear(snapshot); connectionStore.Save(key,snapshot.ViewerName);
            } catch(Exception e) {
                status.Text="Linear 连接失败"; ShowError("读取 Linear 项目与笔记失败。",e);
                if(lastSnapshot!=null) RenderLinear(lastSnapshot); else ShowLocal();
            } finally { loading=false; SetModeButtons(); }
        }

        void ShowLocal() {
            linearMode=false; SetModeButtons(); window.RenderBoard(); status.Text="保存在此电脑";
        }

        void SetModeButtons() {
            if(sourceButton==null) return;
            sourceButton.SetResourceReference(Control.BackgroundProperty,linearMode?"SelectionBrush":"CanvasBrush");
            sourceButton.ToolTip=linearMode?"返回本地便签":"Linear";
            AutomationProperties.SetName(sourceButton,linearMode?"返回本地便签":"Linear");
            if(refreshButton!=null) refreshButton.Visibility=linearMode?Visibility.Visible:Visibility.Collapsed;
        }

        void RenderLinear(LinearSnapshot snapshot) {
            linearMode=true; SetModeButtons(); board.Children.Clear();
            var projects=snapshot.Projects.OrderByDescending(p=>ParseTime(p.UpdatedAt)).ToList();
            foreach(LinearProjectItem project in projects) board.Children.Add(ProjectGroup(project));
            if(snapshot.LooseDocuments.Count>0) board.Children.Add(LooseDocumentsGroup(snapshot.LooseDocuments));
            if(projects.Count==0 && snapshot.LooseDocuments.Count==0) board.Children.Add(EmptyCard("Linear 里还没有可显示的项目或文档。"));
            int documents=projects.Sum(p=>p.Documents.Count)+snapshot.LooseDocuments.Count;
            count.Text=projects.Count+" 个项目 · "+documents+" 条笔记";
            status.Text="Linear · 只读 · "+DateTime.Now.ToString("HH:mm")+" 同步";
        }

        FrameworkElement ProjectGroup(LinearProjectItem project) {
            var group=new StackPanel { Margin=new Thickness(0,0,0,23) };
            var row=new Grid { Margin=new Thickness(0,0,0,10),Cursor=Cursors.Hand }; row.ColumnDefinitions.Add(new ColumnDefinition()); row.ColumnDefinitions.Add(new ColumnDefinition { Width=GridLength.Auto });
            var title=new StackPanel { Orientation=Orientation.Horizontal,VerticalAlignment=VerticalAlignment.Center };
            var mark=new Border { Width=4,Height=16,CornerRadius=new CornerRadius(2),Margin=new Thickness(0,0,9,0) }; mark.SetResourceReference(Border.BackgroundProperty,"MutedBrush"); title.Children.Add(mark);
            title.Children.Add(Text(project.Name,15,"TextBrush",true)); title.Children.Add(Text(project.Documents.Count.ToString("00"),10.5,"MutedBrush",false,new Thickness(9,3,0,0))); row.Children.Add(title);
            if(!string.IsNullOrWhiteSpace(project.Status)) { var state=Text(project.Status,10,"MutedBrush"); state.VerticalAlignment=VerticalAlignment.Center; Grid.SetColumn(state,1); row.Children.Add(state); }
            if(!string.IsNullOrWhiteSpace(project.Url)) row.MouseLeftButtonUp+=(s,e)=>OpenUrl(project.Url);
            group.Children.Add(row);
            string intro=!string.IsNullOrWhiteSpace(project.Summary)?project.Summary:Preview(project.Description,180);
            if(!string.IsNullOrWhiteSpace(intro)) group.Children.Add(ProjectSummaryCard(project,intro));
            var docs=project.Documents.OrderByDescending(d=>ParseTime(d.UpdatedAt)).ToList();
            if(docs.Count==0) group.Children.Add(EmptyCard("这个项目还没有 Linear 文档。")); else foreach(var doc in docs) group.Children.Add(DocumentCard(doc));
            return group;
        }

        FrameworkElement ProjectSummaryCard(LinearProjectItem project,string intro) {
            var card=new Border { CornerRadius=new CornerRadius(8),Padding=new Thickness(15,12,15,12),Margin=new Thickness(0,0,0,8),Cursor=Cursors.Hand }; card.SetResourceReference(Border.BackgroundProperty,"SurfaceBrush");
            var stack=new StackPanel(); stack.Children.Add(Text(intro,11.5,"BodyBrush"));
            if(!string.IsNullOrWhiteSpace(project.Url)) card.MouseLeftButtonUp+=(s,e)=>OpenUrl(project.Url); card.Child=stack; return card;
        }

        FrameworkElement DocumentCard(LinearDocumentItem document) {
            var card=new Border { CornerRadius=new CornerRadius(8),Padding=new Thickness(15,13,12,11),Margin=new Thickness(0,0,0,8),Cursor=Cursors.Hand }; card.SetResourceReference(Border.BackgroundProperty,"SurfaceBrush");
            var stack=new StackPanel(); card.Child=stack;
            stack.Children.Add(Text(string.IsNullOrWhiteSpace(document.Title)?"无标题文档":document.Title,14,"TextBrush",true));
            string preview=Preview(document.Content,280); if(!string.IsNullOrWhiteSpace(preview)) { var body=Text(preview,12,"BodyBrush",false,new Thickness(0,7,0,0)); body.LineHeight=20; body.MaxHeight=82; body.TextTrimming=TextTrimming.CharacterEllipsis; stack.Children.Add(body); }
            DateTimeOffset updated=ParseTime(document.UpdatedAt); string foot=updated==DateTimeOffset.MinValue?"Linear 文档":"更新 "+updated.ToLocalTime().ToString("MM.dd HH:mm"); stack.Children.Add(Text(foot,10,"MutedBrush",false,new Thickness(0,10,0,0)));
            if(!string.IsNullOrWhiteSpace(document.Url)) card.MouseLeftButtonUp+=(s,e)=>OpenUrl(document.Url); return card;
        }

        FrameworkElement LooseDocumentsGroup(List<LinearDocumentItem> docs) {
            var group=new StackPanel { Margin=new Thickness(0,0,0,23) };
            var title=new StackPanel { Orientation=Orientation.Horizontal,VerticalAlignment=VerticalAlignment.Center,Margin=new Thickness(0,0,0,10) };
            var mark=new Border { Width=4,Height=16,CornerRadius=new CornerRadius(2),Margin=new Thickness(0,0,9,0) }; mark.SetResourceReference(Border.BackgroundProperty,"MutedBrush"); title.Children.Add(mark); title.Children.Add(Text("其他 Linear 文档",15,"TextBrush",true)); title.Children.Add(Text(docs.Count.ToString("00"),10.5,"MutedBrush",false,new Thickness(9,3,0,0))); group.Children.Add(title);
            foreach(var doc in docs.OrderByDescending(d=>ParseTime(d.UpdatedAt))) group.Children.Add(DocumentCard(doc)); return group;
        }

        FrameworkElement EmptyCard(string message) {
            var border=new Border { CornerRadius=new CornerRadius(8),Padding=new Thickness(16,17,16,17),Margin=new Thickness(0,0,0,8) }; border.SetResourceReference(Border.BackgroundProperty,"SurfaceBrush"); border.Child=Text(message,11.5,"MutedBrush"); return border;
        }

        TextBlock Text(string value,double size,string brush,bool bold=false,Thickness? margin=null) {
            var text=new TextBlock { Text=value??"",FontFamily=new FontFamily("Microsoft YaHei UI"),FontSize=size,FontWeight=bold?FontWeights.SemiBold:FontWeights.Normal,TextWrapping=TextWrapping.Wrap,Margin=margin??new Thickness(0) }; text.SetResourceReference(TextBlock.ForegroundProperty,brush); return text;
        }

        static DateTimeOffset ParseTime(string value) { DateTimeOffset result; return DateTimeOffset.TryParse(value,CultureInfo.InvariantCulture,DateTimeStyles.RoundtripKind,out result)?result:DateTimeOffset.MinValue; }
        static string Preview(string markdown,int max) {
            if(string.IsNullOrWhiteSpace(markdown)) return "";
            string text=markdown.Replace("\r",""); text=Regex.Replace(text,@"(?m)^\s{0,3}#{1,6}\s*",""); text=Regex.Replace(text,@"!\[[^\]]*\]\([^\)]*\)",""); text=Regex.Replace(text,@"\[([^\]]+)\]\([^\)]*\)","$1"); text=text.Replace("**","").Replace("__","").Replace("`",""); text=Regex.Replace(text,@"\n{3,}","\n\n").Trim();
            if(text.Length>max) text=text.Substring(0,max).TrimEnd()+"…"; return text;
        }
        static void OpenUrl(string url) { try { if(!string.IsNullOrWhiteSpace(url)) Process.Start(url); } catch { } }

        void ShowSetup() {
            if(setupOverlay!=null) return;
            setupOverlay=new Grid(); setupOverlay.Background=new SolidColorBrush(Color.FromArgb(72,0,0,0)); Panel.SetZIndex(setupOverlay,100); root.Children.Add(setupOverlay);
            var card=new Border { Width=360,MaxWidth=420,Padding=new Thickness(22),CornerRadius=new CornerRadius(10),BorderThickness=new Thickness(1),HorizontalAlignment=HorizontalAlignment.Center,VerticalAlignment=VerticalAlignment.Center,Margin=new Thickness(18) }; card.SetResourceReference(Border.BackgroundProperty,"CanvasBrush"); card.SetResourceReference(Border.BorderBrushProperty,"LineBrush"); setupOverlay.Children.Add(card);
            var stack=new StackPanel(); card.Child=stack; stack.Children.Add(Text("连接 Linear",19,"TextBrush",true)); stack.Children.Add(Text("输入个人 API Key。密钥会用当前 Windows 用户的 DPAPI 加密后保存在本机。",11,"MutedBrush",false,new Thickness(0,7,0,15)));
            var key=new PasswordBox { MinHeight=42,Padding=new Thickness(10),FontSize=13 }; key.SetResourceReference(Control.BackgroundProperty,"SurfaceBrush"); key.SetResourceReference(Control.ForegroundProperty,"TextBrush"); key.SetResourceReference(Control.BorderBrushProperty,"LineBrush"); stack.Children.Add(key);
            var hint=Text("Linear → Settings → Security & access → Personal API keys",10.5,"MutedBrush",false,new Thickness(0,8,0,0)); stack.Children.Add(hint);
            var message=Text("",10.5,"MutedBrush",false,new Thickness(0,10,0,0)); stack.Children.Add(message);
            var actions=new Grid { Margin=new Thickness(0,16,0,0) }; actions.ColumnDefinitions.Add(new ColumnDefinition()); actions.ColumnDefinitions.Add(new ColumnDefinition { Width=GridLength.Auto }); actions.ColumnDefinitions.Add(new ColumnDefinition { Width=GridLength.Auto }); stack.Children.Add(actions);
            var disconnect=new Button { Content="断开连接",Padding=new Thickness(0,7,8,7),HorizontalAlignment=HorizontalAlignment.Left,Visibility=connectionStore.HasKey()?Visibility.Visible:Visibility.Collapsed }; disconnect.Click+=(s,e)=> { connectionStore.Clear(); lastSnapshot=null; message.Text="已清除本机 Linear 连接。"; disconnect.Visibility=Visibility.Collapsed; }; actions.Children.Add(disconnect);
            var cancel=new Button { Content="取消",MinWidth=68,Margin=new Thickness(0,0,7,0) }; cancel.Click+=(s,e)=>CloseSetup(); Grid.SetColumn(cancel,1); actions.Children.Add(cancel);
            var connect=new Button { Content="连接",MinWidth=76 }; connect.SetResourceReference(Control.BackgroundProperty,"TextBrush"); connect.SetResourceReference(Control.ForegroundProperty,"CanvasBrush"); Grid.SetColumn(connect,2); actions.Children.Add(connect);
            connect.Click+=async (s,e)=> {
                if(string.IsNullOrWhiteSpace(key.Password)) { message.Text="请输入 API Key。"; return; }
                connect.IsEnabled=false; cancel.IsEnabled=false; message.Text="正在验证…";
                try { var snapshot=await new LinearApiClient(key.Password).LoadAsync(); connectionStore.Save(key.Password,snapshot.ViewerName); lastSnapshot=snapshot; CloseSetup(); linearMode=true; RenderLinear(snapshot); }
                catch(Exception ex) { message.Text=ex.Message; connect.IsEnabled=true; cancel.IsEnabled=true; }
            };
            key.KeyDown+=(s,e)=> { if(e.Key==Key.Escape) { CloseSetup(); e.Handled=true; } };
            key.Focus();
        }

        void CloseSetup() { if(setupOverlay==null) return; root.Children.Remove(setupOverlay); setupOverlay=null; }
        void ShowError(string title,Exception error) { MessageBox.Show(window,title+"\n\n"+error.Message,"Linear",MessageBoxButton.OK,MessageBoxImage.Error); }
    }

    public static class LinearProgram {
        [DllImport("user32.dll",CharSet=CharSet.Unicode)] static extern IntPtr FindWindow(string className,string name);
        [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr handle,int command);
        [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr handle);
        [STAThread] public static void Main(string[] args) {
            string path=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"ZoeyDesktopNotes","notes.json");
            if(args.Length==2 && args[0]=="--data") path=Path.GetFullPath(args[1]);
            string hash; using(var sha=SHA256.Create()) hash=BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(path.ToUpperInvariant()))).Replace("-","").Substring(0,24);
            bool first; using(var mutex=new Mutex(true,"Local\\ZoeyDesktopNotes-"+hash,out first)) {
                if(!first) { var handle=FindWindow(null,"桌面便签"); if(handle!=IntPtr.Zero) { ShowWindow(handle,9); SetForegroundWindow(handle); } return; }
                try {
                    var store=new NotesStore(path); store.Load(); var app=new Application { ShutdownMode=ShutdownMode.OnMainWindowClose }; var window=new NotesWindow(store); LinearIntegration.Attach(window,store); app.Run(window);
                } catch(Exception e) { MessageBox.Show(e.Message,"桌面便签",MessageBoxButton.OK,MessageBoxImage.Error); }
                finally { mutex.ReleaseMutex(); }
            }
        }
    }
}
