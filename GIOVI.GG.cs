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

[assembly: AssemblyVersion("1.0.0.0")]
[assembly: AssemblyFileVersion("1.0.0.0")]

namespace GioviGG {
  class Match {
    public string Game, Date, Result;
    public int Kills, Deaths, Assists;
    public string MatchId="", Champion="", Queue="", Source="Manual", Role="";
    public int Duration;
    public int Headshots=-1, VisionScore=-1, Wards=-1, Damage=-1, Gold=-1, Cs=-1;
    public bool HeadshotsPartial=false;
  }
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
    int MaybeInt(string s) { int v; return int.TryParse(s,out v)?v:-1; }
    long ToLong(string s) { long x; return long.TryParse(s, out x) ? x : 0; }
    string A(XElement e, string key) { XAttribute a = e.Attribute(key); return a == null ? "" : a.Value; }
    void Load() {
      if (!System.IO.File.Exists(FileName)) return;
      try {
        XDocument d = XDocument.Load(FileName);
        foreach (XElement e in d.Descendants("match")) Matches.Add(new Match { Game=A(e,"game"),Date=A(e,"date"),Result=A(e,"result"),Kills=ToInt(A(e,"kills")),Deaths=ToInt(A(e,"deaths")),Assists=ToInt(A(e,"assists")),MatchId=A(e,"id"),Champion=A(e,"champion"),Queue=A(e,"queue"),Source=string.IsNullOrEmpty(A(e,"source"))?"Manual":A(e,"source"),Duration=ToInt(A(e,"duration")),Headshots=MaybeInt(A(e,"headshots")),
          HeadshotsPartial=A(e,"hsPartial")=="1", VisionScore=MaybeInt(A(e,"vision")),Wards=MaybeInt(A(e,"wards")),
          Damage=MaybeInt(A(e,"damage")),Gold=MaybeInt(A(e,"gold")),Cs=MaybeInt(A(e,"cs")),Role=A(e,"role") });
        foreach (XElement e in d.Descendants("session")) Sessions.Add(new Session { Game=A(e,"game"),Start=A(e,"start"),Seconds=ToLong(A(e,"seconds")) });
      } catch (Exception ex) { MessageBox.Show("Nepodařilo se načíst data: " + ex.Message, "GIOVI.GG"); }
    }
    public void Reload() { Matches.Clear(); Sessions.Clear(); Load(); }
    public void Save() {
      XDocument d = new XDocument(new XElement("giovi", new XElement("matches", Matches.Select(m => new XElement("match",new XAttribute("game",m.Game),new XAttribute("date",m.Date),new XAttribute("result",m.Result),new XAttribute("kills",m.Kills),new XAttribute("deaths",m.Deaths),new XAttribute("assists",m.Assists),new XAttribute("id",m.MatchId??""),new XAttribute("champion",m.Champion??""),new XAttribute("queue",m.Queue??""),new XAttribute("source",m.Source??"Manual"),new XAttribute("duration",m.Duration),
          new XAttribute("headshots",m.Headshots),new XAttribute("hsPartial",m.HeadshotsPartial?"1":"0"),
          new XAttribute("vision",m.VisionScore),new XAttribute("wards",m.Wards),
          new XAttribute("damage",m.Damage),new XAttribute("gold",m.Gold),new XAttribute("cs",m.Cs),
          new XAttribute("role",m.Role??"")))),new XElement("sessions",Sessions.Select(s => new XElement("session",new XAttribute("game",s.Game),new XAttribute("start",s.Start),new XAttribute("seconds",s.Seconds))))));
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
            wc.Headers[HttpRequestHeader.UserAgent]="GioviGG/1.0.0 (personal stats tracker)";
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
          Source="Riot API", Duration=(int)Math.Min(86400,Math.Max(0,duration)),
          VisionScore=(int)Num(Field(you,"visionScore")),Wards=(int)Num(Field(you,"wardsPlaced")),
          Damage=(int)Num(Field(you,"totalDamageDealtToChampions")),Gold=(int)Num(Field(you,"goldEarned")),
          Cs=(int)(Num(Field(you,"totalMinionsKilled"))+Num(Field(you,"neutralMinionsKilled"))),
          Role=Str(Field(you,"teamPosition")) };
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
    public int Headshots=-1;
    public bool HeadshotsPartial=false;
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
    int trackedRound=-1, maxHsThisRound=0, totalHeadshots=0;
    bool sawHeadshots=false, partialHeadshots=false;
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
          "    \"player_id\" \"1\"\r\n    \"player_state\" \"1\"\r\n    \"player_match_stats\" \"1\"\r\n  }\r\n}\r\n";
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
      int roundNo=-1;
      if(map!=null){
        if(Has(map,"name"))state.Map=Str(Val(map,"name"));
        if(Has(map,"mode"))state.Mode=Str(Val(map,"mode"));
        if(Has(map,"phase"))state.Phase=Str(Val(map,"phase"));
        if(Has(map,"round"))roundNo=Number(Val(map,"round"));
        var ct=Dict(Val(map,"team_ct"));var t=Dict(Val(map,"team_t"));
        if(Has(ct,"score"))state.Ct=Number(Val(ct,"score"));
        if(Has(t,"score"))state.T=Number(Val(t,"score"));
      }
      if(Has(provider,"steamid"))providerSteamId=Str(Val(provider,"steamid"));
      bool newMatch=state.Phase.Equals("live",StringComparison.OrdinalIgnoreCase) && state.Map.Length>0 &&
        (!sawLive || recorded || !state.Map.Equals(currentMap,StringComparison.OrdinalIgnoreCase));
      if(newMatch){
        currentMap=state.Map;matchStarted=DateTime.UtcNow;sawLive=true;recorded=false;
        state.HasStats=false;state.Kills=0;state.Deaths=0;state.Assists=0;
        trackedRound=-1;maxHsThisRound=0;totalHeadshots=0;sawHeadshots=false;partialHeadshots=false;
        state.Headshots=-1;state.HeadshotsPartial=false;
        if(roundNo>0)partialHeadshots=true; // GSI was attached after the match began.
      }
      if(player!=null){
        if(Has(player,"steamid"))playerSteamId=Str(Val(player,"steamid"));
        bool own=providerSteamId.Length>0 && playerSteamId==providerSteamId;
        if(own){
          if(Has(player,"team"))state.Team=Str(Val(player,"team"));
          var stats=Dict(Val(player,"match_stats"));
          if(stats!=null && Has(stats,"kills") && Has(stats,"deaths") && Has(stats,"assists")){
            state.Kills=Number(Val(stats,"kills"));state.Deaths=Number(Val(stats,"deaths"));
            state.Assists=Number(Val(stats,"assists"));state.HasStats=true;
          }
          var playerState=Dict(Val(player,"state"));
          if(sawLive && !recorded && playerState!=null && Has(playerState,"round_killhs") && roundNo>=0){
            int currentHs=Number(Val(playerState,"round_killhs"));
            if(trackedRound!=roundNo){
              if(trackedRound>=0 && roundNo>trackedRound+1)partialHeadshots=true;
              trackedRound=roundNo;maxHsThisRound=currentHs;totalHeadshots+=currentHs;
            }else if(currentHs>maxHsThisRound){
              totalHeadshots+=currentHs-maxHsThisRound;maxHsThisRound=currentHs;
            }else if(currentHs<maxHsThisRound){
              // State correction/reset: do not subtract previously recorded headshots.
              maxHsThisRound=currentHs;partialHeadshots=true;
            }
            sawHeadshots=true;
          }
        }
      }
      if(sawHeadshots){
        state.Headshots=totalHeadshots;
        state.HeadshotsPartial=partialHeadshots || (state.HasStats && totalHeadshots>state.Kills);
      }
      state.SeenUtc=DateTime.UtcNow;
      Match completed=null;
      if(sawLive && !recorded && state.Phase.Equals("gameover",StringComparison.OrdinalIgnoreCase)){
        recorded=true;
        bool ct=state.Team.Equals("CT",StringComparison.OrdinalIgnoreCase);
        bool t=state.Team.Equals("T",StringComparison.OrdinalIgnoreCase);
        bool teamGame=!state.Mode.Equals("deathmatch",StringComparison.OrdinalIgnoreCase);
        if(state.HasStats && (ct||t) && state.Map.Length>0 && teamGame && (state.Ct>0 || state.T>0)){
          int ours=ct?state.Ct:state.T, theirs=ct?state.T:state.Ct;
          string result=ours==theirs?"Draw":ours>theirs?"Win":"Loss";
          completed=new Match {Game="CS2",Date=DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss"),Result=result,
            Kills=state.Kills,Deaths=state.Deaths,Assists=state.Assists,Queue=state.Map+" · "+state.Mode,
            Source="CS2 GSI",MatchId="CS2-GSI-"+Guid.NewGuid().ToString("N"),
            Headshots=sawHeadshots?Math.Min(totalHeadshots,state.Kills):-1,
            HeadshotsPartial=state.HeadshotsPartial,
            Duration=matchStarted==DateTime.MinValue?0:(int)Math.Max(0,Math.Min(21600,(DateTime.UtcNow-matchStarted).TotalSeconds))};
        }
      }
      if(onUpdate!=null){
        Cs2State report=new Cs2State {SeenUtc=state.SeenUtc,Map=state.Map,Mode=state.Mode,Phase=state.Phase,
          Team=state.Team,Kills=state.Kills,Deaths=state.Deaths,Assists=state.Assists,Ct=state.Ct,T=state.T,
          Headshots=state.Headshots,HeadshotsPartial=state.HeadshotsPartial,HasStats=state.HasStats};
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
    const string CurrentVersion = "1.0.0";
    const string ReleaseApi = "https://api.github.com/repos/KingGiovi/GIOVI-GG/releases/latest";
    UpdateInfo availableUpdate;
    bool updateBusy = false;
    string updateMessage = "Zatím nezkontrolováno.";
    Color bg = Color.FromArgb(11,13,24), side = Color.FromArgb(17,19,32), card = Color.FromArgb(28,31,50), fg=Color.FromArgb(243,240,255), muted=Color.FromArgb(157,158,181), accent=Color.FromArgb(166,123,255), green=Color.FromArgb(104,220,167);
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
    // Small CS2 preview and the full LIVE MATCH view share one low-cost UI refresh.
    readonly Dictionary<string,Label> cs2Ui=new Dictionary<string,Label>();
    Panel cs2CtBar, cs2TBar;
    readonly Color ctBlue=Color.FromArgb(104,188,255);
    readonly Color tGold=Color.FromArgb(255,194,111);
    bool riotSyncBusy=false;
    string riotStatus="Nepřipojeno. Přidej svůj Riot API klíč.";
    DateTime lastRiotAttempt=DateTime.MinValue;
    Timer riotTimer;
    Panel sidebar, mainArea, content, header, navHost;
    Dictionary<string,Button> navButtons = new Dictionary<string,Button>();
    string gameTab="Overview", settingsTab="Updates", historyFilter="All";
    Label status, heading;
    NotifyIcon tray;
    Timer scanTimer, liveTimer;
    string page = "Overview";
    string analyticsGame = "LoL";
    string analyticsPeriod = "30D";
    string historyResult = "All", historyPeriod = "All", historySearch = "";
    string recordsGame="CS2";
    Panel historyListPanel;
    Label historyCount;
    DateTime calendarMonth=new DateTime(DateTime.Now.Year,DateTime.Now.Month,1);
    DateTime selectedDay=DateTime.Today;
    bool exiting = false;
    public App() {
      riot=new RiotConfig(store.Folder);
      Text = "GIOVI.GG — ULTIMATE EDITION v1.0.0"; Width = 1240; Height = 820; MinimumSize = new Size(1020,640); StartPosition = FormStartPosition.CenterScreen; BackColor = bg; ForeColor = fg; Font = new Font("Segoe UI", 10f);
      // Fixed two-column layout: sidebar occupies its own column, never overlays content.
      TableLayoutPanel shell = new TableLayoutPanel { Dock=DockStyle.Fill, ColumnCount=2, RowCount=1, Margin=new Padding(0), Padding=new Padding(0), BackColor=bg };
      shell.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute,210));
      shell.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));
      shell.RowStyles.Add(new RowStyle(SizeType.Percent,100));
      Controls.Add(shell);
      sidebar = new Panel { Dock=DockStyle.Fill, BackColor=side, Margin=new Padding(0) }; shell.Controls.Add(sidebar,0,0);
      TableLayoutPanel sideLayout=new TableLayoutPanel {Dock=DockStyle.Fill,ColumnCount=1,RowCount=3,Margin=new Padding(0),Padding=new Padding(0)};
      sideLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));
      sideLayout.RowStyles.Add(new RowStyle(SizeType.Absolute,106));
      sideLayout.RowStyles.Add(new RowStyle(SizeType.Percent,100));
      sideLayout.RowStyles.Add(new RowStyle(SizeType.Absolute,39));
      sidebar.Controls.Add(sideLayout);
      Panel branding=new Panel {Dock=DockStyle.Fill,BackColor=side};sideLayout.Controls.Add(branding,0,0);
      Label brand=Lbl("◆ GIOVI.GG",22,accent,true);brand.Location=new Point(17,22);brand.Size=new Size(190,42);branding.Controls.Add(brand);
      Label sub=Lbl("ULTIMATE EDITION  1.0",8,muted,true);sub.Location=new Point(19,67);sub.Size=new Size(185,24);branding.Controls.Add(sub);
      navHost=new Panel{Dock=DockStyle.Fill,AutoScroll=true,BackColor=side};sideLayout.Controls.Add(navHost,0,1);
      AddNav("⌂   Dashboard","Overview",6);
      AddNav("▣   Counter-Strike 2","CS2",56);
      AddNav("◇   League of Legends","LoL",106);
      AddNav("⬡   Rainbow Six Siege","R6",156);
      AddNav("▤   Historie zápasů","History",206);
      AddNav("◈   Analýza výkonu","Analytics",256);
      AddNav("★   Achievementy","Achievements",306);
      AddNav("▦   Herní kalendář","Calendar",356);
      AddNav("◉   Herní session","Sessions",406);
      AddNav("✦   Smart Insights","Insights",456);
      AddNav("♛   Osobní rekordy","Records",506);
      AddNav("⚙   Nastavení","Settings",556);
      navHost.AutoScrollMinSize=new Size(0,616);
      Panel privacy=new Panel{Dock=DockStyle.Fill,BackColor=side};sideLayout.Controls.Add(privacy,0,2);
      Label local=Lbl("● DATA POUZE V PC",8,green,true);local.Dock=DockStyle.Fill;
      local.TextAlign=ContentAlignment.MiddleCenter;privacy.Controls.Add(local);
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
      liveTimer=new Timer { Interval=1000 };liveTimer.Tick+=(s,e)=>{ if (page=="Overview" || Array.IndexOf(games,page)>=0) { UpdateLiveStatus(); } if(page=="CS2")UpdateCs2Widgets(); };liveTimer.Start();
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
    void AddNav(string label,string key,int y){Button b=Btn(label,side);b.FlatAppearance.BorderSize=0;b.Font=new Font("Segoe UI",9.6f,FontStyle.Bold);b.TextAlign=ContentAlignment.MiddleLeft;b.Location=new Point(9,y);b.Size=new Size(192,44);b.Click+=(s,e)=>ShowPage(key);b.MouseEnter+=(s,e)=>{if(page!=key)b.BackColor=shade;};b.MouseLeave+=(s,e)=>{if(page!=key)b.BackColor=side;};navHost.Controls.Add(b);navButtons[key]=b;}
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
      content.AutoScrollPosition=new Point(0,0);
      content.AutoScrollMinSize=Size.Empty;
      cs2Ui.Clear();
      cs2CtBar=null;cs2TBar=null;
      heading.Text=Array.IndexOf(games,p)>=0?names[Array.IndexOf(games,p)]:p=="History"?"Historie zápasů":p=="Settings"?"Nastavení":p=="Analytics"?"Analýza výkonu":p=="Achievements"?"Achievementy":p=="Calendar"?"Herní kalendář":p=="Sessions"?"Herní session":p=="Insights"?"Smart Insights":p=="Records"?"Osobní rekordy":"Přehled";
      heading.Font=new Font("Segoe UI",22f,FontStyle.Bold);
      SetNavActive();UpdateLiveStatus();
      if(p=="Overview")BuildOverview();
      else if(Array.IndexOf(games,p)>=0)BuildGamePage(p);
      else if(p=="History")BuildHistory();
      else if(p=="Analytics")BuildAnalytics();
      else if(p=="Achievements")BuildAchievements();
      else if(p=="Calendar")BuildCalendar();
      else if(p=="Sessions")BuildSessions();
      else if(p=="Insights")BuildInsights();
      else if(p=="Records")BuildRecords();
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
    static string Optional(int n){return n<0?"—":n.ToString("N0");}
    static string OptionalCsv(int n){return n<0?"":n.ToString(System.Globalization.CultureInfo.InvariantCulture);}
    static string FormatHs(Match match){
      return match.Headshots<0 || match.Kills<=0?"—":
        (Math.Min(match.Kills,match.Headshots)*100.0/match.Kills).ToString("0.0")+"%";
    }
    List<Match> KnownHs(List<Match> matches){
      return matches.Where(m=>m.Game=="CS2" && m.Headshots>=0 && !m.HeadshotsPartial && m.Kills>0).ToList();
    }
    string AllHs(List<Match> matches){
      List<Match> known=KnownHs(matches);long kills=known.Sum(m=>(long)m.Kills);
      if(kills==0)return "—";
      return (100.0*known.Sum(m=>(long)m.Headshots)/kills).ToString("0.0")+"%";
    }
    int TotalHeadshots(List<Match> matches){return matches.Where(m=>m.Game=="CS2"&&m.Headshots>=0&&!m.HeadshotsPartial).Sum(m=>Math.Max(0,m.Headshots));}

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
      Metric(content,analyticsGame=="CS2"?"HS% (ověřené)":"Průměr asistencí",
        analyticsGame=="CS2"?AllHs(results):Avg(results,m=>m.Assists),x+2*(tileW+gap),214,tileW,72,
        analyticsGame=="CS2"?tGold:fg);
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
        Copy(highlights,analyticsGame=="CS2"?
          "HS% je počítané pouze z kompletně sledovaných GSI zápasů. Ostatní zůstávají neznámé.":
          "U R6 sledujeme čas; výsledky lze zatím zadávat ručně.",
          14,213,rightWidth-28,62,9,muted,false).AutoEllipsis=false;
      }
      // Results chart is based strictly on recorded matches; no fabricated live stats.
    }

    List<Achievement> GetAchievements(){
      List<Match> all=Of(null);
      long wins=all.Count(m=>m.Result=="Win"),kills=all.Sum(m=>(long)m.Kills);
      int best=games.Max(g=>BestWinningStreak(Of(g)));
      long played=all.Count,gameCount=games.Count(g=>Of(g).Count>0);
      List<Achievement> badges=new List<Achievement>{
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
        new Achievement("Night owl","Nasbírej 10 hodin sledovaného hraní.",Seconds(null)/3600,10),
        new Achievement("Trpělivost","Zaznamenej 250 zápasů.",played,250),
        new Achievement("Iron Will","Zaznamenej 500 zápasů.",played,500),
        new Achievement("Nesmrtelný","Zaznamenej 1 000 zápasů.",played,1000),
        new Achievement("Lovec II","Nasbírej 5 000 killů.",kills,5000),
        new Achievement("Lovec III","Nasbírej 10 000 killů.",kills,10000),
        new Achievement("Šampión","Vyhraj 250 zápasů.",wins,250),
        new Achievement("Legenda","Vyhraj 500 zápasů.",wins,500),
        new Achievement("Bez porážky","Vyhraj 5 zápasů v řadě v jedné hře.",best,5),
        new Achievement("Perfektní série","Vyhraj 10 zápasů v řadě v jedné hře.",best,10),
        new Achievement("LoL tovaryš","Odehraj 50 LoL zápasů.",Of("LoL").Count,50),
        new Achievement("LoL veterán II","Odehraj 100 LoL zápasů.",Of("LoL").Count,100),
        new Achievement("LoL legenda","Odehraj 500 LoL zápasů.",Of("LoL").Count,500),
        new Achievement("Support master","Získej 100 asistencí v LoL.",Of("LoL").Sum(m=>(long)m.Assists),100),
        new Achievement("Assist king","Získej 1 000 asistencí v LoL.",Of("LoL").Sum(m=>(long)m.Assists),1000),
        new Achievement("Vision game","Zaznamenej LoL zápas s vision score alespoň 50.",Of("LoL").Count(m=>m.VisionScore>=50),1),
        new Achievement("Thresh main","Odehraj 20 zápasů za Threshe.",Of("LoL").Count(m=>string.Equals(m.Champion,"Thresh",StringComparison.OrdinalIgnoreCase)),20),
        new Achievement("Hook legend","Odehraj 100 zápasů za Threshe.",Of("LoL").Count(m=>string.Equals(m.Champion,"Thresh",StringComparison.OrdinalIgnoreCase)),100),
        new Achievement("CS2 začátečník","Zaznamenej 10 CS2 zápasů.",Of("CS2").Count,10),
        new Achievement("CS2 veterán","Zaznamenej 100 CS2 zápasů.",Of("CS2").Count,100),
        new Achievement("CS2 profesionál","Zaznamenej 500 CS2 zápasů.",Of("CS2").Count,500),
        new Achievement("První headshot","Zaznamenej první CS2 headshot přes GSI.",TotalHeadshots(Of("CS2")),1),
        new Achievement("Sharp shooter","Zaznamenej 100 CS2 headshotů přes GSI.",TotalHeadshots(Of("CS2")),100),
        new Achievement("One tap","Dosáhni HS% alespoň 60 % s 10+ killy (kompletní záznam).",
          KnownHs(Of("CS2")).Count(m=>m.Kills>=10 && 100.0*m.Headshots/m.Kills>=60),1),
        new Achievement("Sniper","Zaznamenej 500 CS2 headshotů přes GSI.",TotalHeadshots(Of("CS2")),500),
        new Achievement("R6 začátečník","Zaznamenej 10 R6 zápasů.",Of("R6").Count,10),
        new Achievement("R6 veterán","Zaznamenej 100 R6 zápasů.",Of("R6").Count,100),
        new Achievement("Noc je mladá","Sleduj 50 hodin herního času.",Seconds(null)/3600,50),
        new Achievement("Gamer života","Sleduj 100 hodin herního času.",Seconds(null)/3600,100),
        new Achievement("Maratonec","Sleduj 500 hodin herního času.",Seconds(null)/3600,500),
        new Achievement("Calendar king","Hraj alespoň v 7 různých dnech.",store.Sessions.Select(z=>ParseDate(z.Start)).Where(z=>z.HasValue).Select(z=>z.Value.Date).Distinct().Count(),7)
      };
      int[] playedGoals={5,25,75,200};int[] winGoals={3,15,50,150};int[] killGoals={50,500,2500};
      foreach(string game in games){
        List<Match> subset=Of(game);
        long gameWins=subset.Count(m=>m.Result=="Win"),gameKills=subset.Sum(m=>(long)m.Kills);
        foreach(int goal in playedGoals)badges.Add(new Achievement(game+" • "+goal+" her","Odehraj "+goal+" zápasů v "+game+".",subset.Count,goal));
        foreach(int goal in winGoals)badges.Add(new Achievement(game+" • "+goal+" výher","Vyhraj "+goal+" zápasů v "+game+".",gameWins,goal));
        foreach(int goal in killGoals)badges.Add(new Achievement(game+" • "+goal+" killů","Zaznamenej "+goal+" killů v "+game+".",gameKills,goal));
      }
      return badges;
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
      Copy(content,"MOJE HRY",x,239,w-320,24,11,fg,true);
      ButtonAt(content,"KALENDÁŘ",x+w-315,231,100,34,()=>ShowPage("Calendar"),false);
      ButtonAt(content,"SESSION",x+w-207,231,95,34,()=>ShowPage("Sessions"),false);
      ButtonAt(content,"INSIGHTS",x+w-104,231,104,34,()=>ShowPage("Insights"),false);
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
      string[] labels=game=="LoL"?new string[]{"PŘEHLED","ZÁPASY","ŠAMPIONI","DEEP STATS"}:
        game=="CS2"?new string[]{"PŘEHLED","● LIVE MATCH","ZÁPASY"}:new string[]{"PŘEHLED","ZÁPASY"};
      string[] keys=game=="LoL"?new string[]{"Overview","Matches","Champions","Deep"}:
        game=="CS2"?new string[]{"Overview","Live","Matches"}:new string[]{"Overview","Matches"};
      Tabs(labels,keys,gameTab,12,selected=>{gameTab=selected;ShowPage(game);});
      if(game=="CS2" && gameTab=="Live") {BuildCs2Live();return;}
      if(gameTab=="Matches") {BuildGameMatches(game);return;}
      if(gameTab=="Champions"&&game=="LoL") {BuildChampions();return;}
      if(gameTab=="Deep"&&game=="LoL") {BuildLoLDeepStats();return;}
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
      int columns=game=="CS2"?6:5,small=(w-(columns-1)*gap)/columns;
      string[] labels=game=="CS2"?new string[]{"Výhry","Prohry","Killy","Smrti","Asistence","HS%"}:
        new string[]{"Výhry","Prohry","Killy","Smrti","Asistence"};
      string[] values=game=="CS2"?new string[]{win.ToString(),loss.ToString(),kills.ToString(),deaths.ToString(),assists.ToString(),AllHs(mm)}:
        new string[]{win.ToString(),loss.ToString(),kills.ToString(),deaths.ToString(),assists.ToString()};
      for(int i=0;i<columns;i++)Metric(content,labels[i],values[i],x+i*(small+gap),198,small,65,i==0?green:i==1?danger:i==5?tGold:fg);
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
        BuildCs2Mini(connection,rightWidth,boxH);
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
    void BuildLoLDeepStats(){
      int x=24,w=ViewWidth,h=ViewHeight,gap=12;
      if(h<670){content.AutoScroll=true;content.AutoScrollMinSize=new Size(0,700);}
      List<Match> mm=Of("LoL");
      var vision=mm.Where(m=>m.VisionScore>=0).ToList();
      var gold=mm.Where(m=>m.Gold>=0).ToList();
      var damage=mm.Where(m=>m.Damage>=0).ToList();
      var cs=mm.Where(m=>m.Cs>=0).ToList();
      Copy(content,"RIOT API  •  DEEP STATS",x,65,w-20,29,13,accent,true);
      Copy(content,"Pokročilé hodnoty se ukládají z nově importovaných zápasů; starší záznamy mohou mít —.",x,96,w-20,24,9,muted,false);
      int small=(w-3*gap)/4;
      Metric(content,"Průměr Vision",vision.Count==0?"—":vision.Average(m=>m.VisionScore).ToString("0.0"),x,132,small,93,ctBlue);
      Metric(content,"Průměr DMG",damage.Count==0?"—":damage.Average(m=>m.Damage).ToString("N0"),x+small+gap,132,small,93,danger);
      Metric(content,"Průměr CS",cs.Count==0?"—":cs.Average(m=>m.Cs).ToString("0"),x+2*(small+gap),132,small,93,green);
      Metric(content,"Průměr Gold",gold.Count==0?"—":gold.Average(m=>m.Gold).ToString("N0"),x+3*(small+gap),132,small,93,tGold);
      int left=(int)(w*.53),right=w-left-gap;
      Panel champ=Surface(content,x,241,left,Math.Max(269,h-258),card);
      Copy(champ,"THRESH PERFORMANCE",15,14,left-30,27,14,accent,true);
      List<Match> th=mm.Where(m=>string.Equals(m.Champion,"Thresh",StringComparison.OrdinalIgnoreCase)).ToList();
      if(th.Count==0){Copy(champ,"Zatím žádné zápasy za Threshe. Po importu se zde objeví statistiky.",15,58,left-30,57,10,muted,false).AutoEllipsis=false;}
      else{
        int tw=th.Count(m=>m.Result=="Win");
        Copy(champ,th.Count+" zápasů  ·  "+(100.0*tw/th.Count).ToString("0.0")+" % WR",15,60,left-30,37,18,green,true);
        Copy(champ,"KDA: "+Ratio(th,"LoL")+"   •   AVG A: "+Avg(th,m=>m.Assists),15,110,left-30,31,12,fg,true);
        var thv=th.Where(m=>m.VisionScore>=0).ToList();
        Copy(champ,"VISION SCORE Ø: "+(thv.Count==0?"—":thv.Average(m=>m.VisionScore).ToString("0.0")),15,157,left-30,30,12,ctBlue,true);
        Copy(champ,"Nejvíce asistencí: "+th.Max(m=>m.Assists),15,205,left-30,27,11,muted,false);
      }
      Panel roles=Surface(content,x+left+gap,241,right,Math.Max(269,h-258),card);
      roles.AutoScroll=true;
      Copy(roles,"ROLE PERFORMANCE",15,14,right-30,29,13,fg,true);
      var byRole=mm.Where(m=>!string.IsNullOrEmpty(m.Role)).GroupBy(m=>m.Role)
        .OrderByDescending(g=>g.Count()).ToList();
      int yy=62;
      foreach(var r in byRole.Take(6)){
        int rw=r.Count(m=>m.Result=="Win");
        Copy(roles,r.Key,15,yy,right-150,26,10,fg,true);
        Copy(roles,r.Count()+" her · "+(rw*100.0/r.Count()).ToString("0")+"% WR",right-161,yy,147,26,9,green,true).TextAlign=ContentAlignment.MiddleRight;
        yy+=43;
      }
      if(byRole.Count==0)Copy(roles,"Nové zápasy teprve získají data o rolích.",15,61,right-30,58,10,muted,false).AutoEllipsis=false;
      roles.AutoScrollMinSize=new Size(0,Math.Max(0,yy+12));
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
        Copy(row,count+" ZÁPASŮ",(int)(row.Width*0.41),8,115,24,9,muted,true);
        var vision=champ.Where(m=>m.VisionScore>=0).ToList();
        Copy(row,"AVG VISION " +(vision.Count>0?vision.Average(m=>m.VisionScore).ToString("0"):"—"),
          (int)(row.Width*0.41),29,142,19,8,muted,false);
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
    // v1.0: date-driven insights built from local recorded matches/sessions only.
    static string DayLabel(DateTime d){return d.ToString("dd.MM.yyyy");}
    List<Match> MatchesOn(DateTime date){
      return store.Matches.Where(m=>MatchDate(m).HasValue&&MatchDate(m).Value.Date==date.Date).OrderByDescending(m=>m.Date).ToList();
    }
    List<Session> SessionsOn(DateTime date){
      return store.Sessions.Where(x=>ParseDate(x.Start).HasValue&&ParseDate(x.Start).Value.Date==date.Date).ToList();
    }
    DateTime? ParseDate(string val){
      DateTime d;return DateTime.TryParse(val,System.Globalization.CultureInfo.InvariantCulture,
        System.Globalization.DateTimeStyles.None,out d)?(DateTime?)d:null;
    }
    void BuildCalendar(){
      int x=24,w=ViewWidth,h=ViewHeight;
      if(h<658){content.AutoScroll=true;content.AutoScrollMinSize=new Size(0,730);}
      string monthName=calendarMonth.ToString("MMMM yyyy",new System.Globalization.CultureInfo("cs-CZ")).ToUpperInvariant();
      Panel head=Surface(content,x,7,w,69,shade);
      Copy(head,"TVŮJ HERNÍ KALENDÁŘ",17,8,w-34,20,9,accent,true);
      Copy(head,monthName,17,29,w-230,31,20,fg,true);
      ButtonAt(head,"◀",w-167,15,43,37,()=>{calendarMonth=calendarMonth.AddMonths(-1);ShowPage("Calendar");},false);
      ButtonAt(head,"DNES",w-118,15,67,37,()=>{calendarMonth=new DateTime(DateTime.Now.Year,DateTime.Now.Month,1);selectedDay=DateTime.Today;ShowPage("Calendar");},false);
      ButtonAt(head,"▶",w-45,15,38,37,()=>{calendarMonth=calendarMonth.AddMonths(1);ShowPage("Calendar");},false);
      int gridW=Math.Max(425,(int)(w*.61)),rightX=x+gridW+12,rightW=w-gridW-12;
      Panel grid=Surface(content,x,89,gridW,Math.Max(477,h-99),card);
      string[] days={"PO","ÚT","ST","ČT","PÁ","SO","NE"};
      int gap=4,cellW=(gridW-25-6*gap)/7,cellH=56;
      for(int d=0;d<7;d++)Copy(grid,days[d],12+d*(cellW+gap),10,cellW,22,9,muted,true).TextAlign=ContentAlignment.MiddleCenter;
      int offset=((int)calendarMonth.DayOfWeek+6)%7;
      int limit=DateTime.DaysInMonth(calendarMonth.Year,calendarMonth.Month);
      for(int i=0;i<42;i++){
        int day=i-offset+1;if(day<1||day>limit)continue;
        DateTime date=calendarMonth.AddDays(day-1);
        int row=i/7,col=i%7;
        int bx=12+col*(cellW+gap),by=37+row*(cellH+gap);
        var found=MatchesOn(date);int wins=found.Count(m=>m.Result=="Win");
        bool chosen=selectedDay.Date==date.Date;
        Panel cell=Surface(grid,bx,by,cellW,cellH,chosen?Color.FromArgb(68,53,97):shade);
        if(date.Date==DateTime.Today.Date)Surface(cell,0,0,cellW,3,green);
        Copy(cell,day.ToString(),6,4,cellW-12,24,11,chosen?accent:fg,true);
        if(found.Count>0){
          Copy(cell,found.Count+" HER · "+wins+" W",5,30,cellW-10,20,Math.Max(7f,cellW<62?7f:8f),green,false);
        }else if(SessionsOn(date).Count>0)Copy(cell,"● HRÁNO",5,33,cellW-10,17,7,muted,true);
        cell.Cursor=Cursors.Hand;
        cell.Click+=(sender,args)=>{selectedDay=date;ShowPage("Calendar");};
        foreach(Control inner in cell.Controls){
          inner.Cursor=Cursors.Hand;
          inner.Click+=(sender,args)=>{selectedDay=date;ShowPage("Calendar");};
        }
      }
      Panel details=Surface(content,rightX,89,rightW,Math.Max(477,h-99),card);
      List<Match> mm=MatchesOn(selectedDay);List<Session> sessions=SessionsOn(selectedDay);
      Copy(details,DayLabel(selectedDay),17,15,rightW-30,37,18,fg,true);
      Rule(details,15,59,rightW-30,line);
      int winsDay=mm.Count(m=>m.Result=="Win"), losses=mm.Count(m=>m.Result=="Loss");
      Copy(details,"ZÁPASY",17,74,rightW-28,19,9,muted,true);
      Copy(details,mm.Count+"  ·  "+winsDay+" výher / "+losses+" proher",17,98,rightW-27,31,14,green,true);
      long played=sessions.Sum(a=>a.Seconds);
      Copy(details,"HERNÍ ČAS",17,137,rightW-28,18,9,muted,true);
      Copy(details,(played/3600)+" h "+((played%3600)/60)+" min",17,160,rightW-25,29,14,fg,true);
      Rule(details,15,203,rightW-30,line);
      Copy(details,"CO JSI HRÁL",17,211,rightW-25,22,10,accent,true);
      int py=243;
      foreach(string game in games){
        List<Match> gm=mm.Where(m=>m.Game==game).ToList();
        long sec=sessions.Where(a=>a.Game==game).Sum(a=>a.Seconds);
        if(gm.Count==0 && sec==0)continue;
        Copy(details,game+" • "+gm.Count+" zápasů",16,py,rightW-30,25,11,fg,true);
        Copy(details,(sec/60)+" minut  |  "+gm.Count(m=>m.Result=="Win")+" výher",16,py+23,rightW-30,22,9,muted,false);
        py+=60;
      }
      if(py==243)Copy(details,"V tento den zatím nejsou zaznamenaná data.",16,247,rightW-30,60,10,muted,false).AutoEllipsis=false;
      ButtonAt(details,"OTEVŘÍT HISTORII  →",14,details.Height-47,rightW-28,34,()=>ShowPage("History"),true);
    }
    void BuildSessions(){
      int x=24,w=ViewWidth,h=ViewHeight;
      Panel hero=Surface(content,x,9,w,101,shade);
      Copy(hero,"GAMING SESSION RECAP",17,11,w-36,22,10,accent,true);
      Copy(hero,store.Sessions.Count+" ukončených herních session",17,38,w-32,43,20,fg,true);
      Panel list=Surface(content,x,127,w,Math.Max(200,h-136),card);list.AutoScroll=true;
      var recent=store.Sessions.OrderByDescending(a=>a.Start).Take(160).ToList();
      if(recent.Count==0){Copy(list,"Spusť CS2, LoL nebo R6 s běžící aplikací. Po ukončení hry tu najdeš recap.",18,24,w-40,70,11,muted,false).AutoEllipsis=false;return;}
      int yy=10;
      foreach(Session session in recent){
        DateTime? date=ParseDate(session.Start);
        DateTime start=date??DateTime.Today;DateTime end=start.AddSeconds(session.Seconds);
        var matches=store.Matches.Where(m=>m.Game==session.Game&&MatchDate(m).HasValue&&
          MatchDate(m).Value>=start&&MatchDate(m).Value<=end.AddSeconds(12)).ToList();
        int wins=matches.Count(m=>m.Result=="Win");
        Panel row=Surface(list,12,yy,Math.Max(330,w-48),86,shade);
        Color mark=session.Game=="CS2"?tGold:session.Game=="LoL"?ctBlue:accent;
        Surface(row,0,0,4,86,mark);
        Copy(row,session.Game+"   /   "+start.ToString("dd.MM.yyyy  HH:mm"),16,7,row.Width-210,30,12,mark,true);
        Copy(row,matches.Count+" zápasů  ·  "+wins+" výher  ·  "+matches.Sum(m=>m.Kills)+" killů",16,45,row.Width-28,27,10,fg,false);
        Copy(row,(session.Seconds/3600)+"h "+((session.Seconds%3600)/60)+"m",row.Width-150,11,134,33,15,fg,true).TextAlign=ContentAlignment.MiddleRight;
        yy+=96;
      }
      list.AutoScrollMinSize=new Size(0,yy+10);
    }
    Panel InsightCard(Control parent,int x,int y,int w,int height,string title,string body,Color hue){
      Panel cardPanel=Surface(parent,x,y,w,height,card);
      Surface(cardPanel,0,0,4,height,hue);
      Copy(cardPanel,title,17,10,w-32,28,11,hue,true);
      Label info=Copy(cardPanel,body,17,42,w-34,height-51,10,fg,false);info.AutoEllipsis=false;
      return cardPanel;
    }
    string TrendText(List<Match> mm){
      var sorted=mm.OrderByDescending(m=>m.Date).ToList();
      if(sorted.Count<10)return "Pro smysluplné srovnání potřebujeme alespoň 10 zápasů.";
      List<Match> last=sorted.Take(5).ToList(),previous=sorted.Skip(5).Take(5).ToList();
      int a=last.Count(m=>m.Result=="Win"),b=previous.Count(m=>m.Result=="Win");
      return "Posledních 5 her: "+a+" výher. Předchozích 5: "+b+" výher. "+
        (a>b?"Zlepšení o "+((a-b)*20)+" procentních bodů.":a<b?"Pokles o "+((b-a)*20)+" procentních bodů.":"Stejná úspěšnost v obou obdobích.");
    }
    void BuildInsights(){
      int x=24,w=ViewWidth,h=ViewHeight,gap=12;
      if(h<665){content.AutoScroll=true;content.AutoScrollMinSize=new Size(0,700);}
      Panel hero=Surface(content,x,9,w,82,shade);
      Copy(hero,"SMART INSIGHTS",17,8,w-30,28,12,accent,true);
      Copy(hero,"Data z tvých zápasů. Žádné vymyšlené statistiky ani placená AI.",17,38,w-36,24,10,fg,false);
      int half=(w-gap)/2;
      List<Match> lol=Of("LoL"),cs=Of("CS2"),r6=Of("R6");
      InsightCard(content,x,105,half,135,"⚔  LEAGUE OF LEGENDS",TrendText(lol),ctBlue);
      InsightCard(content,x+half+gap,105,half,135,"🔫  COUNTER-STRIKE 2",TrendText(cs),tGold);
      var champs=lol.Where(m=>!string.IsNullOrEmpty(m.Champion)).GroupBy(m=>m.Champion).Where(g=>g.Count()>=3)
        .OrderByDescending(g=>(double)g.Count(m=>m.Result=="Win")/g.Count()).ThenByDescending(g=>g.Count()).ToList();
      string championInsight=champs.Count==0?"Na vývoj win rate u šampionů potřebujeme alespoň 3 zápasy na každého.":
        "Nejúspěšnější šampion (min. 3 hry): "+champs[0].Key+" • "+
        (100.0*champs[0].Count(m=>m.Result=="Win")/champs[0].Count()).ToString("0.0")+"% WR z "+champs[0].Count()+" her.";
      InsightCard(content,x,253,half,135,"🏆  TVŮJ ŠAMPION",championInsight,green);
      var known=KnownHs(cs);string hsInsight=known.Count==0?"HS% začne fungovat po nové instalaci GSI konfigurace a zaznamenání celého zápasu.":
        "HS% napříč "+known.Count+" plně sledovanými zápasy: "+AllHs(cs)+
        ". Neúplné a starší zápasy nejsou započítané.";
      InsightCard(content,x+half+gap,253,half,135,"🎯  CS2 HEADSHOT INSIGHTS",hsInsight,tGold);
      string r6msg=r6.Count==0?"Rainbow Six Siege zatím sleduje herní čas. Zápasy lze přidávat ručně.":
        "Z "+r6.Count+" zaznamenaných zápasů máš "+r6.Count(m=>m.Result=="Win")+" výher a K/D "+Ratio(r6,"R6")+".";
      InsightCard(content,x,401,half,125,"⬡  RAINBOW SIX SIEGE",r6msg,accent);
      long secs=Seconds(null);int days=store.Sessions.Select(z=>ParseDate(z.Start)).Where(z=>z.HasValue).Select(z=>z.Value.Date).Distinct().Count();
      InsightCard(content,x+half+gap,401,half,125,"◷  HERNÍ AKTIVITA",
        "Sledováno "+(secs/3600)+"h "+((secs%3600)/60)+"min v "+days+" různých dnech. Celkem "+store.Matches.Count+" zápasů.",green);
      ButtonAt(content,"ZOBRAZIT REKORDY  →",x,540,half,44,()=>ShowPage("Records"),true);
      ButtonAt(content,"OTEVŘÍT KALENDÁŘ  →",x+half+gap,540,half,44,()=>ShowPage("Calendar"),false);
    }
    void BuildRecords(){
      int x=24,w=ViewWidth,h=ViewHeight,gap=12;
      if(h<670){content.AutoScroll=true;content.AutoScrollMinSize=new Size(0,720);}
      Tabs(new string[]{"CS2","LOL","R6 SIEGE"},new string[]{"CS2","LoL","R6"},recordsGame,12,
        chosen=>{recordsGame=chosen;ShowPage("Records");});
      var mm=Of(recordsGame);
      int gapW=(w-3*gap)/4;
      Metric(content,"Zápasy",mm.Count.ToString(),x,73,gapW,95,accent);
      Metric(content,"Nejvíce killů",mm.Count==0?"—":mm.Max(m=>m.Kills).ToString(),x+gapW+gap,73,gapW,95,green);
      Metric(content,"Nejvíce asistencí",mm.Count==0?"—":mm.Max(m=>m.Assists).ToString(),x+2*(gapW+gap),73,gapW,95,fg);
      Metric(content,"Nejdelší win streak",BestWinningStreak(mm).ToString(),x+3*(gapW+gap),73,gapW,95,tGold);
      Panel box=Surface(content,x,184,w,Math.Max(425,h-196),card);
      Copy(box,recordsGame=="CS2"?"MAP PERFORMANCE + HS%":recordsGame=="LoL"?"NEJHRANĚJŠÍ ŠAMPIONI":"VÝSLEDKY PODLE REŽIMU",16,15,w-32,33,15,fg,true);
      Panel scrolling=Surface(box,12,58,w-24,Math.Max(180,box.Height-133),card);
      scrolling.AutoScroll=true;
      if(recordsGame=="CS2"){
        var maps=mm.GroupBy(m=>(m.Queue??"").Split('·')[0].Trim()).OrderByDescending(g=>g.Count()).ToList();
        int yy=62;
        foreach(var map in maps.Take(10)){
          string mapName=string.IsNullOrWhiteSpace(map.Key)?"Neznámá mapa":map.Key;
          var rows=map.ToList();int wins=rows.Count(m=>m.Result=="Win");
          Panel row=Surface(scrolling,3,yy-62,w-48,39,shade);
          Copy(row,mapName,13,8,(int)(row.Width*.31),27,10,fg,true);
          Copy(row,rows.Count+" her",(int)(row.Width*.34),8,76,25,9,muted,false);
          Copy(row,"WR "+(100.0*wins/rows.Count).ToString("0")+"%",(int)(row.Width*.55),8,95,26,10,green,true);
          Copy(row,"HS "+AllHs(rows),row.Width-122,7,110,28,10,tGold,true).TextAlign=ContentAlignment.MiddleRight;
          yy+=47;
        }
        if(maps.Count==0)Copy(scrolling,"Zatím nemáš uložené CS2 zápasy.",5,5,w-50,32,11,muted,false);
        scrolling.AutoScrollMinSize=new Size(0,Math.Max(0,yy-62+12));
        int bestHs=KnownHs(mm).Count==0?-1:KnownHs(mm).Max(m=>m.Headshots);
        Copy(box,"CELKOVÉ HS%: "+AllHs(mm)+"    •    NEJVÍCE HS V ZÁPASE: "+Optional(bestHs),17,box.Height-61,w-35,31,12,tGold,true);
      }else{
        Func<Match,string> key=m=>recordsGame=="LoL"?(string.IsNullOrEmpty(m.Champion)?"Neznámý šampion":m.Champion):(string.IsNullOrEmpty(m.Queue)?"Neznámý režim":m.Queue);
        var groups=mm.GroupBy(key).OrderByDescending(g=>g.Count()).Take(9).ToList();
        int yy=62;
        foreach(var group in groups){
          Panel row=Surface(scrolling,3,yy-62,w-48,42,shade);
          int wins=group.Count(m=>m.Result=="Win");
          Copy(row,group.Key,14,9,(int)(row.Width*.45),29,11,fg,true);
          Copy(row,group.Count()+" her",(int)(row.Width*.50),10,75,25,10,muted,false);
          Copy(row,"WR "+(100.0*wins/group.Count()).ToString("0")+"%",row.Width-124,8,112,28,11,green,true).TextAlign=ContentAlignment.MiddleRight;
          yy+=50;
        }
        if(groups.Count==0)Copy(scrolling,"Zatím nejsou uložené zápasy.",5,5,w-50,29,11,muted,false);
        scrolling.AutoScrollMinSize=new Size(0,Math.Max(0,yy-62+12));
        if(recordsGame=="LoL"){
          var vis=mm.Where(m=>m.VisionScore>=0).ToList();
          Copy(box,"PRŮMĚRNÝ VISION SCORE: "+(vis.Count>0?vis.Average(m=>m.VisionScore).ToString("0.0"):"—")+
            "   •   CELKOVÉ ZAZNAMENANÉ DMG: "+mm.Where(m=>m.Damage>=0).Sum(m=>m.Damage).ToString("N0"),
            18,box.Height-61,w-38,31,11,ctBlue,true);
        }
      }
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
        Copy(area,"Novinky ve verzi 1.0.0",20,264,width-40,26,11,fg,true);
        Copy(area,"ULTIMATE 1.0: CS2 headshoty a HS%, live statistiky, mapa, osobní rekordy, herní kalendář, session recap, smart insights, hlubší LoL statistiky, rozšířené achievementy a přehlednější navigace.",20,298,width-44,76,10,muted,false).AutoEllipsis=false;
      }else if(settingsTab=="Connect"){
        Copy(area,"LEAGUE OF LEGENDS",20,20,width-40,30,13,accent,true);
        Copy(area,riot.GameName+"#"+riot.TagLine+"  •  "+riot.Region.ToUpperInvariant(),20,60,width-40,27,12,fg,true);
        Copy(area,riotStatus,20,95,width-44,55,10,muted,false).AutoEllipsis=false;
        ButtonAt(area,"RIOT ÚČET / API KEY",20,162,232,40,()=>ConfigureRiot(),true);
        Button sync=ButtonAt(area,riotSyncBusy?"SYNCHRONIZUJI...":"SYNC MATCHES",265,162,202,40,()=>TrySyncLoL(false),false);sync.Enabled=!riotSyncBusy;
        Rule(area,20,226,width-40,line);
        Copy(area,"COUNTER-STRIKE 2 • GSI",20,237,width-40,25,12,fg,true);
        Copy(area,"Živá K/D/A, skóre a HS% z GSI. Pro HS% je NUTNÉ znovu vygenerovat CFG a restartovat CS2. Staré zápasy bez HS dat zůstávají neznámé.",20,269,width-42,48,9,muted,false).AutoEllipsis=false;
        ButtonAt(area,"NASTAVIT CS2 GSI",20,322,220,38,()=>ConfigureCs2Gsi(),true);
        Copy(area,cs2Status,257,320,Math.Max(160,width-284),72,9,muted,false).AutoEllipsis=false;
        Copy(area,"R6 Siege: zatím jen herní čas + ruční zápasy · Riot API klíč zůstává v paměti.",20,403,width-40,32,9,muted,false);
      }else{
        Copy(area,"MOJE DATA",20,20,width-40,28,13,accent,true);
        Copy(area,"Statistiky jsou uložené lokálně na tvém počítači.",20,59,width-40,40,10,muted,false);
        Copy(area,store.FileName,20,101,width-40,38,9,fg,false);
        ButtonAt(area,"OTEVŘÍT SLOŽKU",20,153,205,39,()=>Process.Start("explorer.exe",store.Folder),false);
        ButtonAt(area,"ZÁLOHOVAT DATA",239,153,205,39,()=>BackupStats(),true);
        ButtonAt(area,"OBNOVIT ZE ZÁLOHY",458,153,218,39,()=>RestoreStats(),false);
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
        Copy(row,m.Kills+" / "+m.Deaths+" / "+m.Assists,(int)(rowW*0.46),6,(int)(rowW*0.25),25,10,fg,true);
        if(m.Game=="CS2" && rowW>=510)Copy(row,"HS "+FormatHs(m)+(m.HeadshotsPartial?" *":""),(int)(rowW*0.46),28,(int)(rowW*0.24),17,8,m.Headshots>=0?tGold:muted,true);
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
        dlg.BackColor=side;dlg.ForeColor=fg;dlg.ClientSize=new Size(500,505);
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
        if(match.Game=="CS2"){
          AddText(dlg,"HEADSHOTS: "+(match.Headshots<0?"— (nezaznamenáno)":match.Headshots+"  /  "+FormatHs(match))+(match.HeadshotsPartial?"  (neúplná data)":""),23,314,456,30,12,tGold,true);
        }else if(match.Game=="LoL"){
          AddText(dlg,"ROLE: "+(match.Role==""?"—":match.Role)+"    VISION: "+Optional(match.VisionScore)+"    WARDS: "+Optional(match.Wards),23,314,456,26,10,accent,true);
          AddText(dlg,"DMG: "+Optional(match.Damage)+"    GOLD: "+Optional(match.Gold)+"    CS: "+Optional(match.Cs),23,344,456,25,10,fg,false);
        }
        ButtonAt(dlg,"ZAVŘÍT",25,445,200,39,()=>dlg.Close(),false);
        ButtonAt(dlg,"SMAZAT ZÁPAS",244,445,225,39,()=>{
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
            w.WriteLine("Game,Date,Result,Kills,Deaths,Assists,Champion,Queue,Source,MatchID,Headshots,HSPercent,HSPartial,Vision,Wards,Damage,Gold,CS,Role");
            foreach(Match m in store.Matches) w.WriteLine(Csv(m.Game)+","+Csv(m.Date)+","+Csv(m.Result)+","+m.Kills+","+m.Deaths+","+m.Assists+","+Csv(m.Champion)+","+Csv(m.Queue)+","+Csv(m.Source)+","+Csv(m.MatchId)+","+Csv(m.Headshots<0?"":m.Headshots.ToString())+","+Csv(m.Headshots<0?"":FormatHs(m))+","+Csv(m.HeadshotsPartial?"partial":"")+","+Csv(OptionalCsv(m.VisionScore))+","+Csv(OptionalCsv(m.Wards))+","+Csv(OptionalCsv(m.Damage))+","+Csv(OptionalCsv(m.Gold))+","+Csv(OptionalCsv(m.Cs))+","+Csv(m.Role));
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
    void RestoreStats(){
      if(active.Count>0){MessageBox.Show(this,"Obnovení proveď až po ukončení her, aby se nenačetla neúplná session.","GIOVI.GG");return;}
      using(OpenFileDialog dialog=new OpenFileDialog()){
        dialog.Filter="XML záloha (*.xml)|*.xml";dialog.Title="Obnovit statistiky z XML zálohy";
        if(dialog.ShowDialog(this)!=DialogResult.OK)return;
        if(MessageBox.Show(this,"Aktuální data se zazálohují do složky GioviGG, potom se nahradí zvolenou zálohou. Pokračovat?",
          "Obnova statistik",MessageBoxButtons.YesNo,MessageBoxIcon.Warning)!=DialogResult.Yes)return;
        try {
          FileInfo source=new FileInfo(dialog.FileName);
          if(source.Length<10 || source.Length>100*1024*1024)throw new Exception("Neplatná velikost zálohy.");
          XDocument doc=XDocument.Load(dialog.FileName);
          if(doc.Root==null || doc.Root.Name!="giovi")throw new Exception("Soubor není záloha GIOVI.GG.");
          string backup=Path.Combine(store.Folder,"tracker-before-restore-"+DateTime.Now.ToString("yyyyMMdd-HHmmss")+".xml");
          store.Save();File.Copy(store.FileName,backup,true);
          string temp=store.FileName+".restore";
          File.Copy(dialog.FileName,temp,true);
          if(File.Exists(store.FileName))File.Replace(temp,store.FileName,null);else File.Move(temp,store.FileName);
          store.Reload();ShowPage(page);
          MessageBox.Show(this,"Obnoveno! Předchozí data: "+backup,"GIOVI.GG",MessageBoxButtons.OK,MessageBoxIcon.Information);
        }catch(Exception ex){MessageBox.Show(this,"Obnova selhala: "+ex.Message,"GIOVI.GG",MessageBoxButtons.OK,MessageBoxIcon.Error);}
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
    // Simple, responsive CS2 live UI. No chart dependencies or game-memory reading.
    bool Cs2Online(){return cs2State.SeenUtc!=DateTime.MinValue &&
      DateTime.UtcNow-cs2State.SeenUtc<TimeSpan.FromSeconds(40);}
    Label Cs2Bind(string key,Control parent,string initial,int x,int y,int width,int height,float size,Color color,bool bold){
      Label item=Copy(parent,initial,x,y,width,height,size,color,bold);
      cs2Ui[key]=item;
      return item;
    }
    void Cs2Set(string key,string value,Color color){
      Label label;
      if(cs2Ui.TryGetValue(key,out label) && label!=null && !label.IsDisposed){
        if(label.Text!=value)label.Text=value;
        if(label.ForeColor!=color)label.ForeColor=color;
      }
    }
    string Cs2HsText(){
      if(cs2State.Headshots<0 || cs2State.Kills<=0)return "—";
      return (100.0*Math.Min(cs2State.Kills,cs2State.Headshots)/cs2State.Kills).ToString("0.0")+"%"+(cs2State.HeadshotsPartial?" *":"");
    }
    void UpdateCs2Widgets(){
      bool online=Cs2Online();
      string map=online && !string.IsNullOrEmpty(cs2State.Map)?cs2State.Map:"ČEKÁM NA MAPU";
      string mode=online && !string.IsNullOrEmpty(cs2State.Mode)?cs2State.Mode.ToUpperInvariant():"CS2 / GAME STATE INTEGRATION";
      string phase=online && !string.IsNullOrEmpty(cs2State.Phase)?cs2State.Phase.ToUpperInvariant():"OFFLINE";
      string sideName=cs2State.Team.Equals("CT",StringComparison.OrdinalIgnoreCase)?"COUNTER-TERRORISTS":
        cs2State.Team.Equals("T",StringComparison.OrdinalIgnoreCase)?"TERRORISTS":"NEURČENO";
      string freshness=online?"● LIVE DATA":"○ ČEKÁM NA DATA";
      Cs2Set("previewStatus",freshness,online?green:muted);
      Cs2Set("previewMap",map,fg);
      Cs2Set("previewMode",mode,muted);
      Cs2Set("previewCt",online?cs2State.Ct.ToString():"—",ctBlue);
      Cs2Set("previewT",online?cs2State.T.ToString():"—",tGold);
      Cs2Set("previewKills",online&&cs2State.HasStats?cs2State.Kills.ToString():"—",fg);
      Cs2Set("previewDeaths",online&&cs2State.HasStats?cs2State.Deaths.ToString():"—",fg);
      Cs2Set("previewAssists",online&&cs2State.HasStats?cs2State.Assists.ToString():"—",fg);
      Cs2Set("previewHs",online&&cs2State.Headshots>=0?Cs2HsText():"—",tGold);
      Cs2Set("liveStatus",online?"●  PŘIPOJENO — REAL-TIME":"○  ČEKÁM NA PŘIPOJENÍ",online?green:muted);
      Cs2Set("liveMap",map,fg);
      Cs2Set("liveMode",mode,muted);
      Cs2Set("liveCt",online?cs2State.Ct.ToString():"—",ctBlue);
      Cs2Set("liveT",online?cs2State.T.ToString():"—",tGold);
      Cs2Set("liveKills",online&&cs2State.HasStats?cs2State.Kills.ToString():"—",green);
      Cs2Set("liveDeaths",online&&cs2State.HasStats?cs2State.Deaths.ToString():"—",danger);
      Cs2Set("liveAssists",online&&cs2State.HasStats?cs2State.Assists.ToString():"—",accent);
      Cs2Set("liveHeadshots",online&&cs2State.Headshots>=0?cs2State.Headshots.ToString():"—",tGold);
      Cs2Set("liveHs",online&&cs2State.Headshots>=0?Cs2HsText():"—",tGold);
      Cs2Set("livePhase",phase,online?green:muted);
      Cs2Set("liveTeam",sideName,online?fg:muted);
      string ratio=online&&cs2State.HasStats?(cs2State.Deaths==0?""+cs2State.Kills:(cs2State.Kills/(double)cs2State.Deaths).ToString("0.00")):"—";
      Cs2Set("liveKdr",ratio,fg);
      Cs2Set("liveSignal",online?"Poslední herní data: "+(int)Math.Max(0,(DateTime.UtcNow-cs2State.SeenUtc).TotalSeconds)+" s zpět":"GIOVI.GG čeká na události z CS2",online?green:muted);
      Cs2Set("liveHint",online?"Zápas se uloží po zachycení konce hry (gameover) a platných výsledků.":
        "Spusť CS2 s nastaveným GSI a nech GIOVI.GG běžet na pozadí.",muted);
      if(cs2CtBar!=null&&!cs2CtBar.IsDisposed && cs2TBar!=null&&!cs2TBar.IsDisposed){
        int total=cs2State.Ct+cs2State.T;
        int width=Math.Max(1,cs2CtBar.Parent.ClientSize.Width);
        cs2CtBar.Width=online&&total>0?Math.Max(3,(int)(width*cs2State.Ct/(double)total)):3;
        cs2TBar.Width=online&&total>0?Math.Max(3,(int)(width*cs2State.T/(double)total)):3;
      }
    }
    void BuildCs2Mini(Panel panel,int width,int height){
      Surface(panel,0,0,width,3,ctBlue);
      Copy(panel,"CS2  /  LIVE MATCH",16,13,width-32,25,11,fg,true);
      Cs2Bind("previewStatus",panel,"",16,42,width-32,22,9,muted,true);
      Rule(panel,15,70,width-30,line);
      Cs2Bind("previewMap",panel,"",16,78,width-32,30,14,fg,true);
      Cs2Bind("previewMode",panel,"",16,105,width-32,22,8,muted,true);
      Panel board=Surface(panel,15,131,width-30,64,shade);
      Copy(board,"CT",14,8,40,20,9,ctBlue,true);
      Cs2Bind("previewCt",board,"—",14,27,62,34,21,ctBlue,true);
      Copy(board,"T",board.Width-76,8,60,20,9,tGold,true).TextAlign=ContentAlignment.MiddleRight;
      Cs2Bind("previewT",board,"—",board.Width-75,27,60,34,21,tGold,true).TextAlign=ContentAlignment.MiddleRight;
      Copy(board,"VS",board.Width/2-16,22,32,22,9,muted,true).TextAlign=ContentAlignment.MiddleCenter;
      int buttonY=height-45;
      if(height>=330){
        int col=(width-30)/3;
        string[] names={"K","D","A"};string[] keys={"previewKills","previewDeaths","previewAssists"};
        for(int i=0;i<3;i++){
          int px=15+i*col;
          Copy(panel,names[i],px,209,col-3,18,9,muted,true).TextAlign=ContentAlignment.MiddleCenter;
          Cs2Bind(keys[i],panel,"—",px,226,col-3,29,17,fg,true).TextAlign=ContentAlignment.MiddleCenter;
        }
      }
      if(height>=330){
        Copy(panel,"HS%",15,263,58,20,9,muted,true);
        Cs2Bind("previewHs",panel,"—",70,262,Math.Max(80,width-84),24,13,tGold,true);
      }
      ButtonAt(panel,"OTEVŘÍT LIVE MATCH  →",13,buttonY,width-26,34,()=>{gameTab="Live";ShowPage("CS2");},true);
      UpdateCs2Widgets();
    }
    void Cs2Metric(Control parent,string label,string key,int x,int y,int width,int height,Color color){
      Panel tile=Surface(parent,x,y,width,height,card);
      Surface(tile,0,0,4,height,color);
      Copy(tile,label,17,13,width-32,19,9,muted,true);
      Cs2Bind(key,tile,"—",17,38,width-32,49,27,color,true);
    }
    void BuildCs2Live(){
      int w=ViewWidth,h=ViewHeight,x=24;
      // Only the dedicated live screen scrolls, and only if the window is short.
      if(content.ClientSize.Height<715){content.AutoScroll=true;content.AutoScrollMinSize=new Size(0,718);}
      Panel hero=Surface(content,x,65,w,190,shade);
      Surface(hero,0,0,5,190,ctBlue);
      Cs2Bind("liveMap",hero,"ČEKÁM NA MAPU",22,15,Math.Max(190,w-340),32,19,fg,true);
      Cs2Bind("liveMode",hero,"CS2 / GSI",23,49,Math.Max(180,w-340),22,9,muted,true);
      Cs2Bind("liveStatus",hero,"",Math.Max(220,w-306),20,280,29,10,green,true).TextAlign=ContentAlignment.MiddleRight;
      ButtonAt(hero,"NASTAVIT GSI",w-189,48,167,28,()=>ConfigureCs2Gsi(),false);
      Rule(hero,21,83,w-42,line);
      int two=(w-70)/2;
      Panel ct=Surface(hero,20,95,two,78,Color.FromArgb(29,44,66));
      Panel t=Surface(hero,50+two,95,two,78,Color.FromArgb(63,43,35));
      Copy(ct,"COUNTER-TERRORISTS",13,9,two-95,21,9,ctBlue,true);
      Cs2Bind("liveCt",ct,"—",two-88,13,75,52,31,ctBlue,true).TextAlign=ContentAlignment.MiddleRight;
      Copy(t,"TERRORISTS",13,9,two-95,21,9,tGold,true);
      Cs2Bind("liveT",t,"—",two-88,13,75,52,31,tGold,true).TextAlign=ContentAlignment.MiddleRight;
      Panel ctTrack=Surface(ct,13,63,two-26,5,line);
      Panel tTrack=Surface(t,13,63,two-26,5,line);
      cs2CtBar=Surface(ctTrack,0,0,3,5,ctBlue);
      cs2TBar=Surface(tTrack,0,0,3,5,tGold);
      Copy(hero,"VS",w/2-18,124,37,23,10,muted,true).TextAlign=ContentAlignment.MiddleCenter;
      int gap=9,metric=(w-4*gap)/5,yy=267;
      Cs2Metric(content,"KILLS","liveKills",x,yy,metric,96,green);
      Cs2Metric(content,"DEATHS","liveDeaths",x+(metric+gap),yy,metric,96,danger);
      Cs2Metric(content,"ASSISTS","liveAssists",x+2*(metric+gap),yy,metric,96,accent);
      Cs2Metric(content,"HEADSHOTS","liveHeadshots",x+3*(metric+gap),yy,metric,96,tGold);
      Cs2Metric(content,"HS%","liveHs",x+4*(metric+gap),yy,metric,96,tGold);
      Panel details=Surface(content,x,376,w,105,card);
      Copy(details,"STAV ZÁPASU",17,14,166,20,9,muted,true);
      Cs2Bind("livePhase",details,"OFFLINE",17,41,Math.Max(145,w/4),33,18,fg,true);
      Surface(details,(int)(w*.29),12,1,72,line);
      Copy(details,"TVŮJ TÝM",(int)(w*.32),14,Math.Max(150,w/3),20,9,muted,true);
      Cs2Bind("liveTeam",details,"NEURČENO",(int)(w*.32),41,Math.Max(150,(int)(w*.40)),32,13,fg,true);
      Copy(details,"K/D",w-147,14,120,22,9,muted,true).TextAlign=ContentAlignment.MiddleRight;
      Cs2Bind("liveKdr",details,"—",w-147,39,120,35,20,fg,true).TextAlign=ContentAlignment.MiddleRight;
      Cs2Bind("liveSignal",details,"",17,81,w-32,19,8,muted,false);
      Copy(content,"* HS% s hvězdičkou = neúplný záznam (tracker se připojil během zápasu).",x,482,w,20,8,muted,false);
      Panel recent=Surface(content,x,508,w,Math.Max(151,h-517),card);
      Copy(recent,"POSLEDNÍ CS2 ZÁPASY",15,14,w-195,24,11,fg,true);
      ButtonAt(recent,"VŠECHNY  →",w-151,10,135,32,()=>{gameTab="Matches";ShowPage("CS2");},false);
      List<Match> rows=Of("CS2").OrderByDescending(m=>m.Date).Take(3).ToList();
      MatchList(recent,rows,12,51,w-24,Math.Max(86,recent.Height-62),3);
      // The tracking explanation stays visible at the top; only the recent list scrolls.
      content.AutoScrollMinSize=new Size(0,Math.Max(h+4,704));
      UpdateCs2Widgets();
    }
    void ReceiveCs2(Cs2State snapshot,Match completed){
      cs2State=snapshot;
      cs2Status="CS2 GSI: "+snapshot.Map+" / "+snapshot.Phase+" · "+snapshot.Kills+" / "+snapshot.Deaths+" / "+snapshot.Assists+" · HS "+Cs2HsText();
      if(completed!=null){
        if(!store.Matches.Any(m=>m.MatchId==completed.MatchId)){
          store.Matches.Add(completed);
          try {store.Save();cs2Status="CS2 GSI: zápas na "+snapshot.Map+" uložen ("+completed.Result+").";}
          catch(Exception ex){store.Matches.Remove(completed);cs2Status="CS2 GSI: chyba uložení: "+ex.Message;}
        }
        if(page=="CS2"||page=="Overview"||page=="History"||page=="Analytics")ShowPage(page);
      }
      if(page=="CS2")UpdateCs2Widgets();
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
    void Finish(string game,DateTime end){DateTime start=active[game];active.Remove(game);long seconds=Math.Max(0,(long)(end-start).TotalSeconds);if(seconds>0){store.Sessions.Add(new Session{Game=game,Start=start.ToString("yyyy-MM-ddTHH:mm:ss"),Seconds=seconds});try{store.Save();}catch(Exception ex){MessageBox.Show("Chyba ukládání session: "+ex.Message);}
        if(tray!=null && !exiting){
          List<Match> recap=store.Matches.Where(m=>m.Game==game && MatchDate(m).HasValue &&
            MatchDate(m).Value>=start && MatchDate(m).Value<=end.AddSeconds(10)).ToList();
          int wins=recap.Count(m=>m.Result=="Win");
          tray.BalloonTipTitle="GIOVI.GG — " + game + " session";
          tray.BalloonTipText=(seconds/3600)+" h "+((seconds%3600)/60)+" min • "+recap.Count+" zápasů • "+wins+" výher";
          tray.ShowBalloonTip(4000);
        }
      }}
    bool AutoStartEnabled(){using(RegistryKey k=Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run",false)){return k!=null&&k.GetValue("GioviGG")!=null;}}
    void ToggleAutoStart(){try{using(RegistryKey k=Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run",true)){if(AutoStartEnabled()) k.DeleteValue("GioviGG",false);else k.SetValue("GioviGG","\""+Application.ExecutablePath+"\"");}}catch(Exception ex){MessageBox.Show(ex.Message,"Autostart error");}}
    void RestoreApp(){Show();WindowState=FormWindowState.Normal;Activate();}
    void ExitApp(){exiting=true;if(cs2Receiver!=null)cs2Receiver.Dispose();scanTimer.Stop();liveTimer.Stop();riotTimer.Stop();foreach(string g in active.Keys.ToList())Finish(g,DateTime.Now);tray.Visible=false;tray.Dispose();Close();Application.Exit();}
    [STAThread] static void Main(){Application.EnableVisualStyles();Application.SetCompatibleTextRenderingDefault(false);Application.Run(new App());}
  }
}
