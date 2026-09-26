using CleanC.Licensing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Automation;
namespace CleanC.App;

internal sealed class ActivationPage : Grid
{
    readonly Func<OfflineActivationSession> createSession;
    readonly Func<string, Task> onlineActivate, offlineActivate;
    readonly Action copyDevice;
    readonly ContentControl host = new() { HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Stretch };
    readonly Viewbox fit = new() { Stretch = Stretch.Uniform, StretchDirection = StretchDirection.DownOnly };
    readonly DispatcherTimer expiryTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    OfflineActivationSession session;
    FrameworkElement? landing;
    TextBlock? status;
    TextBox? codeInput;
    Button? codeSubmit;
    string onlineKey = "";
    bool busy;
    string error = "";
    string screen = "landing";

    public ActivationPage(Func<OfflineActivationSession> createSession, Func<string, Task> onlineActivate,
        Func<string, Task> offlineActivate, Action copyDevice)
    {
        this.createSession = createSession; this.onlineActivate = onlineActivate;
        this.offlineActivate = offlineActivate; this.copyDevice = copyDevice;
        session = createSession();
        fit.Child = host;
        fit.HorizontalAlignment = HorizontalAlignment.Center;
        fit.VerticalAlignment = VerticalAlignment.Center;
        Children.Add(fit);
        ShowLanding();
        expiryTimer.Tick += (_, _) => UpdateExpiry();
        Loaded += (_, _) => { expiryTimer.Start(); UpdateExpiry(); };
        Unloaded += (_, _) => expiryTimer.Stop();
    }

    static TextBlock Text(string value, double size = 14, bool strong = false, bool secondary = false)
        => Ui.T(value, size, strong, secondary ? Ui.Muted : Ui.Text);
    static Button Button(string label, Action action, bool primary = false)
    {
        var button = new Button
        {
            Content = label, Height = 42, Padding = new Thickness(16,0,16,0), CornerRadius = new CornerRadius(10),
            HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Center,
            Background = Ui.B(primary ? "2876DF" : Ui.Dark ? "1E2D40" : "E9EFF7"),
            Foreground = primary ? Ui.B("FFFFFF") : Ui.Text, BorderThickness = new Thickness(0)
        };
        button.Click += (_, _) => action();
        return button;
    }
    static Button Link(string label, Action action)
    {
        var b = Button(label, action);
        b.Background = null; b.Foreground = Ui.Accent; b.Height = 32; b.Padding = new Thickness(4,0,4,0);
        b.HorizontalAlignment = HorizontalAlignment.Left;
        return b;
    }
    static TextBox Input(string placeholder, string name, int length)
    {
        var box = new TextBox { PlaceholderText = placeholder, Height = 46, FontSize = 14, MaxLength = length,
            CornerRadius = new CornerRadius(10), Background = Ui.B(Ui.Dark ? "192A3C" : "FBFCFE"),
            FontFamily = new FontFamily("Consolas"), Padding = new Thickness(12,10,12,8) };
        AutomationProperties.SetName(box, name);
        return box;
    }
    Grid Frame(string title, string subtitle, FrameworkElement body, double height = 600)
    {
        var frame = new Grid { Width = 830, Height = height, Padding = new Thickness(12,8,12,4), RowSpacing = 14 };
        frame.RowDefinitions.Add(new() { Height = GridLength.Auto });
        frame.RowDefinitions.Add(new() { Height = new GridLength(1,GridUnitType.Star) });
        frame.RowDefinitions.Add(new() { Height = new GridLength(26) });
        var titleBlock = Ui.Stack(6, Text(title,28,true), Text(subtitle,13,false,true));
        frame.Children.Add(titleBlock);
        Grid.SetRow(body,1); frame.Children.Add(body);
        status = Text("",11,false,true); Grid.SetRow(status,2); frame.Children.Add(status);
        return frame;
    }
    public void ShowLanding()
    {
        screen = "landing"; error = ""; codeInput = null; codeSubmit = null;
        if(landing is null)
        {
            var key = Input("CLC-XXXX-XXXX-XXXX-XXXX","电脑联网授权码",200); key.Text = onlineKey;
            key.TextChanged += (_, _) => onlineKey = key.Text;
            var activate = Button("激活这台电脑",() => _ = Run(() => onlineActivate(key.Text)),true);
            activate.IsEnabled = !string.IsNullOrWhiteSpace(key.Text);
            key.TextChanged += (_, _) => activate.IsEnabled = !string.IsNullOrWhiteSpace(key.Text);
            key.KeyDown += (_, e) => { if(e.Key == Windows.System.VirtualKey.Enter && activate.IsEnabled) _ = Run(() => onlineActivate(key.Text)); };
            var left = Ui.Stack(16, Text("电脑联网激活",18,true), Text("输入授权码，完成本机授权。",13,false,true),
                key, activate, Link("复制设备码",copyDevice));
            left.MaxWidth = 308; left.HorizontalAlignment = HorizontalAlignment.Stretch; left.VerticalAlignment = VerticalAlignment.Center;
            var galaxy = new ConstellationQrCard(session.Url) { Width = 350, Height = 350 };
            var actions = new Grid { ColumnSpacing = 7 };
            actions.ColumnDefinitions.Add(new() { Width = new GridLength(105) });
            actions.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
            Ui.Add(actions,Button("放大扫码",ShowScan,true),0);
            Ui.Add(actions,Button("我已扫码，输入激活码",ShowCode),1);
            var right = Ui.Stack(3, Text("手机扫码 · 电脑离线",18,true), Text("手机相机或微信扫一扫",12,false,true), galaxy, actions,Link("刷新本次扫码图案",Refresh));
            right.MaxWidth = 370; right.HorizontalAlignment = HorizontalAlignment.Center; right.VerticalAlignment = VerticalAlignment.Center;
            foreach(var text in right.Children.OfType<TextBlock>()) text.HorizontalAlignment = HorizontalAlignment.Center;
            var columns = new Grid { ColumnSpacing = 25 };
            columns.ColumnDefinitions.Add(new() { Width = new GridLength(.9,GridUnitType.Star) });
            columns.ColumnDefinitions.Add(new() { Width = new GridLength(1) });
            columns.ColumnDefinitions.Add(new() { Width = new GridLength(1,GridUnitType.Star) });
            columns.Children.Add(left);
            Ui.Add(columns,new Border { Background = Ui.B(Ui.Dark ? "2A394C" : "E1E8F1"),Margin = new Thickness(0,30,0,30) },1);
            Ui.Add(columns,right,2);
            landing = Frame("激活 CleanC","为每一份数据留心。选择适合当前网络的激活方式。",columns);
        }
        host.Content = landing;
        // Frame's status reference must refer to the visible frame after returning.
        status = ((Grid)landing).Children.OfType<TextBlock>().Last();
        UpdateExpiry();
    }

