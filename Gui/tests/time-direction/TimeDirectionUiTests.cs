using System;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace DotaArchive
{
    // Constructs the application form without showing it or initializing WebView2.
    // These are state-transition checks, not pixel/layout snapshots.
    internal static class TimeDirectionUiTests
    {
        private static int checks;
        private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.NonPublic;
        private static T Field<T>(PrototypeForm form, string name) { return (T)typeof(PrototypeForm).GetField(name, Flags).GetValue(form); }
        private static void Set(PrototypeForm form, string name, object value) { typeof(PrototypeForm).GetField(name, Flags).SetValue(form, value); }
        private static object Call(PrototypeForm form, string name, params object[] arguments) { return typeof(PrototypeForm).GetMethod(name, Flags).Invoke(form, arguments); }
        private static void Check(bool value, string label) { if(!value) throw new Exception(label); checks++; }
        [STAThread]
        public static int Main(string[] args)
        {
            PrototypeForm form = null;
            try
            {
                form = new PrototypeForm();
                ComboBox mode = Field<ComboBox>(form, "queryMode"), direction = Field<ComboBox>(form, "timeDirection");
                TextBox input = Field<TextBox>(form, "queryValue");
                Button download = Field<Button>(form, "download"), launch = Field<Button>(form, "launch");
                string welcome = Field<TextBox>(form, "planText").Text;
                int patchSection = welcome.IndexOf("补丁号 · 全程本地查询", StringComparison.Ordinal);
                Check(patchSection >= 0, "welcome identifies offline patch query section");
                string patchWelcome = welcome.Substring(patchSection);
                Check(patchWelcome.Contains("此前最近版本") && patchWelcome.Contains("此后最近版本") && patchWelcome.Contains("严格排除"), "welcome describes both strict patch date directions");
                Check(patchWelcome.Contains("不会自动打开 SteamDB") && patchWelcome.Contains("结果可能属于相邻补丁"), "welcome discloses local scope and neighboring patch possibility");
                Check(!welcome.Contains("首发证据时优先") && !welcome.Contains("不受时间方向影响"), "welcome does not describe superseded patch direction behavior");
                Check(mode.SelectedIndex == 0 && direction.SelectedIndex == 0 && direction.Enabled, "new form defaults to enabled before-time selection");
                input.Text = "2015-12-21 12:00:00";
                var before = (HistoryQuery)Call(form, "ReadQuery");
                Check(before.TimeDirection == HistoryTimeDirection.Before && before.BeijingTime.Value.ToUniversalTime() == DateTimeOffset.Parse("2015-12-21T04:00:00Z"), "form reads default direction and exact Beijing instant");
                Set(form, "resolved", new ResolvedHistoryPlan { Complete = true, BuildId = "907474" });
                Set(form, "verifiedFingerprint", "old-result"); Set(form, "targetDirectory", "C:\\obsolete-client");
                Call(form, "UpdateButtons"); Check(download.Enabled, "complete prior selection is download-enabled before direction switch");
                direction.SelectedIndex = 1;
                Check(input.Text == "2015-12-21 12:00:00", "changing direction preserves the typed timestamp");
                Check(Field<ResolvedHistoryPlan>(form, "resolved") == null && Field<string>(form, "verifiedFingerprint") == null && Field<string>(form, "targetDirectory") == null, "changing direction invalidates old result, files and launch proof");
                Check(!download.Enabled && !launch.Enabled, "stale plan cannot download or launch after direction change");
                var after = (HistoryQuery)Call(form, "ReadQuery");
                Check(after.TimeDirection == HistoryTimeDirection.After && after.BeijingTime == before.BeijingTime, "after selection maps to same exact query instant");
                Check(((string)Call(form, "QueryRuleDescription")).Contains("严格晚于"), "visible match rule agrees with after selection");
                Set(form, "busy", true); Call(form, "UpdateButtons");
                Check(!direction.Enabled && !mode.Enabled && !input.Enabled, "running operation prevents changing its direction or input");
                Set(form, "busy", false); Call(form, "UpdateButtons");
                Check(direction.Enabled, "finished time operation restores direction control");
                Set(form, "resolved", new ResolvedHistoryPlan { Complete = true, BuildId = "907474" });
                Set(form, "verifiedFingerprint", "old-result"); Set(form, "targetDirectory", "C:\\obsolete-client");
                Call(form, "UpdateButtons");
                mode.SelectedIndex = 1;
                Check(direction.Enabled && direction.SelectedIndex == 1 && input.Text == "7.00", "patch mode enables and preserves chosen after direction");
                Check(Field<ResolvedHistoryPlan>(form, "resolved") == null && Field<string>(form, "verifiedFingerprint") == null && Field<string>(form, "targetDirectory") == null, "mode change invalidates old plan and launch proof");
                Check(!download.Enabled && !launch.Enabled, "switch to patch mode prevents stale download and launch");
                var patch = (HistoryQuery)Call(form, "ReadQuery");
                Check(patch.Patch == "7.00" && !patch.BeijingTime.HasValue && !patch.RequestedUtc.HasValue && patch.TimeDirection == HistoryTimeDirection.After, "patch ReadQuery carries chosen after direction");
                Check(((string)Call(form, "QueryRuleDescription")).Contains("严格晚于") && ((string)Call(form, "QueryRuleDescription")).Contains("相等排除"), "patch rule states strict after without first-release shortcut");
                input.Text = "7.22B";
                Check(((HistoryQuery)Call(form, "ReadQuery")).Patch == "7.22b", "letter patch normalized without losing suffix");
                Set(form, "resolved", new ResolvedHistoryPlan { Complete = true, BuildId = "3863157" });
                Set(form, "verifiedFingerprint", "patch-result"); Set(form, "targetDirectory", "C:\\obsolete-patch");
                Call(form, "UpdateButtons");
                direction.SelectedIndex = 0;
                Check(input.Text == "7.22B" && direction.Enabled, "patch direction change preserves typed version");
                Check(Field<ResolvedHistoryPlan>(form, "resolved") == null && Field<string>(form, "verifiedFingerprint") == null && Field<string>(form, "targetDirectory") == null, "patch direction change invalidates old plan and launch proof");
                Check(!download.Enabled && !launch.Enabled, "changed patch direction prevents stale download and launch");
                Check(((HistoryQuery)Call(form, "ReadQuery")).TimeDirection == HistoryTimeDirection.Before && ((string)Call(form, "QueryRuleDescription")).Contains("严格早于"), "patch ReadQuery and rule reflect before");
                Set(form, "busy", true); Call(form, "UpdateButtons");
                Check(!direction.Enabled && !mode.Enabled && !input.Enabled, "busy patch query cannot change direction");
                Set(form, "busy", false); Call(form, "UpdateButtons");
                Check(direction.Enabled, "finished patch query restores direction control");
                direction.SelectedIndex = 1;
                mode.SelectedIndex = 0;
                Check(direction.Enabled && direction.SelectedIndex == 1 && ((HistoryQuery)Call(form, "ReadQuery")).TimeDirection == HistoryTimeDirection.After, "returning to time mode retains chosen after preference");
                foreach(HistoryTimeDirection savedDirection in new[] { HistoryTimeDirection.Before, HistoryTimeDirection.After }) {
                    var saved = new ResolvedHistoryPlan { Complete = true, BuildId = "1532815", Patch = "7.01", PatchMatchKind = "LocalReleaseDate" + savedDirection + "Candidate" };
                    int expectedIndex = savedDirection == HistoryTimeDirection.After ? 1 : 0;
                    Set(form, "busy", true);
                    mode.SelectedIndex = 0; direction.SelectedIndex = 1 - expectedIndex;
                    Call(form, "RestoreQuerySelection", saved);
                    Set(form, "restoredSavedPlan", true); Set(form, "resolved", saved);
                    Set(form, "verifiedFingerprint", "saved-proof"); Set(form, "targetDirectory", "C:\\saved-patch");
                    Set(form, "busy", false); Call(form, "UpdateButtons");
                    Check(mode.SelectedIndex == 1 && input.Text == "7.01" && direction.SelectedIndex == expectedIndex && direction.Enabled, "restored patch selection carries saved direction " + savedDirection);
                    Check(Object.ReferenceEquals(Field<ResolvedHistoryPlan>(form, "resolved"), saved) && Field<bool>(form, "restoredSavedPlan"), "busy restore preserves exact saved plan " + savedDirection);
                    Check(((HistoryQuery)Call(form, "ReadQuery")).TimeDirection == savedDirection && ((HistoryQuery)Call(form, "ReadQuery")).Patch == "7.01", "restored patch ReadQuery matches saved direction " + savedDirection);
                    Check(((string)Call(form, "QueryRuleDescription")).Contains("不重新按时间匹配"), "restored plan is described as exact restore " + savedDirection);
                    direction.SelectedIndex = 1 - expectedIndex;
                    Check(Field<ResolvedHistoryPlan>(form, "resolved") == null && !Field<bool>(form, "restoredSavedPlan") && Field<string>(form, "verifiedFingerprint") == null && Field<string>(form, "targetDirectory") == null, "changing restored direction discards saved plan and launch proof " + savedDirection);
                    Check(!download.Enabled && !launch.Enabled && input.Text == "7.01", "changed restored direction keeps input but blocks stale download and launch " + savedDirection);
                    Check(((HistoryQuery)Call(form, "ReadQuery")).TimeDirection != savedDirection && ((string)Call(form, "QueryRuleDescription")).Contains("相等排除"), "changed restored direction becomes a new strict query " + savedDirection);
                }
                foreach(string legacyKind in new[] { "OfficialReleaseWindow", "LocalReleaseDateCandidate", null }) {
                    foreach(int selected in new[] { 0, 1 }) {
                        Set(form, "busy", true); direction.SelectedIndex = selected;
                        var legacy = new ResolvedHistoryPlan { Complete = true, Patch = "7.00", PatchMatchKind = legacyKind };
                        Call(form, "RestoreQuerySelection", legacy);
                        Check(direction.SelectedIndex == selected && mode.SelectedIndex == 1 && input.Text == "7.00", "legacy saved kind preserves existing selection " + (legacyKind ?? "absent") + " " + selected);
                        Set(form, "busy", false); Call(form, "UpdateButtons");
                    }
                }
                Check(!Field<bool>(form, "browserReady") && Field<object>(form, "engine") == null, "state-only tests never initialize browser or download engine");
                string report = new JavaScriptSerializer().Serialize(new { Result = "Passed", Assertions = checks, VisibleWindowOpened = false, BrowserInitialized = false, DownloadEngineStarted = false, Utc = DateTime.UtcNow.ToString("o") });
                File.WriteAllText(Path.Combine(args[0], "ui-results.json"), report); Console.WriteLine(report); return 0;
            }
            catch(Exception ex) { Console.Error.WriteLine(ex); return 1; }
            finally
            {
                if(form != null) { Field<System.Windows.Forms.Timer>(form, "progressTimer").Stop(); Field<System.Windows.Forms.Timer>(form, "progressTimer").Dispose(); Field<BrowserSession>(form, "browser").Dispose(); form.Dispose(); }
            }
        }
    }
}
