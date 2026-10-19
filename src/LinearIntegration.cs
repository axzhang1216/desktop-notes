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
        public string Color { get; set; }
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

    public sealed class LinearIssueItem {
        public string Id { get; set; }
        public string Identifier { get; set; }
        public string Title { get; set; }
        public string Url { get; set; }
        public string UpdatedAt { get; set; }
        public int Priority { get; set; }
        public string StateName { get; set; }
        public string StateType { get; set; }
        public string ProjectId { get; set; }
        public string ProjectName { get; set; }
    }

    public sealed class LinearIssueGroup {
        public string Key { get; set; }
        public string Title { get; set; }
        public string Status { get; set; }
        public string Url { get; set; }
        public string Color { get; set; }
        public bool Unassigned { get; set; }
        public List<LinearIssueItem> Issues { get; set; }
    }

    public static class LinearIssueGroups {
        public static bool IsOpen(LinearIssueItem issue) {
            if(issue==null) return false;
            string type=(issue.StateType??"").Trim().ToLowerInvariant();
            return type!="completed" && type!="canceled" && type!="cancelled";
        }
        public static List<LinearIssueGroup> Build(LinearSnapshot snapshot) {
            var result=new List<LinearIssueGroup>();
            if(snapshot==null) return result;
            var open=(snapshot.Issues??new List<LinearIssueItem>()).Where(IsOpen).ToList();
            var byProject=open.Where(i=>!string.IsNullOrEmpty(i.ProjectId)).GroupBy(i=>i.ProjectId).ToDictionary(g=>g.Key,g=>g.ToList());
            var seen=new HashSet<string>();
            foreach(LinearProjectItem project in snapshot.Projects??new List<LinearProjectItem>()) {
                if(project==null || string.IsNullOrEmpty(project.Id) || seen.Contains(project.Id)) continue;
                List<LinearIssueItem> issues;
                if(!byProject.TryGetValue(project.Id,out issues) || issues.Count==0) continue;
                seen.Add(project.Id);
                string title=string.IsNullOrWhiteSpace(project.Name)?issues[0].ProjectName:project.Name;
                result.Add(Make(project.Id,string.IsNullOrWhiteSpace(title)?"未命名项目":title,project.Status,project.Url,project.Color,false,issues));
            }
            foreach(var pair in byProject.OrderBy(p=>p.Value[0].ProjectName)) {
                if(seen.Contains(pair.Key)) continue;
                string title=pair.Value[0].ProjectName;
                result.Add(Make(pair.Key,string.IsNullOrWhiteSpace(title)?"未命名项目":title,"","","",false,pair.Value));
            }
            var loose=open.Where(i=>string.IsNullOrEmpty(i.ProjectId)).ToList();
            if(loose.Count>0) result.Add(Make("","无项目","","","",true,loose));
            return result;
        }
        static LinearIssueGroup Make(string key,string title,string status,string url,string color,bool unassigned,List<LinearIssueItem> issues) {
            return new LinearIssueGroup {
                Key=key, Title=title??"", Status=status??"", Url=url??"", Color=color??"", Unassigned=unassigned,
                Issues=issues.OrderByDescending(i=>ParseTime(i.UpdatedAt)).ToList()
            };
        }
        static DateTimeOffset ParseTime(string value) {
            DateTimeOffset result;
            return DateTimeOffset.TryParse(value,CultureInfo.InvariantCulture,DateTimeStyles.RoundtripKind,out result)?result:DateTimeOffset.MinValue;
        }
    }

    public sealed class LinearSnapshot {
        public string ViewerName { get; set; }
        public List<LinearProjectItem> Projects { get; set; }
        public List<LinearDocumentItem> LooseDocuments { get; set; }
        public List<LinearIssueItem> Issues { get; set; }
        public LinearSnapshot() { Projects=new List<LinearProjectItem>(); LooseDocuments=new List<LinearDocumentItem>(); Issues=new List<LinearIssueItem>(); }
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
    nodes { id name url updatedAt color status { name type } }
  }
  issues(first: 100, orderBy: updatedAt, filter: { state: { type: { nin: [""completed"", ""canceled""] } } }) {
    nodes { id identifier title url updatedAt priority state { name type } project { id name } }
  }
}";
        readonly string apiKey;
        readonly JavaScriptSerializer serializer=new JavaScriptSerializer { MaxJsonLength=16*1024*1024, RecursionLimit=80 };
        public LinearApiClient(string key) { apiKey=key; }
        public static LinearSnapshot ParseResponse(string json) { return new LinearApiClient("test").Parse(json); }

        public async Task<LinearSnapshot> LoadAsync() {
            if(string.IsNullOrWhiteSpace(apiKey)) throw new InvalidOperationException("尚未连接 Linear。");
            EnableModernTls();
            using(var client=new HttpClient()) {
                client.Timeout=TimeSpan.FromSeconds(20);
                using(var request=new HttpRequestMessage(HttpMethod.Post,Endpoint)) {
                    request.Headers.TryAddWithoutValidation("Authorization",apiKey.Trim());
                    string payload=serializer.Serialize(new Dictionary<string,object> { { "query",Query } });
                    request.Content=new StringContent(payload,Encoding.UTF8,"application/json");
                    using(var response=await client.SendAsync(request)) {
                        string json=await response.Content.ReadAsStringAsync();
                        if(!response.IsSuccessStatusCode) {
                            string detail=GraphQlMessage(json);
                            throw new InvalidOperationException(string.IsNullOrWhiteSpace(detail)
                                ?"Linear 返回 "+(int)response.StatusCode+"。请检查 API Key 和网络连接。"
                                :detail);
                        }
                        return Parse(json);
                    }
                }
            }
        }

        static void EnableModernTls() {
            const System.Net.SecurityProtocolType tls12=(System.Net.SecurityProtocolType)3072;
            const System.Net.SecurityProtocolType tls13=(System.Net.SecurityProtocolType)12288;
            System.Net.ServicePointManager.SecurityProtocol |= tls12 | tls13;
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
                    Id=String(item,"id"), Name=String(item,"name"), Url=String(item,"url"), UpdatedAt=String(item,"updatedAt"),
                    Color=String(item,"color"), Status=status==null?"":String(status,"name")
                });
            }
            foreach(var item in ConnectionNodes(data,"issues")) {
                var state=Dict(item,"state");
                var project=Dict(item,"project");
                snapshot.Issues.Add(new LinearIssueItem {
                    Id=String(item,"id"), Identifier=String(item,"identifier"), Title=String(item,"title"), Url=String(item,"url"), UpdatedAt=String(item,"updatedAt"),
                    Priority=Int(item,"priority"), StateName=state==null?"":String(state,"name"), StateType=state==null?"":String(state,"type"),
                    ProjectId=project==null?null:String(project,"id"), ProjectName=project==null?"":String(project,"name")
                });
            }
            return snapshot;
        }

        static string GraphQlMessage(string json) {
            try {
                var root=new JavaScriptSerializer { MaxJsonLength=16*1024*1024, RecursionLimit=80 }.DeserializeObject(json) as Dictionary<string,object>;
                object errorObject; if(root==null || !root.TryGetValue("errors",out errorObject)) return null;
                object[] errors=errorObject as object[]; if(errors==null || errors.Length==0) return null;
                var first=errors[0] as Dictionary<string,object>; return first==null?null:String(first,"message");
            } catch { return null; }
        }
        static Dictionary<string,object> Dict(Dictionary<string,object> source,string key) {
            object value; if(source==null || !source.TryGetValue(key,out value)) return null; return value as Dictionary<string,object>;
        }
        static string String(Dictionary<string,object> source,string key) {
            object value; if(source==null || !source.TryGetValue(key,out value) || value==null) return ""; return Convert.ToString(value,CultureInfo.InvariantCulture)??"";
        }
        static int Int(Dictionary<string,object> source,string key) {
            object value; if(source==null || !source.TryGetValue(key,out value) || value==null) return 0;
            try { return Convert.ToInt32(value,CultureInfo.InvariantCulture); } catch { return 0; }
        }
        static List<Dictionary<string,object>> ConnectionNodes(Dictionary<string,object> source,string key) {
            var connection=Dict(source,key); var result=new List<Dictionary<string,object>>(); if(connection==null) return result;
            object raw; if(!connection.TryGetValue("nodes",out raw)) return result; object[] nodes=raw as object[]; if(nodes==null) return result;
            foreach(object item in nodes) { var dict=item as Dictionary<string,object>; if(dict!=null) result.Add(dict); }
            return result;
        }
    }

    public static class LinearBoardView {
        public static void Render(StackPanel board,TextBlock status,TextBlock count,LinearSnapshot snapshot) {
            board.Children.Clear();
            var root=new StackPanel(); AutomationProperties.SetAutomationId(root,"LinearPolishedRoot"); board.Children.Add(root);
            var groups=LinearIssueGroups.Build(snapshot);
            if(groups.Count==0) root.Children.Add(EmptyLine("Linear 里还没有未完成的待办。"));
            foreach(LinearIssueGroup group in groups) root.Children.Add(ProjectGroup(group));
            int issues=groups.Sum(g=>g.Issues.Count);
            int projects=groups.Count(g=>!g.Unassigned);
            if(count!=null) count.Text=projects+" 个项目 · "+issues+" 条待办";
            if(status!=null) status.Text="Linear · 只读 · "+DateTime.Now.ToString("HH:mm")+" 同步";
        }
        static FrameworkElement ProjectGroup(LinearIssueGroup group) {
            var panel=new StackPanel { Margin=new Thickness(0,0,0,20) };
            var row=new Grid { Margin=new Thickness(0,0,0,8) };
            row.ColumnDefinitions.Add(new ColumnDefinition());
            row.ColumnDefinitions.Add(new ColumnDefinition { Width=GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width=GridLength.Auto });
            var title=new StackPanel { Orientation=Orientation.Horizontal, VerticalAlignment=VerticalAlignment.Center };
            var mark=new Border { Width=4, Height=16, CornerRadius=new CornerRadius(2), Margin=new Thickness(0,0,9,0) };
            string color=NormalizeColor(group.Color);
            if(color.Length==7) { var brush=new SolidColorBrush((Color)ColorConverter.ConvertFromString(color)); brush.Freeze(); mark.Background=brush; }
            else mark.SetResourceReference(Border.BackgroundProperty,"MutedBrush");
            title.Children.Add(mark);
            title.Children.Add(Text(group.Title,15,"TextBrush",true));
            title.Children.Add(Text(group.Issues.Count.ToString("00"),10.5,"MutedBrush",false,new Thickness(9,3,0,0)));
            row.Children.Add(title);
            if(!string.IsNullOrWhiteSpace(group.Status)) { var state=Text(group.Status,10,"MutedBrush"); state.VerticalAlignment=VerticalAlignment.Center; state.Margin=new Thickness(8,0,3,0); Grid.SetColumn(state,1); row.Children.Add(state); }
            if(!string.IsNullOrWhiteSpace(group.Url)) { var open=OpenButton(group.Url,"打开 Linear 项目"); Grid.SetColumn(open,2); row.Children.Add(open); }
            panel.Children.Add(row);
            foreach(LinearIssueItem issue in group.Issues) panel.Children.Add(IssueCard(issue));
            return panel;
        }
        static FrameworkElement IssueCard(LinearIssueItem issue) {
            var card=new Border { CornerRadius=new CornerRadius(8), Padding=new Thickness(12,8,6,8), Margin=new Thickness(0,0,0,6), Cursor=Cursors.Hand };
            card.SetResourceReference(Border.BackgroundProperty,"SurfaceBrush");
            var grid=new Grid(); card.Child=grid;
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width=GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition());
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width=GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width=GridLength.Auto });
            string identifier=string.IsNullOrWhiteSpace(issue.Identifier)?"":issue.Identifier.Trim();
            if(identifier.Length>0) { var id=Text(identifier,11,"MutedBrush",false,new Thickness(0,2,8,0)); id.VerticalAlignment=VerticalAlignment.Center; grid.Children.Add(id); }
            string title=string.IsNullOrWhiteSpace(issue.Title)?"无标题":issue.Title.Trim();
            var name=Text(title,13,"TextBrush",true,new Thickness(0,1,8,0)); name.TextTrimming=TextTrimming.CharacterEllipsis; name.TextWrapping=TextWrapping.NoWrap; name.VerticalAlignment=VerticalAlignment.Center; name.ToolTip=identifier.Length==0?title:identifier+"  "+title; Grid.SetColumn(name,1); grid.Children.Add(name);
            if(!string.IsNullOrWhiteSpace(issue.StateName)) { var state=Text(issue.StateName,10,"MutedBrush"); state.VerticalAlignment=VerticalAlignment.Center; state.Margin=new Thickness(0,0,2,0); Grid.SetColumn(state,2); grid.Children.Add(state); }
            if(!string.IsNullOrWhiteSpace(issue.Url)) { var open=OpenButton(issue.Url,"在 Linear 中打开"); open.VerticalAlignment=VerticalAlignment.Center; Grid.SetColumn(open,3); grid.Children.Add(open); card.MouseLeftButtonUp+=(s,e)=> { OpenUrl(issue.Url); e.Handled=true; }; }
            return card;
        }
        static Button OpenButton(string url,string label) {
            var button=new Button { Content="↗", Width=27, Height=25, Padding=new Thickness(0), FontFamily=new FontFamily("Segoe UI Symbol"), FontSize=13, ToolTip=label, VerticalAlignment=VerticalAlignment.Top };
            AutomationProperties.SetName(button,label);
            button.Click+=(s,e)=> { OpenUrl(url); e.Handled=true; };
            return button;
        }
        static FrameworkElement EmptyLine(string message) { return Text(message,11,"MutedBrush",false,new Thickness(0,8,0,0)); }
        static TextBlock Text(string value,double size,string brush,bool bold=false,Thickness? margin=null) {
            var text=new TextBlock { Text=value??"", FontFamily=new FontFamily("Microsoft YaHei UI"), FontSize=size, FontWeight=bold?FontWeights.SemiBold:FontWeights.Normal, TextWrapping=TextWrapping.Wrap, Margin=margin??new Thickness(0) };
            text.SetResourceReference(TextBlock.ForegroundProperty,brush); return text;
        }
        static string NormalizeColor(string value) {
            if(string.IsNullOrWhiteSpace(value)) return "";
            value=value.Trim();
            if(value.Length==6) value="#"+value;
            return NotesStore.ValidColor(value)?value.ToUpperInvariant():"";
        }
        static void OpenUrl(string url) { try { if(!string.IsNullOrWhiteSpace(url)) Process.Start(url); } catch { } }
    }

    public sealed class LinearIntegration {
        readonly NotesWindow window;
        readonly NotesStore notesStore;
        readonly LinearConnectionStore connectionStore;
        readonly Grid root;
        readonly StackPanel board;
        readonly TextBlock status;
        readonly TextBlock count;
        Button notesButton;
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
            notesButton=IconToolbarButton(NoteIcon(),"便签","NotesSource");
            notesButton.Click+=(s,e)=> { if(linearMode) ShowLocal(); };
            sourceButton=IconToolbarButton(LinearIcon(),"Linear","LinearSource");
            sourceButton.Click+=async (s,e)=> { if(!linearMode) await ShowLinearAsync(false); };
            refreshButton=ToolbarButton("\uE72C","刷新 Linear","LinearRefresh"); refreshButton.Visibility=Visibility.Collapsed; refreshButton.Click+=async (s,e)=>await ShowLinearAsync(true);
            int insert=Math.Min(1,tools.Children.Count);
            tools.Children.Insert(insert,notesButton); tools.Children.Insert(insert+1,sourceButton); tools.Children.Insert(insert+2,refreshButton);
            SetModeButtons();
        }
        static System.Windows.Shapes.Path StrokeIcon(string geometry) {
            var path=new System.Windows.Shapes.Path { Data=Geometry.Parse(geometry), StrokeThickness=1.45, StrokeStartLineCap=PenLineCap.Round, StrokeEndLineCap=PenLineCap.Round, StrokeLineJoin=PenLineJoin.Round, Stretch=Stretch.Uniform, Width=16, Height=16, SnapsToDevicePixels=true, IsHitTestVisible=false };
            path.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty,"TextBrush");
            RenderOptions.SetEdgeMode(path,EdgeMode.Unspecified);
            return path;
        }
        static System.Windows.Shapes.Path FillIcon(string geometry) {
            var path=new System.Windows.Shapes.Path { Data=Geometry.Parse(geometry), Stretch=Stretch.Uniform, Width=16, Height=16, SnapsToDevicePixels=true, IsHitTestVisible=false };
            path.SetResourceReference(System.Windows.Shapes.Shape.FillProperty,"TextBrush");
            RenderOptions.SetEdgeMode(path,EdgeMode.Unspecified);
            return path;
        }
        static System.Windows.Shapes.Path NoteIcon() { return StrokeIcon("M3.1,2.2 H9.3 L12.9,5.8 V13.8 H3.1 Z M9.3,2.4 V5.8 H12.7 M5.2,8.1 H10.5 M5.2,10.6 H8.7"); }
        static System.Windows.Shapes.Path LinearIcon() { return FillIcon("M 2.886 4.18 A 11.982 11.982 0 0 1 11.99 0 C 18.624 0 24 5.376 24 12.009 c 0 3.64 -1.62 6.903 -4.18 9.105 L 2.887 4.18 Z M 1.817 5.626 l 16.556 16.556 c -0.524 0.33 -1.075 0.62 -1.65 0.866 L 0.951 7.277 c 0.247 -0.575 0.537 -1.126 0.866 -1.65 Z M 0.322 9.163 l 14.515 14.515 c -0.71 0.172 -1.443 0.282 -2.195 0.322 L 0 11.358 a 12 12 0 0 1 0.322 -2.195 Z m -0.17 4.862 9.823 9.824 a 12.02 12.02 0 0 1 -9.824 -9.824 Z"); }
        Button IconToolbarButton(UIElement icon,string label,string id) {
            var button=new Button { Width=32, Height=32, Padding=new Thickness(6), ToolTip=label, Content=icon };
            AutomationProperties.SetAutomationId(button,id); AutomationProperties.SetName(button,label); return button;
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
            loading=true; linearMode=true; SetModeButtons(); status.Text="正在读取待办…"; count.Text="";
            try {
                var snapshot=await new LinearApiClient(key).LoadAsync(); lastSnapshot=snapshot; RenderLinear(snapshot); connectionStore.Save(key,snapshot.ViewerName);
            } catch(Exception e) {
                status.Text="Linear 连接失败"; ShowError("读取 Linear 待办失败。",e);
                if(lastSnapshot!=null) RenderLinear(lastSnapshot); else ShowLocal();
            } finally { loading=false; SetModeButtons(); }
        }

        void ShowLocal() {
            linearMode=false; SetModeButtons(); window.RenderBoard(); status.Text="保存在此电脑";
        }

        void SetModeButtons() {
            Highlight(notesButton,!linearMode);
            Highlight(sourceButton,linearMode);
            if(notesButton!=null) { notesButton.ToolTip="便签"; AutomationProperties.SetName(notesButton,"便签"); }
            if(sourceButton!=null) { sourceButton.ToolTip="Linear · 右键管理连接"; AutomationProperties.SetName(sourceButton,linearMode?"待办":"Linear"); }
            if(refreshButton!=null) refreshButton.Visibility=linearMode?Visibility.Visible:Visibility.Collapsed;
            window.SetHeading(linearMode?"待办":"便签");
            window.PreserveStatusOnSave=linearMode;
        }
        static void Highlight(Button button,bool active) { if(button!=null) button.SetResourceReference(Control.BackgroundProperty,active?"SelectionBrush":"CanvasBrush"); }

        void RenderLinear(LinearSnapshot snapshot) {
            linearMode=true; SetModeButtons(); LinearBoardView.Render(board,status,count,snapshot);
        }
        TextBlock Text(string value,double size,string brush,bool bold=false,Thickness? margin=null) {
            var text=new TextBlock { Text=value??"",FontFamily=new FontFamily("Microsoft YaHei UI"),FontSize=size,FontWeight=bold?FontWeights.SemiBold:FontWeights.Normal,TextWrapping=TextWrapping.Wrap,Margin=margin??new Thickness(0) };
            text.SetResourceReference(TextBlock.ForegroundProperty,brush); return text;
        }

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
                catch(Exception ex) { message.Text=ex.InnerException==null || string.IsNullOrWhiteSpace(ex.InnerException.Message)?ex.Message:ex.InnerException.Message; connect.IsEnabled=true; cancel.IsEnabled=true; }
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