    public void ShowScan()
    {
        screen = "scan"; error = ""; codeInput = null; codeSubmit = null;
        var info = Ui.Stack(12,
            Text("01  用手机扫一扫",17,true),
            Text("相机 / 微信扫一扫均可",14,true),
            Text("对准中央完整图案，打开授权网页。\n在手机上输入授权码，取得激活码。",13,false,true),
            Button("我已扫码，输入激活码",ShowCode,true),
            Link("返回粒子动画页",ShowLanding),
            Link("重新生成扫码图案",Refresh));
        info.Width = 260; info.VerticalAlignment = VerticalAlignment.Center;
        var raster = new ConstellationQrCard(session.Url) { Width = 470,Height = 470, HorizontalAlignment = HorizontalAlignment.Center };
        var columns = Ui.Columns(270,-1); columns.ColumnSpacing = 36;
        Ui.Add(columns,info,0); Ui.Add(columns,raster,1);
        host.Content = Frame("手机扫码激活","动态粒子围绕完整的扫码图案，手机识别后打开授权网页。",columns,600);
        UpdateExpiry();
    }
    public void ShowCode()
    {
        screen = "code"; error = "";
        codeInput = Input("XXXX-XXXX-XXXX-XXXX","手机返回的16位激活码",40);
        codeSubmit = Button("完成激活",() => _ = Run(() => offlineActivate(codeInput.Text)),true);
        codeSubmit.IsEnabled = false;
        codeInput.TextChanged += (_, _) => { error = ""; UpdateExpiry(); };
        codeInput.KeyDown += (_, e) => { if(e.Key == Windows.System.VirtualKey.Enter && codeSubmit.IsEnabled) _ = Run(() => offlineActivate(codeInput.Text)); };
        var body = Ui.Stack(15,Text("手机激活码",15,true),codeInput,codeSubmit,
            Link("返回粒子动画页",ShowLanding),Link("重新扫码",ShowScan));
        body.Width = 410; body.HorizontalAlignment = HorizontalAlignment.Center; body.VerticalAlignment = VerticalAlignment.Center;
        host.Content = Frame("输入激活码","输入手机网页显示的 16 位激活码，支持整段粘贴。",body);
        codeInput.Loaded += (_, _) => codeInput?.Focus(FocusState.Programmatic);
        UpdateExpiry();
    }
    void Refresh()
    {
        if(busy) return;
        try { session = createSession(); landing = null; ShowScan(); }
        catch(Exception e) { error = e.Message; UpdateExpiry(); }
    }
    void UpdateExpiry()
    {
        if(status is null || busy) return;
        if(screen == "landing") status.Text = session.IsValid ? "手机扫码模式无需电脑联网 · 扫描困难时点击“放大扫码”" : "本次扫码已过期，请点击“刷新本次扫码图案”。";
        else
        {
            var remaining = TimeSpan.FromMinutes(10)-session.Elapsed;
            status.Text = session.IsValid ? $"本次扫码剩余 {Math.Max(0,(int)remaining.TotalMinutes):00}:{remaining.Seconds:00} · 返回页面不会更换本次会话"
                : "本次扫码已过期或尝试次数已用完，请重新生成扫码图案。";
        }
        if(codeSubmit is not null && codeInput is not null)
            codeSubmit.IsEnabled = session.IsValid && OfflineActivationSession.Normalize(codeInput.Text).Length == 16 && !busy;
        if(!string.IsNullOrEmpty(error)) { status.Text = error; status.Foreground = Ui.Danger; }
        else status.Foreground = screen == "scan" ? Ui.B("65768C") : Ui.Muted;
    }
    async Task Run(Func<Task> action)
    {
        if(busy) return;
        busy = true; host.IsEnabled = false;
        if(status is not null) status.Text = "正在激活…";
        try { await action(); }
        catch(Exception e) { error = e.Message; if(status is not null) { status.Text = error; status.Foreground = Ui.Danger; } }
        finally { busy = false; host.IsEnabled = true; if(codeSubmit is not null && codeInput is not null) codeSubmit.IsEnabled = session.IsValid && OfflineActivationSession.Normalize(codeInput.Text).Length == 16; }
    }
}
