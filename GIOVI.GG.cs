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

[assembly: AssemblyVersion("0.5.1.0")]
[assembly: AssemblyFileVersion("0.5.1.0")]

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
