using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Shapes;
using System.Numerics;
using Windows.UI;
namespace CleanC.App;
internal enum LiquidButtonTone { Neutral, Accent, Success, Ghost }
internal static class Ui
{
 public static bool Dark;
 public static Color Color(string hex){hex=hex.TrimStart('#');return Windows.UI.Color.FromArgb(255,Convert.ToByte(hex[..2],16),Convert.ToByte(hex.Substring(2,2),16),Convert.ToByte(hex.Substring(4,2),16));}
 public static SolidColorBrush B(string h)=>new(Color(h));
 public static Brush Text=>B(Dark?"EDF3FC":"172942");
 public static Brush Muted=>B(Dark?"9EAEC3":"718198");
 public static Brush Accent=>B(Dark?"80B3FF":"387AE8");
 public static Brush DriverAccent=>B(Dark?"B8D6FF":"2F6FD8");
 public static Brush Warning=>B(Dark?"FFD27A":"9A5A00");
 public static Brush Success=>B(Dark?"69D89C":"208A56");
 public static Brush Danger=>B(Dark?"FF8A8A":"C93F3F");
 public static TextBlock T(string text,double size=14,bool bold=false,Brush? color=null)=>new(){Text=text,FontSize=size,FontWeight=bold?Microsoft.UI.Text.FontWeights.SemiBold:Microsoft.UI.Text.FontWeights.Normal,Foreground=color??Text,TextWrapping=TextWrapping.Wrap};
 public static StackPanel Stack(double gap=12,params UIElement[] children){var s=new StackPanel{Spacing=gap};foreach(var c in children)s.Children.Add(c);return s;}
 public static StackPanel Row(double gap=12,params UIElement[] children){var s=Stack(gap,children);s.Orientation=Orientation.Horizontal;s.VerticalAlignment=VerticalAlignment.Center;return s;}
 public static Border Card(UIElement child,Thickness? padding=null)=>new(){Child=child,Padding=padding??new Thickness(24),CornerRadius=new CornerRadius(24),Background=new SolidColorBrush(Windows.UI.Color.FromArgb(Dark?(byte)150:(byte)205,Dark?(byte)30:(byte)255,Dark?(byte)46:(byte)255,Dark?(byte)68:(byte)255)),BorderBrush=new SolidColorBrush(Windows.UI.Color.FromArgb(90,255,255,255)),BorderThickness=new Thickness(1)};
 public static Brush GlassBrush(){try{return new AcrylicBrush{TintColor=Color(Dark?"1D2B3D":"F5FAFF"),TintOpacity=Dark ? .82 : .78,FallbackColor=Color(Dark?"223247":"EDF5FF")};}catch{return new SolidColorBrush(Windows.UI.Color.FromArgb(Dark?(byte)220:(byte)235,Dark?(byte)34:(byte)245,Dark?(byte)50:(byte)250,Dark?(byte)71:(byte)255));}}
 public static Brush NavigationGlassBrush(){try{return new AcrylicBrush{TintColor=Color(Dark?"F4F6F8":"FFFFFF"),TintOpacity=Dark ? .22 : .34,FallbackColor=Color(Dark?"343638":"F2F3F4")};}catch{return new SolidColorBrush(Windows.UI.Color.FromArgb(Dark?(byte)82:(byte)110,Dark?(byte)244:(byte)255,Dark?(byte)246:(byte)255,Dark?(byte)248:(byte)255));}}
 public static Brush NavigationHoverBrush(){try{return new AcrylicBrush{TintColor=Color(Dark?"EEF4FA":"FFFFFF"),TintOpacity=Dark ? .25 : .46,FallbackColor=Color(Dark?"34404C":"F4F8FC")};}catch{return new SolidColorBrush(Windows.UI.Color.FromArgb(Dark?(byte)46:(byte)62,255,255,255));}}
 public static Brush NavigationPressedBrush(){try{return new AcrylicBrush{TintColor=Color(Dark?"FFFFFF":"FFFFFF"),TintOpacity=Dark ? .36 : .58,FallbackColor=Color(Dark?"3E4A56":"EDF3F8")};}catch{return new SolidColorBrush(Windows.UI.Color.FromArgb(Dark?(byte)72:(byte)88,255,255,255));}}
 public static Brush TransitionGlassBrush(){try{return new AcrylicBrush{TintColor=Color(Dark?"ECEEEF":"FFFFFF"),TintOpacity=Dark ? .09 : .13,FallbackColor=Color(Dark?"242628":"FAFAFA")};}catch{return new SolidColorBrush(Windows.UI.Color.FromArgb(Dark?(byte)30:(byte)38,255,255,255));}}
 public static Border GlassCard(UIElement child,Thickness? padding=null)=>new(){Child=child,Padding=padding??new Thickness(24),CornerRadius=new CornerRadius(24),Background=GlassBrush(),BorderBrush=new SolidColorBrush(Windows.UI.Color.FromArgb(88,255,255,255)),BorderThickness=new Thickness(1)};
 public static Button Button(string label,Action action,bool primary=false)
 {
  var b=new Button
  {
   Content=label,Height=44,MinWidth=0,Padding=new Thickness(18,0,18,0),
   CornerRadius=new CornerRadius(16),HorizontalAlignment=HorizontalAlignment.Left,
   VerticalAlignment=VerticalAlignment.Center
  };
  ApplyLiquidButton(b,primary?LiquidButtonTone.Accent:LiquidButtonTone.Neutral);
  b.Click+=(_,_)=>action();
  return b;
 }

