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
                if(placement!=null && placement.EdgeDock) {
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
            if(!IsDocked && ShouldDock(window.Top,area.Top)) {
                IsDocked=true;
                suppressLocation=true;
                try { window.Top=area.Top; } finally { suppressLocation=false; }
                if(placement!=null) placement.EdgeDock=true;
                ScheduleHide();
            } else if(IsDocked && Math.Abs(window.Top-area.Top)>DockThreshold) {
                IsDocked=false;
                if(placement!=null) placement.EdgeDock=false;
                hideTimer.Stop();
                if(IsHidden) RevealEdge();
            }
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

        Rect WorkArea() {
            try {
                var helper=new WindowInteropHelper(window);
                if(helper.Handle!=IntPtr.Zero) {
                    System.Drawing.Rectangle bounds=System.Windows.Forms.Screen.FromHandle(helper.Handle).WorkingArea;
                    PresentationSource source=PresentationSource.FromVisual(window);
                    if(source!=null && source.CompositionTarget!=null) {
                        Matrix transform=source.CompositionTarget.TransformFromDevice;
                        Point topLeft=transform.Transform(new Point(bounds.Left,bounds.Top));
                        Point bottomRight=transform.Transform(new Point(bounds.Right,bounds.Bottom));
                        return new Rect(topLeft,bottomRight);
                    }
                    return new Rect(bounds.X,bounds.Y,bounds.Width,bounds.Height);
                }
            } catch(Exception) { }
            return SystemParameters.WorkArea;
        }

        [StructLayout(LayoutKind.Sequential)] struct NativePoint { public int X; public int Y; }
        [DllImport("user32.dll")] static extern bool GetCursorPos(out NativePoint point);
        bool TryCursorPosition(out Point point) {
            point=new Point();
            NativePoint native;
            if(!GetCursorPos(out native)) return false;
            try {
                PresentationSource source=PresentationSource.FromVisual(window);
                if(source!=null && source.CompositionTarget!=null) point=source.CompositionTarget.TransformFromDevice.Transform(new Point(native.X,native.Y));
                else point=new Point(native.X,native.Y);
                return true;
            } catch(Exception) { return false; }
        }
    }
}
