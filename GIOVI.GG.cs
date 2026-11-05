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
using System.Net.Sockets;

[assembly: AssemblyVersion("0.7.0.0")]
[assembly: AssemblyFileVersion("0.7.0.0")]

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
            wc.Headers[HttpRequestHeader.UserAgent]="GioviGG/0.7.0 (personal stats tracker)";
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

  // Valve CS2 Game State Integration. Localhost only; it never accesses game memory.
  // CS2 sends snapshots to 127.0.0.1:29876. No third-party server or API key.
  class Cs2State {
    public DateTime SeenUtc=DateTime.MinValue;
    public string Map="", Mode="", Phase="", Team="";
    public int Kills, Deaths, Assists, Ct, T;
    public bool HasStats;
  }
  class Cs2GsiReceiver : IDisposable {
    public const int Port=29876;
    readonly string token;
    readonly Action<Cs2State,Match> onUpdate;
    TcpListener listener;
    volatile bool stopping;
    Cs2State state=new Cs2State();
    string currentMap="", providerSteamId="", playerSteamId="";
    bool sawLive, recorded;
    DateTime matchStarted=DateTime.MinValue;
    public Cs2GsiReceiver(string folder, Action<Cs2State,Match> callback){
      onUpdate=callback;
      string tokenPath=Path.Combine(folder,"cs2-gsi-token.txt");
      if(File.Exists(tokenPath)) token=File.ReadAllText(tokenPath).Trim();
      else { token=Guid.NewGuid().ToString("N")+Guid.NewGuid().ToString("N"); File.WriteAllText(tokenPath,token,Encoding.ASCII); }
      if(token.Length<32) throw new Exception("CS2 GSI token je neplatný; smaž cs2-gsi-token.txt a znovu spusť tracker.");
    }
    public void Start(){
      stopping=false;
      listener=new TcpListener(IPAddress.Loopback,Port);
      listener.Start();
      Task.Run(()=>Loop());
    }
    public string ConfigText {
      get {
        return "\"GIOVI.GG CS2 integration\"\r\n{\r\n"+
          "  \"uri\" \"http://127.0.0.1:"+Port+"/\"\r\n"+
          "  \"timeout\" \"5.0\"\r\n  \"buffer\" \"0.1\"\r\n  \"throttle\" \"0.5\"\r\n  \"heartbeat\" \"15.0\"\r\n"+
          "  \"auth\" { \"token\" \""+token+"\" }\r\n"+
          "  \"data\"\r\n  {\r\n"+
          "    \"provider\" \"1\"\r\n    \"map\" \"1\"\r\n    \"round\" \"1\"\r\n"+
          "    \"player_id\" \"1\"\r\n    \"player_match_stats\" \"1\"\r\n  }\r\n}\r\n";
      }
    }
    public void InstallConfig(string cfgFolder){
      DirectoryInfo di=new DirectoryInfo(cfgFolder);
      if(!di.Exists || !di.Name.Equals("cfg",StringComparison.OrdinalIgnoreCase) ||
         di.Parent==null || !di.Parent.Name.Equals("csgo",StringComparison.OrdinalIgnoreCase) ||
         di.Parent.Parent==null || !di.Parent.Parent.Name.Equals("game",StringComparison.OrdinalIgnoreCase))
        throw new Exception("Vyber složku CS2: ...\\Counter-Strike Global Offensive\\game\\csgo\\cfg");
      string file=Path.Combine(cfgFolder,"gamestate_integration_giovigg.cfg");
      File.WriteAllText(file,ConfigText,new UTF8Encoding(false));
    }
    static Dictionary<string,object> Dict(object o){return o as Dictionary<string,object>;}
    static object Val(Dictionary<string,object> d,string key){return d!=null&&d.ContainsKey(key)?d[key]:null;}
    static string Str(object o){return o==null?"":Convert.ToString(o,System.Globalization.CultureInfo.InvariantCulture);}
    static int Number(object o){int n;return int.TryParse(Str(o),out n)?Math.Max(0,n):0;}
    static bool Has(Dictionary<string,object> d,string key){return d!=null&&d.ContainsKey(key);}
    void Loop(){
      while(!stopping){
        try {
          using(TcpClient c=listener.AcceptTcpClient()) Handle(c);
        }catch(SocketException){if(stopping)return;System.Threading.Thread.Sleep(200);}
        catch(ObjectDisposedException){return;}
        catch(Exception){if(stopping)return;System.Threading.Thread.Sleep(150);}
      }
    }
    static void Answer(NetworkStream stream,bool success){
      try {
        string message=success?"HTTP/1.1 200 OK\r\nContent-Length: 2\r\nConnection: close\r\nContent-Type: text/plain\r\n\r\nOK":
          "HTTP/1.1 403 Forbidden\r\nContent-Length: 0\r\nConnection: close\r\n\r\n";
        byte[] bytes=Encoding.ASCII.GetBytes(message);
        stream.Write(bytes,0,bytes.Length);stream.Flush();
      }catch(IOException){}
    }
    void Handle(TcpClient client){
      client.ReceiveTimeout=4000;client.SendTimeout=4000;
      using(NetworkStream stream=client.GetStream()){
        // Bounded request: only POST and at most 256 KiB, sent locally by CS2.
        List<byte> header=new List<byte>(); int b; int end=0;
        while(header.Count<16384 && (b=stream.ReadByte())!=-1){
          header.Add((byte)b);
          end=b==13&&end==0?1:b==10&&end==1?2:b==13&&end==2?3:b==10&&end==3?4:0;
          if(end==4)break;
        }
        if(end!=4){Answer(stream,false);return;}
        string raw=Encoding.ASCII.GetString(header.ToArray());
        if(!raw.StartsWith("POST / HTTP/",StringComparison.OrdinalIgnoreCase)){Answer(stream,false);return;}
        int length=-1;
        foreach(string item in raw.Split(new string[]{"\r\n"},StringSplitOptions.RemoveEmptyEntries)){
          if(item.StartsWith("Content-Length:",StringComparison.OrdinalIgnoreCase)){
            int.TryParse(item.Substring(15).Trim(),out length);
          }
        }
        if(length<2 || length>262144){Answer(stream,false);return;}
        byte[] bytes=new byte[length];int offset=0;
        while(offset<length){int read=stream.Read(bytes,offset,length-offset);if(read<=0)break;offset+=read;}
        if(offset!=length){Answer(stream,false);return;}
        Dictionary<string,object> json=null;
        try {
          JavaScriptSerializer parser=new JavaScriptSerializer();
          parser.MaxJsonLength=262144;
          json=Dict(parser.DeserializeObject(Encoding.UTF8.GetString(bytes)));
        }catch(Exception){Answer(stream,false);return;}
        if(Str(Val(Dict(Val(json,"auth")),"token"))!=token){Answer(stream,false);return;}
        Answer(stream,true);
        ProcessPayload(json);
      }
    }
    void ProcessPayload(Dictionary<string,object> data){
      Dictionary<string,object> map=Dict(Val(data,"map")), player=Dict(Val(data,"player"));
      Dictionary<string,object> provider=Dict(Val(data,"provider"));
      if(map!=null){
        if(Has(map,"name"))state.Map=Str(Val(map,"name"));
        if(Has(map,"mode"))state.Mode=Str(Val(map,"mode"));
        if(Has(map,"phase"))state.Phase=Str(Val(map,"phase"));
        var ct=Dict(Val(map,"team_ct"));var t=Dict(Val(map,"team_t"));
        if(Has(ct,"score"))state.Ct=Number(Val(ct,"score"));
        if(Has(t,"score"))state.T=Number(Val(t,"score"));
      }
      if(Has(provider,"steamid"))providerSteamId=Str(Val(provider,"steamid"));
      if(player!=null){
        if(Has(player,"steamid"))playerSteamId=Str(Val(player,"steamid"));
        bool own=Has(player,"steamid") && providerSteamId.Length>0 && playerSteamId==providerSteamId;
        if(own){
          if(Has(player,"team"))state.Team=Str(Val(player,"team"));
          var stats=Dict(Val(player,"match_stats"));
          if(stats!=null && Has(stats,"kills") && Has(stats,"deaths") && Has(stats,"assists")){
            state.Kills=Number(Val(stats,"kills"));
            state.Deaths=Number(Val(stats,"deaths"));
            state.Assists=Number(Val(stats,"assists"));
            state.HasStats=true;
          }
        }
      }
      state.SeenUtc=DateTime.UtcNow;
      if(state.Phase.Equals("live",StringComparison.OrdinalIgnoreCase) && state.Map.Length>0 &&
         (!sawLive || recorded || !state.Map.Equals(currentMap,StringComparison.OrdinalIgnoreCase))){
        currentMap=state.Map;matchStarted=DateTime.UtcNow;sawLive=true;recorded=false;state.HasStats=false;
        state.Kills=0;state.Deaths=0;state.Assists=0;
        // If the first live payload includes our own stats, keep them for this session.
        bool own=Has(player,"steamid") && providerSteamId.Length>0 && playerSteamId==providerSteamId;
        var stats=own?Dict(Val(player,"match_stats")):null;
        if(stats!=null && Has(stats,"kills") && Has(stats,"deaths") && Has(stats,"assists")){
          state.Kills=Number(Val(stats,"kills"));state.Deaths=Number(Val(stats,"deaths"));
          state.Assists=Number(Val(stats,"assists"));state.HasStats=true;
        }
      }
      Match completed=null;
      if(sawLive && !recorded && state.Phase.Equals("gameover",StringComparison.OrdinalIgnoreCase)){
        recorded=true; // one record per observed match, never on every heartbeat
        bool ct=state.Team.Equals("CT",StringComparison.OrdinalIgnoreCase);
        bool t=state.Team.Equals("T",StringComparison.OrdinalIgnoreCase);
        // Deathmatch and modes without team scores do not have a reliable W/L result.
        bool teamGame=!state.Mode.Equals("deathmatch",StringComparison.OrdinalIgnoreCase);
        if(state.HasStats && (ct||t) && state.Map.Length>0 && teamGame && (state.Ct>0 || state.T>0)){
          int ours=ct?state.Ct:state.T, theirs=ct?state.T:state.Ct;
          string result=ours==theirs?"Draw":ours>theirs?"Win":"Loss";
          completed=new Match {Game="CS2",Date=DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss"),Result=result,
            Kills=state.Kills,Deaths=state.Deaths,Assists=state.Assists,Queue=state.Map+" · "+state.Mode,
            Source="CS2 GSI",MatchId="CS2-GSI-"+Guid.NewGuid().ToString("N"),
            Duration=matchStarted==DateTime.MinValue?0:(int)Math.Max(0,Math.Min(21600,(DateTime.UtcNow-matchStarted).TotalSeconds))};
        }
      }
      if(onUpdate!=null){
        Cs2State report=new Cs2State {SeenUtc=state.SeenUtc,Map=state.Map,Mode=state.Mode,Phase=state.Phase,
          Team=state.Team,Kills=state.Kills,Deaths=state.Deaths,Assists=state.Assists,Ct=state.Ct,T=state.T,HasStats=state.HasStats};
        onUpdate(report,completed);
      }
    }
    public void Dispose(){stopping=true;try{if(listener!=null)listener.Stop();}catch(Exception){}}
  }

  class Achievement {
    public string Name, Description;
    public long Progress, Goal;
    public Achievement(string name,string description,long progress,long goal){
      Name=name;Description=description;Progress=progress;Goal=goal;
    }
    public bool Unlocked {get{return Progress>=Goal;}}
  }
  class UpdateInfo {
    public Version Number;
    public string Tag, Notes, DownloadUrl, Digest;
  }

  // A compact graphics-only chart for the latest match results (no dependencies).
  class ResultsChart : Control {
    readonly List<Match> results;
    readonly Color green=Color.FromArgb(104,220,167);
    readonly Color red=Color.FromArgb(255,139,158);
    readonly Color muted=Color.FromArgb(157,158,181);
    readonly Color grid=Color.FromArgb(59,62,83);
    public ResultsChart(IEnumerable<Match> matches) {
      results=matches.OrderBy(m=>m.Date).Take(16).ToList();
      DoubleBuffered=true;
      BackColor=Color.FromArgb(29,31,49);
      ForeColor=Color.White;
      Font=new Font("Segoe UI",9f);
    }
    protected override void OnPaint(PaintEventArgs e) {
      base.OnPaint(e);
      Graphics g=e.Graphics;
      g.SmoothingMode=System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
      Rectangle area=new Rectangle(13,24,Math.Max(12,Width-26),Math.Max(50,Height-65));
      using(Pen pen=new Pen(grid,1)){
        for(int i=0;i<3;i++){
          int y=area.Top+i*area.Height/2;
          g.DrawLine(pen,area.Left,y,area.Right,y);
        }
      }
      if(results.Count==0){
        using(Brush b=new SolidBrush(muted))g.DrawString("Zatím nemáme zápasy pro zvolené období.",Font,b,area.Left,area.Top+area.Height/2-10);
        return;
      }
      int count=results.Count;
      float slot=area.Width/(float)count;
      float bar=Math.Max(5f,Math.Min(26f,slot*0.64f));
      for(int i=0;i<count;i++){
        Match m=results[i];
        float x=area.Left+i*slot+(slot-bar)/2f;
        float top=m.Result=="Win"?area.Top+area.Height*0.18f:m.Result=="Loss"?area.Top+area.Height*0.53f:area.Top+area.Height*0.37f;
        float bottom=area.Bottom;
        Color color=m.Result=="Win"?green:m.Result=="Loss"?red:muted;
        using(Brush b=new SolidBrush(color))g.FillRectangle(b,x,top,bar,bottom-top);
        if(count<=12){
          using(Brush b=new SolidBrush(muted)){
            string t=(i+1).ToString();
            SizeF size=g.MeasureString(t,Font);
            g.DrawString(t,Font,b,x+bar/2f-size.Width/2f,area.Bottom+5);
          }
        }
      }
      using(Brush b=new SolidBrush(muted)){
        g.DrawString("STARŠÍ",Font,b,area.Left,2);
        string end="NEJNOVĚJŠÍ";
        SizeF size=g.MeasureString(end,Font);
        g.DrawString(end,Font,b,Math.Max(area.Left,area.Right-size.Width),2);
      }
    }
  }

  class App : Form {
    const string CurrentVersion = "0.7.0";
    const string ReleaseApi = "https://api.github.com/repos/KingGiovi/GIOVI-GG/releases/latest";
    UpdateInfo availableUpdate;
    bool updateBusy = false;
    string updateMessage = "Zatím nezkontrolováno.";
    Color bg = Color.FromArgb(12,13,23), side = Color.FromArgb(17,19,32), card = Color.FromArgb(29,31,49), fg=Color.FromArgb(243,240,255), muted=Color.FromArgb(157,158,181), accent=Color.FromArgb(166,123,255), green=Color.FromArgb(104,220,167);
    Color shade = Color.FromArgb(37,40,60), line = Color.FromArgb(59,62,83), danger=Color.FromArgb(255,139,158);
    string[] games = { "CS2", "LoL", "R6" };
    string[] names = { "Counter-Strike 2", "League of Legends", "Rainbow Six Siege" };
    string[][] processes = { new string[]{"cs2"}, new string[]{"League of Legends"}, new string[]{"RainbowSix","RainbowSix_Vulkan","RainbowSix_DX11"} };
    Dictionary<string, DateTime> active = new Dictionary<string,DateTime>();
    Dictionary<string, long> runningSeconds = new Dictionary<string,long>();
    Storage store = new Storage();
    RiotConfig riot;
    Cs2GsiReceiver cs2Receiver;
    Cs2State cs2State=new Cs2State();
    string cs2Status="CS2 GSI: čekám na první data.";
    Label cs2LiveLabel;
    bool riotSyncBusy=false;
    string riotStatus="Nepřipojeno. Přidej svůj Riot API klíč.";
    DateTime lastRiotAttempt=DateTime.MinValue;
    Timer riotTimer;
    Panel sidebar, mainArea, content, header;
    Dictionary<string,Button> navButtons = new Dictionary<string,Button>();
    string gameTab="Overview", settingsTab="Updates", historyFilter="All";
    Label status, heading;
    NotifyIcon tray;
    Timer scanTimer, liveTimer;
    string page = "Overview";
    string analyticsGame = "LoL";
    string analyticsPeriod = "30D";
    string historyResult = "All", historyPeriod = "All", historySearch = "";
    Panel historyListPanel;
    Label historyCount;
    bool exiting = false;
    public App() {
      riot=new RiotConfig(store.Folder);
      Text = "GIOVI.GG — Ultimate Gaming Hub v0.7.0"; Width = 1160; Height = 780; MinimumSize = new Size(1020,640); StartPosition = FormStartPosition.CenterScreen; BackColor = bg; ForeColor = fg; Font = new Font("Segoe UI", 10f);
      // Fixed two-column layout: sidebar occupies its own column, never overlays content.
      TableLayoutPanel shell = new TableLayoutPanel { Dock=DockStyle.Fill, ColumnCount=2, RowCount=1, Margin=new Padding(0), Padding=new Padding(0), BackColor=bg };
      shell.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute,210));
      shell.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));
      shell.RowStyles.Add(new RowStyle(SizeType.Percent,100));
      Controls.Add(shell);
      sidebar = new Panel { Dock=DockStyle.Fill, BackColor=side, Margin=new Padding(0) }; shell.Controls.Add(sidebar,0,0);
      Label brand=Lbl("◆ GIOVI.GG",22,accent,true); brand.Location=new Point(18,25); brand.Size=new Size(190,42);sidebar.Controls.Add(brand);
      Label sub=Lbl("ULTIMATE GAMING HUB",9,muted,true);sub.Location=new Point(20,70);sub.Size=new Size(185,24);sidebar.Controls.Add(sub);
      AddNav("⌂   Přehled","Overview",124);
      AddNav("▣   Counter-Strike 2","CS2",177);
      AddNav("◇   League of Legends","LoL",230);
      AddNav("⬡   Rainbow Six Siege","R6",283);
      AddNav("▤   Historie zápasů","History",336);
      AddNav("◈   Analýza výkonu","Analytics",389);
      AddNav("★   Achievementy","Achievements",442);
      AddNav("⚙   Nastavení","Settings",495);
      Label privacy=Lbl("● DATA STORED LOCALLY",9,green,true);privacy.Dock=DockStyle.Bottom;privacy.Height=46;privacy.TextAlign=ContentAlignment.MiddleCenter;sidebar.Controls.Add(privacy);
      mainArea=new Panel { Dock=DockStyle.Fill,BackColor=bg, Margin=new Padding(0) };shell.Controls.Add(mainArea,1,0);
      header=new Panel { Dock=DockStyle.Top,Height=82,BackColor=bg };mainArea.Controls.Add(header);
      heading=Lbl("Overview",24,fg,true);heading.Location=new Point(26,18);heading.Size=new Size(560,44);header.Controls.Add(heading);
      status=Lbl("● MONITORING",10,green,true); status.Dock=DockStyle.Right;status.Width=175;status.TextAlign=ContentAlignment.MiddleCenter;header.Controls.Add(status);
      content=new Panel { Dock=DockStyle.Fill, AutoScroll=false, BackColor=bg }; mainArea.Controls.Add(content);
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
      content.Resize+=(s,e)=>{ if (WindowState==FormWindowState.Normal && content!=null && content.Width>400 && content.Height>300) ShowPage(page); };
      Resize+=(s,e)=>{ if (WindowState==FormWindowState.Minimized) { Hide();tray.BalloonTipTitle="GIOVI.GG";tray.BalloonTipText="Sledování her běží dál na pozadí.";tray.ShowBalloonTip(1500); } };
      FormClosing+=(s,e)=>{if(!exiting){e.Cancel=true;WindowState=FormWindowState.Minimized;Hide();}};
      try {
        cs2Receiver=new Cs2GsiReceiver(store.Folder,(s,m)=>{
          if(IsDisposed || !IsHandleCreated)return;
          try {BeginInvoke((Action)(()=>ReceiveCs2(s,m)));}catch(InvalidOperationException){}
        });
        cs2Receiver.Start();
        cs2Status="CS2 GSI listener připraven (127.0.0.1:29876). Nastav CS2 v Nastavení → Propojení.";
      }catch(Exception ex){cs2Status="CS2 GSI listener nelze spustit: "+ex.Message;}
      Scan();ShowPage("Overview");
      // Run the API request off the UI thread and notify once per launch if a newer release exists.
      Shown += (sender, args) => BeginUpdateCheck(true);
    }
    Label Lbl(string t,float size,Color c,bool bold=false){ return new Label {Text=t,Font=new Font("Segoe UI",size,bold?FontStyle.Bold:FontStyle.Regular),ForeColor=c,BackColor=Color.Transparent,AutoEllipsis=true}; }
    Button Btn(string t,Color color){return new Button {Text=t,BackColor=color,ForeColor=Color.White,FlatStyle=FlatStyle.Flat,Font=new Font("Segoe UI",10,FontStyle.Bold),Cursor=Cursors.Hand,UseVisualStyleBackColor=false};}
    void AddNav(string label,string key,int y){Button b=Btn(label,side);b.FlatAppearance.BorderSize=0;b.Font=new Font("Segoe UI",9.6f,FontStyle.Bold);b.TextAlign=ContentAlignment.MiddleLeft;b.Location=new Point(9,y);b.Size=new Size(192,44);b.Click+=(s,e)=>ShowPage(key);b.MouseEnter+=(s,e)=>{if(page!=key)b.BackColor=shade;};b.MouseLeave+=(s,e)=>{if(page!=key)b.BackColor=side;};sidebar.Controls.Add(b);navButtons[key]=b;}
    Panel Card(int x,int y,int w,int h){Panel p=new Panel {Location=new Point(x,y),Size=new Size(w,h),BackColor=card};content.Controls.Add(p);return p;}
    void AddText(Control p,string text,int x,int y,int w,int h,float size,Color color,bool bold=false){Label l=Lbl(text,size,color,bold);l.Location=new Point(x,y);l.Size=new Size(w,h);p.Controls.Add(l);}
    Button Action(Control p,string text,int x,int y,int w,int h,Action act){Button b=Btn(text,accent);b.FlatAppearance.BorderSize=0;b.Location=new Point(x,y);b.Size=new Size(w,h);b.Click+=(s,e)=>act();p.Controls.Add(b);return b;}
    void Section(string title,int y){AddText(content,title,28,y,800,32,16,fg,true);}
    void Tile(string title,string value,int x,int y,int width,Color c){Panel p=Card(x,y,width,110);AddText(p,title.ToUpperInvariant(),15,12,width-24,25,9,muted,true);AddText(p,value,15,43,width-24,52,24,c,true);}
    List<Match> Of(string g){return g==null?store.Matches:store.Matches.Where(m=>m.Game==g).ToList();}
    long Seconds(string g){long s=store.Sessions.Where(a=>g==null || a.Game==g).Sum(a=>a.Seconds);foreach(var kv in active){if(g==null||g==kv.Key)s+=(long)(DateTime.Now-kv.Value).TotalSeconds;}return Math.Max(0,s);}
    string Playtime(string g){long s=Seconds(g);return (s/3600)+"h "+((s%3600)/60)+"m";}
    void UpdateLiveStatus(){status.Text=active.Count>0?"● "+active.Count+" GAME(S) RUNNING":"● MONITORING";}
    // v0.6.0: Screen-sized, responsive pages. Only long MATCH LISTS scroll, never the dashboard.
    int ViewWidth { get { return Math.Max(670,content.ClientSize.Width-48); } }
    int ViewHeight { get { return Math.Max(420,content.ClientSize.Height-34); } }
    Panel Surface(Control parent,int x,int y,int width,int height,Color fill) {
      Panel panel=new Panel {Location=new Point(x,y),Size=new Size(Math.Max(1,width),Math.Max(1,height)),BackColor=fill};
      parent.Controls.Add(panel);return panel;
    }
    Label Copy(Control parent,string value,int x,int y,int width,int height,float size,Color color,bool bold) {
      Label label=Lbl(value,size,color,bold);
      label.AutoEllipsis=true;label.Location=new Point(x,y);label.Size=new Size(Math.Max(1,width),Math.Max(1,height));
      parent.Controls.Add(label);return label;
    }
    Button ButtonAt(Control parent,string text,int x,int y,int width,int height,Action callback,bool primary) {
      Button b=Btn(text,primary?accent:shade);b.FlatAppearance.BorderSize=0;
      b.ForeColor=primary?Color.FromArgb(22,17,39):fg;
      b.Font=new Font("Segoe UI",9.2f,FontStyle.Bold);
      b.Location=new Point(x,y);b.Size=new Size(Math.Max(50,width),Math.Max(30,height));
      b.Click+=(sender,args)=>callback();parent.Controls.Add(b);return b;
    }
    void Rule(Control parent,int x,int y,int width,Color color){Surface(parent,x,y,Math.Max(1,width),1,color);}
    Panel Metric(Control parent,string label,string value,int x,int y,int width,int height,Color valueColor) {
      Panel panel=Surface(parent,x,y,width,height,card);
      Surface(panel,0,0,3,height,valueColor);
      Surface(panel,3,0,width-3,1,Color.FromArgb(72,69,99));
      Copy(panel,label.ToUpperInvariant(),16,13,width-27,19,8.5f,muted,true);
      Copy(panel,value,16,33,width-25,Math.Max(30,height-36),height>=85?21f:16f,valueColor,true);
      return panel;
    }
    void SetNavActive(){
      foreach(KeyValuePair<string,Button> item in navButtons) {
        bool activeItem=item.Key==page;
        item.Value.BackColor=activeItem?shade:side;
        item.Value.ForeColor=activeItem?accent:fg;
      }
    }
    void Tabs(string[] labels,string[] keys,string current,int y,Action<string> onSelect){
      int x=24;
      for(int i=0;i<labels.Length;i++){
        string key=keys[i];int width=Math.Max(112,labels[i].Length*9+34);
        Button b=ButtonAt(content,labels[i],x,y,width,38,()=>onSelect(key),current==key);
        if(current==key){b.BackColor=shade;b.ForeColor=accent;Surface(content,x,y+38,width,3,accent);}
        x+=width+10;
      }
    }
    void ShowPage(string p){
      if(content==null || content.Width<100)return;
      if(page!=p && Array.IndexOf(games,p)>=0)gameTab="Overview";
      page=p;
      content.SuspendLayout();
      foreach(Control c in content.Controls.Cast<Control>().ToArray()){content.Controls.Remove(c);c.Dispose();}
      content.AutoScroll=false;
      heading.Text=Array.IndexOf(games,p)>=0?names[Array.IndexOf(games,p)]:p=="History"?"Historie zápasů":p=="Settings"?"Nastavení":p=="Analytics"?"Analýza výkonu":p=="Achievements"?"Achievementy":"Přehled";
      heading.Font=new Font("Segoe UI",22f,FontStyle.Bold);
      SetNavActive();UpdateLiveStatus();
      if(p=="Overview")BuildOverview();
      else if(Array.IndexOf(games,p)>=0)BuildGamePage(p);
      else if(p=="History")BuildHistory();
      else if(p=="Analytics")BuildAnalytics();
      else if(p=="Achievements")BuildAchievements();
      else if(p=="Settings")BuildSettings();
      content.ResumeLayout(true);
    }

    DateTime? MatchDate(Match m){
      DateTime date;
      return DateTime.TryParse(m.Date,System.Globalization.CultureInfo.InvariantCulture,System.Globalization.DateTimeStyles.None,out date)?(DateTime?)date:null;
    }
    int PeriodDays(string period){return period=="7D"?7:period=="30D"?30:0;}
    List<Match> PeriodMatches(string game,string period){
      DateTime from=DateTime.Now.AddDays(-PeriodDays(period));
      return Of(game).Where(m=>PeriodDays(period)==0 || (MatchDate(m).HasValue && MatchDate(m).Value>=from))
        .OrderByDescending(m=>m.Date).ToList();
    }
    long PeriodSeconds(string game,string period){
      if(PeriodDays(period)==0)return Seconds(game);
      DateTime from=DateTime.Now.AddDays(-PeriodDays(period));
      long stored=0;
      foreach(Session session in store.Sessions.Where(x=>x.Game==game)){
        DateTime started;
        if(DateTime.TryParse(session.Start,System.Globalization.CultureInfo.InvariantCulture,System.Globalization.DateTimeStyles.None,out started) && started>=from)
          stored+=Math.Max(0,session.Seconds);
      }
      if(active.ContainsKey(game))stored+=Math.Max(0,(long)(DateTime.Now-active[game]).TotalSeconds);
      return stored;
    }
    int WinningStreak(List<Match> matches){
      int current=0;
      foreach(Match m in matches.OrderByDescending(m=>m.Date)){
        if(m.Result=="Win")current++;
        else if(m.Result=="Loss")break;
      }
      return current;
    }
    int BestWinningStreak(List<Match> matches){
      int best=0,now=0;
      foreach(Match m in matches.OrderBy(m=>m.Date)){
        if(m.Result=="Win"){now++;best=Math.Max(best,now);}
        else if(m.Result=="Loss")now=0;
      }
      return best;
    }
    string Ratio(List<Match> matches,string game){
      if(matches.Count==0)return "—";
      long kills=matches.Sum(m=>(long)m.Kills),deaths=matches.Sum(m=>(long)m.Deaths),assists=matches.Sum(m=>(long)m.Assists);
      if(game=="LoL")return ((kills+assists)/(double)Math.Max(1L,deaths)).ToString("0.00");
      return deaths==0?"—":(kills/(double)deaths).ToString("0.00");
    }
    string Avg(List<Match> matches,Func<Match,int> property){
      return matches.Count==0?"—":matches.Average(m=>property(m)).ToString("0.0");
    }
    void PeriodButtons(int x,int y){
      string[] keys={"7D","30D","ALL"};
      string[] labels={"7 DNÍ","30 DNÍ","CELKEM"};
      for(int i=0;i<keys.Length;i++){
        string key=keys[i];
        Button b=ButtonAt(content,labels[i],x+i*106,y,100,32,()=>{analyticsPeriod=key;ShowPage("Analytics");},analyticsPeriod==key);
        if(analyticsPeriod==key){b.BackColor=accent;b.ForeColor=Color.FromArgb(24,17,39);}
      }
    }
    void BuildAnalytics(){
      int x=24,w=ViewWidth,h=ViewHeight,gap=12;
      if(content.ClientSize.Height<600){content.AutoScroll=true;content.AutoScrollMinSize=new Size(0,600);}
      Tabs(new string[]{"LOL","CS2","R6 SIEGE"},new string[]{"LoL","CS2","R6"},analyticsGame,12,
        selected=>{analyticsGame=selected;ShowPage("Analytics");});
      PeriodButtons(x,64);
      Copy(content,"Každá hra má vlastní statistiky • žádné míchání KDA mezi hrami",x+328,67,Math.Max(100,w-328),25,9,muted,false);
      List<Match> results=PeriodMatches(analyticsGame,analyticsPeriod);
      int count=results.Count,wins=results.Count(m=>m.Result=="Win"),losses=results.Count(m=>m.Result=="Loss");
      int best=BestWinningStreak(results),streak=WinningStreak(results);
      int tileW=(w-3*gap)/4;
      Metric(content,"Zápasy",count.ToString(),x,111,tileW,92,accent);
      Metric(content,"Win rate",count==0?"—":(100.0*wins/count).ToString("0.0")+" %",x+tileW+gap,111,tileW,92,green);
      Metric(content,analyticsGame=="LoL"?"KDA":"K/D",Ratio(results,analyticsGame),x+2*(tileW+gap),111,tileW,92,fg);
      Metric(content,"Výhry v řadě",streak.ToString(),x+3*(tileW+gap),111,tileW,92,Color.FromArgb(241,195,120));
      Metric(content,"Průměr killů",Avg(results,m=>m.Kills),x,214,tileW,72,fg);
      Metric(content,"Průměr smrtí",Avg(results,m=>m.Deaths),x+tileW+gap,214,tileW,72,fg);
      Metric(content,"Průměr asistencí",Avg(results,m=>m.Assists),x+2*(tileW+gap),214,tileW,72,fg);
      long seconds=PeriodSeconds(analyticsGame,analyticsPeriod);
      Metric(content,"Herní čas",(seconds/3600)+"h "+((seconds%3600)/60)+"m",x+3*(tileW+gap),214,tileW,72,fg);
      int y=301,graphWidth=(int)(w*0.61),rightWidth=w-graphWidth-gap;
      int blockHeight=Math.Max(275,h-y+10);
      Panel graph=Surface(content,x,y,graphWidth,blockHeight,card);
      Copy(graph,"FORMA • POSLEDNÍCH 16 ZÁPASŮ",15,13,graphWidth-27,25,10,fg,true);
      int played=wins+losses;
      Copy(graph,wins+" VÝHER   /   "+losses+" PROHER",15,42,graphWidth-30,24,9,muted,true);
      ResultsChart chart=new ResultsChart(results.Take(16));
      chart.Location=new Point(11,77);
      chart.Size=new Size(Math.Max(150,graphWidth-22),Math.Max(100,blockHeight-89));
      graph.Controls.Add(chart);
      Panel highlights=Surface(content,x+graphWidth+gap,y,rightWidth,blockHeight,card);
      Copy(highlights,"HIGHLIGHTS",14,14,rightWidth-28,25,10,accent,true);
      Rule(highlights,13,46,rightWidth-26,line);
      Copy(highlights,"Nejdelší série výher",14,64,rightWidth-28,22,9,muted,false);
      Copy(highlights,best+" výher",14,86,rightWidth-28,33,16,green,true);
      Rule(highlights,13,130,rightWidth-26,line);
      if(analyticsGame=="LoL"){
        var top=results.Where(m=>!string.IsNullOrEmpty(m.Champion)).GroupBy(m=>m.Champion)
          .OrderByDescending(g=>g.Count()).Take(2).ToList();
        Copy(highlights,"NEJHRANĚJŠÍ ŠAMPIONI",14,146,rightWidth-28,21,9,muted,true);
        int pos=171;
        foreach(var champ in top){
          string detail=champ.Count()+" her / "+(100.0*champ.Count(m=>m.Result=="Win")/champ.Count()).ToString("0")+"% WR";
          Copy(highlights,champ.Key,14,pos,rightWidth-28,23,10,fg,true);
          Copy(highlights,detail,14,pos+23,rightWidth-28,22,9,muted,false);
          pos+=48;
        }
        if(top.Count==0)Copy(highlights,"Zatím žádná data o šampionech.",14,174,rightWidth-28,54,9,muted,false).AutoEllipsis=false;
      }else{
        Copy(highlights,"ÚSPĚŠNOST",14,146,rightWidth-28,22,9,muted,true);
        Copy(highlights,played==0?"—":wins+" : "+losses,14,172,rightWidth-28,33,18,fg,true);
        Copy(highlights,"Výsledky CS2/R6 zatím zadáváme ručně.",14,213,rightWidth-28,48,9,muted,false).AutoEllipsis=false;
      }
      // Results chart is based strictly on recorded matches; no fabricated live stats.
    }

    List<Achievement> GetAchievements(){
      List<Match> all=Of(null);
      long wins=all.Count(m=>m.Result=="Win"),kills=all.Sum(m=>(long)m.Kills);
      int best=games.Max(g=>BestWinningStreak(Of(g)));
      long played=all.Count,gameCount=games.Count(g=>Of(g).Count>0);
      return new List<Achievement>{
        new Achievement("První zápas","Zaznamenej svůj první herní zápas.",played,1),
        new Achievement("První vítězství","Zaznamenej svou první výhru.",wins,1),
        new Achievement("Deset výher","Dosáhni 10 vítězství napříč hrami.",wins,10),
        new Achievement("Veterán","Dosáhni 100 vítězství napříč hrami.",wins,100),
        new Achievement("Lovec","Získej dohromady 100 killů.",kills,100),
        new Achievement("Elitní lovec","Získej dohromady 1 000 killů.",kills,1000),
        new Achievement("Nezastavitelný","Vyhraj 3 zápasy za sebou v jedné hře.",best,3),
        new Achievement("LoL veteran","Zaznamenej 20 LoL zápasů.",Of("LoL").Count,20),
        new Achievement("Multi-gamer","Zaznamenej zápas ve všech třech hrách.",gameCount,3),
        new Achievement("Centurion","Zaznamenej dohromady 100 zápasů.",played,100),
        new Achievement("Perfect match","Vyhraj zápas s 0 smrtmi.",all.Count(m=>m.Result=="Win"&&m.Deaths==0),1),
        new Achievement("Night owl","Nasbírej 10 hodin sledovaného hraní.",Seconds(null)/3600,10)
      };
    }
    void BuildAchievements(){
      int w=ViewWidth,h=ViewHeight,x=24,gap=12;
      List<Achievement> badges=GetAchievements();
      int done=badges.Count(a=>a.Unlocked);
      Panel summary=Surface(content,x,12,w,97,shade);
      Copy(summary,"ODZNAKY Z TVÝCH SKUTEČNÝCH VÝSLEDKŮ",17,11,w-34,21,10,accent,true);
      Copy(summary,done+" / "+badges.Count+" ODEMČENO",17,37,260,42,22,fg,true);
      int barX=286,barW=Math.Max(80,w-barX-20);
      Surface(summary,barX,53,barW,9,line);
      Surface(summary,barX,53,Math.Max(3,(int)(barW*(double)done/badges.Count)),9,green);
      Copy(content,"Plnění se počítá automaticky z uložených zápasů a herního času.",x,117,w,26,9,muted,false);
      Panel holder=Surface(content,x,150,w,Math.Max(170,h-157),bg);
      holder.AutoScroll=true;
      int colW=(w-gap-20)/2;
      for(int i=0;i<badges.Count;i++){
        Achievement a=badges[i];int col=i%2,row=i/2;
        Panel badge=Surface(holder,col*(colW+gap),row*101,colW,91,card);
        Color indicator=a.Unlocked?green:line;
        Surface(badge,0,0,4,91,indicator);
        Copy(badge,a.Name,14,9,colW-27,23,11,a.Unlocked?green:fg,true);
        Copy(badge,a.Description,14,34,colW-29,21,8,muted,false);
        string progress=a.Unlocked?"✓ ODEMČENO":Math.Min(a.Progress,a.Goal)+" / "+a.Goal;
        Copy(badge,progress,14,60,Math.Max(95,colW-130),22,9,a.Unlocked?green:muted,true);
        int progressX=colW-107;
        Surface(badge,progressX,68,90,5,line);
        Surface(badge,progressX,68,(int)Math.Max(2,90.0*Math.Min(a.Progress,a.Goal)/a.Goal),5,indicator);
      }
      holder.AutoScrollMinSize=new Size(0,((badges.Count+1)/2)*101);
    }
    void BuildOverview(){
      int w=ViewWidth,h=ViewHeight,x=24;
      List<Match> all=Of(null);
      int wins=all.Count(m=>m.Result=="Win");
      int losses=all.Count(m=>m.Result=="Loss");
      long xp=wins*100L+losses*30L+all.Sum(m=>(long)m.Kills)*5L+all.Sum(m=>(long)m.Assists)*2L;
      Panel hero=Surface(content,x,15,w,104,shade);
      Surface(hero,0,0,5,104,accent);
      Surface(hero,6,0,w-6,2,Color.FromArgb(82,62,120));
      Copy(hero,"GIOVI  /  LEVEL "+(1+xp/1000),20,14,w-38,35,20,accent,true);
      Copy(hero,xp.ToString("N0")+" XP CELKEM",20,54,220,20,9,muted,true);
      List<Achievement> achieved=GetAchievements();
      Copy(hero,achieved.Count(a=>a.Unlocked)+" / "+achieved.Count+" ACHIEVEMENTŮ",Math.Max(240,w-264),16,247,28,10,green,true).TextAlign=ContentAlignment.MiddleRight;
      int progressX=230,progressW=Math.Max(80,w-progressX-24);
      Surface(hero,progressX,63,progressW,8,line);
      Surface(hero,progressX,63,Math.Max(3,(int)(progressW*(xp%1000)/1000.0)),8,accent);
      Copy(hero,(xp%1000)+" / 1000 XP",progressX,80,progressW,18,9,muted,false);
      int gap=12,metricW=(w-3*gap)/4;
      Metric(content,"Zápasy",all.Count.ToString(),x,132,metricW,90,accent);
      Metric(content,"Vítězství",wins.ToString(),x+metricW+gap,132,metricW,90,green);
      Metric(content,"Win rate",all.Count==0?"—":(wins*100.0/all.Count).ToString("0.0")+"%",x+(metricW+gap)*2,132,metricW,90,fg);
      Metric(content,"Herní čas",Playtime(null),x+(metricW+gap)*3,132,metricW,90,fg);
      Copy(content,"MOJE HRY",x,239,w,24,11,fg,true);
      int gW=(w-2*gap)/3;
      for(int i=0;i<3;i++){
        string game=games[i];int idx=i;
        Panel tile=Surface(content,x+i*(gW+gap),271,gW,139,card);
        Color mark=i==0?Color.FromArgb(240,188,117):i==1?Color.FromArgb(106,201,225):accent;
        Surface(tile,0,0,gW,3,mark);
        Copy(tile,game,16,14,gW-28,31,19,mark,true);
        Copy(tile,names[i],16,46,gW-25,24,10,fg,true);
        int gm=Of(game).Count;int gw=Of(game).Count(m=>m.Result=="Win");
        Copy(tile,gm+" zápasů  •  "+(gm==0?"—":(gw*100.0/gm).ToString("0")+" % WR"),16,74,gW-25,23,9,muted,false);
        if(game=="CS2" && cs2State.SeenUtc!=DateTime.MinValue && DateTime.UtcNow-cs2State.SeenUtc<TimeSpan.FromSeconds(40))
          Copy(tile,"● CS2 GSI LIVE",Math.Max(115,gW-153),14,137,21,8,green,true).TextAlign=ContentAlignment.MiddleRight;
        ButtonAt(tile,"OTEVŘÍT STATISTIKY  →",13,105,gW-26,28,()=>ShowPage(games[idx]),false);
      }
      int bottomY=424;
      Panel bottom=Surface(content,x,bottomY,w,Math.Max(100,h-bottomY+12),card);
      Copy(bottom,"POSLEDNÍ AKTIVITA",16,12,w-36,22,10,fg,true);
      int visibleRows=Math.Min(3,Math.Max(1,(bottom.Height-36)/42));
      List<Match> latest=all.OrderByDescending(m=>m.Date).Take(visibleRows).ToList();
      if(latest.Count==0)Copy(bottom,"Zatím žádné zápasy. V LoL použij SYNC a stáhni si první výsledky.",16,47,w-30,35,10,muted,false);
      else {
        int row=44;
        foreach(Match m in latest){
          Copy(bottom,m.Game+"  •  "+(m.Result=="Win"?"VÝHRA":m.Result=="Loss"?"PROHRA":m.Result),16,row,Math.Max(130,w/3),26,11,m.Result=="Win"?green:danger,true);
          Copy(bottom,m.Kills+" / "+m.Deaths+" / "+m.Assists+" K/D/A",w/3+10,row+2,180,24,10,fg,true);
          row+=42;
        }
        ButtonAt(bottom,"ANALÝZY  →",w-164,Math.Max(42,bottom.Height-45),149,34,()=>ShowPage("Analytics"),false);
      }
    }
    void BuildGamePage(string game){
      string[] labels=game=="LoL"?new string[]{"PŘEHLED","ZÁPASY","ŠAMPIONI"}:new string[]{"PŘEHLED","ZÁPASY"};
      string[] keys=game=="LoL"?new string[]{"Overview","Matches","Champions"}:new string[]{"Overview","Matches"};
      Tabs(labels,keys,gameTab,12,selected=>{gameTab=selected;ShowPage(game);});
      if(gameTab=="Matches") {BuildGameMatches(game);return;}
      if(gameTab=="Champions"&&game=="LoL") {BuildChampions();return;}
      BuildGameOverview(game);
    }
    void BuildGameOverview(string game){
      int w=ViewWidth,h=ViewHeight,x=24,gap=12;
      List<Match> mm=Of(game);
      int win=mm.Count(m=>m.Result=="Win"),loss=mm.Count(m=>m.Result=="Loss");
      int kills=mm.Sum(m=>m.Kills),deaths=mm.Sum(m=>m.Deaths),assists=mm.Sum(m=>m.Assists);
      Copy(content,active.ContainsKey(game)?"● HRA SPUŠTĚNA":"○ HRA NENÍ SPUŠTĚNA",x,67,300,23,9,active.ContainsKey(game)?green:muted,true);
      Copy(content,"Sledovaný čas: "+Playtime(game),w-245+x,67,245,23,9,muted,false).TextAlign=ContentAlignment.MiddleRight;
      int big=(w-2*gap)/3;
      Metric(content,"Zápasy",mm.Count.ToString(),x,99,big,87,accent);
      Metric(content,"Win rate",mm.Count==0?"—":(win*100.0/mm.Count).ToString("0.0")+"%",x+big+gap,99,big,87,green);
      string ratio=mm.Count==0?"—":game=="LoL"?((kills+assists)/(double)Math.Max(1,deaths)).ToString("0.00"):deaths==0?"—":(kills/(double)deaths).ToString("0.00");
      Metric(content,game=="LoL"?"KDA":"K/D",ratio,x+(big+gap)*2,99,big,87,fg);
      int small=(w-4*gap)/5;
      string[] labels={"Výhry","Prohry","Killy","Smrti","Asistence"};
      int[] values={win,loss,kills,deaths,assists};
      for(int i=0;i<5;i++)Metric(content,labels[i],values[i].ToString(),x+i*(small+gap),198,small,65,i==0?green:i==1?danger:fg);
      int y=277, boxH=Math.Max(218,h-y+11);
      int leftWidth=Math.Max(310,(int)(w*0.59));int rightX=x+leftWidth+gap;int rightWidth=w-leftWidth-gap;
      Panel history=Surface(content,x,y,leftWidth,boxH,card);
      Copy(history,"POSLEDNÍ ZÁPASY",14,14,leftWidth-30,25,11,fg,true);
      ButtonAt(history,"VŠECHNY  →",leftWidth-146,10,130,31,()=>{gameTab="Matches";ShowPage(game);},false);
      Rule(history,13,51,leftWidth-26,line);
      List<Match> recent=mm.OrderByDescending(m=>m.Date).Take(5).ToList();
      MatchList(history,recent,12,60,leftWidth-24,Math.Max(70,boxH-72),5);
      Panel connection=Surface(content,rightX,y,rightWidth,boxH,card);
      if(game=="LoL"){
        Copy(connection,"RIOT  •  LIVE DATA",14,13,rightWidth-24,25,10,accent,true);
        Rule(connection,13,42,rightWidth-26,line);
        Copy(connection,riot.GameName+"#"+riot.TagLine,13,51,rightWidth-26,28,11,fg,true);
        Copy(connection,riot.Region.ToUpperInvariant()+" SERVER",13,81,rightWidth-26,20,9,muted,true);
        Label syncText=Copy(connection,riotStatus,13,108,rightWidth-26,Math.Max(30,boxH-190),9,muted,false);
        syncText.AutoEllipsis=false;
        ButtonAt(connection,"PROPOJIT ÚČET",12,boxH-83,rightWidth-24,33,()=>ConfigureRiot(),false);
        Button sync=ButtonAt(connection,riotSyncBusy?"SYNCHRONIZUJI...":"SYNCHRONIZOVAT",12,boxH-43,rightWidth-24,33,()=>TrySyncLoL(false),true);
        sync.Enabled=!riotSyncBusy;
      }else if(game=="CS2"){
        Copy(connection,"CS2  •  LIVE GAME STATE",14,13,rightWidth-28,25,10,accent,true);
        Rule(connection,13,43,rightWidth-26,line);
        cs2LiveLabel=Copy(connection,Cs2LiveText(),14,55,rightWidth-28,Math.Max(70,boxH-145),10,fg,false);
        cs2LiveLabel.AutoEllipsis=false;
        Copy(connection,"Automatický zápis výsledků vyžaduje GSI a konec zápasu.",14,Math.Max(119,boxH-85),rightWidth-26,31,8,muted,false);
        ButtonAt(connection,"NASTAVIT CS2 GSI",12,boxH-44,rightWidth-24,34,()=>ConfigureCs2Gsi(),true);
      }else{
        Copy(connection,"SLEDOVÁNÍ HRY",14,14,rightWidth-28,25,10,accent,true);
        Rule(connection,13,47,rightWidth-26,line);
        Copy(connection,"Herní čas se ukládá automaticky.",13,61,rightWidth-26,60,10,fg,false).AutoEllipsis=false;
        Copy(connection,"Výsledky Rainbow Six Siege zatím zadávej ručně.",13,124,rightWidth-26,54,9,muted,false).AutoEllipsis=false;
        ButtonAt(connection,"+ PŘIDAT ZÁPAS",12,boxH-45,rightWidth-24,34,()=>AddMatch(game),true);
      }
    }
    void BuildGameMatches(string game){
      int w=ViewWidth,h=ViewHeight,x=24;
      List<Match> mm=Of(game).OrderByDescending(m=>m.Date).ToList();
      Copy(content,"HISTORIE  /  "+mm.Count+" ZÁPASŮ",x,70,Math.Max(180,w-270),29,12,fg,true);
      ButtonAt(content,"+ PŘIDAT ZÁPAS",x+w-183,64,183,37,()=>AddMatch(game),true);
      Panel rows=Surface(content,x,115,w,Math.Max(230,h-117),card);
      MatchList(rows,mm,12,12,w-24,rows.Height-22,mm.Count);
    }
    void BuildChampions(){
      int w=ViewWidth,h=ViewHeight,x=24;
      List<Match> mm=Of("LoL").Where(m=>!string.IsNullOrEmpty(m.Champion)).ToList();
      var group=mm.GroupBy(m=>m.Champion).OrderByDescending(g=>g.Count()).ToList();
      Copy(content,"TOP ŠAMPIONI",x,70,w,29,12,fg,true);
      Copy(content,"Výsledky načtené z Riot API • počet her a úspěšnost",x,103,w,26,9,muted,false);
      Panel list=Surface(content,x,140,w,Math.Max(180,h-146),card);
      if(group.Count==0){Copy(list,"Po synchronizaci se tu zobrazí tvoji nejhranější šampioni.",20,30,w-40,45,11,muted,false);return;}
      Panel scroll=Surface(list,12,12,w-24,list.Height-23,card);scroll.AutoScroll=true;
      int y=0;
      foreach(var champ in group){
        int count=champ.Count(),wins=champ.Count(m=>m.Result=="Win");
        Panel row=Surface(scroll,0,y,Math.Max(250,scroll.Width-20),54,shade);
        Copy(row,champ.Key,15,10,(int)(row.Width*0.37),33,12,fg,true);
        Copy(row,count+" ZÁPASŮ",(int)(row.Width*0.41),15,115,24,9,muted,true);
        string wr=(wins*100.0/count).ToString("0")+" % WR";
        Copy(row,wr,row.Width-132,13,122,28,11,wins*2>=count?green:danger,true).TextAlign=ContentAlignment.MiddleRight;
        y+=61;
      }
      scroll.AutoScrollMinSize=new Size(0,Math.Max(0,y));
    }
    List<Match> FilterHistory(){
      List<Match> rows=store.Matches.Where(m=>historyFilter=="All"||m.Game==historyFilter).ToList();
      if(historyResult!="All")rows=rows.Where(m=>m.Result==historyResult).ToList();
      if(historyPeriod!="All"){
        int days=PeriodDays(historyPeriod);
        DateTime from=DateTime.Now.AddDays(-days);
        rows=rows.Where(m=>MatchDate(m).HasValue&&MatchDate(m).Value>=from).ToList();
      }
      if(!string.IsNullOrWhiteSpace(historySearch)){
        string q=historySearch.Trim();
        rows=rows.Where(m=>(m.Champion??"").IndexOf(q,StringComparison.OrdinalIgnoreCase)>=0
          ||(m.Queue??"").IndexOf(q,StringComparison.OrdinalIgnoreCase)>=0
          ||(m.Game??"").IndexOf(q,StringComparison.OrdinalIgnoreCase)>=0
          ||(m.MatchId??"").IndexOf(q,StringComparison.OrdinalIgnoreCase)>=0).ToList();
      }
      return rows.OrderByDescending(m=>m.Date).ToList();
    }
    void RefreshHistory(){
      if(historyListPanel==null || historyListPanel.IsDisposed || historyListPanel.Width<210 || historyListPanel.Height<90)return;
      List<Match> rows=FilterHistory();
      if(historyCount!=null&&!historyCount.IsDisposed)historyCount.Text=rows.Count+" ZÁPASŮ";
      foreach(Control control in historyListPanel.Controls.Cast<Control>().ToArray()){
        historyListPanel.Controls.Remove(control);control.Dispose();
      }
      MatchList(historyListPanel,rows,12,12,Math.Max(180,historyListPanel.Width-24),Math.Max(80,historyListPanel.Height-24),rows.Count);
    }
    void BuildHistory(){
      // The filter toolbar is its own fixed row. Only the match list scrolls.
      // This avoids previous absolute positioning/dock overlap and clipped inputs.
      TableLayoutPanel stack=new TableLayoutPanel {
        Dock=DockStyle.Fill,ColumnCount=1,RowCount=2,Margin=new Padding(0),Padding=new Padding(0),
        BackColor=bg
      };
      stack.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));
      stack.RowStyles.Add(new RowStyle(SizeType.Absolute,166));
      stack.RowStyles.Add(new RowStyle(SizeType.Percent,100));
      content.Controls.Add(stack);
      Panel tools=new Panel{Dock=DockStyle.Fill,Margin=new Padding(12,0,12,0),BackColor=bg};
      stack.Controls.Add(tools,0,0);
      Panel results=new Panel{Dock=DockStyle.Fill,Margin=new Padding(12,0,12,10),BackColor=card,Padding=new Padding(8)};
      stack.Controls.Add(results,0,1);
      int widths=Math.Max(680,content.ClientSize.Width-24);
      int rowWidth=widths-24;
      string[] names={"VŠE","LEAGUE OF LEGENDS","CS2","R6 SIEGE"};
      string[] keys={"All","LoL","CS2","R6"};
      int tabX=12;
      for(int i=0;i<keys.Length;i++){
        string chosen=keys[i];int tabW=i==1?156:115;
        Button b=ButtonAt(tools,names[i],tabX,7,tabW,35,()=>{historyFilter=chosen;ShowPage("History");},historyFilter==chosen);
        if(historyFilter==chosen){b.BackColor=accent;b.ForeColor=bg;}else {b.BackColor=shade;b.ForeColor=fg;}
        tabX+=tabW+9;
      }
      int comboW=Math.Max(150,Math.Min(215,(rowWidth-310)/3));
      Label l1=Copy(tools,"VÝSLEDEK",12,51,comboW,20,8,muted,true);
      Label l2=Copy(tools,"OBDOBÍ",23+comboW,51,comboW,20,8,muted,true);
      int searchX=34+2*comboW;
      Copy(tools,"HLEDAT ŠAMPIONA / MAPU / MATCH ID",searchX,51,Math.Max(160,rowWidth-searchX),20,8,muted,true);
      ComboBox result=new ComboBox {
        Location=new Point(12,76),Size=new Size(comboW,32),DropDownStyle=ComboBoxStyle.DropDownList,
        FlatStyle=FlatStyle.Flat,BackColor=shade,ForeColor=fg,Font=new Font("Segoe UI",10)
      };
      result.Items.AddRange(new object[]{"Všechny výsledky","Jen výhry","Jen prohry"});
      result.SelectedIndex=historyResult=="Win"?1:historyResult=="Loss"?2:0;
      tools.Controls.Add(result);
      result.SelectedIndexChanged+=(sender,args)=>{
        historyResult=result.SelectedIndex==1?"Win":result.SelectedIndex==2?"Loss":"All";RefreshHistory();
      };
      ComboBox period=new ComboBox {
        Location=new Point(23+comboW,76),Size=new Size(comboW,32),DropDownStyle=ComboBoxStyle.DropDownList,
        FlatStyle=FlatStyle.Flat,BackColor=shade,ForeColor=fg,Font=new Font("Segoe UI",10)
      };
      period.Items.AddRange(new object[]{"Celá historie","Posledních 7 dní","Posledních 30 dní"});
      period.SelectedIndex=historyPeriod=="7D"?1:historyPeriod=="30D"?2:0;
      tools.Controls.Add(period);
      period.SelectedIndexChanged+=(sender,args)=>{
        historyPeriod=period.SelectedIndex==1?"7D":period.SelectedIndex==2?"30D":"All";RefreshHistory();
      };
      TextBox search=new TextBox{
        Location=new Point(searchX,77),Size=new Size(Math.Max(165,rowWidth-searchX-14),30),
        Font=new Font("Segoe UI",11),BackColor=shade,ForeColor=fg,BorderStyle=BorderStyle.FixedSingle,Text=historySearch
      };
      tools.Controls.Add(search);
      search.TextChanged+=(sender,args)=>{historySearch=search.Text;RefreshHistory();};
      historyCount=Copy(tools,"",12,130,170,25,11,fg,true);
      Copy(tools,"FILTROVÁNÍ ZŮSTÁVÁ VŽDY NAHOŘE",171,133,Math.Max(140,rowWidth-520),22,8,muted,true);
      ButtonAt(tools,"EXPORT CSV",rowWidth-296,123,137,34,()=>ExportCsv(),false);
      ButtonAt(tools,"SMAZAT POSLEDNÍ",rowWidth-149,123,149,34,()=>DeleteMatch(),false);
      historyListPanel=new Panel{Dock=DockStyle.Fill,BackColor=card};
      results.Controls.Add(historyListPanel);
      historyListPanel.Resize+=(sender,args)=>{
        if(historyListPanel!=null && !historyListPanel.IsDisposed && historyListPanel.Width>220)RefreshHistory();
      };
      RefreshHistory();
    }
    void BuildSettings(){
      int w=ViewWidth,h=ViewHeight,x=24;
      Tabs(new string[]{"AKTUALIZACE","PROPOJENÍ","DATA A START"},new string[]{"Updates","Connect","Data"},settingsTab,12,selected=>{settingsTab=selected;ShowPage("Settings");});
      Panel area=Surface(content,x,66,w,Math.Max(260,h-69),card);
      int width=area.Width;
      if(settingsTab=="Updates"){
        Copy(area,"GIOVI.GG  v"+CurrentVersion,20,20,width-36,36,21,accent,true);
        Copy(area,"Aktualizace aplikace",20,67,width-36,24,11,fg,true);
        Label message=Copy(area,updateMessage,20,104,width-45,65,10,muted,false);message.AutoEllipsis=false;
        Button check=ButtonAt(area,updateBusy?"KONTROLUJI...":"CHECK FOR UPDATES",20,185,222,40,()=>BeginUpdateCheck(false),true);check.Enabled=!updateBusy;
        if(availableUpdate!=null){Button install=ButtonAt(area,"INSTALL v"+availableUpdate.Number.ToString(),256,185,216,40,()=>StartUpdate(),false);install.Enabled=!updateBusy;}
        Rule(area,20,247,width-40,line);
        Copy(area,"Novinky ve verzi 0.7.0",20,264,width-40,26,11,fg,true);
        Copy(area,"NOVĚ: CS2 Game State Integration (živá K/D/A a detekce konce zápasu), připnuté filtry historie, modernizovaný přehled, lepší orientace a zachované statistiky.",20,298,width-44,76,10,muted,false).AutoEllipsis=false;
      }else if(settingsTab=="Connect"){
        Copy(area,"LEAGUE OF LEGENDS",20,20,width-40,30,13,accent,true);
        Copy(area,riot.GameName+"#"+riot.TagLine+"  •  "+riot.Region.ToUpperInvariant(),20,60,width-40,27,12,fg,true);
        Copy(area,riotStatus,20,95,width-44,55,10,muted,false).AutoEllipsis=false;
        ButtonAt(area,"RIOT ÚČET / API KEY",20,162,232,40,()=>ConfigureRiot(),true);
        Button sync=ButtonAt(area,riotSyncBusy?"SYNCHRONIZUJI...":"SYNC MATCHES",265,162,202,40,()=>TrySyncLoL(false),false);sync.Enabled=!riotSyncBusy;
        Rule(area,20,226,width-40,line);
        Copy(area,"COUNTER-STRIKE 2 • GSI",20,237,width-40,25,12,fg,true);
        Copy(area,"Živá K/D/A a skóre přímo z CS2. Při zachyceném konci zápasu uložíme výsledek. Bez zásahů do hry.",20,269,width-42,48,9,muted,false).AutoEllipsis=false;
        ButtonAt(area,"NASTAVIT CS2 GSI",20,322,220,38,()=>ConfigureCs2Gsi(),true);
        Copy(area,cs2Status,257,320,Math.Max(160,width-284),72,9,muted,false).AutoEllipsis=false;
        Copy(area,"R6 Siege: nyní automaticky pouze herní čas · Riot API klíč zůstává v paměti.",20,403,width-40,32,9,muted,false);
      }else{
        Copy(area,"MOJE DATA",20,20,width-40,28,13,accent,true);
        Copy(area,"Statistiky jsou uložené lokálně na tvém počítači.",20,59,width-40,40,10,muted,false);
        Copy(area,store.FileName,20,101,width-40,38,9,fg,false);
        ButtonAt(area,"OTEVŘÍT SLOŽKU",20,153,205,39,()=>Process.Start("explorer.exe",store.Folder),false);
        ButtonAt(area,"ZÁLOHOVAT DATA",239,153,205,39,()=>BackupStats(),true);
        Rule(area,20,219,width-40,line);
        Copy(area,"SPUŠTĚNÍ S WINDOWS",20,241,width-40,27,12,fg,true);
        Copy(area,"Detekce spuštěných her běží každých 15 sekund i po minimalizaci do lišty.",20,274,width-40,49,10,muted,false).AutoEllipsis=false;
        ButtonAt(area,AutoStartEnabled()?"VYPNOUT AUTOSTART":"ZAPNOUT AUTOSTART",20,343,245,40,()=>{ToggleAutoStart();ShowPage("Settings");},false);
      }
    }
    void MatchList(Control parent,List<Match> matches,int x,int y,int width,int height,int limit){
      Panel list=Surface(parent,x,y,width,Math.Max(55,height),card);
      list.AutoScroll=true;
      if(matches.Count==0){Copy(list,"Žádné zápasy. V LoL je stáhneš přes Sync, jiné můžeš přidat ručně.",8,12,list.Width-24,70,10,muted,false).AutoEllipsis=false;return;}
      int yy=0;
      foreach(Match m in matches.Take(Math.Max(0,limit))){
        int rowW=Math.Max(180,width-21);
        Panel row=Surface(list,0,yy,rowW,49,shade);
        Surface(row,0,0,3,49,m.Result=="Win"?green:m.Result=="Loss"?danger:muted);
        int titleW=Math.Max(110,(int)(rowW*0.43));
        Copy(row,m.Game+"  •  "+(m.Result=="Win"?"WIN":m.Result=="Loss"?"LOSS":m.Result),12,4,titleW-10,23,10,m.Result=="Win"?green:m.Result=="Loss"?danger:muted,true);
        string description=!string.IsNullOrEmpty(m.Champion)?m.Champion+(string.IsNullOrEmpty(m.Queue)?"":" · "+m.Queue):(m.Source??"Manual");
        Copy(row,description,12,26,titleW-10,18,8,muted,false);
        Copy(row,m.Kills+" / "+m.Deaths+" / "+m.Assists,(int)(rowW*0.46),11,(int)(rowW*0.25),27,10,fg,true);
        string date=m.Date??"";
        if(date.Length>=16)date=date.Substring(5,11).Replace('T',' ');
        if(rowW>=520)Copy(row,date,rowW-178,13,94,22,8,muted,false).TextAlign=ContentAlignment.MiddleRight;
        ButtonAt(row,"DETAIL",Math.Max(220,rowW-79),11,71,29,()=>ShowMatchDetail(m),false);
        yy+=55;
      }
      list.AutoScrollMinSize=new Size(0,yy+2);
    }
    void ShowMatchDetail(Match match){
      using(Form dlg=new Form()){
        dlg.Text="GIOVI.GG — Detail zápasu";
        dlg.BackColor=side;dlg.ForeColor=fg;dlg.ClientSize=new Size(492,413);
        dlg.StartPosition=FormStartPosition.CenterParent;dlg.FormBorderStyle=FormBorderStyle.FixedDialog;
        dlg.MaximizeBox=false;dlg.MinimizeBox=false;
        AddText(dlg,match.Game+"   •   "+(match.Result=="Win"?"VÝHRA":match.Result=="Loss"?"PROHRA":"REMÍZA"),23,19,445,34,18,match.Result=="Win"?green:danger,true);
        AddText(dlg,"KILLS  /  DEATHS  /  ASSISTS",23,72,440,20,10,muted,true);
        AddText(dlg,match.Kills+"  /  "+match.Deaths+"  /  "+match.Assists,23,98,440,46,27,fg,true);
        string[] items={"Šampion: "+(string.IsNullOrEmpty(match.Champion)?"—":match.Champion),
          "Herní režim: "+(string.IsNullOrEmpty(match.Queue)?"—":match.Queue),
          "Datum: "+(match.Date??"" ).Replace('T',' '),
          "Délka: "+(match.Duration>0?(match.Duration/60)+" min "+(match.Duration%60)+" s":"—"),
          "Zdroj: "+(match.Source??"Manual")};
        for(int i=0;i<items.Length;i++)AddText(dlg,items[i],23,158+30*i,448,26,10,i==4?muted:fg,false);
        ButtonAt(dlg,"ZAVŘÍT",25,351,200,39,()=>dlg.Close(),false);
        ButtonAt(dlg,"SMAZAT ZÁPAS",244,351,225,39,()=>{
          if(MessageBox.Show(dlg,"Opravdu chceš tento zápas odstranit?"+(match.Source=="Riot API"?"\nRiot sync ho může později znovu načíst.":""),
            "Potvrzení smazání",MessageBoxButtons.YesNo,MessageBoxIcon.Warning)!=DialogResult.Yes)return;
          if(!store.Matches.Contains(match))return;
          store.Matches.Remove(match);
          try {store.Save();dlg.Close();ShowPage(page);}
          catch(Exception ex){store.Matches.Add(match);MessageBox.Show(dlg,ex.Message,"Chyba ukládání");}
        },false).BackColor=Color.FromArgb(97,47,72);
        dlg.ShowDialog(this);
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
    void ReceiveCs2(Cs2State snapshot,Match completed){
      cs2State=snapshot;
      cs2Status="CS2 GSI: "+snapshot.Map+" / "+snapshot.Phase+" · "+snapshot.Kills+" / "+snapshot.Deaths+" / "+snapshot.Assists;
      if(completed!=null){
        if(!store.Matches.Any(m=>m.MatchId==completed.MatchId)){
          store.Matches.Add(completed);
          try {store.Save();cs2Status="CS2 GSI: zápas na "+snapshot.Map+" uložen ("+completed.Result+").";}
          catch(Exception ex){store.Matches.Remove(completed);cs2Status="CS2 GSI: chyba uložení: "+ex.Message;}
        }
        if(page=="CS2"||page=="Overview"||page=="History"||page=="Analytics")ShowPage(page);
      }
      if(cs2LiveLabel!=null && !cs2LiveLabel.IsDisposed)cs2LiveLabel.Text=Cs2LiveText();
    }
    string Cs2LiveText(){
      if(cs2State.SeenUtc==DateTime.MinValue || DateTime.UtcNow-cs2State.SeenUtc>TimeSpan.FromSeconds(40))
        return "Čekám na data z CS2. V Nastavení → Propojení nainstaluj GSI konfiguraci a restartuj CS2.";
      return "MAPA: "+cs2State.Map+"
FÁZE: "+cs2State.Phase+"     SKÓRE CT/T: "+cs2State.Ct+" : "+cs2State.T+
        "
TVÉ K/D/A: "+cs2State.Kills+" / "+cs2State.Deaths+" / "+cs2State.Assists;
    }
    void ConfigureCs2Gsi(){
      if(cs2Receiver==null){MessageBox.Show(this,cs2Status,"CS2 GSI",MessageBoxButtons.OK,MessageBoxIcon.Warning);return;}
      if(MessageBox.Show(this,"Vybereme složku CS2 ...\\game\\csgo\\cfg a vložíme do ní lokální GSI konfigurační soubor.\nCS2 musíš potom úplně restartovat.\n\nPokračovat?",
        "GIOVI.GG — CS2 GSI",MessageBoxButtons.YesNo,MessageBoxIcon.Question)!=DialogResult.Yes)return;
      using(FolderBrowserDialog dialog=new FolderBrowserDialog()){
        dialog.Description="Vyber CS2 složku game\\csgo\\cfg (Steam → CS2 → Spravovat → Procházet místní soubory)";
        string defaultCfg=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
          @"Steam\steamapps\common\Counter-Strike Global Offensive\game\csgo\cfg");
        if(Directory.Exists(defaultCfg))dialog.SelectedPath=defaultCfg;
        if(dialog.ShowDialog(this)!=DialogResult.OK)return;
        try {
          string file=Path.Combine(dialog.SelectedPath,"gamestate_integration_giovigg.cfg");
          if(File.Exists(file) && MessageBox.Show(this,"Konfigurace už existuje. Přepsat?","CS2 GSI",
            MessageBoxButtons.YesNo,MessageBoxIcon.Question)!=DialogResult.Yes)return;
          cs2Receiver.InstallConfig(dialog.SelectedPath);
          cs2Status="CS2 GSI: konfigurace nainstalována. Restartuj CS2 a potom spusť zápas.";
          MessageBox.Show(this,"Hotovo! Soubor gamestate_integration_giovigg.cfg je na místě.\n\nÚPLNĚ restartuj CS2. GIOVI.GG musí běžet na pozadí.",
            "CS2 GSI nastaveno",MessageBoxButtons.OK,MessageBoxIcon.Information);
          if(page=="Settings" || page=="CS2")ShowPage(page);
        }catch(Exception ex){MessageBox.Show(this,ex.Message,"CS2 GSI chyba",MessageBoxButtons.OK,MessageBoxIcon.Warning);}
      }
    }
    void Scan(){try {HashSet<string> running=new HashSet<string>(Process.GetProcesses().Select(p=>{try{return p.ProcessName;}catch{return "";}}),StringComparer.OrdinalIgnoreCase);bool changed=false;DateTime now=DateTime.Now;
       for(int i=0;i<games.Length;i++){string g=games[i];bool found=processes[i].Any(exe=>running.Contains(exe));if(found&&!active.ContainsKey(g)){active[g]=now;changed=true;}else if(!found&&active.ContainsKey(g)){Finish(g,now);changed=true;if(g=="LoL")TrySyncLoL(true);}}
       if(changed){UpdateLiveStatus();ShowPage(page);}
      }catch(Exception){status.Text="● DETECTION ERROR";}}
    void Finish(string game,DateTime end){DateTime start=active[game];active.Remove(game);long seconds=Math.Max(0,(long)(end-start).TotalSeconds);if(seconds>0){store.Sessions.Add(new Session{Game=game,Start=start.ToString("yyyy-MM-ddTHH:mm:ss"),Seconds=seconds});try{store.Save();}catch(Exception ex){MessageBox.Show("Chyba ukládání session: "+ex.Message);}}}
    bool AutoStartEnabled(){using(RegistryKey k=Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run",false)){return k!=null&&k.GetValue("GioviGG")!=null;}}
    void ToggleAutoStart(){try{using(RegistryKey k=Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run",true)){if(AutoStartEnabled()) k.DeleteValue("GioviGG",false);else k.SetValue("GioviGG","\""+Application.ExecutablePath+"\"");}}catch(Exception ex){MessageBox.Show(ex.Message,"Autostart error");}}
    void RestoreApp(){Show();WindowState=FormWindowState.Normal;Activate();}
    void ExitApp(){exiting=true;if(cs2Receiver!=null)cs2Receiver.Dispose();scanTimer.Stop();liveTimer.Stop();riotTimer.Stop();foreach(string g in active.Keys.ToList())Finish(g,DateTime.Now);tray.Visible=false;tray.Dispose();Close();Application.Exit();}
    [STAThread] static void Main(){Application.EnableVisualStyles();Application.SetCompatibleTextRenderingDefault(false);Application.Run(new App());}
  }
}