 public static void ApplyLiquidButton(Button b,LiquidButtonTone tone=LiquidButtonTone.Neutral,bool compact=false)
 {
  var tint=tone switch
  {
   LiquidButtonTone.Accent=>Color(Dark?"5E98F2":"397BE9"),
   LiquidButtonTone.Success=>Color(Dark?"4FC486":"20A464"),
   _=>Color(Dark?"EAF2FB":"FFFFFF")
  };
  var text=tone switch
  {
   LiquidButtonTone.Accent=>B("FFFFFF"),
   LiquidButtonTone.Success=>B(Dark?"9AF0BD":"137A48"),
   _=>Text
  };
  var normal=tone switch
  {
   LiquidButtonTone.Ghost=>new SolidColorBrush(Windows.UI.Color.FromArgb(0,0,0,0)),
   LiquidButtonTone.Accent=>LiquidAcrylic(tint,Dark ? .70 : .82,Color(Dark?"356CB5":"2F73DD")),
   LiquidButtonTone.Success=>LiquidAcrylic(tint,Dark ? .24 : .18,Color(Dark?"244A38":"E8F7EF")),
   _=>LiquidAcrylic(tint,Dark ? .18 : .34,Color(Dark?"263544":"F2F7FB"))
  };
  var hover=tone switch
  {
   LiquidButtonTone.Ghost=>LiquidAcrylic(Color(Dark?"F3F8FE":"FFFFFF"),Dark ? .22 : .52,Color(Dark?"31404E":"F4F8FC")),
   LiquidButtonTone.Accent=>LiquidAcrylic(Color(Dark?"76AAFA":"4B88ED"),Dark ? .77 : .88,Color(Dark?"3A78C9":"367FE8")),
   LiquidButtonTone.Success=>LiquidAcrylic(Color(Dark?"70D8A1":"DDF7E9"),Dark ? .34 : .62,Color(Dark?"285A40":"E4F7EC")),
   _=>LiquidAcrylic(Color(Dark?"F7FBFF":"FFFFFF"),Dark ? .29 : .58,Color(Dark?"334454":"F7FAFD"))
  };
  var pressed=tone switch
  {
   LiquidButtonTone.Ghost=>LiquidAcrylic(Color(Dark?"FFFFFF":"FFFFFF"),Dark ? .34 : .66,Color(Dark?"3C4A57":"EEF4F8")),
   LiquidButtonTone.Accent=>LiquidAcrylic(Color(Dark?"4C84D9":"2F70D5"),Dark ? .86 : .94,Color(Dark?"3065AA":"2868CB")),
   LiquidButtonTone.Success=>LiquidAcrylic(Color(Dark?"56C78D":"CFEEDC"),Dark ? .42 : .72,Color(Dark?"245238":"D7F0E1")),
   _=>LiquidAcrylic(Color(Dark?"FFFFFF":"FFFFFF"),Dark ? .38 : .70,Color(Dark?"3A4A59":"EDF4F8"))
  };
  var edgeTint=tone switch
  {
   LiquidButtonTone.Accent=>Color("B9D7FF"),
   LiquidButtonTone.Success=>Color("8DE0B0"),
   _=>Color("FFFFFF")
  };

  b.Foreground=text;
  b.Background=normal;
  b.BorderBrush=LiquidEdge(edgeTint,false,.5);
  b.BorderThickness=new Thickness(1);
  b.CornerRadius=new CornerRadius(compact?13:16);
  b.RenderTransformOrigin=new Windows.Foundation.Point(.5,.5);
  b.UseSystemFocusVisuals=true;
  var hovering=false;

  b.PointerEntered+=(_,_)=>{
   if(!b.IsEnabled)return;
   hovering=true;b.Background=hover;b.BorderBrush=LiquidEdge(edgeTint,true,.34);
   AnimateLiquidScale(b,1.012f,150);
  };
  b.PointerMoved+=(_,e)=>{
   if(!hovering||!b.IsEnabled||b.ActualWidth<2)return;
   var x=Math.Clamp(e.GetCurrentPoint(b).Position.X/b.ActualWidth,0d,1d);
   b.BorderBrush=LiquidEdge(edgeTint,true,x);
  };
  b.PointerPressed+=(_,_)=>{
   if(!b.IsEnabled)return;
   b.Background=pressed;b.BorderBrush=LiquidEdge(edgeTint,true,.62);
   AnimateLiquidScale(b,.985f,90);
  };
  b.PointerReleased+=(_,_)=>{
   if(!b.IsEnabled)return;
   b.Background=hovering?hover:normal;b.BorderBrush=LiquidEdge(edgeTint,hovering,.50);
   AnimateLiquidScale(b,1.012f,120);
  };
  b.PointerExited+=(_,_)=>{
   hovering=false;b.Background=normal;b.BorderBrush=LiquidEdge(edgeTint,false,.5);b.Opacity=1;
   AnimateLiquidScale(b,1f,170);
  };
 }

