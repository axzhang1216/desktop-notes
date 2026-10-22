using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
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
            readonly object integration;
            readonly StackPanel board;
            readonly TextBlock status;
            readonly TextBlock count;
            bool applying;

            public LinearViewPolish(NotesWindow notesWindow,object linearIntegration) {
                integration=linearIntegration;
                board=Private<StackPanel>(notesWindow,"board");
                status=Private<TextBlock>(notesWindow,"status");
                count=Private<TextBlock>(notesWindow,"count");
            }

            public void Attach() { board.LayoutUpdated+=OnLayoutUpdated; }

            void OnLayoutUpdated(object sender,EventArgs e) {
                if(applying || !LinearMode()) return;
                LinearSnapshot snapshot=Field<LinearSnapshot>(integration,"lastSnapshot");
                if(snapshot==null || IsPolished()) return;
                applying=true;
                try { LinearBoardView.Render(board,status,count,snapshot); } finally { applying=false; }
            }

            bool LinearMode() {
                FieldInfo field=integration.GetType().GetField("linearMode",BindingFlags.Instance|BindingFlags.NonPublic);
                return field!=null && (bool)field.GetValue(integration);
            }

            bool IsPolished() {
                return board.Children.Count==1 && AutomationProperties.GetAutomationId(board.Children[0])=="LinearPolishedRoot";
            }

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
                    var store=new NotesStore(path); store.Load(); var app=new Application { ShutdownMode=ShutdownMode.OnMainWindowClose }; var window=new NotesWindow(store); LinearIntegration.Attach(window,store); LinearIntegrationPolish.Attach(window); ApocalypseIntegration.Attach(window,new ApocalypseBriefStore(),new ApocalypseBriefRunner(null)); OrcaIntegration.Attach(window,store); MiniAgentChat.Attach(window,store); app.Run(window);
                } catch(Exception e) { MessageBox.Show(e.Message,"桌面便签",MessageBoxButton.OK,MessageBoxImage.Error); }
                finally { mutex.ReleaseMutex(); }
            }
        }
    }
}
