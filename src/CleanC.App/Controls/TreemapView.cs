using CleanC.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
namespace CleanC.App;
public sealed class TreemapView : Canvas
{
 readonly IReadOnlyList<DirectoryStat> items;readonly Action<DirectoryStat> selected;
 static readonly string[] Palette=["5A8DD7","69A8C9","8099CF","82B7AA","919CD0","72A0BA","96ADCE"];
 public TreemapView(IReadOnlyList<DirectoryStat> items,Action<DirectoryStat> selected){this.items=items.Where(x=>x.Bytes>0).OrderByDescending(x=>x.Bytes).Take(40).ToList();this.selected=selected;Height=310;SizeChanged+=(_,_)=>Render();}
 void Render(){Children.Clear();if(ActualWidth<1||items.Count==0)return;Split(items.Select((v,i)=>(v,i)).ToList(),new Rect(0,0,ActualWidth,Height));}
 void Split(List<(DirectoryStat v,int i)> nodes,Rect rect)
 {
  if(nodes.Count==1){
   var (v,i)=nodes[0];var content=Ui.Stack(6,Ui.T(v.Name,rect.Width>170?18:13,true,Ui.B("FFFFFF")),Ui.T(Display.Bytes(v.Bytes),13,false,Ui.B("F4F8FF")));
   var tile=new Border{Width=Math.Max(0,rect.Width-4),Height=Math.Max(0,rect.Height-4),CornerRadius=new CornerRadius(12),Background=Ui.B(Palette[i%Palette.Length]),Padding=new Thickness(rect.Width>80?14:4),Child=content};
   tile.Clip=new RectangleGeometry{Rect=new Rect(0,0,tile.Width,tile.Height)};
   if(rect.Width<62||rect.Height<46)content.Visibility=Visibility.Collapsed;
   ToolTipService.SetToolTip(tile,v.Path+"\n"+Display.Bytes(v.Bytes));tile.Tapped+=(_,_)=>selected(v);
   SetLeft(tile,rect.X);SetTop(tile,rect.Y);Children.Add(tile);return;
  }
  double sum=nodes.Sum(n=>(double)n.v.Bytes),partial=0;int split=0;
  while(split<nodes.Count-1&&(partial<sum/2||split==0)){partial+=nodes[split].v.Bytes;split++;}
  double fraction=partial/sum;
  if(rect.Width>=rect.Height){var a=rect.Width*fraction;Split(nodes.Take(split).ToList(),new(rect.X,rect.Y,a,rect.Height));Split(nodes.Skip(split).ToList(),new(rect.X+a,rect.Y,rect.Width-a,rect.Height));}
  else{var a=rect.Height*fraction;Split(nodes.Take(split).ToList(),new(rect.X,rect.Y,rect.Width,a));Split(nodes.Skip(split).ToList(),new(rect.X,rect.Y+a,rect.Width,rect.Height-a));}
 }
}