 static Brush LiquidAcrylic(Color tint,double opacity,Color fallback)
 {
  try{return new AcrylicBrush{TintColor=tint,TintOpacity=opacity,FallbackColor=fallback};}
  catch{return new SolidColorBrush(fallback);}
 }

 static Brush LiquidEdge(Color tint,bool hot,double focus)
 {
  focus=Math.Clamp(focus,0d,1d);
  var left=Math.Max(0d,focus-.30);
  var right=Math.Min(1d,focus+.30);
  var strength=hot?(byte)190:(byte)92;
  return new LinearGradientBrush
  {
   StartPoint=new Windows.Foundation.Point(left,0),
   EndPoint=new Windows.Foundation.Point(right,1),
   GradientStops=
   {
    new GradientStop{Color=Windows.UI.Color.FromArgb((byte)(strength*.30),255,255,255),Offset=0},
    new GradientStop{Color=Windows.UI.Color.FromArgb(strength,255,255,255),Offset=.42},
    new GradientStop{Color=Windows.UI.Color.FromArgb((byte)(strength*.72),tint.R,tint.G,tint.B),Offset=.62},
    new GradientStop{Color=Windows.UI.Color.FromArgb((byte)(strength*.25),255,255,255),Offset=1}
   }
  };
 }

 static void AnimateLiquidScale(Button b,float target,int milliseconds)
 {
  try
  {
   var visual=ElementCompositionPreview.GetElementVisual(b);
   visual.CenterPoint=new Vector3((float)Math.Max(0,b.ActualWidth/2),(float)Math.Max(0,b.ActualHeight/2),0);
   if(!new Windows.UI.ViewManagement.UISettings().AnimationsEnabled){visual.StopAnimation("Scale");visual.Scale=Vector3.One;return;}
   var compositor=visual.Compositor;
   var animation=compositor.CreateVector3KeyFrameAnimation();
   animation.Duration=TimeSpan.FromMilliseconds(milliseconds);
   var ease=compositor.CreateCubicBezierEasingFunction(new Vector2(.20f,.72f),new Vector2(.22f,1f));
   animation.InsertKeyFrame(1,new Vector3(target,target,1),ease);
   visual.StartAnimation("Scale",animation);
  }
  catch{}
 }

