using System;
using System.Collections.Generic;
using System.Linq;
using System.Web.Script.Serialization;
namespace DotaArchive {
internal static class OfficialEvidenceReview {
public static void Main() {
 var t=new DateTimeOffset(2016,12,13,0,0,0,TimeSpan.Zero);
 var cases=new[]{
 new[]{"unrelated-subject","The 7.00 update notes are ready. The new terrain is now available to everyone."},
 new[]{"explicit-negation","7.00 is not now available to everyone."},
 new[]{"dota-test-scope","A preview of 7.00 is now available to everyone on Dota 2 Test."},
 new[]{"retold-past-future","Last year, we announced that 7.00 will go live to the world."}
 };
 var observations=new List<object>();
 foreach(var test in cases) {
  var result=OfficialPatchResolver.ExtractReleaseStatements("7.00","7.00 Update",test[1],t,"1","synthetic-review");
  observations.Add(new {Case=test[0],Text=test[1],ExpectedStates=new string[0],ActualStates=result.Select(x=>x.State).ToArray()});
 }
 var anchor=new OfficialPatchResolver.PatchAnchor {AnnouncedAtUtc=t,Title="7.00 Update",PlainText="The release"};anchor.PatchUrls.Add("dota2.com/700");
 var build=new HistoryBuildEvent {BuildId="999",PublishedAtUtc=t.AddHours(1),Title="Client hotfix",SourceUrl="https://steamdb.info/patchnotes/999/"};
 var page=new HistoryPageSnapshot {Url=build.SourceUrl,Html="<article><h1>Client hotfix</h1><p>This fixes a crash introduced in <a href='https://www.dota2.com/700/'>7.00</a>.</p></article>"};
 observations.Add(new {Case="hotfix-references-release",ExpectedFirstReleaseAssociation=false,ActualAnchorMatches=OfficialPatchResolver.MatchPatchPage(page,build,"7.00",new[]{anchor}).Count});
 var hotfixAnchor=new OfficialPatchResolver.PatchAnchor {AnnouncedAtUtc=t,Title="7.00 hotfix",PlainText="Fixes a crash in 7.00."};hotfixAnchor.NewsIds.Add("123");
 var hotfixPage=new HistoryPageSnapshot {Url=build.SourceUrl,Html="<article><h1>7.00 hotfix</h1><p>Fixes a crash. <a href='https://steamcommunity.com/games/dota2/announcements/detail/123'>Official notes</a></p></article>"};
 observations.Add(new {Case="hotfix-is-own-official-article",ExpectedFirstReleaseAssociation=false,ActualAnchorMatches=OfficialPatchResolver.MatchPatchPage(hotfixPage,build,"7.00",new[]{hotfixAnchor}).Count});
 long unix=(long)(t-new DateTimeOffset(1970,1,1,0,0,0,TimeSpan.Zero)).TotalSeconds;
 var eventJson=new JavaScriptSerializer().Serialize(new[]{new{event_name="7.00 hotfix",appid=570,rtime32_start_time=unix,build_id=999,build_branch="public",published=1,hidden=0,announcement_body=new{headline="7.00 hotfix"}}});
 string eventHtml="<div data-partnereventstore='"+eventJson.Replace("'","&#39;")+"'></div>";
 observations.Add(new {Case="hotfix-has-explicit-official-build",ExpectedFirstReleaseAssociation=false,ActualBuildMatches=OfficialPatchResolver.ParseOfficialEventBuilds(eventHtml,"https://steamcommunity.com/games/dota2/announcements/detail/123","7.00",hotfixAnchor,new[]{build}).Count});
 Console.WriteLine(new JavaScriptSerializer().Serialize(new{Scope="Synthetic adversarial examples; no network or UI",ObservedAtUtc=DateTime.UtcNow.ToString("o"),Observations=observations}));
}
}}
