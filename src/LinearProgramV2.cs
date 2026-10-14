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
using System.Windows.Input;
using System.Windows.Media;

namespace DesktopNotes {
    public static class LinearIntegrationPolish {
        public static void Attach(NotesWindow window) {
            var source=FindButton(window,"LinearSource");
            if(source!=null) {
                source.ToolTip="Linear · 右键管理连接";
                source.MouseRightButtonUp+=(s,e)=> {
                    object integration=window.Tag;
                    if(integration!=null) Invoke(integration,"ShowSetup");
                    e.Handled=true;
                };
            }
            var settings=FindButton(window,"Settings");
            if(settings!=null) settings.Click+=(s,e)=>ResetLinearMode(window.Tag);
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