 public static FontIcon Icon(string glyph,double size=20)=>new(){Glyph=glyph,FontSize=size,FontFamily=new FontFamily("Segoe Fluent Icons"),Foreground=Accent};
 public static FontIcon Icon(string glyph,double size,Brush color)=>new(){Glyph=glyph,FontSize=size,FontFamily=new FontFamily("Segoe Fluent Icons"),Foreground=color};
 public static Grid Columns(params double[] widths){var g=new Grid{ColumnSpacing=20};foreach(var w in widths)g.ColumnDefinitions.Add(new(){Width=w<0?new GridLength(-w,GridUnitType.Star):new GridLength(w)});return g;}
 public static void Add(Grid grid,FrameworkElement item,int column,int row=0){Grid.SetColumn(item,column);Grid.SetRow(item,row);grid.Children.Add(item);}
 public static Border Pill(string text)=>new(){Child=T(text,12,true,Accent),Padding=new Thickness(12,6,12,6),CornerRadius=new CornerRadius(16),Background=new SolidColorBrush(Windows.UI.Color.FromArgb(24,80,145,255)),HorizontalAlignment=HorizontalAlignment.Left};
 static BitmapImage? logoImage;
 static BitmapImage LogoImage=>logoImage??=new BitmapImage(new Uri(System.IO.Path.Combine(AppContext.BaseDirectory,"Assets","CleanC-logo.png"))){DecodePixelWidth=512};
 public static FrameworkElement Logo(double size=44)=>new Image{Width=size,Height=size,Source=LogoImage,Stretch=Stretch.Uniform,UseLayoutRounding=true};
}
public sealed class DiskGauge : Grid
{
 public DiskGauge(double used,string free)
 {
  Width=256;Height=256;
  Children.Add(new Ellipse{Stroke=Ui.B(Ui.Dark?"34445B":"E9EFF9"),StrokeThickness=18,Margin=new Thickness(12)});
  double ratio=Math.Clamp(used,.001,.999),angle=ratio*2*Math.PI;
  var figure=new PathFigure{StartPoint=new(128,21),IsClosed=false};
  figure.Segments.Add(new ArcSegment{Point=new(128+107*Math.Sin(angle),128-107*Math.Cos(angle)),Size=new(107,107),IsLargeArc=ratio>.5,SweepDirection=SweepDirection.Clockwise});
  var geometry=new PathGeometry();geometry.Figures.Add(figure);
  Children.Add(new Microsoft.UI.Xaml.Shapes.Path{Data=geometry,Stroke=new LinearGradientBrush{StartPoint=new(0,0),EndPoint=new(1,1),GradientStops={new GradientStop{Color=Ui.Color("8CC9FC"),Offset=0},new GradientStop{Color=Ui.Color("4B80E5"),Offset=1}}},StrokeThickness=18,StrokeStartLineCap=PenLineCap.Round,StrokeEndLineCap=PenLineCap.Round});
  var center=Ui.Stack(4,Ui.T("C:",20,true),Ui.T(free,32,true),Ui.T("可用空间",13,false,Ui.Muted));center.HorizontalAlignment=HorizontalAlignment.Center;center.VerticalAlignment=VerticalAlignment.Center;
  foreach(var item in center.Children.OfType<TextBlock>())item.HorizontalAlignment=HorizontalAlignment.Center;
  Children.Add(center);
 }
}


