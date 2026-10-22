using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using System.Drawing;
using System.Globalization;

[assembly: System.Runtime.Versioning.TargetFramework(".NETFramework,Version=v4.8")]

namespace DotaArchive
{
    internal static class BrowserProbeProgram
    {
        [STAThread]
        private static void Main(string[] args)
        {
            ProbeArguments options = ParseArguments(args);
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new BrowserProbeForm(Path.GetFullPath(options.CaptureDirectory), options.InitialUrl, options.SaveOnLoad, options.BeforeDate));
        }
        internal sealed class ProbeArguments { internal string CaptureDirectory, InitialUrl; internal bool SaveOnLoad; internal DateTime? BeforeDate; }
        internal static ProbeArguments ParseArguments(string[] args)
        {
            var options = new ProbeArguments { CaptureDirectory = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "fixtures") };
            for (int i=0;i<args.Length;i++) {
                if(args[i]=="--capture-dir" && i+1<args.Length) options.CaptureDirectory=args[++i];
                else if(args[i].StartsWith("--capture-dir=",StringComparison.Ordinal)) options.CaptureDirectory=args[i].Substring("--capture-dir=".Length);
                else if(args[i]=="--url" && i+1<args.Length) options.InitialUrl=args[++i];
                else if(args[i].StartsWith("--url=",StringComparison.Ordinal)) options.InitialUrl=args[i].Substring("--url=".Length);
                else if(args[i]=="--save-on-load") options.SaveOnLoad=true;
                else if(args[i].StartsWith("--before-date=",StringComparison.Ordinal)) options.BeforeDate=DateTime.ParseExact(args[i].Substring("--before-date=".Length),"yyyy-MM-dd",CultureInfo.InvariantCulture);
            }
            return options;
        }
    }

    internal sealed class BrowserProbeForm : Form
    {
        private readonly BrowserSession session;
        private readonly string captureDirectory;
        private readonly Label status;
        private readonly TextBox address;
        private readonly FlowLayoutPanel toolbar;
        private readonly Button login, depots, history, patches, open, capture;
        private bool working;

        public BrowserProbeForm(string captures, string initialUrl = null, bool saveOnLoad = false, DateTime? beforeDate = null)
        {
            captureDirectory=captures;
            Text="SteamDB 浏览器原型 · 登录与网页读取";
            Width=1280; Height=880; MinimumSize=new Size(850,600);
            StartPosition=FormStartPosition.CenterScreen;
            Font=new Font("Microsoft YaHei UI",9F);
            session=new BrowserSession();
            toolbar=new FlowLayoutPanel {Dock=DockStyle.Top,Height=96,Padding=new Padding(8),WrapContents=true,AutoScroll=true};
            login=MakeButton("登录 SteamDB", delegate { return Navigate("https://steamdb.info/login/"); });
            depots=MakeButton("当前 Public Depots", delegate { return Navigate("https://steamdb.info/app/570/depots/?branch=public"); });
            history=MakeButton("构建历史", delegate { return Navigate("https://steamdb.info/app/570/history/?filterkey=2"); });
            patches=MakeButton("更新历史", delegate { return Navigate("https://steamdb.info/app/570/patchnotes/"); });
            Button resumeButton=MakeButton("已完成验证，继续", delegate {session.Continue();return Task.FromResult(true);});
            Button cancelButton=MakeButton("取消查询", delegate {session.CancelPending();return Task.FromResult(true);});
            capture=MakeButton("保存当前历史页面", SaveCapture);
            address=new TextBox {Width=660,Text="https://steamdb.info/patchnotes/3938917/",Margin=new Padding(3,8,3,3)};
            open=MakeButton("打开地址",delegate{return Navigate(address.Text);});
            toolbar.Controls.AddRange(new Control[]{login,depots,history,patches,resumeButton,cancelButton,capture,address,open});
            status=new Label {Dock=DockStyle.Bottom,Height=58,Padding=new Padding(10),Text="浏览器准备中……",AutoEllipsis=true};
            Controls.Add(session.View); Controls.Add(toolbar); Controls.Add(status);
            session.StatusChanged+=delegate(string value){status.Text=value;};
            Shown+=async delegate
            {
                try {await session.InitializeAsync();status.Text="请选择“登录 SteamDB”，在浏览器内完成登录与验证。登录信息只保存在本软件的专用浏览器目录。";
                    if(initialUrl != null) { await Navigate(initialUrl); if(beforeDate.HasValue) await session.LoadHistoryBeforeAsync(beforeDate.Value,CancellationToken.None); if(saveOnLoad) await SaveCapture(); }
                }
                catch(Exception ex){status.Text=ex.Message;}
            };
            FormClosing+=delegate {session.CancelPending();};
            FormClosed+=delegate {session.Dispose();};
        }

        private Button MakeButton(string text, Func<Task> action)
        {
            Button button=new Button {Text=text,AutoSize=true,Height=30,Margin=new Padding(3)};
            button.Click+=async delegate
            {
                try {await action();}
                catch(OperationCanceledException){status.Text="查询已取消。";}
                catch(Exception ex){status.Text=ex.Message;MessageBox.Show(this,ex.Message,"浏览器提示",MessageBoxButtons.OK,MessageBoxIcon.Information);}
            };
            return button;
        }

        private async Task Navigate(string url)
        {
            if(working)return;
            SetWorking(true);
            try {HistoryPageSnapshot page=await session.NavigateAsync(url,CancellationToken.None);address.Text=page.Url;}
            finally {SetWorking(false);}
        }

        private async Task SaveCapture()
        {
            HistoryPageSnapshot page=await session.CaptureAsync(CancellationToken.None);
            Uri uri;
            if(!Uri.TryCreate(page.Url,UriKind.Absolute,out uri)||uri.Host!="steamdb.info"||!(uri.AbsolutePath.StartsWith("/app/570/",StringComparison.Ordinal)||uri.AbsolutePath.StartsWith("/patchnotes/",StringComparison.Ordinal)||uri.AbsolutePath.StartsWith("/depot/",StringComparison.Ordinal)))
                throw new InvalidOperationException("只保存 Dota 2 历史、构建和 Depot 的正常内容页面；登录与验证页面不能保存。");
            Directory.CreateDirectory(captureDirectory);
            string name=DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff")+"-"+uri.AbsolutePath.Trim('/').Replace('/','-');
            string htmlPath=Path.Combine(captureDirectory,name+".html");
            string jsonPath=Path.Combine(captureDirectory,name+".json");
            File.WriteAllText(htmlPath,page.Html,new UTF8Encoding(false));
            JavaScriptSerializer serializer=new JavaScriptSerializer {MaxJsonLength=128*1024*1024};
            File.WriteAllText(jsonPath,serializer.Serialize(page),new UTF8Encoding(false));
            status.Text="已保存经过过滤的历史页面："+htmlPath;
        }

        private void SetWorking(bool value)
        {
            working=value;
            login.Enabled=depots.Enabled=history.Enabled=patches.Enabled=open.Enabled=!value;
            capture.Enabled=true;
        }
    }
}
