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
        readonly DispatcherTimer hideTimer=new DispatcherTimer { Interval=TimeSpan.FromMilliseconds(180) };
        readonly DispatcherTimer pollTimer=new DispatcherTimer(DispatcherPriority.Input) { Interval=TimeSpan.FromMilliseconds(30) };
        Rect dockArea;
        NativeRect dockWork;
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
                    SetDockArea(WorkArea());
                    window.Top=dockArea.Top;
                }
                if(IsDocked) ScheduleHide();
            };
            window.LocationChanged+=(s,e)=>CheckLocation();
            window.SizeChanged+=(s,e)=> { if(!IsSliding && !suppressLocation) KeepDockedPosition(); };
            window.MouseEnter+=(s,e)=>PollCursor();
            // 点到收起后露出的边条（此时是窗口底部内容，MouseEnter 可能因动画时序没触发）也要能唤回。
            window.PreviewMouseLeftButtonDown+=(s,e)=> { if(IsHidden) { if(IsDocked) RevealEdge(); else ForceShow(WorkArea()); } };
            window.MouseLeave+=(s,e)=>PollCursor();
            window.Deactivated+=(s,e)=>ScheduleHide();
            window.Activated+=(s,e)=> { if(IsHidden) RevealEdge(); };
            window.StateChanged+=(s,e)=> {
                if(window.WindowState!=WindowState.Normal) hideTimer.Stop();
                else ScheduleHide();
            };
            hideTimer.Tick+=(s,e)=>HideTick();
            pollTimer.Tick+=(s,e)=>PollCursor();
            pollTimer.Start();
            window.Closed+=(s,e)=> { CompositionTarget.Rendering-=SlideTick; hideTimer.Stop(); pollTimer.Stop(); };
        }

        public void ScheduleHide() {
            if(!IsDocked || IsHidden || IsSliding || hideTimer.IsEnabled) return;
            hideTimer.Start();
        }

        // 用户按住标题栏时调用：停掉自动收起，未完成的滑动立即到位，拖动不和动画打架。
        public void BeginUserDrag() {
            hideTimer.Stop();
            if(!IsSliding) return;
            CompositionTarget.Rendering-=SlideTick;
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
            if(!IsDocked || IsHidden || window.WindowState!=WindowState.Normal) return;
            SlideTo(TuckedTop(WorkArea().Top,window.ActualHeight),true);
        }

        public void RevealEdge() {
            if(!IsDocked || !IsHidden || window.WindowState!=WindowState.Normal) return;
            hideTimer.Stop();
            // 悬停展开时浮到普通窗口前面，但不抢走正在使用的应用的键盘焦点。
            SetWindowPos(new WindowInteropHelper(window).Handle,IntPtr.Zero,0,0,0,0,0x0013);
            SlideTo(WorkArea().Top,false);
        }

        void HideTick() {
            hideTimer.Stop();
            if(!IsDocked || IsHidden || IsSliding) return;
            if(CursorOverWindow() || System.Windows.Input.Mouse.LeftButton==System.Windows.Input.MouseButtonState.Pressed || window.IsMouseCaptureWithin) return;
            HideEdge();
        }

        void CheckLocation() {
            if(suppressLocation || IsSliding || window.WindowState!=WindowState.Normal) return;
            Rect area=WorkArea();
            if(!IsDocked && ShouldDock(window.Top,area.Top) && !MonitorAbove()) {
                SetDockArea(area);
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
            CompositionTarget.Rendering-=SlideTick;
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
            CompositionTarget.Rendering-=SlideTick;
            slideFrom=window.Top; slideTo=top; slideMilliseconds=180;
            IsSliding=true; IsHidden=hide; suppressLocation=true;
            slideClock.Restart(); CompositionTarget.Rendering+=SlideTick;
        }

        void SlideTick(object sender,EventArgs args) {
            double progress=(double)slideClock.ElapsedMilliseconds/slideMilliseconds;
            if(progress>=1) progress=1;
            double eased=progress*progress*(3-2*progress);
            window.Top=slideFrom+(slideTo-slideFrom)*eased;
            if(progress<1) return;
            CompositionTarget.Rendering-=SlideTick;
            window.Top=slideTo;
            IsSliding=false; suppressLocation=false;
        }

        void PollCursor() {
            if(!IsDocked || window.WindowState!=WindowState.Normal) return;
            if(CursorOverWindow()) {
                hideTimer.Stop();
                if(IsHidden) RevealEdge();
            } else ScheduleHide();
        }

        // 边框属于原生窗口，WPF MouseEnter 不一定触发；鼠标和边条统一用物理像素判断。
        bool CursorOverWindow() {
            NativePoint cursor; NativeRect bounds;
            if(!GetCursorPos(out cursor) || !GetWindowRect(new WindowInteropHelper(window).Handle,out bounds)) return false;
            Rect hit=new Rect(bounds.Left,bounds.Top,Math.Max(0,bounds.Right-bounds.Left),Math.Max(0,bounds.Bottom-bounds.Top));
            if(IsHidden) {
                var source=PresentationSource.FromVisual(window);
                double scale=source==null || source.CompositionTarget==null?1:source.CompositionTarget.TransformToDevice.M22;
                hit=new Rect(bounds.Left,dockWork.Top,hit.Width,Math.Ceiling(RevealStrip*scale)+2);
            }
            return hit.Contains(new Point(cursor.X,cursor.Y));
        }

        void SetDockArea(Rect area) {
            dockArea=area;
            var source=PresentationSource.FromVisual(window);
            if(source==null || !TryMonitorWorkArea(SelfMonitor(source),out dockWork)) {
                dockWork=new NativeRect { Top=(int)area.Top };
            }
            IsDocked=true;
        }

        // 停靠目标屏 = 窗口顶边中点所在的屏。不能用 FromHandle（按面积归属）：
        // 窗口跨在上下两块屏的接缝上时，高度一变归属就翻转，窗口会被来回拽到两块屏的顶边。
        Rect WorkArea() {
            // 收起动画会把窗口移出目标屏；停靠期间始终使用进入停靠时的屏幕。
            if(IsDocked) return dockArea;
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
        [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr window,out NativeRect bounds);
        [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr window,IntPtr after,int x,int y,int width,int height,uint flags);
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
    }
}
