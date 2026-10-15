using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace DesktopNotes {
    public static class LinearIntegrationPolish {
        public static void Attach(NotesWindow window) {
            object integration=window.Tag;
            var source=FindButton(window,"LinearSource");
            if(source!=null) {
                source.ToolTip="Linear · 右键管理连接";
                source.MouseRightButtonUp+=(s,e)=> {
                    if(integration!=null) Invoke(integration,"ShowSetup");
                    e.Handled=true;
                };
            }
            var settings=FindButton(window,"Settings");
            if(settings!=null) settings.Click+=(s,e)=>ResetLinearMode(integration);
            if(integration!=null) new LinearViewPolish(window,integration).Attach();
        }

        static void ResetLinearMode(object integration) {
            if(integration==null) return;
            FieldInfo field=integration.GetType().GetField("linearMode",BindingFlags.Instance|BindingFlags.NonPublic);
            if(field==null || !(bool)field.GetValue(integration)) return;
            field.SetValue(integration,false); Invoke(integration,"SetModeButtons");
        }

        static void Invoke(object target,string method) {
            MethodInfo action=target.GetType().GetMethod(method,BindingFlags.Instance|BindingFlags.NonPublic);
            if(action!=null) action.Invoke(target,null);
        }

        static Button FindButton(DependencyObject node,string id) {
            if(node==null) return null;
            var button=node as Button;
            if(button!=null && AutomationProperties.GetAutomationId(button)==id) return button;
            int children=VisualTreeHelper.GetChildrenCount(node);
            for(int i=0;i<children;i++) { var found=FindButton(VisualTreeHelper.GetChild(node,i),id); if(found!=null) return found; }
            return null;
        }

        sealed class LinearViewPolish {
            readonly NotesWindow window;
            readonly object integration;
            readonly StackPanel board;
            readonly TextBlock status;
            readonly TextBlock count;
            bool applying;

            public LinearViewPolish(NotesWindow notesWindow,object linearIntegration) {
                window=notesWindow; integration=linearIntegration;
                board=Private<StackPanel>(window,"board");
                status=Private<TextBlock>(window,"status");
                count=Private<TextBlock>(window,"count");
            }

            public void Attach() { board.LayoutUpdated+=OnLayoutUpdated; }

            void OnLayoutUpdated(object sender,EventArgs e) {
                if(applying || !LinearMode()) return;
                LinearSnapshot snapshot=Field<LinearSnapshot>(integration,"lastSnapshot");
                if(snapshot==null || IsPolished()) return;
                applying=true;
                try { Render(snapshot); } finally { applying=false; }
            }

            bool LinearMode() {
                FieldInfo field=integration.GetType().GetField("linearMode",BindingFlags.Instance|BindingFlags.NonPublic);
                return field!=null && (bool)field.GetValue(integration);
            }

            bool IsPolished() {
                return board.Children.Count==1 && AutomationProperties.GetAutomationId(board.Children[0])=="LinearPolishedRoot";
            }

            void Render(LinearSnapshot snapshot) {
                board.Children.Clear();
                var root=new StackPanel(); AutomationProperties.SetAutomationId(root,"LinearPolishedRoot"); board.Children.Add(root);
                var projects=snapshot.Projects.OrderByDescending(p=>ParseTime(p.UpdatedAt)).ToList();
                foreach(LinearProjectItem project in projects) root.Children.Add(ProjectGroup(project));
                if(snapshot.LooseDocuments.Count>0) root.Children.Add(LooseDocumentsGroup(snapshot.LooseDocuments));
                if(projects.Count==0 && snapshot.LooseDocuments.Count==0) root.Children.Add(EmptyLine("Linear 里还没有可显示的项目或文档。"));
                int documents=projects.Sum(p=>p.Documents.Count)+snapshot.LooseDocuments.Count;
                count.Text=projects.Count+" 个项目 · "+documents+" 条笔记";
                status.Text="Linear · 只读 · "+DateTime.Now.ToString("HH:mm")+" 同步";
            }

            FrameworkElement ProjectGroup(LinearProjectItem project) {
                var group=new StackPanel { Margin=new Thickness(0,0,0,20) };
                var row=new Grid { Margin=new Thickness(0,0,0,5) };
                row.ColumnDefinitions.Add(new ColumnDefinition());
                row.ColumnDefinitions.Add(new ColumnDefinition { Width=GridLength.Auto });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width=GridLength.Auto });

                var title=new StackPanel { Orientation=Orientation.Horizontal,VerticalAlignment=VerticalAlignment.Center };
                var mark=new Border { Width=4,Height=16,CornerRadius=new CornerRadius(2),Margin=new Thickness(0,0,9,0) };
                mark.SetResourceReference(Border.BackgroundProperty,"MutedBrush"); title.Children.Add(mark);
                title.Children.Add(Text(project.Name,15,"TextBrush",true));
                title.Children.Add(Text(project.Documents.Count.ToString("00"),10.5,"MutedBrush",false,new Thickness(9,3,0,0)));
                row.Children.Add(title);

                if(!string.IsNullOrWhiteSpace(project.Status)) {
                    var state=Text(project.Status,10,"MutedBrush"); state.VerticalAlignment=VerticalAlignment.Center; state.Margin=new Thickness(8,0,3,0); Grid.SetColumn(state,1); row.Children.Add(state);
                }
                if(!string.IsNullOrWhiteSpace(project.Url)) { var open=OpenButton(project.Url,"打开 Linear 项目"); Grid.SetColumn(open,2); row.Children.Add(open); }
                group.Children.Add(row);

                string source=!string.IsNullOrWhiteSpace(project.Summary)?project.Summary:project.Description;
                string intro=Clip(CleanMarkdown(source),220);
                if(!string.IsNullOrWhiteSpace(intro)) {
                    var summary=Text(intro,11,"MutedBrush",false,new Thickness(13,0,2,9)); summary.LineHeight=18; summary.MaxHeight=40; summary.TextTrimming=TextTrimming.CharacterEllipsis; group.Children.Add(summary);
                }

                var docs=project.Documents.OrderByDescending(d=>ParseTime(d.UpdatedAt)).ToList();
                if(docs.Count==0) group.Children.Add(EmptyLine("暂无 Linear 文档",new Thickness(13,3,0,1)));
                else foreach(LinearDocumentItem doc in docs) group.Children.Add(DocumentCard(doc));
                return group;
            }

            FrameworkElement DocumentCard(LinearDocumentItem document) {
                var card=new Border { CornerRadius=new CornerRadius(8),Padding=new Thickness(14,11,10,9),Margin=new Thickness(0,0,0,7) };
                card.SetResourceReference(Border.BackgroundProperty,"SurfaceBrush");
                var stack=new StackPanel(); card.Child=stack;

                var header=new Grid(); header.ColumnDefinitions.Add(new ColumnDefinition()); header.ColumnDefinitions.Add(new ColumnDefinition { Width=GridLength.Auto });
                var title=Text(string.IsNullOrWhiteSpace(document.Title)?"无标题文档":document.Title,13.5,"TextBrush",true,new Thickness(0,1,7,0));
                header.Children.Add(title);
                if(!string.IsNullOrWhiteSpace(document.Url)) { var open=OpenButton(document.Url,"在 Linear 中打开"); Grid.SetColumn(open,1); header.Children.Add(open); }
                stack.Children.Add(header);

                string full=CleanMarkdown(document.Content);
                bool lengthy=full.Length>480 || full.Count(c=>c=='\n')>5;
                TextBlock body=null;
                if(!string.IsNullOrWhiteSpace(full)) {
                    body=Text(lengthy?Clip(full,480):full,12,"BodyBrush",false,new Thickness(0,6,2,0));
                    body.LineHeight=20; body.MaxHeight=82; body.TextTrimming=TextTrimming.CharacterEllipsis; stack.Children.Add(body);
                }

                var footer=new Grid { Margin=new Thickness(0,7,0,0) }; footer.ColumnDefinitions.Add(new ColumnDefinition()); footer.ColumnDefinitions.Add(new ColumnDefinition { Width=GridLength.Auto });
                if(lengthy && body!=null) {
                    bool expanded=false;
                    var expand=new Button { Content="展开全文",FontSize=10.5,HorizontalAlignment=HorizontalAlignment.Left,Padding=new Thickness(0,3,8,3),Margin=new Thickness(0,-2,0,-2) };
                    expand.SetResourceReference(Control.ForegroundProperty,"MutedBrush");
                    expand.Click+=(s,e)=> {
                        expanded=!expanded;
                        body.Text=expanded?full:Clip(full,480);
                        body.MaxHeight=expanded?double.PositiveInfinity:82;
                        body.TextTrimming=expanded?TextTrimming.None:TextTrimming.CharacterEllipsis;
                        expand.Content=expanded?"收起":"展开全文";
                        e.Handled=true;
                    };
                    footer.Children.Add(expand);
                }
                DateTimeOffset updated=ParseTime(document.UpdatedAt);
                var time=Text(updated==DateTimeOffset.MinValue?"Linear 文档":"更新 "+updated.ToLocalTime().ToString("MM.dd HH:mm"),10,"MutedBrush");
                time.VerticalAlignment=VerticalAlignment.Center; Grid.SetColumn(time,1); footer.Children.Add(time); stack.Children.Add(footer);
                return card;
            }

            FrameworkElement LooseDocumentsGroup(System.Collections.Generic.List<LinearDocumentItem> docs) {
                var group=new StackPanel { Margin=new Thickness(0,0,0,20) };
                var title=new StackPanel { Orientation=Orientation.Horizontal,VerticalAlignment=VerticalAlignment.Center,Margin=new Thickness(0,0,0,8) };
                var mark=new Border { Width=4,Height=16,CornerRadius=new CornerRadius(2),Margin=new Thickness(0,0,9,0) };
                mark.SetResourceReference(Border.BackgroundProperty,"MutedBrush"); title.Children.Add(mark);
                title.Children.Add(Text("其他 Linear 文档",15,"TextBrush",true));
                title.Children.Add(Text(docs.Count.ToString("00"),10.5,"MutedBrush",false,new Thickness(9,3,0,0))); group.Children.Add(title);
                foreach(LinearDocumentItem doc in docs.OrderByDescending(d=>ParseTime(d.UpdatedAt))) group.Children.Add(DocumentCard(doc));
                return group;
            }

            Button OpenButton(string url,string label) {
                var button=new Button { Content="↗",Width=27,Height=25,Padding=new Thickness(0),FontFamily=new FontFamily("Segoe UI Symbol"),FontSize=13,ToolTip=label,VerticalAlignment=VerticalAlignment.Top };
                AutomationProperties.SetName(button,label);
                button.Click+=(s,e)=> { OpenUrl(url); e.Handled=true; };
                return button;
            }

            FrameworkElement EmptyLine(string message) { return EmptyLine(message,new Thickness(0,8,0,0)); }
            FrameworkElement EmptyLine(string message,Thickness margin) { return Text(message,11,"MutedBrush",false,margin); }

            TextBlock Text(string value,double size,string brush,bool bold=false,Thickness? margin=null) {
                var text=new TextBlock { Text=value??"",FontFamily=new FontFamily("Microsoft YaHei UI"),FontSize=size,FontWeight=bold?FontWeights.SemiBold:FontWeights.Normal,TextWrapping=TextWrapping.Wrap,Margin=margin??new Thickness(0) };
                text.SetResourceReference(TextBlock.ForegroundProperty,brush); return text;
            }

            static string CleanMarkdown(string markdown) {
                if(string.IsNullOrWhiteSpace(markdown)) return "";
                string text=markdown.Replace("\r","");
                text=Regex.Replace(text,@"(?m)^\s{0,3}#{1,6}\s*","");
                text=Regex.Replace(text,@"!\[[^\]]*\]\([^\)]*\)","");
                text=Regex.Replace(text,@"\[([^\]]+)\]\([^\)]*\)","$1");
                text=text.Replace("```","").Replace("**","").Replace("__","").Replace("`","");
                text=Regex.Replace(text,@"\n{3,}","\n\n").Trim();
                return text;
            }

            static string Clip(string text,int max) {
                if(string.IsNullOrEmpty(text) || text.Length<=max) return text??"";
                return text.Substring(0,max).TrimEnd()+"…";
            }

            static DateTimeOffset ParseTime(string value) {
                DateTimeOffset result;
                return DateTimeOffset.TryParse(value,CultureInfo.InvariantCulture,DateTimeStyles.RoundtripKind,out result)?result:DateTimeOffset.MinValue;
            }

            static void OpenUrl(string url) { try { if(!string.IsNullOrWhiteSpace(url)) Process.Start(url); } catch { } }

            static T Private<T>(object owner,string name) where T:class {
                FieldInfo field=owner.GetType().GetField(name,BindingFlags.Instance|BindingFlags.NonPublic);
                if(field==null) throw new MissingFieldException(owner.GetType().FullName,name);
                return field.GetValue(owner) as T;
            }

            static T Field<T>(object owner,string name) where T:class {
                FieldInfo field=owner.GetType().GetField(name,BindingFlags.Instance|BindingFlags.NonPublic);
                return field==null?null:field.GetValue(owner) as T;
            }
        }
    }

    public static class LinearProgramV2 {
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
                    var store=new NotesStore(path); store.Load(); var app=new Application { ShutdownMode=ShutdownMode.OnMainWindowClose }; var window=new NotesWindow(store); LinearIntegration.Attach(window,store); LinearIntegrationPolish.Attach(window); app.Run(window);
                } catch(Exception e) { MessageBox.Show(e.Message,"桌面便签",MessageBoxButton.OK,MessageBoxImage.Error); }
                finally { mutex.ReleaseMutex(); }
            }
        }
    }
}
