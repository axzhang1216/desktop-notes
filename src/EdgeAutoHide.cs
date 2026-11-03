using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace DesktopNotes {
    // QQ 式上沿自动隐藏：窗口拖到屏幕顶端后贴边，鼠标离开就缩到只剩一条边，
    // 鼠标回到顶部再滑出来。只在窗口贴边时生效，其他位置保持普通窗口行为。
    public sealed class EdgeAutoHide {
        public const double RevealStrip=6;
        public const double DockThreshold=10;

        readonly Window window;
        readonly Placement placement;
        readonly DispatcherTimer slideTimer=new DispatcherTimer { Interval=TimeSpan.FromMilliseconds(16) };
        readonly DispatcherTimer hideTimer=new DispatcherTimer { Interval=TimeSpan.FromMilliseconds(700) };
        readonly DispatcherTimer pollTimer=new DispatcherTimer { Interval=TimeSpan.FromMilliseconds(120) };
        readonly Stopwatch slideClock=new Stopwatch();
        double slideFrom,slideTo;
        int slideMilliseconds;
        bool suppressLocation;

        public bool IsDocked { get; private set; }
        public bool IsHidden { get; private set; }
        public bool IsSliding { get; private set; }
        public double DockedTop { get { return IsDocked?WorkArea().Top:window.Top; } }

        public EdgeAutoHide(Window owner,Placement savedPlacement) {
            window=owner; placement=savedPlacement;
        }

        public static bool ShouldDock(double top,double workTop) {
            return top>=workTop-2 && top<=workTop+DockThreshold;
        }
        public static double TuckedTop(double workTop,double height) {
            return workTop-Math.Max(0,height)+RevealStrip;
        }

        public void Attach() {
            window.Loaded+=(s,e)=> {
                if(placement!=null && placement.EdgeDock && !MonitorAbove()) {
                    IsDocked=true;
                    window.Top=WorkArea().Top;
                }
                if(IsDocked) ScheduleHide();
            };
            window.LocationChanged+=(s,e)=>CheckLocation();
            window.SizeChanged+=(s,e)=> { if(!IsSliding && !suppressLocation) KeepDockedPosition(); };
            window.MouseEnter+=(s,e)=> {
                hideTimer.Stop();
                if(IsHidden) RevealEdge();
            };
            // 点到收起后露出的边条（此时是窗口底部内容，MouseEnter 可能因动画时序没触发）也要能唤回。
            window.PreviewMouseLeftButtonDown+=(s,e)=> { if(IsHidden) { if(IsDocked) RevealEdge(); else ForceShow(WorkArea()); } };
            window.MouseLeave+=(s,e)=>ScheduleHide();
            window.Deactivated+=(s,e)=>ScheduleHide();
            window.Activated+=(s,e)=>hideTimer.Stop();
            window.StateChanged+=(s,e)=> {
                if(window.WindowState!=WindowState.Normal) hideTimer.Stop();
                else ScheduleHide();
            };
            slideTimer.Tick+=(s,e)=>SlideTick();
            hideTimer.Tick+=(s,e)=>HideTick();
            pollTimer.Tick+=(s,e)=>PollCursor();
            pollTimer.Start();
            window.Closed+=(s,e)=> { slideTimer.Stop(); hideTimer.Stop(); pollTimer.Stop(); };
        }

        public void ScheduleHide() {
            if(!IsDocked || IsHidden || IsSliding) return;
            hideTimer.Stop(); hideTimer.Start();
        }

        // 用户按住标题栏时调用：停掉自动收起，未完成的滑动立即到位，拖动不和动画打架。
        public void BeginUserDrag() {
            hideTimer.Stop();
            if(!IsSliding) return;
            slideTimer.Stop();
            suppressLocation=true;
            try { window.Top=slideTo; } finally { suppressLocation=false; }
            IsSliding=false;
        }

        // 手动拖动时把靠近或高于屏幕上沿的位置吸到停靠线，代替原生 DragMove 的贴顶行为。
        // 上方还有别的显示器时不吸附：收起会藏进上面那块屏。
        public double SnapDragTop(double top) {
            double workTop=WorkArea().Top;
            if(top>workTop+DockThreshold) return top;
            return MonitorAbove()?top:workTop;
        }

        public void HideEdge() {
            if(!IsDocked || IsHidden || IsSliding || window.WindowState!=WindowState.Normal) return;
            SlideTo(TuckedTop(WorkArea().Top,window.ActualHeight),true);
        }

        public void RevealEdge() {
            if(!IsDocked || IsSliding) return;
            SlideTo(WorkArea().Top,false);
        }

        void HideTick() {
            hideTimer.Stop();
            if(!IsDocked || IsHidden || IsSliding) return;
            if(window.IsMouseOver) return;
            // 鼠标离开了，但键盘还在这个窗口里，比如正在输入时把鼠标挪开：不收起。
            if(window.IsActive && window.IsKeyboardFocusWithin) return;
            HideEdge();
        }

        void CheckLocation() {
            if(suppressLocation || IsSliding || window.WindowState!=WindowState.Normal) return;
            Rect area=WorkArea();
            if(!IsDocked && ShouldDock(window.Top,area.Top) && !MonitorAbove()) {
                IsDocked=true;
                suppressLocation=true;
                try { window.Top=area.Top; } finally { suppressLocation=false; }
                if(placement!=null) placement.EdgeDock=true;
                ScheduleHide();
            } else if(IsDocked && Math.Abs(window.Top-area.Top)>DockThreshold) {
                IsDocked=false;
                if(placement!=null) placement.EdgeDock=false;
                hideTimer.Stop();
                // 收起状态下被拖离/移开：直接放回屏幕内，否则窗口留在屏外无法再操作。
                if(IsHidden) ForceShow(area);
            }
        }

        void ForceShow(Rect area) {
            slideTimer.Stop();
            IsHidden=false; IsSliding=false;
            suppressLocation=true;
            try { window.Top=area.Top+DockThreshold+4; } finally { suppressLocation=false; }
        }

        void KeepDockedPosition() {
            if(!IsDocked || window.WindowState!=WindowState.Normal) return;
            double top=IsHidden?TuckedTop(WorkArea().Top,window.ActualHeight):WorkArea().Top;
            if(Math.Abs(window.Top-top)>0.5) {
                suppressLocation=true;
                try { window.Top=top; } finally { suppressLocation=false; }
            }
        }

        void SlideTo(double top,bool hide) {
            slideFrom=window.Top; slideTo=top; slideMilliseconds=hide?170:200;
            IsSliding=true; IsHidden=hide; suppressLocation=true;
            slideClock.Restart(); slideTimer.Start();
        }

        void SlideTick() {
            double progress=(double)slideClock.ElapsedMilliseconds/slideMilliseconds;
            if(progress>=1) progress=1;
            double eased=1-Math.Pow(1-progress,3);
            window.Top=slideFrom+(slideTo-slideFrom)*eased;
            if(progress<1) return;
            slideTimer.Stop();
            window.Top=slideTo;
            IsSliding=false; suppressLocation=false;
        }

        void PollCursor() {
            if(!IsDocked || !IsHidden || IsSliding) return;
            Point cursor;
            if(!TryCursorPosition(out cursor)) return;
            Rect area=WorkArea();
            if(cursor.X>=window.Left-2 && cursor.X<=window.Left+window.ActualWidth+2 && cursor.Y<=area.Top+RevealStrip+2) RevealEdge();
        }

        // 停靠目标屏 = 窗口顶边中点所在的屏。不能用 FromHandle（按面积归属）：
        // 窗口跨在上下两块屏的接缝上时，高度一变归属就翻转，窗口会被来回拽到两块屏的顶边。
        Rect WorkArea() {
            try {
                var helper=new WindowInteropHelper(window);
                PresentationSource source=PresentationSource.FromVisual(window);
                if(helper.Handle!=IntPtr.Zero && source!=null && source.CompositionTarget!=null) {
                    NativeRect work;
                    if(TryMonitorWorkArea(SelfMonitor(source),out work)) {
                        Matrix fromDevice=source.CompositionTarget.TransformFromDevice;
                        Point topLeft=fromDevice.Transform(new Point(work.Left,work.Top));
                        Point bottomRight=fromDevice.Transform(new Point(work.Right,work.Bottom));
                        return new Rect(topLeft,bottomRight);
                    }
                    System.Drawing.Rectangle bounds=System.Windows.Forms.Screen.FromHandle(helper.Handle).WorkingArea;
                    Matrix transform=source.CompositionTarget.TransformFromDevice;
                    Point tl=transform.Transform(new Point(bounds.Left,bounds.Top));
                    Point br=transform.Transform(new Point(bounds.Right,bounds.Bottom));
                    return new Rect(tl,br);
                }
            } catch(Exception) { }
            return SystemParameters.WorkArea;
        }

        IntPtr SelfMonitor(PresentationSource source) {
            Matrix toDevice=source.CompositionTarget.TransformToDevice;
            Point topCenter=toDevice.Transform(new Point(window.Left+Math.Max(1,window.ActualWidth)/2,window.Top+2));
            return MonitorFromPoint(new NativePoint { X=(int)topCenter.X,Y=(int)topCenter.Y },MONITOR_DEFAULTTONEAREST);
        }

        static bool TryMonitorWorkArea(IntPtr monitor,out NativeRect work) {
            work=new NativeRect();
            if(monitor==IntPtr.Zero) return false;
            var info=new MonitorInfo(); info.Size=Marshal.SizeOf(typeof(MonitorInfo));
            if(!GetMonitorInfo(monitor,ref info)) return false;
            work=info.Work;
            return true;
        }

        // 窗口顶边正上方是否压着另一块显示器（上下分屏的接缝）。是则不允许停靠/收起。
        bool MonitorAbove() {
            try {
                PresentationSource source=PresentationSource.FromVisual(window);
                if(source==null || source.CompositionTarget==null) return false;
                NativeRect work;
                if(!TryMonitorWorkArea(SelfMonitor(source),out work)) return false;
                Matrix toDevice=source.CompositionTarget.TransformToDevice;
                Point left=toDevice.Transform(new Point(window.Left,0));
                Point right=toDevice.Transform(new Point(window.Left+Math.Max(1,window.ActualWidth),0));
                var rect=new NativeRect {
                    Left=(int)Math.Min(left.X,right.X),
                    Right=Math.Max((int)Math.Min(left.X,right.X)+1,(int)Math.Max(left.X,right.X)),
                    Top=work.Top-12,
                    Bottom=work.Top-4
                };
                IntPtr above=MonitorFromRect(ref rect,MONITOR_DEFAULTTONULL);
                return above!=IntPtr.Zero && above!=SelfMonitor(source);
            } catch(Exception) { return false; }
        }

        [StructLayout(LayoutKind.Sequential)] struct NativePoint { public int X; public int Y; }
        [StructLayout(LayoutKind.Sequential)] struct NativeRect { public int Left; public int Top; public int Right; public int Bottom; }
        [StructLayout(LayoutKind.Sequential)] struct MonitorInfo { public int Size; public NativeRect Monitor; public NativeRect Work; public uint Flags; }
        const uint MONITOR_DEFAULTTONULL=0;
        const uint MONITOR_DEFAULTTONEAREST=2;
        [DllImport("user32.dll")] static extern bool GetCursorPos(out NativePoint point);
        [DllImport("user32.dll")] static extern IntPtr MonitorFromPoint(NativePoint point,uint flags);
        [DllImport("user32.dll")] static extern IntPtr MonitorFromRect(ref NativeRect rect,uint flags);
        [DllImport("user32.dll")] static extern bool GetMonitorInfo(IntPtr monitor,ref MonitorInfo info);
        public static bool CursorPosition(Visual visual,out Point point) {
            point=new Point();
            NativePoint native;
            if(!GetCursorPos(out native)) return false;
            try {
                PresentationSource source=PresentationSource.FromVisual(visual);
                if(source!=null && source.CompositionTarget!=null) point=source.CompositionTarget.TransformFromDevice.Transform(new Point(native.X,native.Y));
                else point=new Point(native.X,native.Y);
                return true;
            } catch(Exception) { return false; }
        }
        bool TryCursorPosition(out Point point) {
            return CursorPosition(window,out point);
        }
    }
}
