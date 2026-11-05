using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using System.Xml.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Threading.Tasks;
using System.Text;
using System.Web.Script.Serialization;
using Microsoft.Win32;
using System.Reflection;

[assembly: AssemblyVersion("0.5.0.0")]
[assembly: AssemblyFileVersion("0.5.0.0")]

namespace GioviGG {
  class Match { public string Game, Date, Result; public int Kills, Deaths, Assists; public string MatchId="", Champion="", Queue="", Source="Manual"; public int Duration; }
  class Session { public string Game, Start; public long Seconds; }
  class Storage {
    public List<Match> Matches = new List<Match>();
    public List<Session> Sessions = new List<Session>();
    public string Folder, FileName;
    public Storage() {
      Folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GioviGG");
      Directory.CreateDirectory(Folder);
      FileName = Path.Combine(Folder, "tracker.xml");
      Load();
    }
    int ToInt(string s) { int x; return int.TryParse(s, out x) ? x : 0; }
    long ToLong(string s) { long x; return long.TryParse(s, out x) ? x : 0; }
    string A(XElement e, string key) { XAttribute a = e.Attribute(key); return a == null ? "" : a.Value; }
    void Load() {
      if (!System.IO.File.Exists(FileName)) return;
      try {
        XDocument d = XDocument.Load(FileName);
        foreach (XElement e in d.Descendants("match")) Matches.Add(new Match { Game=A(e,"game"),Date=A(e,"date"),Result=A(e,"result"),Kills=ToInt(A(e,"kills")),Deaths=ToInt(A(e,"deaths")),Assists=ToInt(A(e,"assists")),MatchId=A(e,"id"),Champion=A(e,"champion"),Queue=A(e,"queue"),Source=string.IsNullOrEmpty(A(e,"source"))?"Manual":A(e,"source"),Duration=ToInt(A(e,"duration")) });
        foreach (XElement e in d.Descendants("session")) Sessions.Add(new Session { Game=A(e,"game"),Start=A(e,"start"),Seconds=ToLong(A(e,"seconds")) });
      } catch (Exception ex) { MessageBox.Show("Nepodařilo se načíst data: " + ex.Message, "GIOVI.GG"); }
    }
    public void Save() {
      XDocument d = new XDocument(new XElement("giovi", new XElement("matches", Matches.Select(m => new XElement("match",new XAttribute("game",m.Game),new XAttribute("date",m.Date),new XAttribute("result",m.Result),new XAttribute("kills",m.Kills),new XAttribute("deaths",m.Deaths),new XAttribute("assists",m.Assists),new XAttribute("id",m.MatchId??""),new XAttribute("champion",m.Champion??""),new XAttribute("queue",m.Queue??""),new XAttribute("source",m.Source??"Manual"),new XAttribute("duration",m.Duration)))),new XElement("sessions",Sessions.Select(s => new XElement("session",new XAttribute("game",s.Game),new XAttribute("start",s.Start),new XAttribute("seconds",s.Seconds))))));
      string temp = FileName + ".tmp";
      d.Save(temp);
      if (System.IO.File.Exists(FileName)) System.IO.File.Replace(temp,FileName,null);
      else System.IO.File.Move(temp, FileName);
    }
  }
  // Profile settings are local. The Riot API key is kept IN MEMORY ONLY, never in GitHub or tracker.xml.
  class RiotConfig {
    public string GameName = "Threshwìllcarry", TagLine = "EUNE", Region = "europe", ApiKey = "";
    public readonly string PathName;
    public RiotConfig(string folder) {
      PathName=Path.Combine(folder,"riot-profile.xml");
      try {
        if (!File.Exists(PathName)) return;
        XElement e=XDocument.Load(PathName).Root;
        if(e==null) return;
        GameName=(string)e.Attribute("name")??GameName;
        TagLine=(string)e.Attribute("tag")??TagLine;
        Region=(string)e.Attribute("region")??Region;
      } catch(Exception) { /* The account can be configured again in the UI. */ }
    }
    public void SaveProfile() {
      new XDocument(new XElement("riot-profile",
        new XAttribute("name",GameName), new XAttribute("tag",TagLine),
        new XAttribute("region",Region))).Save(PathName);
    }
  }
  class RiotApi {
    // Official Riot Account-V1 -> PUUID -> LoL Match-V5. No scraping or process injection.
    static Dictionary<string,object> Obj(object v) { return v as Dictionary<string,object>; }
    static object Field(Dictionary<string,object> d,string key) {
      if(d==null || !d.ContainsKey(key)) return null;
      return d[key];
    }
    static string Str(object v) { return v==null?"":Convert.ToString(v,System.Globalization.CultureInfo.InvariantCulture); }
    static long Num(object v) { long result; return long.TryParse(Str(v),out result)?result:0; }
    static bool Boolean(object v) { return v is bool && (bool)v; }
    static string QueueName(long id) {
      switch(id) {
        case 420: return "Ranked Solo/Duo";
        case 440: return "Ranked Flex";
        case 400: return "Normal Draft";
        case 430: return "Normal Blind";
        case 450: return "ARAM";
        case 490: return "Quickplay";
        case 1700: return "Arena";
        case 900: return "URF";
        default: return "Queue " + id;
      }
    }
    static string Get(string url,string key) {
      ServicePointManager.SecurityProtocol=(SecurityProtocolType)3072;
      // Riot limits calls per key and endpoint; we intentionally pace our requests.
      for(int retry=0;retry<2;retry++) {
        try {
          using(WebClient wc=new WebClient()) {
            wc.Encoding=Encoding.UTF8;
            wc.Headers["X-Riot-Token"]=key;
            wc.Headers[HttpRequestHeader.UserAgent]="GioviGG/0.5 (personal stats tracker)";
            return wc.DownloadString(url);
          }
        } catch(WebException ex) {
          HttpWebResponse response=ex.Response as HttpWebResponse;
          int code=response==null?0:(int)response.StatusCode;
          if(code==429) {
            string wait=response==null?"":response.Headers["Retry-After"];
            int seconds;
            if(retry==0 && int.TryParse(wait,out seconds) && seconds>=1 && seconds<=8) {
              response.Close(); System.Threading.Thread.Sleep((seconds+1)*1000); continue;
            }
            if(response!=null) response.Close();
            throw new Exception("Riot API limit (429). Počkej " + (wait==""?"chvíli":wait+" s") + " a zkus synchronizaci později.");
          }
          if(response!=null) response.Close();
          if((code==502 || code==503 || code==504) && retry==0) {
            System.Threading.Thread.Sleep(1200);continue;
          }
          if(code==401 || code==403) throw new Exception("Riot API klíč je neplatný nebo vypršel ("+code+"). Obnov ho na developer.riotgames.com.");
          if(code==404) throw new Exception("Riot účet nebo záznam nebyl nalezen (404). Zkontroluj Riot ID a region.");
          if(code==502 || code==503 || code==504) throw new Exception("Riot API je dočasně nedostupné ("+code+"). Zkus synchronizaci později.");
          throw new Exception("Spojení s Riot API selhalo"+(code>0?" (HTTP "+code+")":"")+": "+ex.Message);
        }
      }
      throw new Exception("Riot API limit; zkus to později.");
    }
    static string Endpoints(string region) {
      if(region=="europe" || region=="americas" || region=="asia" || region=="sea") return region;
      return "europe";
    }
    public static List<Match> LoadRecent(RiotConfig profile,HashSet<string> known,int count) {
      string region=Endpoints(profile.Region);
      string host="https://"+region+".api.riotgames.com";
      string uri=host+"/riot/account/v1/accounts/by-riot-id/"+Uri.EscapeDataString(profile.GameName)+"/"+Uri.EscapeDataString(profile.TagLine);
      var account=Obj(new JavaScriptSerializer().DeserializeObject(Get(uri,profile.ApiKey)));
      string puuid=Str(Field(account,"puuid"));
      if(string.IsNullOrEmpty(puuid)) throw new Exception("Riot Account API nevrátilo PUUID.");
      System.Threading.Thread.Sleep(250);
      string listUrl=host+"/lol/match/v5/matches/by-puuid/"+Uri.EscapeDataString(puuid)+"/ids?start=0&count="+count;
      object[] ids=new JavaScriptSerializer().DeserializeObject(Get(listUrl,profile.ApiKey)) as object[];
      if(ids==null) throw new Exception("Riot Match API vrátilo neplatný seznam zápasů.");
      List<Match> found=new List<Match>();
      foreach(object item in ids) {
        string matchId=Str(item);
        if(string.IsNullOrEmpty(matchId) || known.Contains(matchId)) continue;
        System.Threading.Thread.Sleep(250);
        string url=host+"/lol/match/v5/matches/"+Uri.EscapeDataString(matchId);
        var json=Obj(new JavaScriptSerializer().DeserializeObject(Get(url,profile.ApiKey)));
        var info=Obj(Field(json,"info"));
        object[] participants=Field(info,"participants") as object[];
        if(participants==null) continue;
        Dictionary<string,object> you=null;
        foreach(object obj in participants) {
          var user=Obj(obj);
          if(Str(Field(user,"puuid"))==puuid) { you=user; break; }
        }
        if(you==null) continue;
        long timestamp=Num(Field(info,"gameEndTimestamp"));
        if(timestamp<=0) timestamp=Num(Field(info,"gameStartTimestamp"));
        DateTime date=DateTime.Now;
        if(timestamp>0) {
          // Match timestamps are Unix milliseconds; use .NET Framework-compatible conversion.
          try { date=new DateTime(1970,1,1,0,0,0,DateTimeKind.Utc).AddMilliseconds(timestamp).ToLocalTime(); }
          catch(ArgumentOutOfRangeException) { date=DateTime.Now; }
        }
        long duration=Num(Field(info,"gameDuration"));
        if(duration>100000) duration/=1000;
        Match m=new Match { Game="LoL", MatchId=matchId, Date=date.ToString("yyyy-MM-ddTHH:mm:ss"),
          Result=Boolean(Field(you,"win"))?"Win":"Loss", Kills=(int)Num(Field(you,"kills")),
          Deaths=(int)Num(Field(you,"deaths")), Assists=(int)Num(Field(you,"assists")),
          Champion=Str(Field(you,"championName")), Queue=QueueName(Num(Field(info,"queueId"))),
          Source="Riot API", Duration=(int)Math.Min(86400,Math.Max(0,duration)) };
        found.Add(m);
        known.Add(matchId);
      }
      return found;
    }
  }
  class UpdateInfo {
    public Version Number;
    public string Tag, Notes, DownloadUrl, Digest;
  }
  class App : Form {
    const string CurrentVersion = "0.5.0";
    const string ReleaseApi = "https://api.github.com/repos/KingGiovi/GIOVI-GG/releases/latest";
    UpdateInfo availableUpdate;
    bool updateBusy = false;
    string updateMessage = "Zatím nezkontrolováno.";
    Color bg = Color.FromArgb(12,13,22), side = Color.FromArgb(20,22,35), card = Color.FromArgb(29,31,48), fg=Color.FromArgb(240,238,255), muted=Color.FromArgb(158,157,181), accent=Color.FromArgb(166,123,255), green=Color.FromArgb(104,220,167);
    string[] games = { "CS2", "LoL", "R6" };
    string[] names = { "Counter-Strike 2", "League of Legends", "Rainbow Six Siege" };
    string[][] processes = { new string[]{"cs2"}, new string[]{"League of Legends"}, new string[]{"RainbowSix","RainbowSix_Vulkan","RainbowSix_DX11"} };
    Dictionary<string, DateTime> active = new Dictionary<string,DateTime>();
    Dictionary<string, long> runningSeconds = new Dictionary<string,long>();
    Storage store = new Storage();
    RiotConfig riot;
    bool riotSyncBusy=false;
    string riotStatus="Nepřipojeno. Přidej svůj Riot API klíč.";
    DateTime lastRiotAttempt=DateTime.MinValue;
    Timer riotTimer;
    Panel sidebar, mainArea, content, header;
    Label status, heading;
    NotifyIcon tray;
    Timer scanTimer, liveTimer;
    string page = "Overview";
    bool exiting = false;
    public App() {
      riot=new RiotConfig(store.Folder);
      Text = "GIOVI.GG — Ultimate Gaming Hub v0.5.0"; Width = 1160; Height = 780; MinimumSize = new Size(1050,650); StartPosition = FormStartPosition.CenterScreen; BackColor = bg; ForeColor = fg; Font = new Font("Segoe UI", 10f);
      // Fixed two-column layout: sidebar occupies its own column, never overlays content.
      TableLayoutPanel shell = new TableLayoutPanel { Dock=DockStyle.Fill, ColumnCount=2, RowCount=1, Margin=new Padding(0), Padding=new Padding(0), BackColor=bg };
      shell.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute,230));
      shell.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));
      shell.RowStyles.Add(new RowStyle(SizeType.Percent,100));
      Controls.Add(shell);
      sidebar = new Panel { Dock=DockStyle.Fill, BackColor=side, Margin=new Padding(0) }; shell.Controls.Add(sidebar,0,0);
      Label brand=Lbl("◆ GIOVI.GG",22,accent,true); brand.Location=new Point(20,26); brand.Size=new Size(205,42);sidebar.Controls.Add(brand);
      Label sub=Lbl("ULTIMATE GAMING HUB",9,muted,true);sub.Location=new Point(23,74);sub.Size=new Size(205,24);sidebar.Controls.Add(sub);
      AddNav("⌂   Overview","Overview",128);
      AddNav("▣   Counter-Strike 2","CS2",183);
      AddNav("◇   League of Legends","LoL",238);
      AddNav("⬡   Rainbow Six Siege","R6",293);
      AddNav("▤   Match history","History",348);
      AddNav("⚙   Settings","Settings",403);
      Label privacy=Lbl("● DATA STORED LOCALLY",9,green,true);privacy.Dock=DockStyle.Bottom;privacy.Height=55;privacy.TextAlign=ContentAlignment.MiddleCenter;sidebar.Controls.Add(privacy);
      mainArea=new Panel { Dock=DockStyle.Fill,BackColor=bg, Margin=new Padding(0) };shell.Controls.Add(mainArea,1,0);
      header=new Panel { Dock=DockStyle.Top,Height=90,BackColor=bg };mainArea.Controls.Add(header);
      heading=Lbl("Overview",24,fg,true);heading.Location=new Point(32,24);heading.Size=new Size(480,44);header.Controls.Add(heading);
      status=Lbl("● MONITORING",10,green,true); status.Dock=DockStyle.Right;status.Width=210;status.TextAlign=ContentAlignment.MiddleCenter;header.Controls.Add(status);
      content=new Panel { Dock=DockStyle.Fill, AutoScroll=true, BackColor=bg }; mainArea.Controls.Add(content);
      // Main area contains header and scrollable page; sidebar is a separate left dock.
      // Keep the top header ahead of the fill panel in WinForms dock order.
      content.SendToBack();header.BringToFront();
      tray=new NotifyIcon { Text="GIOVI.GG — Gaming Hub",Icon=SystemIcons.Application,Visible=true };
      ContextMenuStrip menu=new ContextMenuStrip();menu.Items.Add("Otevřít GIOVI.GG",null,(s,e)=>RestoreApp());menu.Items.Add("Ukončit",null,(s,e)=>ExitApp());tray.ContextMenuStrip=menu;tray.DoubleClick+=(s,e)=>RestoreApp();
      scanTimer=new Timer { Interval=15000 };scanTimer.Tick+=(s,e)=>Scan();scanTimer.Start();
      riotTimer=new Timer { Interval=600000 }; // Check every 10 minutes while the key is active.
      riotTimer.Tick+=(s,e)=>TrySyncLoL(true);
      riotTimer.Start();
      liveTimer=new Timer { Interval=1000 };liveTimer.Tick+=(s,e)=>{ if (page=="Overview" || Array.IndexOf(games,page)>=0) { UpdateLiveStatus(); } };liveTimer.Start();
      Resize+=(s,e)=>{ if (WindowState==FormWindowState.Normal && content!=null && content.Width>0) ShowPage(page); };
      Resize+=(s,e)=>{ if (WindowState==FormWindowState.Minimized) { Hide();tray.BalloonTipTitle="GIOVI.GG";tray.BalloonTipText="Sledování her běží dál na pozadí.";tray.ShowBalloonTip(1500); } };
      FormClosing+=(s,e)=>{if(!exiting){e.Cancel=true;WindowState=FormWindowState.Minimized;Hide();}};
      Scan();ShowPage("Overview");
      // Run the API request off the UI thread and notify once per launch if a newer release exists.
      Shown += (sender, args) => BeginUpdateCheck(true);
    }
    Label Lbl(string t,float size,Color c,bool bold=false){ return new Label {Text=t,Font=new Font("Segoe UI",size,bold?FontStyle.Bold:FontStyle.Regular),ForeColor=c,BackColor=Color.Transparent,AutoEllipsis=true}; }
    Button Btn(string t,Color color){return new Button {Text=t,BackColor=color,ForeColor=Color.White,FlatStyle=FlatStyle.Flat,Font=new Font("Segoe UI",10,FontStyle.Bold),Cursor=Cursors.Hand,UseVisualStyleBackColor=false};}
    void AddNav(string label,string key,int y){Button b=Btn(label,side);b.FlatAppearance.BorderSize=0;b.TextAlign=ContentAlignment.MiddleLeft;b.Location=new Point(10,y);b.Size=new Size(210,48);b.Click+=(s,e)=>ShowPage(key);sidebar.Controls.Add(b);}
    Panel Card(int x,int y,int w,int h){Panel p=new Panel {Location=new Point(x,y),Size=new Size(w,h),BackColor=card};content.Controls.Add(p);return p;}
    void AddText(Control p,string text,int x,int y,int w,int h,float size,Color color,bool bold=false){Label l=Lbl(text,size,color,bold);l.Location=new Point(x,y);l.Size=new Size(w,h);p.Controls.Add(l);}
    Button Action(Control p,string text,int x,int y,int w,int h,Action act){Button b=Btn(text,accent);b.FlatAppearance.BorderSize=0;b.Location=new Point(x,y);b.Size=new Size(w,h);b.Click+=(s,e)=>act();p.Controls.Add(b);return b;}
    void Section(string title,int y){AddText(content,title,28,y,800,32,16,fg,true);}
    void Tile(string title,string value,int x,int y,int width,Color c){Panel p=Card(x,y,width,110);AddText(p,title.ToUpperInvariant(),15,12,width-24,25,9,muted,true);AddText(p,value,15,43,width-24,52,24,c,true);}
    List<Match> Of(string g){return g==null?store.Matches:store.Matches.Where(m=>m.Game==g).ToList();}
    long Seconds(string g){long s=store.Sessions.Where(a=>g==null || a.Game==g).Sum(a=>a.Seconds);foreach(var kv in active){if(g==null||g==kv.Key)s+=(long)(DateTime.Now-kv.Value).TotalSeconds;}return Math.Max(0,s);}
    string Playtime(string g){long s=Seconds(g);return (s/3600)+"h "+((s%3600)/60)+"m";}
    void UpdateLiveStatus(){status.Text=active.Count>0?"● "+active.Count+" GAME(S) RUNNING":"● MONITORING";}
    void ShowPage(string p){page=p;content.SuspendLayout();content.Controls.Clear();heading.Text=Array.IndexOf(games,p)>=0?names[Array.IndexOf(games,p)]:p;UpdateLiveStatus();
      if(p=="Overview"){
        List<Match> all=Of(null);int wins=all.Count(m=>m.Result=="Win"),loss=all.Count(m=>m.Result=="Loss");long xp=wins*100L+loss*30L+all.Sum(m=>(long)m.Kills)*5L+all.Sum(m=>(long)m.Assists)*2L;
        AddText(content,"GIOVI   /   LEVEL "+(1+xp/1000),30,15,700,44,22,accent,true);
        AddText(content,xp.ToString("N0")+" XP celkem   •   "+(xp%1000)+" / 1000 XP do dalšího levelu",30,62,720,26,11,muted);
        Tile("Matches",all.Count.ToString(),30,104,220,accent);Tile("Victories",wins.ToString(),267,104,220,green);Tile("Playtime",Playtime(null),504,104,230,fg);
        Section("Your games",242);
        for(int i=0;i<3;i++){
          string g=games[i];int x=30+i*246;Panel c=Card(x,292,230,222);AddText(c,g,17,17,195,44,25,i==0?Color.FromArgb(237,174,91):i==1?Color.FromArgb(101,201,223):accent,true);
          AddText(c,names[i],17,70,200,28,11,fg,true);AddText(c,Of(g).Count(m=>m.Result=="Win")+" W    /    "+Of(g).Count(m=>m.Result=="Loss")+" L",17,111,200,24,11,muted);
          AddText(c,active.ContainsKey(g)?"● RUNNING":"○ IDLE",17,142,200,24,10,active.ContainsKey(g)?green:muted,true);Action(c,"VIEW STATS  →",14,176,200,36,()=>ShowPage(g));
        }
        Section("Achievements",548);AddText(content,"First Victory: "+(wins>0?"UNLOCKED":"LOCKED")+"     •     Veteran (100 wins): "+Math.Min(wins,100)+"/100     •     Legend (1000 wins): "+Math.Min(wins,1000)+"/1000",30,598,785,44,11,muted);
      } else if(Array.IndexOf(games,p)>=0){
        List<Match> mm=Of(p);int w=mm.Count(m=>m.Result=="Win"),l=mm.Count(m=>m.Result=="Loss"),k=mm.Sum(m=>m.Kills),d=mm.Sum(m=>m.Deaths),a=mm.Sum(m=>m.Assists);
        AddText(content,active.ContainsKey(p)?"● GAME DETECTED":"○ GAME NOT RUNNING",30,10,400,30,11,active.ContainsKey(p)?green:muted,true);
        Tile("Matches",mm.Count.ToString(),30,53,220,accent);Tile("Win rate",mm.Count==0?"—":((double)w*100/mm.Count).ToString("0.0")+"%",267,53,220,green);Tile(p=="LoL"?"KDA":"K/D",mm.Count==0?"—":(p=="LoL"?((double)(k+a)/Math.Max(1,d)).ToString("0.00"):(d==0?"—":((double)k/d).ToString("0.00"))),504,53,230,fg);
        string[] titles={"Wins","Losses","Kills","Deaths","Assists"};int[] values={w,l,k,d,a};for(int i=0;i<5;i++)Tile(titles[i],values[i].ToString(),30+146*i,187,130,fg);
        AddText(content,"Observed playtime: "+Playtime(p),30,319,420,32,12,muted,true);
        Action(content,"+ ADD MATCH",559,318,175,43,()=>AddMatch(p));
        if(p=="LoL") {
          Section("Riot Games · Live data",368);
          AddText(content,"Účet: "+riot.GameName+"#"+riot.TagLine+"   ·   "+riot.Region.ToUpperInvariant(),30,410,715,27,11,fg,true);
          AddText(content,riotStatus,30,442,720,54,10,riotSyncBusy?accent:muted);
          Action(content,"CONNECT / API KEY",30,505,226,42,()=>ConfigureRiot());
          Button sync=Action(content,riotSyncBusy?"SYNCING...":"SYNC MATCHES",270,505,226,42,()=>TrySyncLoL(false));sync.Enabled=!riotSyncBusy;
          Section("Recent matches",576);
          DrawMatches(mm.OrderByDescending(m=>m.Date).Take(7).ToList(),618);
          Section("Top champions",1095);
          var champions=mm.Where(m=>!string.IsNullOrEmpty(m.Champion)).GroupBy(m=>m.Champion).OrderByDescending(x=>x.Count()).Take(5).ToList();
          if(champions.Count==0) AddText(content,"Po synchronizaci se zobrazí tvoji nejhranější šampioni.",30,1142,710,34,11,muted);
          for(int i=0;i<champions.Count;i++) {
            var c=champions[i];int cw=c.Count(m=>m.Result=="Win");
            Panel champ=Card(30,1140+i*53,705,46);
            AddText(champ,c.Key,15,10,255,26,12,fg,true);
            AddText(champ,c.Count()+" matches    "+(cw*100.0/c.Count()).ToString("0")+"% WR",295,10,390,26,11,green,true);
          }
        } else {
          Section("Recent matches",385);DrawMatches(mm.OrderByDescending(m=>m.Date).Take(7).ToList(),430);
        }
      } else if(p=="History") {
        Section("All matches",15);
        Action(content,"EXPORT CSV",30,62,180,40,()=>ExportCsv());
        Action(content,"DELETE MATCH",225,62,180,40,()=>DeleteMatch());
        DrawMatches(store.Matches.OrderByDescending(m=>m.Date).Take(80).ToList(),125);
      }
      else {Section("GIOVI.GG v"+CurrentVersion,10);
        AddText(content,"Novinka v0.5: Riot API import zápasů, KDA a šampioni pro LoL.",30,48,785,28,11,accent,true);
        Section("Updates",94);
        AddText(content,"Aktuální verze: v"+CurrentVersion,30,139,700,26,12,fg,true);
        AddText(content,updateMessage,30,169,760,58,10,muted);
        Button check=Action(content,"CHECK FOR UPDATES",30,230,230,43,()=>BeginUpdateCheck(false));check.Enabled=!updateBusy;
        if(availableUpdate!=null){Button install=Action(content,"INSTALL v"+availableUpdate.Number.ToString(),278,230,230,43,()=>StartUpdate());install.Enabled=!updateBusy;}
        Section("Monitoring",300);AddText(content,"Detekce Windows procesů probíhá každých 15 sekund. V tray aplikace běží dál.",30,345,790,46,11,muted);
        Section("Your data",395);AddText(content,store.FileName,30,442,790,48,11,muted);Action(content,"OPEN DATA FOLDER",30,498,220,42,()=>Process.Start("explorer.exe",store.Folder));Action(content,"BACKUP STATS",270,498,220,42,()=>BackupStats());
        Section("Start with Windows",570);Action(content,AutoStartEnabled()?"DISABLE AUTOSTART":"ENABLE AUTOSTART",30,615,250,44,()=>{ToggleAutoStart();ShowPage("Settings");});
        Section("Riot Games API",696);
        AddText(content,"League of Legends: "+riot.GameName+"#"+riot.TagLine+" ("+riot.Region.ToUpperInvariant()+")",30,744,725,26,11,fg,true);
        AddText(content,"API klíč zůstává jen v paměti do vypnutí aplikace; není v GitHubu ani na disku.",30,773,725,42,10,muted);
        Action(content,"CONNECT RIOT",30,817,230,42,()=>ConfigureRiot());
        Section("Limitations",890);
        AddText(content,"CS2 a R6: herní čas automaticky; výsledky zatím ručně. LoL: Match-V5 vyžaduje osobní Riot API klíč.",30,940,750,56,11,muted);
      }
      content.AutoScrollMinSize=new Size(780,p=="History"?Math.Max(700,170+store.Matches.Count*60):(p=="Settings"?1050:(p=="LoL"?1470:950)));content.ResumeLayout();
    }
    void DrawMatches(List<Match> mm,int y){
      if(mm.Count==0){AddText(content,"Žádné zápasy. Přidej první ručně nebo spusť Riot Sync.",30,y,750,36,11,muted);return;}
      foreach(Match m in mm){
        Panel row=Card(30,y,705,54);
        AddText(row,m.Game+"   "+m.Result.ToUpperInvariant(),14,4,192,27,11,m.Result=="Win"?green:Color.FromArgb(255,132,159),true);
        string detail=!string.IsNullOrEmpty(m.Champion)?m.Champion+" · "+m.Queue:m.Source;
        AddText(row,detail,14,28,210,22,9,muted);
        AddText(row,m.Kills+" / "+m.Deaths+" / "+m.Assists+"  K/D/A",220,13,240,27,11,fg,true);
        AddText(row,m.Date.Replace('T',' '),511,14,180,25,9,muted);
        y+=60;
      }
    }
    string Csv(string s){return "\""+(s??"").Replace("\"","\"\"")+"\"";}
    void ExportCsv(){
      using(SaveFileDialog dlg=new SaveFileDialog()){
        dlg.Filter="CSV (*.csv)|*.csv";dlg.FileName="GioviGG-matches.csv";
        if(dlg.ShowDialog(this)!=DialogResult.OK)return;
        try{
          using(StreamWriter w=new StreamWriter(dlg.FileName,false,new System.Text.UTF8Encoding(true))){
            w.WriteLine("Game,Date,Result,Kills,Deaths,Assists,Champion,Queue,Source,MatchID");
            foreach(Match m in store.Matches) w.WriteLine(Csv(m.Game)+","+Csv(m.Date)+","+Csv(m.Result)+","+m.Kills+","+m.Deaths+","+m.Assists+","+Csv(m.Champion)+","+Csv(m.Queue)+","+Csv(m.Source)+","+Csv(m.MatchId));
          }
          MessageBox.Show("Exportováno: "+dlg.FileName,"GIOVI.GG");
        }catch(Exception ex){MessageBox.Show(ex.Message,"CSV export error");}
      }
    }
    // Optional user-initiated backup. Never moves or deletes the original tracker.xml.
    void BackupStats(){
      using(SaveFileDialog dlg=new SaveFileDialog()){
        dlg.Filter="XML backup (*.xml)|*.xml";
        dlg.FileName="GioviGG-backup-"+DateTime.Now.ToString("yyyy-MM-dd-HHmm")+".xml";
        dlg.Title="Záloha statistik GIOVI.GG";
        if(dlg.ShowDialog(this)!=DialogResult.OK)return;
        try {
          if(string.Equals(Path.GetFullPath(store.FileName),Path.GetFullPath(dlg.FileName),StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Vyber jiné umístění, než má původní tracker.xml.");
          store.Save();
          File.Copy(store.FileName,dlg.FileName,true);
          MessageBox.Show(this,"Záloha statistik byla úspěšně uložena.","GIOVI.GG",MessageBoxButtons.OK,MessageBoxIcon.Information);
        } catch(Exception ex) {
          MessageBox.Show(this,"Záloha se nezdařila: "+ex.Message,"GIOVI.GG",MessageBoxButtons.OK,MessageBoxIcon.Error);
        }
      }
    }
    void DeleteMatch(){
      if(store.Matches.Count==0){MessageBox.Show("Žádné zápasy k odstranění.");return;}
      Match m=store.Matches.OrderByDescending(a=>a.Date).First();
      string info=m.Game+" | "+m.Result+" | "+m.Date+" | "+m.Kills+"/"+m.Deaths+"/"+m.Assists;
      if(MessageBox.Show("Smazat poslední zápas?\n"+info,"GIOVI.GG",MessageBoxButtons.YesNo,MessageBoxIcon.Question)!=DialogResult.Yes)return;
      store.Matches.Remove(m);
      try{store.Save();ShowPage("History");}catch(Exception ex){store.Matches.Add(m);MessageBox.Show(ex.Message,"Save error");}
    }
    void AddMatch(string g){using(Form dialog=new Form()){dialog.Text="Add match — "+g;dialog.BackColor=side;dialog.ForeColor=fg;dialog.Size=new Size(410,450);dialog.StartPosition=FormStartPosition.CenterParent;dialog.FormBorderStyle=FormBorderStyle.FixedDialog;dialog.MaximizeBox=false;dialog.MinimizeBox=false;
      AddText(dialog,"NEW MATCH — "+g,22,19,330,40,18,accent,true);
      AddText(dialog,"RESULT",22,65,340,25,10,muted,true);ComboBox result=new ComboBox {Location=new Point(22,92),Width=338,DropDownStyle=ComboBoxStyle.DropDownList};result.Items.AddRange(new object[]{"Win","Loss","Draw"});result.SelectedIndex=0;dialog.Controls.Add(result);
      NumericUpDown[] controls=new NumericUpDown[3];string[] labels={"KILLS","DEATHS","ASSISTS"};for(int i=0;i<3;i++){int y=133+i*78;AddText(dialog,labels[i],22,y,330,24,10,muted,true);NumericUpDown n=new NumericUpDown {Location=new Point(22,y+28),Width=338,Maximum=100000,Font=new Font("Segoe UI",12)};controls[i]=n;dialog.Controls.Add(n);}
      Action(dialog,"SAVE MATCH",22,365,338,42,()=>{store.Matches.Add(new Match{Game=g,Date=DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss"),Result=result.Text,Kills=(int)controls[0].Value,Deaths=(int)controls[1].Value,Assists=(int)controls[2].Value});try{store.Save();dialog.DialogResult=DialogResult.OK;dialog.Close();}catch(Exception ex){MessageBox.Show(ex.Message,"Chyba ukládání");}});
      if(dialog.ShowDialog(this)==DialogResult.OK)ShowPage(page);
    }}
    void ConfigureRiot(){
      using(Form dialog=new Form()) {
        dialog.Text="GIOVI.GG — Riot Games Connection";
        dialog.BackColor=side;dialog.ForeColor=fg;dialog.ClientSize=new Size(520,425);
        dialog.StartPosition=FormStartPosition.CenterParent;dialog.FormBorderStyle=FormBorderStyle.FixedDialog;
        dialog.MaximizeBox=false;dialog.MinimizeBox=false;
        AddText(dialog,"CONNECT LEAGUE OF LEGENDS",22,18,470,34,17,accent,true);
        AddText(dialog,"Riot ID — jméno",24,67,340,23,10,muted,true);
        TextBox name=new TextBox { Location=new Point(24,92),Width=460,Text=riot.GameName,Font=new Font("Segoe UI",12) };
        dialog.Controls.Add(name);
        AddText(dialog,"Tag za #",24,132,210,23,10,muted,true);
        TextBox tag=new TextBox { Location=new Point(24,155),Width=206,Text=riot.TagLine,Font=new Font("Segoe UI",12) };
        dialog.Controls.Add(tag);
        AddText(dialog,"Region (Match-V5)",259,132,226,23,10,muted,true);
        ComboBox region=new ComboBox { Location=new Point(259,155),Width=225,DropDownStyle=ComboBoxStyle.DropDownList,Font=new Font("Segoe UI",11) };
        region.Items.AddRange(new object[]{"europe","americas","asia","sea"});
        int selected=region.Items.IndexOf(riot.Region);region.SelectedIndex=selected<0?0:selected;dialog.Controls.Add(region);
        AddText(dialog,"Riot Developer API key (RGAPI-...)",24,203,440,23,10,muted,true);
        TextBox key=new TextBox { Location=new Point(24,230),Width=460,UseSystemPasswordChar=true,Font=new Font("Segoe UI",11) };
        dialog.Controls.Add(key);
        AddText(dialog,string.IsNullOrEmpty(riot.ApiKey)?"Bez API klíče nepůjde načíst historie zápasů.":"Klíč je aktivní pro toto spuštění. Nech prázdné pro zachování.",24,265,466,38,10,muted);
        AddText(dialog,"Klíč se neukládá na disk ani do veřejného GitHubu. Dev key obvykle vyprší za 24 h.",24,308,466,40,9,muted);
        Action(dialog,"SAVE + CONNECT",24,363,222,42,()=>{
          string nameText=name.Text.Trim(),tagText=tag.Text.Trim(),keyText=key.Text.Trim();
          if(nameText.Length==0 || tagText.Length==0) {MessageBox.Show(dialog,"Vyplň Riot ID a tag.");return;}
          if(keyText.Length>0 && !keyText.StartsWith("RGAPI-",StringComparison.Ordinal)) {
            MessageBox.Show(dialog,"Riot API klíč obvykle začíná RGAPI-. Zkontroluj vloženou hodnotu.");return;
          }
          riot.GameName=nameText;riot.TagLine=tagText;riot.Region=region.Text;
          if(keyText.Length>0) riot.ApiKey=keyText;
          try {riot.SaveProfile();}
          catch(Exception ex){MessageBox.Show(dialog,"Uložení profilu se nezdařilo: "+ex.Message);return;}
          riotStatus=string.IsNullOrEmpty(riot.ApiKey)?"Nastav Riot API klíč pro synchronizaci.":"Účet připraven. Klikni SYNC MATCHES.";
          dialog.DialogResult=DialogResult.OK;dialog.Close();
        });
        Action(dialog,"OPEN RIOT DEVELOPER",261,363,224,42,()=>{
          try{Process.Start(new ProcessStartInfo("https://developer.riotgames.com/"){UseShellExecute=true});}
          catch(Exception ex){MessageBox.Show(dialog,ex.Message);}
        });
        if(dialog.ShowDialog(this)==DialogResult.OK) {ShowPage(page);if(!string.IsNullOrEmpty(riot.ApiKey))TrySyncLoL(false);}
      }
    }
    void TrySyncLoL(bool automatic) {
      if(riotSyncBusy) return;
      if(string.IsNullOrEmpty(riot.ApiKey)) {
        if(!automatic) ConfigureRiot();
        return;
      }
      if(automatic && DateTime.UtcNow-lastRiotAttempt<TimeSpan.FromMinutes(3))return;
      riotSyncBusy=true;lastRiotAttempt=DateTime.UtcNow;
      riotStatus="Načítám tvoje poslední LoL zápasy z Riot API...";
      if(page=="LoL") ShowPage("LoL");
      string gameName=riot.GameName,tag=riot.TagLine,region=riot.Region,key=riot.ApiKey;
      HashSet<string> known=new HashSet<string>(store.Matches.Where(m=>m.Game=="LoL"&&!string.IsNullOrEmpty(m.MatchId)).Select(m=>m.MatchId),StringComparer.Ordinal);
      Task.Run(()=>{
        List<Match> added=null;string error=null;
        try {
          var profile=new RiotConfig(store.Folder){GameName=gameName,TagLine=tag,Region=region,ApiKey=key};
          // One bounded page: avoid excessive API calls and Riot rate limits.
          added=RiotApi.LoadRecent(profile,known,20);
        } catch(Exception ex){error=ex.Message;}
        try { BeginInvoke((Action)(()=>{
          if(IsDisposed)return;
          riotSyncBusy=false;
          if(error!=null) riotStatus="Riot Sync error: "+error;
          else {
            int imported=0;
            foreach(Match m in added) {
              if(store.Matches.Any(x=>x.Game=="LoL"&&x.MatchId==m.MatchId))continue;
              store.Matches.Add(m);imported++;
            }
            try {if(imported>0)store.Save();riotStatus="Riot Sync OK: "+imported+" nových zápasů · "+DateTime.Now.ToString("HH:mm")+". Automatická kontrola každých 10 minut.";}
            catch(Exception ex){riotStatus="Chyba ukládání Riot zápasů: "+ex.Message;}
          }
          if(page=="LoL" || page=="Overview" || page=="History")ShowPage(page);
        })); } catch(InvalidOperationException) { /* Window closed. */ }
      });
    }
    // GitHub's public latest-release API is used only to discover signed-off official releases.
    // We enforce HTTPS, a known asset name, a strict version check and a SHA256 digest.
    void BeginUpdateCheck(bool automatic) {
      if(updateBusy) return;
      updateBusy=true;
      updateMessage="Kontroluji GitHub Releases...";
      if(page=="Settings") ShowPage("Settings");
      Task.Run(() => {
        UpdateInfo info=null; string error=null;
        try { info=FetchLatest(); }
        catch(Exception ex){ error=ex.Message; }
        try { BeginInvoke((Action)(() => {
          if(IsDisposed) return;
          updateBusy=false;
          if(error!=null){updateMessage="Kontrola se nezdařila: "+error;}
          else if(info==null){availableUpdate=null; updateMessage="Máš nejnovější vydanou verzi (v"+CurrentVersion+").";}
          else {availableUpdate=info; updateMessage="Dostupná aktualizace: "+info.Tag+". "+(info.Notes??"").Replace("\r"," ").Replace("\n"," ");}
          if(page=="Settings") ShowPage("Settings");
          if(automatic && info!=null){
            if(MessageBox.Show(this,"Je dostupná nová verze "+info.Tag+".\n\nChceš ji teď stáhnout a nainstalovat?", "GIOVI.GG — Update",MessageBoxButtons.YesNo,MessageBoxIcon.Information)==DialogResult.Yes) StartUpdate();
          }
        })); } catch(InvalidOperationException) {}
      });
    }
    UpdateInfo FetchLatest(){
      ServicePointManager.SecurityProtocol=(SecurityProtocolType)3072;
      string json;
      using(WebClient wc=new WebClient()){
        wc.Headers[HttpRequestHeader.UserAgent]="GioviGG-Windows/"+CurrentVersion;
        wc.Headers[HttpRequestHeader.Accept]="application/vnd.github+json";
        json=wc.DownloadString(ReleaseApi);
      }
      var root=new JavaScriptSerializer().DeserializeObject(json) as Dictionary<string,object>;
      if(root==null || !root.ContainsKey("tag_name")) throw new Exception("Neplatná odpověď API.");
      string tag=Convert.ToString(root["tag_name"]);
      Version remote;
      if(!Version.TryParse(tag.TrimStart('v','V'),out remote)) throw new Exception("Neplatné číslo verze.");
      if(remote.CompareTo(new Version(CurrentVersion)) <= 0) return null;
      object[] assets=root.ContainsKey("assets")?root["assets"] as object[]:null;
      if(assets==null) throw new Exception("Release nemá přiložené soubory.");
      foreach(object obj in assets){
        var a=obj as Dictionary<string,object>;
        if(a==null || !a.ContainsKey("name") || Convert.ToString(a["name"])!="GIOVI.GG.exe") continue;
        string url=Convert.ToString(a["browser_download_url"]);
        Uri uri;
        if(!Uri.TryCreate(url,UriKind.Absolute,out uri) || uri.Scheme!="https" || !uri.Host.Equals("github.com",StringComparison.OrdinalIgnoreCase)) throw new Exception("Neočekávaná adresa souboru.");
        string digest=a.ContainsKey("digest")?Convert.ToString(a["digest"]):"";
        if(!digest.StartsWith("sha256:",StringComparison.OrdinalIgnoreCase) || digest.Length!=71) throw new Exception("Release nemá platný SHA256 digest. Z bezpečnostních důvodů nelze aktualizovat.");
        string notes=root.ContainsKey("body")?Convert.ToString(root["body"]):"";
        if(notes.Length>120) notes=notes.Substring(0,120)+"...";
        return new UpdateInfo{Number=remote,Tag=tag,Notes=notes,DownloadUrl=url,Digest=digest.Substring(7).ToLowerInvariant()};
      }
      throw new Exception("Release "+tag+" neobsahuje GIOVI.GG.exe.");
    }
    static string HashOf(string path){
      using(SHA256 hash=SHA256.Create())
      using(FileStream fs=File.OpenRead(path)){
        byte[] b=hash.ComputeHash(fs);return BitConverter.ToString(b).Replace("-","").ToLowerInvariant();
      }
    }
    static string PsQuote(string val){return "'"+val.Replace("'","''")+"'";}
    void StartUpdate(){
      if(updateBusy || availableUpdate==null) return;
      UpdateInfo info=availableUpdate;
      updateBusy=true;updateMessage="Stahuji "+info.Tag+" a ověřuji SHA256...";
      if(page=="Settings") ShowPage("Settings");
      Task.Run(() => {
        string error=null, downloaded=null;
        try {
          ServicePointManager.SecurityProtocol=(SecurityProtocolType)3072;
          downloaded=Path.Combine(Path.GetTempPath(),"GioviGG-update-"+Guid.NewGuid().ToString("N")+".exe");
          using(WebClient wc=new WebClient()){
            wc.Headers[HttpRequestHeader.UserAgent]="GioviGG-Windows/"+CurrentVersion;
            wc.DownloadFile(info.DownloadUrl,downloaded);
          }
          if(!string.Equals(HashOf(downloaded),info.Digest,StringComparison.OrdinalIgnoreCase)) throw new Exception("SHA256 kontrola selhala: stažený soubor se liší.");
          if(new FileInfo(downloaded).Length<10240) throw new Exception("Stažený soubor je podezřele malý.");
        }catch(Exception ex){error=ex.Message;}
        string path=downloaded;
        try {BeginInvoke((Action)(()=>{
          if(IsDisposed) return;
          if(error!=null){updateBusy=false;updateMessage="Aktualizace se nezdařila: "+error;if(page=="Settings")ShowPage("Settings");if(path!=null)try{File.Delete(path);}catch{};return;}
          InstallDownloadedUpdate(path);
        }));}catch(InvalidOperationException){}
      });
    }
    void InstallDownloadedUpdate(string downloaded){
      try {
        string destination=Application.ExecutablePath;
        // The running executable must be writable. We do not touch the user's LocalAppData stats.
        string directory=Path.GetDirectoryName(destination);
        string probe=Path.Combine(directory,".giovi-update-test-"+Guid.NewGuid().ToString("N"));
        File.WriteAllText(probe,"");File.Delete(probe);
        string scriptFile=Path.Combine(Path.GetTempPath(),"GioviGG-updater-"+Guid.NewGuid().ToString("N")+".ps1");
        // The updater runs as a separate process because a running .exe cannot be replaced in-place.
        string script=@"$ErrorActionPreference='Stop'"+"\r\n"+
          "$src="+PsQuote(downloaded)+"\r\n"+
          "$dst="+PsQuote(destination)+"\r\n"+
          "$pidToWait="+Process.GetCurrentProcess().Id+"\r\n"+
          "try { Wait-Process -Id $pidToWait -ErrorAction SilentlyContinue; Start-Sleep -Milliseconds 600; " +
          "$done=$false; for($i=0;$i -lt 30;$i++){ try { Copy-Item -LiteralPath $src -Destination $dst -Force -ErrorAction Stop; $done=$true; break } catch { Start-Sleep -Milliseconds 500 } }; " +
          "if(-not $done){throw 'Nepodařilo se nahradit EXE soubor.'}; Start-Process -FilePath $dst; Remove-Item -LiteralPath $src -Force -ErrorAction SilentlyContinue " +
          "} catch { Add-Type -AssemblyName System.Windows.Forms; [System.Windows.Forms.MessageBox]::Show($_.Exception.Message,'GIOVI.GG update error') | Out-Null }";
        File.WriteAllText(scriptFile,script,Encoding.UTF8);
        ProcessStartInfo psi=new ProcessStartInfo("powershell.exe","-NoProfile -NonInteractive -ExecutionPolicy Bypass -File \""+scriptFile+"\"");
        psi.UseShellExecute=false;psi.CreateNoWindow=true;
        Process.Start(psi);
        ExitApp();
      }catch(Exception ex){updateBusy=false;updateMessage="Instalace selhala: "+ex.Message;if(page=="Settings")ShowPage("Settings");MessageBox.Show(this,ex.Message,"GIOVI.GG updater",MessageBoxButtons.OK,MessageBoxIcon.Error);}
    }
    void Scan(){try {HashSet<string> running=new HashSet<string>(Process.GetProcesses().Select(p=>{try{return p.ProcessName;}catch{return "";}}),StringComparer.OrdinalIgnoreCase);bool changed=false;DateTime now=DateTime.Now;
       for(int i=0;i<games.Length;i++){string g=games[i];bool found=processes[i].Any(exe=>running.Contains(exe));if(found&&!active.ContainsKey(g)){active[g]=now;changed=true;}else if(!found&&active.ContainsKey(g)){Finish(g,now);changed=true;if(g=="LoL")TrySyncLoL(true);}}
       if(changed){UpdateLiveStatus();ShowPage(page);}
      }catch(Exception){status.Text="● DETECTION ERROR";}}
    void Finish(string game,DateTime end){DateTime start=active[game];active.Remove(game);long seconds=Math.Max(0,(long)(end-start).TotalSeconds);if(seconds>0){store.Sessions.Add(new Session{Game=game,Start=start.ToString("yyyy-MM-ddTHH:mm:ss"),Seconds=seconds});try{store.Save();}catch(Exception ex){MessageBox.Show("Chyba ukládání session: "+ex.Message);}}}
    bool AutoStartEnabled(){using(RegistryKey k=Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run",false)){return k!=null&&k.GetValue("GioviGG")!=null;}}
    void ToggleAutoStart(){try{using(RegistryKey k=Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run",true)){if(AutoStartEnabled()) k.DeleteValue("GioviGG",false);else k.SetValue("GioviGG","\""+Application.ExecutablePath+"\"");}}catch(Exception ex){MessageBox.Show(ex.Message,"Autostart error");}}
    void RestoreApp(){Show();WindowState=FormWindowState.Normal;Activate();}
    void ExitApp(){exiting=true;scanTimer.Stop();liveTimer.Stop();riotTimer.Stop();foreach(string g in active.Keys.ToList())Finish(g,DateTime.Now);tray.Visible=false;tray.Dispose();Close();Application.Exit();}
    [STAThread] static void Main(){Application.EnableVisualStyles();Application.SetCompatibleTextRenderingDefault(false);Application.Run(new App());}
  }
}
